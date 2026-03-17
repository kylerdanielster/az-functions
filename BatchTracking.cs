using Azure;
using Azure.Data.Tables;

namespace AzFunctions;

/// <summary>
/// Tracks batch, item, and file status in Azure Table Storage.
/// Used by the Coordinator (App 1) to manage batch lifecycle.
/// Item status is derived from per-file statuses.
/// </summary>
public interface IBatchTracker
{
    Task CreateBatchAsync(string batchId, int itemCount);
    Task CreateItemAsync(string batchId, string itemId);
    Task CreateFileAsync(string batchId, string itemId, string fileType);
    Task UpdateFileStatusAsync(string batchId, string itemId, string fileType, string status, string? errorMessage = null);
    Task UpdateItemFromFilesAsync(string batchId, string itemId);
    Task<bool> IsBatchCompleteAsync(string batchId);
    Task CompleteBatchAsync(string batchId);
    // Testing: query and cleanup methods used by test endpoints
    Task<TableEntity?> GetBatchAsync(string batchId);
    Task<List<TableEntity>> GetBatchItemsAsync(string batchId);
    Task<List<TableEntity>> GetBatchFilesAsync(string batchId);
    Task<int> ClearAllAsync();
}

/// <summary>
/// Azure Table Storage implementation of <see cref="IBatchTracker"/>.
/// Uses a single "BatchTracking" table with three entity levels:
/// batch (PK: "batch"), item (PK: batchId, RK: itemId),
/// and file (PK: batchId, RK: "{itemId}_{fileType}").
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
            ["FileCount"] = itemCount * 2,
            ["CreatedAt"] = DateTimeOffset.UtcNow
        };
        await tableClient.AddEntityAsync(entity);
    }

    public async Task CreateItemAsync(string batchId, string itemId)
    {
        var entity = new TableEntity(batchId, itemId)
        {
            ["Status"] = BatchStatus.Queued,
            ["CreatedAt"] = DateTimeOffset.UtcNow
        };
        await tableClient.AddEntityAsync(entity);
    }

    public async Task CreateFileAsync(string batchId, string itemId, string fileType)
    {
        var entity = new TableEntity(batchId, $"{itemId}_{fileType}")
        {
            ["ItemId"] = itemId,
            ["FileType"] = fileType,
            ["Status"] = BatchStatus.Queued,
            ["CreatedAt"] = DateTimeOffset.UtcNow
        };
        await tableClient.AddEntityAsync(entity);
    }

    public async Task UpdateFileStatusAsync(string batchId, string itemId, string fileType, string status, string? errorMessage = null)
    {
        string rowKey = $"{itemId}_{fileType}";
        TableEntity entity;
        try
        {
            var response = await tableClient.GetEntityAsync<TableEntity>(batchId, rowKey);
            entity = response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return; // File entity not found — idempotent
        }

        if (entity.GetString("Status") is BatchStatus.Completed or BatchStatus.Failed)
            return; // Already terminal — idempotent

        entity["Status"] = status;
        entity["CompletedAt"] = DateTimeOffset.UtcNow;
        if (errorMessage is not null)
            entity["ErrorMessage"] = errorMessage;

        await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
    }

    public async Task UpdateItemFromFilesAsync(string batchId, string itemId)
    {
        // Query file entities for this item (RowKey starts with "{itemId}_")
        var files = new List<TableEntity>();
        await foreach (var file in tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{batchId}' and ItemId eq '{itemId}'"))
        {
            if (file.RowKey?.Contains('_') == true)
                files.Add(file);
        }

        bool allCompleted = files.Count > 0 && files.All(f => f.GetString("Status") == BatchStatus.Completed);
        bool anyFailed = files.Any(f => f.GetString("Status") == BatchStatus.Failed);
        bool anyQueued = files.Any(f => f.GetString("Status") == BatchStatus.Queued);

        string status;
        if (allCompleted)
            status = BatchStatus.Completed;
        else if (anyFailed && !anyQueued)
            status = BatchStatus.Failed;
        else
            return; // Still processing — no update

        // Update the item entity
        TableEntity itemEntity;
        try
        {
            var response = await tableClient.GetEntityAsync<TableEntity>(batchId, itemId);
            itemEntity = response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return;
        }

        string? errorMessage = anyFailed
            ? string.Join("; ", files
                .Where(f => f.GetString("Status") == BatchStatus.Failed && f.GetString("ErrorMessage") is not null)
                .Select(f => $"{f.GetString("FileType")}: {f.GetString("ErrorMessage")}"))
            : null;

        itemEntity["Status"] = status;
        itemEntity["CompletedAt"] = DateTimeOffset.UtcNow;
        if (errorMessage is not null)
            itemEntity["ErrorMessage"] = errorMessage;

        await tableClient.UpdateEntityAsync(itemEntity, itemEntity.ETag, TableUpdateMode.Replace);
    }

    public async Task<bool> IsBatchCompleteAsync(string batchId)
    {
        var batchEntity = await tableClient.GetEntityAsync<TableEntity>("batch", batchId);
        int fileCount = batchEntity.Value.GetInt32("FileCount")
            ?? throw new InvalidOperationException($"Batch {batchId} missing FileCount.");

        int completedFileCount = 0;
        await foreach (var entity in tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{batchId}'"))
        {
            // File entities have RowKeys containing "_" (e.g., "item-000_person")
            if (entity.RowKey?.Contains('_') != true)
                continue;

            string status = entity.GetString("Status") ?? "";
            if (status is BatchStatus.Completed or BatchStatus.Failed)
                completedFileCount++;
        }

        return completedFileCount >= fileCount;
    }

    public async Task CompleteBatchAsync(string batchId)
    {
        var response = await tableClient.GetEntityAsync<TableEntity>("batch", batchId);
        var entity = response.Value;

        // Check if any files failed
        bool hasFailures = false;
        await foreach (var file in tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{batchId}'"))
        {
            if (file.RowKey?.Contains('_') == true && file.GetString("Status") == BatchStatus.Failed)
            {
                hasFailures = true;
                break;
            }
        }

        entity["Status"] = hasFailures ? BatchStatus.PartialFailure : BatchStatus.Completed;
        entity["CompletedAt"] = DateTimeOffset.UtcNow;
        await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
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

    public async Task<List<TableEntity>> GetBatchItemsAsync(string batchId)
    {
        var items = new List<TableEntity>();
        await foreach (var entity in tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{batchId}'"))
        {
            // Item entities have RowKeys without "_" (e.g., "item-000")
            if (!entity.RowKey?.Contains('_') ?? true)
                items.Add(entity);
        }
        return items;
    }

    public async Task<List<TableEntity>> GetBatchFilesAsync(string batchId)
    {
        var files = new List<TableEntity>();
        await foreach (var entity in tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{batchId}'"))
        {
            // File entities have RowKeys with "_" (e.g., "item-000_person")
            if (entity.RowKey?.Contains('_') == true)
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
