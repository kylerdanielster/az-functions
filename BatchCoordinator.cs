using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bogus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AzFunctions;

/// <summary>
/// Coordinator (App 1). Generates fake ACH payment batches, submits the entire batch to the SFTP Processor,
/// and receives status callbacks to track batch progress.
/// </summary>
public class BatchCoordinator(IHttpClientFactory httpClientFactory, IBatchTracker batchTracker)
{
    private const int BatchSize = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Daily timer trigger that generates a batch of fake payments and submits the entire batch
    /// to the SFTP Processor for CSV file creation and upload.
    /// </summary>
    [Function(nameof(RunDataFeed))]
    public async Task RunDataFeed(
        [TimerTrigger("0 0 0 * * *")] TimerInfo timerInfo,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(BatchCoordinator));

        try
        {
            await GenerateBatchAsync(logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SFTP] Data feed failed.");
        }
    }

    // TODO: Callback failure resilience (Processed/Error callbacks)
    // Problem: If SftpOrchestration.SendCallback fails after retries for the Processed or Error
    // callback, the batch stays stuck in "Processing" forever.
    // Note: The Processing transition is handled locally (no callback dependency), so only
    // terminal-state callbacks can cause stuck batches.
    // Potential solutions:
    //   1. Reconciliation timer that periodically queries Durable Functions orchestration status
    //      for stuck batches and replays missed callbacks.
    //   2. Simple timeout that marks batches stuck in "Processing" beyond a threshold as "Error".

    /// <summary>
    /// Callback webhook that receives batch status updates from the SFTP Processor.
    /// Updates batch status in Table Storage.
    /// Route: POST /api/batch/callback
    /// </summary>
    [Function(nameof(BatchCompleted))]
    public async Task<HttpResponseData> BatchCompleted(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "batch/callback")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(BatchCompleted));

        var callback = await req.ReadFromJsonAsync<BatchCallback>();
        if (callback is null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid or missing callback data.");
            return badRequest;
        }

        await batchTracker.UpdateBatchStatusAsync(callback.BatchId, callback.Status);

        logger.LogInformation("[SFTP] Batch {batchId} callback — status={status}.",
            callback.BatchId, callback.Status);

        if (callback.Status == BatchStatus.Processed)
        {
            // TODO: Notify third party that batch payment processing is complete.
            logger.LogInformation("[SFTP] Batch {batchId} processed — would notify third party.", callback.BatchId);
        }
        else if (callback.Status == BatchStatus.Error)
        {
            // TODO: Send alert email to users notifying them of the failure.
            logger.LogWarning("[SFTP] Batch {batchId} error — alert email not yet implemented.", callback.BatchId);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { callback.BatchId, callback.Status });
        return response;
    }

    /// <summary>
    /// Creates a batch with payment entities in Table Storage, generates fake ACH payments,
    /// queries back only Queued payments, and POSTs the batch to the SFTP Processor.
    /// On submission failure, marks the batch as Error.
    /// </summary>
    private async Task<string> GenerateBatchAsync(ILogger logger)
    {
        string processorBaseUrl = Environment.GetEnvironmentVariable("PROCESSOR_BASE_URL")
            ?? throw new InvalidOperationException("PROCESSOR_BASE_URL not configured.");
        string coordinatorBaseUrl = Environment.GetEnvironmentVariable("COORDINATOR_BASE_URL")
            ?? throw new InvalidOperationException("COORDINATOR_BASE_URL not configured.");

        string batchId = Guid.NewGuid().ToString("N")[..8];
        string callbackUrl = $"{coordinatorBaseUrl}/api/batch/callback";

        logger.LogInformation("[SFTP] Data feed starting — batch {batchId} with {count} payments.",
            batchId, BatchSize);

        await batchTracker.CreateBatchAsync(batchId, BatchSize);

        var faker = new Faker();
        for (int i = 0; i < BatchSize; i++)
        {
            var payment = new PaymentData(
                $"pmt-{i:D3}",
                faker.Name.FullName(),
                faker.Company.CompanyName(),
                Math.Round(faker.Random.Decimal(100m, 50000m), 2),
                faker.Finance.Account(10),
                faker.Finance.RoutingNumber(),
                faker.Date.Recent(30).ToString("yyyy-MM-dd"));
            await batchTracker.CreatePaymentAsync(batchId, payment);
        }

        var queuedPayments = await batchTracker.GetQueuedPaymentsAsync(batchId);
        var request = new BatchRequest(batchId, queuedPayments, callbackUrl);

        using var httpClient = httpClientFactory.CreateClient();
        try
        {
            var response = await httpClient.PostAsJsonAsync(
                $"{processorBaseUrl}/api/sftp/process", request, JsonOptions);
            response.EnsureSuccessStatusCode();

            await batchTracker.UpdateBatchStatusAsync(batchId, BatchStatus.Processing);

            logger.LogInformation("[SFTP] Batch {batchId} submitted — {count} payments.",
                batchId, queuedPayments.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SFTP] Batch {batchId} — failed to submit.", batchId);
            await batchTracker.UpdateBatchStatusAsync(batchId, BatchStatus.Error);
        }

        return batchId;
    }

    // --- TEST/DEBUG ENDPOINTS ---

    // TEST/DEBUG ENDPOINT — HTTP trigger that returns batchId (used by E2E test script)
    [Function(nameof(TriggerDataFeed))]
    public async Task<HttpResponseData> TriggerDataFeed(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "datafeed/trigger")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(TriggerDataFeed));

        string batchId = await GenerateBatchAsync(logger);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { batchId });
        return response;
    }

    // TEST/DEBUG ENDPOINT — returns batch + payment statuses from Table Storage (used by E2E test script)
    [Function(nameof(GetBatchStatus))]
    public async Task<HttpResponseData> GetBatchStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "batch/{batchId}")] HttpRequestData req,
        string batchId,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(GetBatchStatus));

        try
        {
            var batch = await batchTracker.GetBatchAsync(batchId);
            if (batch is null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync($"Batch not found: {batchId}");
                return notFound;
            }

            var paymentEntities = await batchTracker.GetBatchPaymentsAsync(batchId);

            var result = new BatchStatusResponse(
                BatchId: batchId,
                Status: batch.GetString("Status") ?? "Unknown",
                PaymentCount: batch.GetInt32("PaymentCount") ?? 0,
                CreatedAt: batch.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
                CompletedAt: batch.GetDateTimeOffset("CompletedAt"),
                Payments: paymentEntities
                    .Select(p => new PaymentStatus(
                        PaymentId: p.RowKey,
                        Status: p.GetString("Status") ?? "Unknown",
                        CreatedAt: p.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue))
                    .OrderBy(p => p.PaymentId)
                    .ToList());

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(result);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SFTP] Failed to get batch status for {batchId}.", batchId);
            var error = req.CreateResponse(HttpStatusCode.InternalServerError);
            await error.WriteStringAsync("Failed to retrieve batch status.");
            return error;
        }
    }

    // TEST/DEBUG ENDPOINT — clears all BatchTracking table data (used by E2E test script)
    [Function(nameof(ClearBatchData))]
    public async Task<HttpResponseData> ClearBatchData(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "batch")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(ClearBatchData));

        try
        {
            int count = await batchTracker.ClearAllAsync();
            logger.LogInformation("[SFTP] Cleared {count} entities from BatchTracking table.", count);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { deleted = count });
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SFTP] Failed to clear batch data.");
            var error = req.CreateResponse(HttpStatusCode.InternalServerError);
            await error.WriteStringAsync("Failed to clear batch data.");
            return error;
        }
    }
}
