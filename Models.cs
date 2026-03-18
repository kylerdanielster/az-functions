namespace AzFunctions;

/// <summary>A single ACH payment within a batch.</summary>
public record PaymentData(string PaymentId, string PayorName, string PayeeName,
    decimal Amount, string AccountNumber, string RoutingNumber, string PaymentDate);

/// <summary>
/// Request payload sent from the Coordinator (App 1) to the SFTP Processor (App 2).
/// Contains all payments and the callback URL for completion notification.
/// </summary>
public record SftpBatchRequest(string BatchId, List<PaymentData> Payments, string CallbackUrl);

/// <summary>String constants for file types tracked per batch.</summary>
public static class FileType
{
    public const string Payment = "payment";
    public const string GeneralLedger = "gl";
}

/// <summary>Per-file result reported by the SFTP Processor in the callback payload.</summary>
public record FileResult(string FileType, bool Succeeded, string? ErrorMessage);

/// <summary>
/// Callback payload sent from the SFTP Processor (App 2) back to the Coordinator (App 1)
/// with per-file results for a batch.
/// </summary>
public record SftpBatchResult(string BatchId, List<FileResult> Files);

/// <summary>String constants for batch and file status values stored in Table Storage.</summary>
public static class BatchStatus
{
    public const string Queued = "Queued";
    public const string Processing = "Processing";
    public const string Processed = "Processed";
    public const string PaymentFileFailed = "PaymentFileFailed";
    public const string GLFileFailed = "GLFileFailed";
    public const string Failed = "Failed";
}

/// <summary>Batch status response including file-level and payment-level details.</summary>
public record BatchStatusResponse(string BatchId, string Status, int PaymentCount,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    List<BatchFileStatus> Files, List<PaymentStatus> Payments);

/// <summary>File-level status within a batch (e.g., payment or GL file).</summary>
public record BatchFileStatus(string FileType, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? ErrorMessage);

/// <summary>Payment-level status within a batch.</summary>
public record PaymentStatus(string PaymentId, string Status, DateTimeOffset CreatedAt);
