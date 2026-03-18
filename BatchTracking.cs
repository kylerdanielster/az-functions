using Azure;
using Azure.Data.Tables;

namespace AzFunctions;

/// <summary>
/// Tracks batch and file status in Azure Table Storage.
/// Used by the Coordinator (App 1) to manage batch lifecycle.
/// </summary>
public interface IBatchTracker
{
    /// <summary>Creates a batch entity. Idempotent — ignores 409 Conflict if entity already exists.</summary>
    Task CreateBatchAsync(string batchId, int itemCount);

    /// <summary>Creates a file entity. Idempotent — ignores 409 Conflict if entity already exists.</summary>
    Task CreateFileAsync(string batchId, string fileType);

    /// <summary>
    /// Updates file entities from the callback results and sets the batch status.
    /// Single callback per batch — no race handling needed.
    /// </summary>
    Task CompleteBatchFromResultsAsync(string batchId, List<FileResult> files);

    // Testing: query and cleanup methods used by test endpoints
    Task<TableEntity?> GetBatchAsync(string batchId);
    Task<List<TableEntity>> GetBatchFilesAsync(string batchId);
    Task<int> ClearAllAsync();
}

/// <summary>
/// Azure Table Storage implementation of <see cref="IBatchTracker"/>.
/// Uses a single "BatchTracking" table with two entity levels:
/// batch (PK: "batch", RK: batchId) and file (PK: batchId, RK: fileType).
/// Part of the Coordinator (App 1).
/// </summary>
public class TableBatchTracker(TableClient tableClient) : IBatchTracker
{
    public async Task CreateBatchAsync(string batchId, int itemCount)
    {
        var entity = new TableEntity("batch", batchId)
        {
            ["Status"] = BatchStatus.Processing,
            ["ItemCount"] = itemCount,
            ["FileCount"] = 2,
            ["CreatedAt"] = DateTimeOffset.UtcNow
        };
        try
        {
            await tableClient.AddEntityAsync(entity);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Entity already exists — idempotent, no action needed
        }
    }

    public async Task CreateFileAsync(string batchId, string fileType)
    {
        var entity = new TableEntity(batchId, fileType)
        {
            ["FileType"] = fileType,
            ["Status"] = BatchStatus.Queued,
            ["CreatedAt"] = DateTimeOffset.UtcNow
        };
        try
        {
            await tableClient.AddEntityAsync(entity);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Entity already exists — idempotent, no action needed
        }
    }

    public async Task CompleteBatchFromResultsAsync(string batchId, List<FileResult> files)
    {
        foreach (var file in files)
        {
            try
            {
                var response = await tableClient.GetEntityAsync<TableEntity>(batchId, file.FileType);
                var entity = response.Value;

                entity["Status"] = file.Succeeded ? BatchStatus.Completed : BatchStatus.Failed;
                entity["CompletedAt"] = DateTimeOffset.UtcNow;
                if (file.ErrorMessage is not null)
                    entity["ErrorMessage"] = file.ErrorMessage;

                await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // File entity not found — skip
            }
        }

        // Determine batch status from file results
        bool allSucceeded = files.All(f => f.Succeeded);
        bool allFailed = files.All(f => !f.Succeeded);
        string batchStatus = allSucceeded ? BatchStatus.Completed
            : allFailed ? BatchStatus.Failed
            : BatchStatus.PartialFailure;

        var batchResponse = await tableClient.GetEntityAsync<TableEntity>("batch", batchId);
        var batchEntity = batchResponse.Value;
        batchEntity["Status"] = batchStatus;
        batchEntity["CompletedAt"] = DateTimeOffset.UtcNow;
        await tableClient.UpdateEntityAsync(batchEntity, batchEntity.ETag, TableUpdateMode.Replace);
    }

    // Testing: query and cleanup methods used by test endpoints

    public async Task<TableEntity?> GetBatchAsync(string batchId)
    {
        try
        {
            var response = await tableClient.GetEntityAsync<TableEntity>("batch", batchId);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<List<TableEntity>> GetBatchFilesAsync(string batchId)
    {
        var files = new List<TableEntity>();
        await foreach (var entity in tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{batchId}'"))
        {
            files.Add(entity);
        }
        return files;
    }

    public async Task<int> ClearAllAsync()
    {
        int count = 0;
        await foreach (var entity in tableClient.QueryAsync<TableEntity>())
        {
            await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag);
            count++;
        }
        return count;
    }
}
