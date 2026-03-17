using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bogus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AzFunctions;

/// <summary>
/// Coordinator (App 1). Generates fake payment batches, submits items to the SFTP Processor,
/// and receives completion callbacks to track batch progress.
/// </summary>
public class SftpDataFeed(IHttpClientFactory httpClientFactory, IBatchTracker batchTracker)
{
    private const int BatchSize = 10;

    private static readonly Faker<PersonData> PersonFaker = new Faker<PersonData>()
        .CustomInstantiator(f => new PersonData(
            f.Name.FirstName(),
            f.Name.LastName(),
            f.Date.Past(50, DateTime.Now.AddYears(-18)).ToString("yyyy-MM-dd")));

    private static readonly Faker<AddressData> AddressFaker = new Faker<AddressData>()
        .CustomInstantiator(f => new AddressData(
            f.Address.StreetAddress(),
            f.Address.City(),
            f.Address.StateAbbr(),
            f.Address.ZipCode("#####")));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Daily timer trigger that generates a batch of fake payments and submits each item
    /// to the SFTP Processor for file creation and upload.
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
    // querying orchestration output by instance ID pattern (sftp-{batchId}-*).

    /// <summary>
    /// Callback webhook that receives per-file completion results from the SFTP Processor.
    /// Updates file statuses, derives item status, and detects when the entire batch is done.
    /// Batch completion is race-safe — only one concurrent callback will trigger the
    /// completion notification via <see cref="IBatchTracker.CompleteBatchAsync"/>.
    /// Route: POST /api/batch/callback
    /// </summary>
    [Function(nameof(BatchItemCompleted))]
    public async Task<HttpResponseData> BatchItemCompleted(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "batch/callback")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(BatchItemCompleted));

        var result = await req.ReadFromJsonAsync<SftpProcessResult>();
        if (result is null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid or missing callback data.");
            return badRequest;
        }

        // Update per-file statuses, then derive item status
        foreach (var file in result.Files)
        {
            string fileStatus = file.Succeeded ? BatchStatus.Completed : BatchStatus.Failed;
            await batchTracker.UpdateFileStatusAsync(result.BatchId, result.ItemId,
                file.FileType, fileStatus, file.ErrorMessage);
        }
        await batchTracker.UpdateItemFromFilesAsync(result.BatchId, result.ItemId);

        logger.LogInformation("[SFTP] Callback received — batch {batchId}, item {itemId}, files={fileCount}.",
            result.BatchId, result.ItemId, result.Files.Count);

        if (await batchTracker.IsBatchCompleteAsync(result.BatchId))
        {
            bool wasCompleted = await batchTracker.CompleteBatchAsync(result.BatchId);
            if (wasCompleted)
            {
                // TODO: Notify third party that batch payment processing is complete.
                // This should call the third party's API to mark their payment records as completed.
                logger.LogInformation("[SFTP] Batch {batchId} complete — would notify third party.", result.BatchId);
            }
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { result.BatchId, result.ItemId });
        return response;
    }

    /// <summary>
    /// Creates a batch with item and file entities in Table Storage, generates fake person/address pairs,
    /// and POSTs each item to the SFTP Processor. Returns the batch ID.
    /// Handles partial submission failure: if an individual item fails to submit, its files are
    /// marked as Failed and processing continues. If all items fail, the batch is completed immediately.
    /// </summary>
    private async Task<string> GenerateBatchAsync(ILogger logger)
    {
        string processorBaseUrl = Environment.GetEnvironmentVariable("PROCESSOR_BASE_URL")
            ?? throw new InvalidOperationException("PROCESSOR_BASE_URL not configured.");
        string coordinatorBaseUrl = Environment.GetEnvironmentVariable("COORDINATOR_BASE_URL")
            ?? throw new InvalidOperationException("COORDINATOR_BASE_URL not configured.");

        string batchId = Guid.NewGuid().ToString("N")[..8];
        string callbackUrl = $"{coordinatorBaseUrl}/api/batch/callback";

        logger.LogInformation("[SFTP] Data feed starting — batch {batchId} with {count} items.",
            batchId, BatchSize);

        await batchTracker.CreateBatchAsync(batchId, BatchSize);

        using var httpClient = httpClientFactory.CreateClient();
        int submittedCount = 0;

        for (int i = 0; i < BatchSize; i++)
        {
            string itemId = $"item-{i:D3}";
            var person = PersonFaker.Generate();
            var address = AddressFaker.Generate();

            var request = new SftpProcessRequest(batchId, itemId, person, address, callbackUrl);

            await batchTracker.CreateItemAsync(batchId, itemId);
            await batchTracker.CreateFileAsync(batchId, itemId, FileType.Person);
            await batchTracker.CreateFileAsync(batchId, itemId, FileType.Address);

            try
            {
                var response = await httpClient.PostAsJsonAsync(
                    $"{processorBaseUrl}/api/sftp/process", request, JsonOptions);
                response.EnsureSuccessStatusCode();
                submittedCount++;

                logger.LogInformation("[SFTP] Batch {batchId} — item {itemId} queued ({first} {last}).",
                    batchId, itemId, person.FirstName, person.LastName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[SFTP] Batch {batchId} — failed to submit item {itemId}.", batchId, itemId);

                await batchTracker.UpdateFileStatusAsync(batchId, itemId, FileType.Person, BatchStatus.Failed, "Submission failed");
                await batchTracker.UpdateFileStatusAsync(batchId, itemId, FileType.Address, BatchStatus.Failed, "Submission failed");
                await batchTracker.UpdateItemFromFilesAsync(batchId, itemId);
            }
        }

        if (submittedCount == 0)
        {
            await batchTracker.CompleteBatchAsync(batchId);
            logger.LogError("[SFTP] Batch {batchId} — all {count} items failed to submit.", batchId, BatchSize);
        }
        else
        {
            logger.LogInformation("[SFTP] Data feed batch {batchId} — {submitted}/{total} items submitted.",
                batchId, submittedCount, BatchSize);
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

    // Testing: returns batch + item statuses from Table Storage (used by E2E test script)
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

        var items = await batchTracker.GetBatchItemsAsync(batchId);
        var files = await batchTracker.GetBatchFilesAsync(batchId);

        var result = new BatchStatusResponse(
            BatchId: batchId,
            Status: batch.GetString("Status") ?? "Unknown",
            ItemCount: batch.GetInt32("ItemCount") ?? 0,
            CreatedAt: batch.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
            CompletedAt: batch.GetDateTimeOffset("CompletedAt"),
            Items: items
                .Select(i => new BatchItemStatus(
                    ItemId: i.RowKey ?? "",
                    Status: i.GetString("Status") ?? "Unknown",
                    CreatedAt: i.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
                    CompletedAt: i.GetDateTimeOffset("CompletedAt"),
                    ErrorMessage: i.GetString("ErrorMessage")))
                .OrderBy(i => i.ItemId)
                .ToList(),
            Files: files
                .Select(f => new BatchFileStatus(
                    ItemId: f.GetString("ItemId") ?? "",
                    FileType: f.GetString("FileType") ?? "",
                    Status: f.GetString("Status") ?? "Unknown",
                    CreatedAt: f.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
                    CompletedAt: f.GetDateTimeOffset("CompletedAt"),
                    ErrorMessage: f.GetString("ErrorMessage")))
                .OrderBy(f => f.ItemId).ThenBy(f => f.FileType)
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
