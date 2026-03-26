using Azure;
using Azure.Data.Tables;

namespace AzFunctions;

/// <summary>
/// Stores batch payment data in Azure Table Storage for the Batch Processor (App 2).
/// Used by <see cref="BatchProcessor"/> to persist incoming payment data before queuing,
/// and by <see cref="BatchOrchestration"/> activities to read payment data for file generation.
/// </summary>
public interface IBatchPaymentStore
{
    /// <summary>
    /// Stores batch metadata and all payment entities. Idempotent — ignores 409 Conflict
    /// on the metadata entity and uses batch transactions for payments.
    /// </summary>
    Task StoreBatchAsync(string batchId, string callbackUrl, List<PaymentData> payments);

    /// <summary>Returns all payment entities for a batch, mapped back to PaymentData records.</summary>
    Task<List<PaymentData>> GetPaymentsAsync(string batchId);

    /// <summary>Returns the callback URL from the batch metadata entity.</summary>
    Task<string> GetCallbackUrlAsync(string batchId);
}

/// <summary>
/// Azure Table Storage implementation of <see cref="IBatchPaymentStore"/>.
/// Uses a "BatchPayments" table with two entity levels:
/// metadata (PK: "metadata", RK: batchId) and payment (PK: batchId, RK: paymentId, EntityType: "payment").
/// Part of the Batch Processor (App 2).
/// </summary>
public class TableBatchPaymentStore(TableClient tableClient) : IBatchPaymentStore
{
    public const string TableName = "BatchPayments";
    private const string MetadataPartitionKey = "metadata";

    public async Task StoreBatchAsync(string batchId, string callbackUrl, List<PaymentData> payments)
    {
        var metadataEntity = new TableEntity(MetadataPartitionKey, batchId)
        {
            ["CallbackUrl"] = callbackUrl,
            ["PaymentCount"] = payments.Count,
            ["CreatedAt"] = DateTimeOffset.UtcNow
        };
        try
        {
            await tableClient.AddEntityAsync(metadataEntity);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Entity already exists — idempotent, no action needed
        }

        // Write payment entities in chunks of 100 (table transaction limit)
        var paymentEntities = payments.Select(payment => new TableEntity(batchId, payment.PaymentId)
        {
            ["EntityType"] = "payment",
            ["PayorName"] = payment.PayorName,
            ["PayeeName"] = payment.PayeeName,
            ["Amount"] = (double)payment.Amount,
            ["AccountNumber"] = payment.AccountNumber,
            ["RoutingNumber"] = payment.RoutingNumber,
            ["PaymentDate"] = payment.PaymentDate,
            ["CreatedAt"] = DateTimeOffset.UtcNow
        }).ToList();

        foreach (var chunk in paymentEntities.Chunk(100))
        {
            var actions = chunk.Select(e =>
                new TableTransactionAction(TableTransactionActionType.UpsertReplace, e)).ToList();
            await tableClient.SubmitTransactionAsync(actions);
        }
    }

    public async Task<List<PaymentData>> GetPaymentsAsync(string batchId)
    {
        var payments = new List<PaymentData>();
        await foreach (var entity in tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{batchId}' and EntityType eq 'payment'"))
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

    public async Task<string> GetCallbackUrlAsync(string batchId)
    {
        var response = await tableClient.GetEntityAsync<TableEntity>(MetadataPartitionKey, batchId);
        return response.Value.GetString("CallbackUrl")
            ?? throw new InvalidOperationException($"CallbackUrl not found for batch {batchId}.");
    }
}
