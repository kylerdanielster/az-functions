using System.Net;
using Azure.Storage.Queues;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace Company.Function;

public static class SftpProcessor
{
    public const string QueueName = "sftp-processing-queue";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Function(nameof(ReceiveSftpRequest))]
    public static async Task<HttpResponseData> ReceiveSftpRequest(
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

        string connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
            ?? throw new InvalidOperationException("AzureWebJobsStorage not configured.");

        var queueClient = new QueueClient(connectionString, QueueName, new QueueClientOptions
        {
            MessageEncoding = QueueMessageEncoding.Base64
        });
        await queueClient.CreateIfNotExistsAsync();

        string message = JsonSerializer.Serialize(request, JsonOptions);
        await queueClient.SendMessageAsync(message);

        logger.LogInformation("[SFTP] Queued processing request for batch {batchId}, item {itemId}.",
            request.BatchId, request.ItemId);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new { request.BatchId, request.ItemId, status = "Queued" });
        return response;
    }

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
