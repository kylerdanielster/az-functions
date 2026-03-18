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
/// and receives a single completion callback to track batch progress.
/// </summary>
public class SftpDataFeed(IHttpClientFactory httpClientFactory, IBatchTracker batchTracker)
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
        ILogger logger = executionContext.GetLogger(nameof(SftpDataFeed));

        try
        {
            await GenerateBatchAsync(logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SFTP] Data feed failed.");
        }
    }

    // TODO: Callback failure resilience
    // Problem: If SftpOrchestration.SendCallback fails after retries, the Coordinator never
    // receives the callback and the batch stays stuck in "Processing" forever.
    // Potential solutions:
    //   1. Reconciliation timer that periodically queries Durable Functions orchestration status
    //      for stuck batches and replays missed callbacks.
    //   2. Simple timeout that marks batches stuck in "Processing" beyond a threshold as "Failed".
    // Status: Research required — needs investigation into Durable Functions client API for
    // querying orchestration output by instance ID pattern (sftp-{batchId}).

    /// <summary>
    /// Callback webhook that receives per-file completion results from the SFTP Processor.
    /// Updates file statuses and batch status in a single operation.
    /// Route: POST /api/batch/callback
    /// </summary>
    [Function(nameof(BatchCompleted))]
    public async Task<HttpResponseData> BatchCompleted(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "batch/callback")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(BatchCompleted));

        var result = await req.ReadFromJsonAsync<SftpBatchResult>();
        if (result is null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid or missing callback data.");
            return badRequest;
        }

        await batchTracker.CompleteBatchFromResultsAsync(result.BatchId, result.Files);

        bool paymentOk = result.Files.Where(f => f.FileType == FileType.Payment).All(f => f.Succeeded);
        bool glOk = result.Files.Where(f => f.FileType == FileType.GeneralLedger).All(f => f.Succeeded);

        string status = (paymentOk, glOk) switch
        {
            (true, true) => BatchStatus.Processed,
            (false, true) => BatchStatus.PaymentFileFailed,
            (true, false) => BatchStatus.GLFileFailed,
            (false, false) => BatchStatus.Failed
        };

        logger.LogInformation("[SFTP] Batch {batchId} completed — status={status}, {successCount}/{totalCount} files succeeded.",
            result.BatchId, status, result.Files.Count(f => f.Succeeded), result.Files.Count);

        if (status == BatchStatus.Processed)
        {
            // TODO: Notify third party that batch payment processing is complete.
            // This should call the third party's API to mark their payment records as completed.
            logger.LogInformation("[SFTP] Batch {batchId} processed — would notify third party.", result.BatchId);
        }
        else
        {
            // TODO: Send alert email to users notifying them that one or both files failed.
            var failedFiles = result.Files.Where(f => !f.Succeeded).Select(f => f.FileType);
            logger.LogWarning("[SFTP] Batch {batchId} had file failures ({status}): {failedFiles} — alert email not yet implemented.",
                result.BatchId, status, string.Join(", ", failedFiles));
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { result.BatchId, Status = status });
        return response;
    }

    /// <summary>
    /// Creates a batch with file and payment entities in Table Storage, generates fake ACH payments,
    /// and POSTs the entire batch to the SFTP Processor. Returns the batch ID.
    /// On submission failure, both files are marked as Failed.
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
        await batchTracker.CreateFileAsync(batchId, FileType.Payment);
        await batchTracker.CreateFileAsync(batchId, FileType.GeneralLedger);

        var faker = new Faker();
        var payments = new List<PaymentData>();
        for (int i = 0; i < BatchSize; i++)
        {
            string paymentId = $"pmt-{i:D3}";
            payments.Add(new PaymentData(
                paymentId,
                faker.Name.FullName(),
                faker.Company.CompanyName(),
                Math.Round(faker.Random.Decimal(100m, 50000m), 2),
                faker.Finance.Account(10),
                faker.Finance.RoutingNumber(),
                faker.Date.Recent(30).ToString("yyyy-MM-dd")));
            await batchTracker.CreatePaymentAsync(batchId, paymentId);
        }

        var request = new SftpBatchRequest(batchId, payments, callbackUrl);

        using var httpClient = httpClientFactory.CreateClient();
        try
        {
            var response = await httpClient.PostAsJsonAsync(
                $"{processorBaseUrl}/api/sftp/process", request, JsonOptions);
            response.EnsureSuccessStatusCode();

            logger.LogInformation("[SFTP] Batch {batchId} submitted — {count} payments.",
                batchId, BatchSize);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SFTP] Batch {batchId} — failed to submit.", batchId);

            await batchTracker.CompleteBatchFromResultsAsync(batchId, [
                new FileResult(FileType.Payment, Succeeded: false, ErrorMessage: "Submission failed"),
                new FileResult(FileType.GeneralLedger, Succeeded: false, ErrorMessage: "Submission failed")
            ]);
        }

        return batchId;
    }

    // --- Testing / Verification Endpoints ---

    // Testing: HTTP trigger that returns batchId (used by E2E test script)
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

    // Testing: returns batch + file + payment statuses from Table Storage (used by E2E test script)
    [Function(nameof(GetBatchStatus))]
    public async Task<HttpResponseData> GetBatchStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "batch/{batchId}")] HttpRequestData req,
        string batchId,
        FunctionContext executionContext)
    {
        var batch = await batchTracker.GetBatchAsync(batchId);
        if (batch is null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync($"Batch not found: {batchId}");
            return notFound;
        }

        var files = await batchTracker.GetBatchFilesAsync(batchId);
        var paymentEntities = await batchTracker.GetBatchPaymentsAsync(batchId);

        var result = new BatchStatusResponse(
            BatchId: batchId,
            Status: batch.GetString("Status") ?? "Unknown",
            PaymentCount: batch.GetInt32("PaymentCount") ?? 0,
            CreatedAt: batch.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
            CompletedAt: batch.GetDateTimeOffset("CompletedAt"),
            Files: files
                .Select(f => new BatchFileStatus(
                    FileType: f.GetString("FileType") ?? "",
                    Status: f.GetString("Status") ?? "Unknown",
                    CreatedAt: f.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
                    CompletedAt: f.GetDateTimeOffset("CompletedAt"),
                    ErrorMessage: f.GetString("ErrorMessage")))
                .OrderBy(f => f.FileType)
                .ToList(),
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

    // Testing: clears all BatchTracking table data (used by E2E test script)
    [Function(nameof(ClearBatchData))]
    public async Task<HttpResponseData> ClearBatchData(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "batch")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(ClearBatchData));

        int count = await batchTracker.ClearAllAsync();
        logger.LogInformation("[SFTP] Cleared {count} entities from BatchTracking table.", count);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { deleted = count });
        return response;
    }
}
