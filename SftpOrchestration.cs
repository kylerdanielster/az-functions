using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace AzFunctions;

/// <summary>
/// SFTP Processor (App 2) orchestration and activities. Creates person/address files,
/// uploads each independently to the SFTP server with retry (per-file error isolation),
/// and sends a completion callback with per-file results to the Coordinator.
/// Callback failure is isolated from file uploads — see <see cref="RunOrchestrator"/>.
/// </summary>
public class SftpOrchestration(IHttpClientFactory httpClientFactory, ISftpClientFactory sftpClientFactory)
{
    private static readonly TaskOptions UploadRetryOptions = TaskOptions.FromRetryPolicy(new RetryPolicy(
        maxNumberOfAttempts: 3,
        firstRetryInterval: TimeSpan.FromSeconds(5)));

    private static readonly TaskOptions CallbackRetryOptions = TaskOptions.FromRetryPolicy(new RetryPolicy(
        maxNumberOfAttempts: 3,
        firstRetryInterval: TimeSpan.FromSeconds(5)));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Durable Functions orchestrator. Creates person and address files in parallel,
    /// then uploads each independently to SFTP with retry. Each file has its own try/catch
    /// so a failure in one doesn't prevent the other. Sends a callback with per-file results.
    /// Callback failure is isolated — if the callback fails after retries, the orchestration
    /// still completes (files are already uploaded).
    /// </summary>
    [Function(nameof(SftpOrchestration))]
    public static async Task<string> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ILogger logger = context.CreateReplaySafeLogger(nameof(SftpOrchestration));
        string id = context.InstanceId;

        var request = context.GetInput<SftpProcessRequest>()
            ?? throw new InvalidOperationException("Orchestration input is missing.");

        logger.LogInformation("[SFTP] Orchestration {id} started — batch {batchId}, item {itemId}.",
            id, request.BatchId, request.ItemId);

        // Create both files in parallel
        logger.LogInformation("[SFTP] Orchestration {id} — creating files...", id);
        var personFileTask = context.CallActivityAsync<string>(nameof(CreatePersonFile),
            new CreatePersonFileInput(id, request.Person));
        var addressFileTask = context.CallActivityAsync<string>(nameof(CreateAddressFile),
            new CreateAddressFileInput(id, request.Address));
        await Task.WhenAll(personFileTask, addressFileTask);

        string personFilePath = personFileTask.Result;
        string addressFilePath = addressFileTask.Result;

        // Upload each file independently — a failure in one doesn't prevent the other
        var fileResults = new List<FileResult>();

        logger.LogInformation("[SFTP] Orchestration {id} — uploading person file to SFTP server...", id);
        try
        {
            await context.CallActivityAsync<string>(nameof(UploadFile), personFilePath, UploadRetryOptions);
            fileResults.Add(new FileResult(FileType.Person, Succeeded: true, ErrorMessage: null));
        }
        catch (TaskFailedException ex)
        {
            logger.LogError(ex, "[SFTP] Orchestration {id} — person file upload failed after retries.", id);
            fileResults.Add(new FileResult(FileType.Person, Succeeded: false, ErrorMessage: ex.Message));
            await context.CallActivityAsync(nameof(CleanupTempFiles), new[] { personFilePath });
        }

        logger.LogInformation("[SFTP] Orchestration {id} — uploading address file to SFTP server...", id);
        try
        {
            await context.CallActivityAsync<string>(nameof(UploadFile), addressFilePath, UploadRetryOptions);
            fileResults.Add(new FileResult(FileType.Address, Succeeded: true, ErrorMessage: null));
        }
        catch (TaskFailedException ex)
        {
            logger.LogError(ex, "[SFTP] Orchestration {id} — address file upload failed after retries.", id);
            fileResults.Add(new FileResult(FileType.Address, Succeeded: false, ErrorMessage: ex.Message));
            await context.CallActivityAsync(nameof(CleanupTempFiles), new[] { addressFilePath });
        }

        // Send callback to coordinator with per-file results.
        // Callback failure is isolated — files are already uploaded successfully.
        var result = new SftpProcessResult(request.BatchId, request.ItemId, fileResults);
        try
        {
            await context.CallActivityAsync(nameof(SendCallback),
                new SendCallbackInput(request.CallbackUrl, result), CallbackRetryOptions);
        }
        catch (TaskFailedException ex)
        {
            logger.LogError(ex, "[SFTP] Orchestration {id} — callback to {url} failed after retries. Files are uploaded but Coordinator was not notified.",
                id, request.CallbackUrl);
        }

        bool allSucceeded = fileResults.All(f => f.Succeeded);
        logger.LogInformation("[SFTP] Orchestration {id} — complete (allSucceeded={allSucceeded}).", id, allSucceeded);
        return allSucceeded
            ? $"Uploaded 2 files: {Path.GetFileName(personFilePath)}, {Path.GetFileName(addressFilePath)}."
            : $"Partial failure: {string.Join(", ", fileResults.Where(f => !f.Succeeded).Select(f => f.FileType))}";
    }

    // --- Activity inputs ---

    public record CreatePersonFileInput(string InstanceId, PersonData Person);
    public record CreateAddressFileInput(string InstanceId, AddressData Address);
    public record SendCallbackInput(string CallbackUrl, SftpProcessResult Result);

    // --- Activities ---

    /// <summary>Activity that writes person data to a temp file and returns the file path.</summary>
    [Function(nameof(CreatePersonFile))]
    public static string CreatePersonFile([ActivityTrigger] CreatePersonFileInput input, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(CreatePersonFile));
        var person = input.Person;

        string path = Path.Combine(Path.GetTempPath(), $"person_{input.InstanceId}.txt");
        string content = $"First Name: {person.FirstName}\nLast Name: {person.LastName}\nDate of Birth: {person.DateOfBirth}";
        File.WriteAllText(path, content);
        logger.LogInformation("[SFTP] Created person file at {path}.", path);
        return path;
    }

    /// <summary>Activity that writes address data to a temp file and returns the file path.</summary>
    [Function(nameof(CreateAddressFile))]
    public static string CreateAddressFile([ActivityTrigger] CreateAddressFileInput input, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(CreateAddressFile));
        var address = input.Address;

        string path = Path.Combine(Path.GetTempPath(), $"address_{input.InstanceId}.txt");
        string content = $"Street: {address.Street}\nCity: {address.City}\nState: {address.State}\nZip Code: {address.ZipCode}";
        File.WriteAllText(path, content);
        logger.LogInformation("[SFTP] Created address file at {path}.", path);
        return path;
    }

    /// <summary>Activity that connects to SFTP asynchronously, uploads a local file, and deletes the temp file.</summary>
    [Function(nameof(UploadFile))]
    public async Task<string> UploadFile([ActivityTrigger] string localPath, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(UploadFile));

        string fileName = Path.GetFileName(localPath);
        string remoteFilePath = $"{sftpClientFactory.RemotePath}/{fileName}";

        using var client = await sftpClientFactory.CreateConnectedClientAsync();
        logger.LogInformation("[SFTP] Connected to SFTP server.");

        using var fileStream = File.OpenRead(localPath);
        client.UploadFile(fileStream, remoteFilePath);
        logger.LogInformation("[SFTP] Uploaded {fileName} to {remote}.", fileName, remoteFilePath);

        try { File.Delete(localPath); }
        catch (Exception ex) { logger.LogWarning(ex, "[SFTP] Failed to delete temp file {path}.", localPath); }

        return $"Uploaded {fileName} to {remoteFilePath}.";
    }

    /// <summary>Activity that deletes temp files after a failed upload attempt.</summary>
    [Function(nameof(CleanupTempFiles))]
    public static void CleanupTempFiles([ActivityTrigger] string[] filePaths, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(CleanupTempFiles));
        foreach (string path in filePaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    logger.LogInformation("[SFTP] Cleaned up temp file {path}.", path);
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "[SFTP] Failed to clean up temp file {path}.", path); }
        }
    }

    /// <summary>Activity that POSTs the processing result back to the Coordinator's callback URL.</summary>
    [Function(nameof(SendCallback))]
    public async Task SendCallback([ActivityTrigger] SendCallbackInput input, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(SendCallback));

        using var httpClient = httpClientFactory.CreateClient();
        var response = await httpClient.PostAsJsonAsync(input.CallbackUrl, input.Result, JsonOptions);
        response.EnsureSuccessStatusCode();

        int successCount = input.Result.Files.Count(f => f.Succeeded);
        logger.LogInformation("[SFTP] Callback sent for batch {batchId}, item {itemId} ({successCount}/{totalCount} files succeeded).",
            input.Result.BatchId, input.Result.ItemId, successCount, input.Result.Files.Count);
    }

    // --- SFTP Server Inspection Endpoints ---

    /// <summary>
    /// Testing: Deletes all files from the SFTP server's remote upload directory.
    /// Route: DELETE /api/sftp/files
    /// </summary>
    [Function("SftpOrchestration_DeleteAllFiles")]
    public async Task<HttpResponseData> DeleteAllFiles(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sftp/files")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("SftpOrchestration_DeleteAllFiles");

        using var client = await sftpClientFactory.CreateConnectedClientAsync();

        var files = client.ListDirectory(sftpClientFactory.RemotePath)
            .Where(f => !f.IsDirectory)
            .ToList();

        foreach (var file in files)
        {
            client.DeleteFile($"{sftpClientFactory.RemotePath}/{file.Name}");
        }

        logger.LogInformation("[SFTP] Deleted {count} files from SFTP server.", files.Count);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { deleted = files.Count });
        return response;
    }

    /// <summary>
    /// Testing: Lists all files on the SFTP server with name, size, and last modified time.
    /// Route: GET /api/sftp/files
    /// </summary>
    [Function("SftpOrchestration_ListFiles")]
    public async Task<HttpResponseData> ListFiles(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sftp/files")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("SftpOrchestration_ListFiles");

        using var client = await sftpClientFactory.CreateConnectedClientAsync();

        var files = client.ListDirectory(sftpClientFactory.RemotePath)
            .Where(f => !f.IsDirectory)
            .Select(f => new { name = f.Name, size = f.Length, modified = f.LastWriteTime })
            .ToList();

        logger.LogInformation("[SFTP] Listed {count} files on SFTP server.", files.Count);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await response.WriteAsJsonAsync(files);
        return response;
    }

    /// <summary>
    /// Testing: Returns the contents of a specific file from the SFTP server.
    /// Route: GET /api/sftp/files/{fileName}
    /// </summary>
    [Function("SftpOrchestration_GetFile")]
    public async Task<HttpResponseData> GetFile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sftp/files/{fileName}")] HttpRequestData req,
        string fileName,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("SftpOrchestration_GetFile");

        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
        {
            var badRequest = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid file name.");
            return badRequest;
        }

        string remoteFilePath = $"{sftpClientFactory.RemotePath}/{fileName}";

        using var client = await sftpClientFactory.CreateConnectedClientAsync();

        if (!client.Exists(remoteFilePath))
        {
            var notFound = req.CreateResponse(System.Net.HttpStatusCode.NotFound);
            await notFound.WriteStringAsync($"File not found: {fileName}");
            return notFound;
        }

        using var memoryStream = new MemoryStream();
        client.DownloadFile(remoteFilePath, memoryStream);

        string content = System.Text.Encoding.UTF8.GetString(memoryStream.ToArray());
        logger.LogInformation("[SFTP] Read file {fileName} ({length} bytes) from SFTP server.", fileName, content.Length);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "text/plain");
        await response.WriteStringAsync(content);
        return response;
    }
}
