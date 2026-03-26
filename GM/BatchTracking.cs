using Azure;
using Azure.Data.Tables;

namespace AzFunctions;

/// <summary>
/// Tracks batch and payment status in Azure Table Storage.
/// Used by the Coordinator (App 1) to manage batch lifecycle.
/// </summary>
public interface IBatchTracker
{
    /// <summary>Creates a batch entity with status Queued. Idempotent — ignores 409 Conflict.</summary>
    Task CreateBatchAsync(string batchId, int paymentCount);

    /// <summary>Creates a payment entity with all PaymentData fields. Idempotent — ignores 409 Conflict.</summary>
    Task CreatePaymentAsync(string batchId, PaymentData payment);

    /// <summary>
    /// Updates the batch entity status. When terminal (Processed or Error), sets CompletedAt
    /// and bulk-updates all payment entities to the same status. Idempotent: skips if already terminal.
    /// </summary>
    Task UpdateBatchStatusAsync(string batchId, string status);

    /// <summary>Returns payment entities with status Queued, mapped back to PaymentData records.</summary>
    Task<List<PaymentData>> GetQueuedPaymentsAsync(string batchId);

    // Testing: query and cleanup methods used by test endpoints
    Task<TableEntity?> GetBatchAsync(string batchId);
    Task<List<TableEntity>> GetBatchPaymentsAsync(string batchId);
    Task<int> ClearAllAsync();
}

/// <summary>
/// Azure Table Storage implementation of <see cref="IBatchTracker"/>.
/// Uses a single "BatchTracking" table with two entity levels:
/// batch (PK: "batch", RK: batchId) and payment (PK: batchId, RK: paymentId, EntityType: "payment").
/// Part of the Coordinator (App 1).
/// </summary>
public class TableBatchTracker(TableClient tableClient) : IBatchTracker
{
    public const string TableName = "BatchTracking";
    private const string BatchPartitionKey = "batch";

    public async Task CreateBatchAsync(string batchId, int paymentCount)
    {
        var entity = new TableEntity(BatchPartitionKey, batchId)
        {
            ["Status"] = BatchStatus.Queued,
            ["PaymentCount"] = paymentCount,
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

    public async Task CreatePaymentAsync(string batchId, PaymentData payment)
    {
        var entity = new TableEntity(batchId, payment.PaymentId)
        {
            ["EntityType"] = "payment",
            ["Status"] = BatchStatus.Queued,
            ["PayorName"] = payment.PayorName,
            ["PayeeName"] = payment.PayeeName,
            ["Amount"] = (double)payment.Amount,
            ["AccountNumber"] = payment.AccountNumber,
            ["RoutingNumber"] = payment.RoutingNumber,
            ["PaymentDate"] = payment.PaymentDate,
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

    public async Task UpdateBatchStatusAsync(string batchId, string status)
    {
        TableEntity batchEntity;
        try
        {
            var response = await tableClient.GetEntityAsync<TableEntity>(BatchPartitionKey, batchId);
            batchEntity = response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return;
        }

        // Skip if already terminal
        string currentStatus = batchEntity.GetString("Status") ?? "";
        if (currentStatus is BatchStatus.Processed or BatchStatus.Error)
            return;

        batchEntity["Status"] = status;

        bool isTerminal = status is BatchStatus.Processed or BatchStatus.Error;
        if (isTerminal)
            batchEntity["CompletedAt"] = DateTimeOffset.UtcNow;

        await tableClient.UpdateEntityAsync(batchEntity, batchEntity.ETag, TableUpdateMode.Replace);

        if (isTerminal)
        {
            var batch = new List<TableTransactionAction>();
            await foreach (var paymentEntity in tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{batchId}' and EntityType eq 'payment'"))
            {
                paymentEntity["Status"] = status;
                paymentEntity["CompletedAt"] = DateTimeOffset.UtcNow;
                batch.Add(new TableTransactionAction(TableTransactionActionType.UpdateReplace, paymentEntity, paymentEntity.ETag));
            }
            if (batch.Count > 0)
                await tableClient.SubmitTransactionAsync(batch);
        }
    }

    public async Task<List<PaymentData>> GetQueuedPaymentsAsync(string batchId)
    {
        var payments = new List<PaymentData>();
        await foreach (var entity in tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{batchId}' and EntityType eq 'payment' and Status eq '{BatchStatus.Queued}'"))
        {
            payments.Add(new PaymentData(
                PaymentId: entity.RowKey,
                PayorName: entity.GetString("PayorName") ?? "",
                PayeeName: entity.GetString("PayeeName") ?? "",
                Amount: (decimal)(entity.GetDouble("Amount") ?? 0),
                AccountNumber: entity.GetString("AccountNumber") ?? "",
                RoutingNumber: entity.GetString("RoutingNumber") ?? "",
                PaymentDate: entity.GetString("PaymentDate") ?? ""));
        }
        return payments;
    }

    // Testing: query and cleanup methods used by test endpoints

    public async Task<TableEntity?> GetBatchAsync(string batchId)
    {
        try
        {
            var response = await tableClient.GetEntityAsync<TableEntity>(BatchPartitionKey, batchId);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
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
