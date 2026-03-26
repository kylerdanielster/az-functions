namespace AzFunctions;

/// <summary>A single ACH payment within a batch.</summary>
public record PaymentData(string PaymentId, string PayorName, string PayeeName,
    decimal Amount, string AccountNumber, string RoutingNumber, string PaymentDate);

/// <summary>
/// Request payload sent from the Coordinator (App 1) to the SFTP Processor (App 2).
/// Contains all payments and the callback URL for completion notification.
/// </summary>
public record BatchRequest(string BatchId, List<PaymentData> Payments, string CallbackUrl);

/// <summary>
/// Callback payload sent from the SFTP Processor (App 2) back to the Coordinator (App 1)
/// with the current batch status.
/// </summary>
public record BatchCallback(string BatchId, string Status);

/// <summary>String constants for batch status values stored in Table Storage.</summary>
public static class BatchStatus
{
    public const string Queued = "Queued";
    public const string Processing = "Processing";
    public const string Processed = "Processed";
    public const string Error = "Error";
}

/// <summary>Batch status response including payment-level details.</summary>
public record BatchStatusResponse(string BatchId, string Status, int PaymentCount,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, List<PaymentStatus> Payments);

/// <summary>Payment-level status within a batch.</summary>
public record PaymentStatus(string PaymentId, string Status, DateTimeOffset CreatedAt);
