using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace AzFunctions;

/// <summary>
/// SFTP Processor (App 2) orchestration and activities. Processes files sequentially:
/// payment file first, then GL file only if payment succeeds. Sends callbacks at each stage.
/// GL failures are queued to the GL error queue for manual retry — no callback is sent,
/// so App 1 stays in Processing until the GL is retried and succeeds.
/// </summary>
public class SftpOrchestration(IHttpClientFactory httpClientFactory, ISftpClientFactory sftpClientFactory, IGLErrorQueue glErrorQueue)
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
    /// Durable Functions orchestrator. Processes files sequentially: payment file first, then GL.
    /// If payment fails → callback Error, return early (no GL attempt).
    /// If GL succeeds → callback Processed.
    /// If GL fails → queue to GL error queue, no callback (App 1 stays in Processing).
    /// Note: Processing status is set by App 1 after successful submission — no callback needed.
    /// </summary>
    [Function(nameof(SftpOrchestration))]
    public static async Task<string> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ILogger logger = context.CreateReplaySafeLogger(nameof(SftpOrchestration));
        string id = context.InstanceId;

        var request = context.GetInput<BatchRequest>()
            ?? throw new InvalidOperationException("Orchestration input is missing.");

        logger.LogInformation("[SFTP] Orchestration {id} started — batch {batchId}, {paymentCount} payments.",
            id, request.BatchId, request.Payments.Count);

        // Step 1: Create and upload payment file
        logger.LogInformation("[SFTP] Orchestration {id} — creating payment CSV...", id);
        string paymentContent = await context.CallActivityAsync<string>(nameof(CreatePaymentFile),
            new CreatePaymentFileInput(request.BatchId, request.Payments));

        string paymentFileName = $"payment_{request.BatchId}.csv";

        logger.LogInformation("[SFTP] Orchestration {id} — uploading payment file to SFTP server...", id);
        try
        {
            await context.CallActivityAsync<string>(nameof(UploadFile),
                new UploadFileInput(paymentFileName, paymentContent), UploadRetryOptions);
        }
        catch (TaskFailedException ex)
        {
            logger.LogError(ex, "[SFTP] Orchestration {id} — payment file upload failed after retries.", id);

            try
            {
                await context.CallActivityAsync(nameof(SendCallback),
                    new SendCallbackInput(request.CallbackUrl, new BatchCallback(request.BatchId, BatchStatus.Error)),
                    CallbackRetryOptions);
            }
            catch (TaskFailedException cbEx)
            {
                logger.LogError(cbEx, "[SFTP] Orchestration {id} — Error callback failed after retries.", id);
            }

            return $"Payment file upload failed: {ex.Message}";
        }

        // Step 2: Create and upload GL file
        logger.LogInformation("[SFTP] Orchestration {id} — creating GL CSV...", id);
        string glContent = await context.CallActivityAsync<string>(nameof(CreateGLFile),
            new CreateGLFileInput(request.BatchId, request.Payments));

        string glFileName = $"gl_{request.BatchId}.csv";

        logger.LogInformation("[SFTP] Orchestration {id} — uploading GL file to SFTP server...", id);
        try
        {
            await context.CallActivityAsync<string>(nameof(UploadFile),
                new UploadFileInput(glFileName, glContent), UploadRetryOptions);
        }
        catch (TaskFailedException ex)
        {
            logger.LogError(ex, "[SFTP] Orchestration {id} — GL file upload failed after retries.", id);

            // Queue for manual retry — no callback, App 1 stays in Processing
            try
            {
                await context.CallActivityAsync(nameof(SendToGLErrorQueue),
                    new GLErrorMessage(request.BatchId, request.Payments, request.CallbackUrl, ex.Message));
            }
            catch (TaskFailedException queueEx)
            {
                logger.LogError(queueEx, "[SFTP] Orchestration {id} — failed to queue GL error message.", id);
            }

            return $"Payment file uploaded. GL file upload failed: {ex.Message}";
        }

        // Step 3: GL succeeded — send Processed callback
        logger.LogInformation("[SFTP] Orchestration {id} — GL file uploaded, sending Processed callback...", id);
        try
        {
            await context.CallActivityAsync(nameof(SendCallback),
                new SendCallbackInput(request.CallbackUrl, new BatchCallback(request.BatchId, BatchStatus.Processed)),
                CallbackRetryOptions);
        }
        catch (TaskFailedException ex)
        {
            logger.LogError(ex, "[SFTP] Orchestration {id} — Processed callback failed after retries.", id);
        }

        logger.LogInformation("[SFTP] Orchestration {id} — complete.", id);
        return $"Uploaded 2 files: {paymentFileName}, {glFileName}.";
    }

    // --- Activity inputs ---

    public record CreatePaymentFileInput(string BatchId, List<PaymentData> Payments);
    public record CreateGLFileInput(string BatchId, List<PaymentData> Payments);
    public record UploadFileInput(string FileName, string Content);
    public record SendCallbackInput(string CallbackUrl, BatchCallback Callback);
    public record GLErrorMessage(string BatchId, List<PaymentData> Payments, string CallbackUrl, string ErrorMessage);

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

    /// <summary>Activity that POSTs the batch status callback to the Coordinator.</summary>
    [Function(nameof(SendCallback))]
    public async Task SendCallback([ActivityTrigger] SendCallbackInput input, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(SendCallback));

        using var httpClient = httpClientFactory.CreateClient();
        var response = await httpClient.PostAsJsonAsync(input.CallbackUrl, input.Callback, JsonOptions);
        response.EnsureSuccessStatusCode();

        logger.LogInformation("[SFTP] Callback sent for batch {batchId} — status={status}.",
            input.Callback.BatchId, input.Callback.Status);
    }

    /// <summary>Activity that sends a failed GL upload message to the GL error queue for manual retry.</summary>
    [Function(nameof(SendToGLErrorQueue))]
    public async Task SendToGLErrorQueue([ActivityTrigger] GLErrorMessage input, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(SendToGLErrorQueue));

        string message = JsonSerializer.Serialize(input, JsonOptions);
        await glErrorQueue.SendMessageAsync(message);

        logger.LogWarning("[SFTP] GL error queued for batch {batchId}: {error}",
            input.BatchId, input.ErrorMessage);
    }

    /// <summary>
    /// Escapes a field for CSV output. Wraps in double quotes if the field contains
    /// commas, double quotes, or newlines. Existing double quotes are doubled per RFC 4180.
    /// </summary>
    internal static string CsvEscape(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
            return $"\"{field.Replace("\"", "\"\"")}\"";
        return field;
    }

}
