using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace AzFunctions;

/// <summary>
/// SFTP Processor (App 2) entry points. Validates and accepts processing requests over HTTP,
/// queues them via <see cref="IMessageQueue"/> for reliable delivery, and starts Durable Functions orchestrations.
/// </summary>
public class SftpProcessor(IMessageQueue messageQueue)
{
    public const string QueueName = "sftp-processing-queue";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Accepts an SFTP processing request, validates required fields, drops it onto a
    /// Storage Queue, and returns 202 Accepted. Returns 400 if the body is null or
    /// if BatchId, ItemId, CallbackUrl, Person, or Address are missing.
    /// Route: POST /api/sftp/process
    /// </summary>
    [Function(nameof(ReceiveSftpRequest))]
    public async Task<HttpResponseData> ReceiveSftpRequest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sftp/process")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(ReceiveSftpRequest));

        var request = await req.ReadFromJsonAsync<SftpProcessRequest>();
        if (request is null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid or missing request data.");
            return badRequest;
        }

        if (string.IsNullOrWhiteSpace(request.BatchId) ||
            string.IsNullOrWhiteSpace(request.ItemId) ||
            string.IsNullOrWhiteSpace(request.CallbackUrl) ||
            request.Person is null ||
            request.Address is null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Missing required fields: BatchId, ItemId, CallbackUrl, Person, and Address are required.");
            return badRequest;
        }

        string message = JsonSerializer.Serialize(request, JsonOptions);
        await messageQueue.SendMessageAsync(message);

        logger.LogInformation("[SFTP] Queued processing request for batch {batchId}, item {itemId}.",
            request.BatchId, request.ItemId);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new { request.BatchId, request.ItemId, status = BatchStatus.Queued });
        return response;
    }

    /// <summary>
    /// Queue trigger that deserializes a processing request and starts an SftpOrchestration
    /// with a deterministic instance ID (sftp-{batchId}-{itemId}) to prevent duplicates.
    /// </summary>
    [Function(nameof(ProcessSftpQueue))]
    public static async Task ProcessSftpQueue(
        [QueueTrigger(QueueName)] string messageText,
        [DurableClient] DurableTaskClient durableClient,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(ProcessSftpQueue));

        var request = JsonSerializer.Deserialize<SftpProcessRequest>(messageText, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize queue message.");

        string instanceId = $"sftp-{request.BatchId}-{request.ItemId}";

        logger.LogInformation("[SFTP] Starting orchestration {instanceId} for batch {batchId}, item {itemId}.",
            instanceId, request.BatchId, request.ItemId);

        await durableClient.ScheduleNewOrchestrationInstanceAsync(
            nameof(SftpOrchestration),
            request,
            new StartOrchestrationOptions { InstanceId = instanceId });
    }
}
