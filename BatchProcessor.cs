using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace AzFunctions;

/// <summary>
/// Batch Processor (App 2) entry points. Validates and accepts batch processing requests over HTTP,
/// queues them via <see cref="IMessageQueue"/> for reliable delivery, and starts Durable Functions orchestrations.
/// Also processes GL error queue messages for failed GL uploads.
/// </summary>
public class BatchProcessor(IMessageQueue messageQueue)
{
    public const string QueueName = "batch-processing-queue";
    public const string GLErrorQueueName = "gl-error-queue";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Accepts a batch processing request, validates required fields, drops it onto a
    /// Storage Queue, and returns 202 Accepted. Returns 400 if the body is null or
    /// if BatchId, CallbackUrl, or Payments are missing/empty.
    /// Route: POST /api/batch/process
    /// </summary>
    [Function(nameof(ReceiveBatchRequest))]
    public async Task<HttpResponseData> ReceiveBatchRequest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "batch/process")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(ReceiveBatchRequest));

        var request = await req.ReadFromJsonAsync<BatchRequest>();
        if (request is null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid or missing request data.");
            return badRequest;
        }

        if (string.IsNullOrWhiteSpace(request.BatchId) ||
            string.IsNullOrWhiteSpace(request.CallbackUrl) ||
            request.Payments is null ||
            request.Payments.Count == 0)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Missing required fields: BatchId, CallbackUrl, and Payments (non-empty) are required.");
            return badRequest;
        }

        string message = JsonSerializer.Serialize(request, JsonOptions);
        await messageQueue.SendMessageAsync(message);

        logger.LogInformation("[Batch] Queued batch processing request for batch {batchId} ({paymentCount} payments).",
            request.BatchId, request.Payments.Count);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new { request.BatchId, PaymentCount = request.Payments.Count, status = BatchStatus.Queued });
        return response;
    }

    /// <summary>
    /// Queue trigger that deserializes a batch processing request and starts a BatchOrchestration
    /// with a deterministic instance ID (batch-{batchId}) to prevent duplicates.
    /// </summary>
    [Function(nameof(ProcessBatchQueue))]
    public static async Task ProcessBatchQueue(
        [QueueTrigger(QueueName)] string messageText,
        [DurableClient] DurableTaskClient durableClient,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(ProcessBatchQueue));

        var request = JsonSerializer.Deserialize<BatchRequest>(messageText, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize queue message.");

        string instanceId = $"batch-{request.BatchId}";

        logger.LogInformation("[Batch] Starting orchestration {instanceId} for batch {batchId} ({paymentCount} payments).",
            instanceId, request.BatchId, request.Payments.Count);

        await durableClient.ScheduleNewOrchestrationInstanceAsync(
            nameof(BatchOrchestration),
            request,
            new StartOrchestrationOptions { InstanceId = instanceId });
    }

    /// <summary>
    /// Queue trigger that processes failed GL upload messages from the GL error queue.
    /// Logs the failure for visibility. Future: implement manual retry endpoint.
    /// </summary>
    [Function(nameof(ProcessGLErrorQueue))]
    public static void ProcessGLErrorQueue(
        [QueueTrigger(GLErrorQueueName)] string messageText,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(ProcessGLErrorQueue));

        var errorMessage = JsonSerializer.Deserialize<BatchOrchestration.GLErrorMessage>(messageText, JsonOptions);
        if (errorMessage is null)
        {
            logger.LogError("[Batch] Failed to deserialize GL error queue message.");
            return;
        }

        logger.LogWarning("[Batch] GL file upload failed for batch {batchId}: {error}",
            errorMessage.BatchId, errorMessage.ErrorMessage);

        // TODO: Send error email notification
        // TODO: Implement manual retry endpoint
    }
}
