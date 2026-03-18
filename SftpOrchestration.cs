using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace AzFunctions;

/// <summary>
/// SFTP Processor (App 2) orchestration and activities. Generates payment and GL CSV
/// files from all batch payments and uploads each independently to the SFTP server with retry
/// (per-file error isolation). Sends a completion callback with per-file results to the Coordinator.
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
    /// Durable Functions orchestrator. Generates payment and GL CSV files in parallel
    /// from all batch payments, then uploads each from memory to SFTP with retry. Each file has its
    /// own try/catch so a failure in one doesn't prevent the other. Sends a callback with per-file results.
    /// Callback failure is isolated — if the callback fails after retries, the orchestration
    /// still completes (files are already uploaded).
    /// </summary>
    [Function(nameof(SftpOrchestration))]
    public static async Task<string> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ILogger logger = context.CreateReplaySafeLogger(nameof(SftpOrchestration));
        string id = context.InstanceId;

        var request = context.GetInput<SftpBatchRequest>()
            ?? throw new InvalidOperationException("Orchestration input is missing.");

        logger.LogInformation("[SFTP] Orchestration {id} started — batch {batchId}, {paymentCount} payments.",
            id, request.BatchId, request.Payments.Count);

        // Create file content in parallel
        logger.LogInformation("[SFTP] Orchestration {id} — creating CSV files...", id);
        var paymentContentTask = context.CallActivityAsync<string>(nameof(CreatePaymentFile),
            new CreatePaymentFileInput(request.BatchId, request.Payments));
        var glContentTask = context.CallActivityAsync<string>(nameof(CreateGLFile),
            new CreateGLFileInput(request.BatchId, request.Payments));
        await Task.WhenAll(paymentContentTask, glContentTask);

        string paymentContent = paymentContentTask.Result;
        string glContent = glContentTask.Result;

        string paymentFileName = $"payment_{request.BatchId}.csv";
        string glFileName = $"gl_{request.BatchId}.csv";

        // Upload each file independently — a failure in one doesn't prevent the other
        var fileResults = new List<FileResult>();

        logger.LogInformation("[SFTP] Orchestration {id} — uploading payment file to SFTP server...", id);
        try
        {
            await context.CallActivityAsync<string>(nameof(UploadFile),
                new UploadFileInput(paymentFileName, paymentContent), UploadRetryOptions);
            fileResults.Add(new FileResult(FileType.Payment, Succeeded: true, ErrorMessage: null));
        }
        catch (TaskFailedException ex)
        {
            logger.LogError(ex, "[SFTP] Orchestration {id} — payment file upload failed after retries.", id);
            fileResults.Add(new FileResult(FileType.Payment, Succeeded: false, ErrorMessage: ex.Message));
        }

        logger.LogInformation("[SFTP] Orchestration {id} — uploading GL file to SFTP server...", id);
        try
        {
            await context.CallActivityAsync<string>(nameof(UploadFile),
                new UploadFileInput(glFileName, glContent), UploadRetryOptions);
            fileResults.Add(new FileResult(FileType.GeneralLedger, Succeeded: true, ErrorMessage: null));
        }
        catch (TaskFailedException ex)
        {
            logger.LogError(ex, "[SFTP] Orchestration {id} — GL file upload failed after retries.", id);
            fileResults.Add(new FileResult(FileType.GeneralLedger, Succeeded: false, ErrorMessage: ex.Message));
        }

        // Send callback to coordinator with per-file results.
        // Callback failure is isolated — files are already uploaded successfully.
        var result = new SftpBatchResult(request.BatchId, fileResults);
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
            ? $"Uploaded 2 files: {paymentFileName}, {glFileName}."
            : $"Partial failure: {string.Join(", ", fileResults.Where(f => !f.Succeeded).Select(f => f.FileType))}";
    }

    // --- Activity inputs ---

    public record CreatePaymentFileInput(string BatchId, List<PaymentData> Payments);
    public record CreateGLFileInput(string BatchId, List<PaymentData> Payments);
    public record UploadFileInput(string FileName, string Content);
    public record SendCallbackInput(string CallbackUrl, SftpBatchResult Result);

    // --- Activities ---

    /// <summary>Activity that builds a payment CSV from all batch payments.</summary>
    [Function(nameof(CreatePaymentFile))]
    public static string CreatePaymentFile([ActivityTrigger] CreatePaymentFileInput input, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(CreatePaymentFile));

        var sb = new StringBuilder();
        sb.AppendLine("PaymentId,PayorName,PayeeName,Amount,AccountNumber,RoutingNumber,PaymentDate");
        foreach (var payment in input.Payments)
        {
            sb.AppendLine($"{CsvEscape(payment.PaymentId)},{CsvEscape(payment.PayorName)},{CsvEscape(payment.PayeeName)},{payment.Amount.ToString("F2")},{CsvEscape(payment.AccountNumber)},{CsvEscape(payment.RoutingNumber)},{CsvEscape(payment.PaymentDate)}");
        }

        logger.LogInformation("[SFTP] Created payment CSV for batch {batchId} ({count} payments).", input.BatchId, input.Payments.Count);
        return sb.ToString();
    }

    /// <summary>Activity that builds a GL CSV from all batch payments (omits sensitive banking fields).</summary>
    [Function(nameof(CreateGLFile))]
    public static string CreateGLFile([ActivityTrigger] CreateGLFileInput input, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(CreateGLFile));

        var sb = new StringBuilder();
        sb.AppendLine("PaymentId,PayorName,PayeeName,Amount,PaymentDate");
        foreach (var payment in input.Payments)
        {
            sb.AppendLine($"{CsvEscape(payment.PaymentId)},{CsvEscape(payment.PayorName)},{CsvEscape(payment.PayeeName)},{payment.Amount.ToString("F2")},{CsvEscape(payment.PaymentDate)}");
        }

        logger.LogInformation("[SFTP] Created GL CSV for batch {batchId} ({count} payments).", input.BatchId, input.Payments.Count);
        return sb.ToString();
    }

    /// <summary>Activity that connects to SFTP asynchronously and uploads file content from memory.</summary>
    [Function(nameof(UploadFile))]
    public async Task<string> UploadFile([ActivityTrigger] UploadFileInput input, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(UploadFile));

        string remoteFilePath = $"{sftpClientFactory.RemotePath}/{input.FileName}";

        using var client = await sftpClientFactory.CreateConnectedClientAsync();
        logger.LogInformation("[SFTP] Connected to SFTP server.");

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input.Content));
        client.UploadFile(stream, remoteFilePath);
        logger.LogInformation("[SFTP] Uploaded {fileName} to {remote}.", input.FileName, remoteFilePath);

        return $"Uploaded {input.FileName} to {remoteFilePath}.";
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
        logger.LogInformation("[SFTP] Callback sent for batch {batchId} ({successCount}/{totalCount} files succeeded).",
            input.Result.BatchId, successCount, input.Result.Files.Count);
    }

    private static string CsvEscape(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
            return $"\"{field.Replace("\"", "\"\"")}\"";
        return field;
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
