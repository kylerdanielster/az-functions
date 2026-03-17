using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace Company.Function;

public static class SftpOrchestration
{
    private static readonly TimeSpan SftpTimeout = TimeSpan.FromSeconds(30);

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
        string[] filePaths = [personFilePath, addressFilePath];

        // Upload each file with retry — parallel fan-out
        bool succeeded = true;
        string? errorMessage = null;

        logger.LogInformation("[SFTP] Orchestration {id} — uploading 2 files to SFTP server...", id);
        try
        {
            var uploadPersonTask = context.CallActivityAsync<string>(
                nameof(UploadFile), personFilePath, UploadRetryOptions);
            var uploadAddressTask = context.CallActivityAsync<string>(
                nameof(UploadFile), addressFilePath, UploadRetryOptions);
            await Task.WhenAll(uploadPersonTask, uploadAddressTask);
        }
        catch (TaskFailedException ex)
        {
            logger.LogError(ex, "[SFTP] Orchestration {id} — upload failed after retries.", id);
            succeeded = false;
            errorMessage = ex.Message;
            await context.CallActivityAsync(nameof(CleanupTempFiles), filePaths);
        }

        // Send callback to coordinator
        var result = new SftpProcessResult(request.BatchId, request.ItemId, succeeded, errorMessage);
        await context.CallActivityAsync(nameof(SendCallback),
            new SendCallbackInput(request.CallbackUrl, result), CallbackRetryOptions);

        logger.LogInformation("[SFTP] Orchestration {id} — complete (succeeded={succeeded}).", id, succeeded);
        return succeeded
            ? $"Uploaded 2 files: {Path.GetFileName(personFilePath)}, {Path.GetFileName(addressFilePath)}."
            : $"Failed: {errorMessage}";
    }

    // --- Activity inputs ---

    public record CreatePersonFileInput(string InstanceId, PersonData Person);
    public record CreateAddressFileInput(string InstanceId, AddressData Address);
    public record SendCallbackInput(string CallbackUrl, SftpProcessResult Result);

    // --- Activities ---

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

    [Function(nameof(UploadFile))]
    public static string UploadFile([ActivityTrigger] string localPath, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(UploadFile));

        string host = Environment.GetEnvironmentVariable("SFTP_HOST")
            ?? throw new InvalidOperationException("SFTP_HOST not configured.");
        int port = int.Parse(Environment.GetEnvironmentVariable("SFTP_PORT") ?? "22");
        string username = Environment.GetEnvironmentVariable("SFTP_USERNAME")
            ?? throw new InvalidOperationException("SFTP_USERNAME not configured.");
        string password = Environment.GetEnvironmentVariable("SFTP_PASSWORD")
            ?? throw new InvalidOperationException("SFTP_PASSWORD not configured.");
        string remotePath = Environment.GetEnvironmentVariable("SFTP_REMOTE_PATH") ?? "/upload";

        string fileName = Path.GetFileName(localPath);
        string remoteFilePath = $"{remotePath}/{fileName}";

        using var client = new SftpClient(host, port, username, password);
        client.OperationTimeout = SftpTimeout;
        client.Connect();
        logger.LogInformation("[SFTP] Connected to SFTP server {host}:{port}.", host, port);

        using var fileStream = File.OpenRead(localPath);
        client.UploadFile(fileStream, remoteFilePath);
        logger.LogInformation("[SFTP] Uploaded {fileName} to {remote}.", fileName, remoteFilePath);

        try { File.Delete(localPath); }
        catch (Exception ex) { logger.LogWarning(ex, "[SFTP] Failed to delete temp file {path}.", localPath); }

        return $"Uploaded {fileName} to {remoteFilePath}.";
    }

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

    [Function(nameof(SendCallback))]
    public static async Task SendCallback([ActivityTrigger] SendCallbackInput input, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(SendCallback));

        using var httpClient = new HttpClient();
        var response = await httpClient.PostAsJsonAsync(input.CallbackUrl, input.Result, JsonOptions);
        response.EnsureSuccessStatusCode();

        logger.LogInformation("[SFTP] Callback sent for batch {batchId}, item {itemId} (succeeded={succeeded}).",
            input.Result.BatchId, input.Result.ItemId, input.Result.Succeeded);
    }

    // --- SFTP Server Inspection Endpoints ---

    // Testing: deletes all SFTP files (used by E2E test script)
    [Function("SftpOrchestration_DeleteAllFiles")]
    public static async Task<HttpResponseData> DeleteAllFiles(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sftp/files")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("SftpOrchestration_DeleteAllFiles");

        string host = Environment.GetEnvironmentVariable("SFTP_HOST")
            ?? throw new InvalidOperationException("SFTP_HOST not configured.");
        int port = int.Parse(Environment.GetEnvironmentVariable("SFTP_PORT") ?? "22");
        string username = Environment.GetEnvironmentVariable("SFTP_USERNAME")
            ?? throw new InvalidOperationException("SFTP_USERNAME not configured.");
        string password = Environment.GetEnvironmentVariable("SFTP_PASSWORD")
            ?? throw new InvalidOperationException("SFTP_PASSWORD not configured.");
        string remotePath = Environment.GetEnvironmentVariable("SFTP_REMOTE_PATH") ?? "/upload";

        using var client = new SftpClient(host, port, username, password);
        client.OperationTimeout = SftpTimeout;
        client.Connect();

        var files = client.ListDirectory(remotePath)
            .Where(f => !f.IsDirectory)
            .ToList();

        foreach (var file in files)
        {
            client.DeleteFile($"{remotePath}/{file.Name}");
        }

        logger.LogInformation("[SFTP] Deleted {count} files from SFTP server.", files.Count);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { deleted = files.Count });
        return response;
    }

    [Function("SftpOrchestration_ListFiles")]
    public static async Task<HttpResponseData> ListFiles(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sftp/files")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("SftpOrchestration_ListFiles");

        string host = Environment.GetEnvironmentVariable("SFTP_HOST")
            ?? throw new InvalidOperationException("SFTP_HOST not configured.");
        int port = int.Parse(Environment.GetEnvironmentVariable("SFTP_PORT") ?? "22");
        string username = Environment.GetEnvironmentVariable("SFTP_USERNAME")
            ?? throw new InvalidOperationException("SFTP_USERNAME not configured.");
        string password = Environment.GetEnvironmentVariable("SFTP_PASSWORD")
            ?? throw new InvalidOperationException("SFTP_PASSWORD not configured.");
        string remotePath = Environment.GetEnvironmentVariable("SFTP_REMOTE_PATH") ?? "/upload";

        using var client = new SftpClient(host, port, username, password);
        client.OperationTimeout = SftpTimeout;
        client.Connect();

        var files = client.ListDirectory(remotePath)
            .Where(f => !f.IsDirectory)
            .Select(f => new { name = f.Name, size = f.Length, modified = f.LastWriteTime })
            .ToList();

        logger.LogInformation("[SFTP] Listed {count} files on SFTP server.", files.Count);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await response.WriteAsJsonAsync(files);
        return response;
    }

    [Function("SftpOrchestration_GetFile")]
    public static async Task<HttpResponseData> GetFile(
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

        string host = Environment.GetEnvironmentVariable("SFTP_HOST")
            ?? throw new InvalidOperationException("SFTP_HOST not configured.");
        int port = int.Parse(Environment.GetEnvironmentVariable("SFTP_PORT") ?? "22");
        string username = Environment.GetEnvironmentVariable("SFTP_USERNAME")
            ?? throw new InvalidOperationException("SFTP_USERNAME not configured.");
        string password = Environment.GetEnvironmentVariable("SFTP_PASSWORD")
            ?? throw new InvalidOperationException("SFTP_PASSWORD not configured.");
        string remotePath = Environment.GetEnvironmentVariable("SFTP_REMOTE_PATH") ?? "/upload";

        string remoteFilePath = $"{remotePath}/{fileName}";

        using var client = new SftpClient(host, port, username, password);
        client.OperationTimeout = SftpTimeout;
        client.Connect();

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
