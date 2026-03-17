using Azure;
using Azure.Data.Tables;

namespace Company.Function;

public interface IBatchTracker
{
    Task CreateBatchAsync(string batchId, int itemCount);
    Task CreateItemAsync(string batchId, string itemId);
    Task UpdateItemStatusAsync(string batchId, string itemId, string status, string? errorMessage = null);
    Task<bool> IsBatchCompleteAsync(string batchId);
    Task CompleteBatchAsync(string batchId);
    // Testing: query and cleanup methods used by test endpoints
    Task<TableEntity?> GetBatchAsync(string batchId);
    Task<List<TableEntity>> GetBatchItemsAsync(string batchId);
    Task<int> ClearAllAsync();
}

public class TableBatchTracker(TableClient tableClient) : IBatchTracker
{
    public async Task CreateBatchAsync(string batchId, int itemCount)
    {
        var entity = new TableEntity("batch", batchId)
        {
            ["Status"] = "Processing",
            ["ItemCount"] = itemCount,
            ["CreatedAt"] = DateTimeOffset.UtcNow
        };
        await tableClient.AddEntityAsync(entity);
    }

    public async Task CreateItemAsync(string batchId, string itemId)
    {
        var entity = new TableEntity(batchId, itemId)
        {
            ["Status"] = "Queued",
            ["CreatedAt"] = DateTimeOffset.UtcNow
        };
        await tableClient.AddEntityAsync(entity);
    }

    public async Task UpdateItemStatusAsync(string batchId, string itemId, string status, string? errorMessage = null)
    {
        TableEntity entity;
        try
        {
            var response = await tableClient.GetEntityAsync<TableEntity>(batchId, itemId);
            entity = response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return; // Item not found — idempotent
        }

        // Already completed — idempotent
        if (entity.GetString("Status") == "Completed")
            return;

        entity["Status"] = status;
        entity["CompletedAt"] = DateTimeOffset.UtcNow;
        if (errorMessage is not null)
            entity["ErrorMessage"] = errorMessage;

        await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
    }

    public async Task<bool> IsBatchCompleteAsync(string batchId)
    {
        var batchEntity = await tableClient.GetEntityAsync<TableEntity>("batch", batchId);
        int itemCount = batchEntity.Value.GetInt32("ItemCount")
            ?? throw new InvalidOperationException($"Batch {batchId} missing ItemCount.");

        int completedCount = 0;
        await foreach (var item in tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{batchId}'"))
        {
            string status = item.GetString("Status") ?? "";
            if (status is "Completed" or "Failed")
                completedCount++;
        }

        return completedCount >= itemCount;
    }

    public async Task CompleteBatchAsync(string batchId)
    {
        var response = await tableClient.GetEntityAsync<TableEntity>("batch", batchId);
        var entity = response.Value;

        // Check if any items failed
        bool hasFailures = false;
        await foreach (var item in tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{batchId}'"))
        {
            if (item.GetString("Status") == "Failed")
            {
                hasFailures = true;
                break;
            }
        }

        entity["Status"] = hasFailures ? "PartialFailure" : "Completed";
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
        await foreach (var item in tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{batchId}'"))
        {
            items.Add(item);
        }
        return items;
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
