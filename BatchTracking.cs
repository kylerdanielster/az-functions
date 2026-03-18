using Azure;
using Azure.Data.Tables;

namespace AzFunctions;

/// <summary>
/// Tracks batch, file, and payment status in Azure Table Storage.
/// Used by the Coordinator (App 1) to manage batch lifecycle.
/// </summary>
public interface IBatchTracker
{
    /// <summary>Creates a batch entity. Idempotent — ignores 409 Conflict if entity already exists.</summary>
    Task CreateBatchAsync(string batchId, int paymentCount);

    /// <summary>Creates a file entity. Idempotent — ignores 409 Conflict if entity already exists.</summary>
    Task CreateFileAsync(string batchId, string fileType);

    /// <summary>Creates a payment entity. Idempotent — ignores 409 Conflict if entity already exists.</summary>
    Task CreatePaymentAsync(string batchId, string paymentId);

    /// <summary>
    /// Updates file entities from the callback results and sets the batch status.
    /// Also bulk-updates all payment entities to the derived batch status.
    /// Single callback per batch — no race handling needed.
    /// </summary>
    Task CompleteBatchFromResultsAsync(string batchId, List<FileResult> files);

    // Testing: query and cleanup methods used by test endpoints
    Task<TableEntity?> GetBatchAsync(string batchId);
    Task<List<TableEntity>> GetBatchFilesAsync(string batchId);
    Task<List<TableEntity>> GetBatchPaymentsAsync(string batchId);
    Task<int> ClearAllAsync();
}

/// <summary>
/// Azure Table Storage implementation of <see cref="IBatchTracker"/>.
/// Uses a single "BatchTracking" table with three entity levels:
/// batch (PK: "batch", RK: batchId), file (PK: batchId, RK: fileType, EntityType: "file"),
/// and payment (PK: batchId, RK: paymentId, EntityType: "payment").
/// Part of the Coordinator (App 1).
/// </summary>
public class TableBatchTracker(TableClient tableClient) : IBatchTracker
{
    public async Task CreateBatchAsync(string batchId, int paymentCount)
    {
        var entity = new TableEntity("batch", batchId)
        {
            ["Status"] = BatchStatus.Processing,
            ["PaymentCount"] = paymentCount,
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
            ["EntityType"] = "file",
            ["Status"] = BatchStatus.Processing,
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

    public async Task CreatePaymentAsync(string batchId, string paymentId)
    {
        var entity = new TableEntity(batchId, paymentId)
        {
            ["EntityType"] = "payment",
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

                entity["Status"] = file.Succeeded ? BatchStatus.Processed : BatchStatus.Failed;
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

        // Determine batch status from file results using pattern match
        bool paymentOk = files.Where(f => f.FileType == FileType.Payment).All(f => f.Succeeded);
        bool glOk = files.Where(f => f.FileType == FileType.GeneralLedger).All(f => f.Succeeded);

        string batchStatus = (paymentOk, glOk) switch
        {
            (true, true) => BatchStatus.Processed,
            (false, true) => BatchStatus.PaymentFileFailed,
            (true, false) => BatchStatus.GLFileFailed,
            (false, false) => BatchStatus.Failed
        };

        var batchResponse = await tableClient.GetEntityAsync<TableEntity>("batch", batchId);
        var batchEntity = batchResponse.Value;
        batchEntity["Status"] = batchStatus;
        batchEntity["CompletedAt"] = DateTimeOffset.UtcNow;
        await tableClient.UpdateEntityAsync(batchEntity, batchEntity.ETag, TableUpdateMode.Replace);

        // Bulk-update all payment entities to the derived batch status
        await foreach (var paymentEntity in tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{batchId}' and EntityType eq 'payment'"))
        {
            paymentEntity["Status"] = batchStatus;
            paymentEntity["CompletedAt"] = DateTimeOffset.UtcNow;
            await tableClient.UpdateEntityAsync(paymentEntity, paymentEntity.ETag, TableUpdateMode.Replace);
        }
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
            filter: $"PartitionKey eq '{batchId}' and EntityType eq 'file'"))
        {
            files.Add(entity);
        }
        return files;
    }

    public async Task<List<TableEntity>> GetBatchPaymentsAsync(string batchId)
    {
        var payments = new List<TableEntity>();
        await foreach (var entity in tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{batchId}' and EntityType eq 'payment'"))
        {
            payments.Add(entity);
        }
        return payments;
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
