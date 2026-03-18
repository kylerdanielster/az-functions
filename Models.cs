namespace AzFunctions;

/// <summary>Person demographic data included in a payment batch item.</summary>
public record PersonData(string FirstName, string LastName, string DateOfBirth);

/// <summary>Address data included in a payment batch item.</summary>
public record AddressData(string Street, string City, string State, string ZipCode);

/// <summary>A single record within a batch, combining person and address data.</summary>
public record BatchItem(string ItemId, PersonData Person, AddressData Address);

/// <summary>
/// Request payload sent from the Coordinator (App 1) to the SFTP Processor (App 2).
/// Contains all batch items and the callback URL for completion notification.
/// </summary>
public record SftpBatchRequest(string BatchId, List<BatchItem> Items, string CallbackUrl);

/// <summary>String constants for file types tracked per batch.</summary>
public static class FileType
{
    public const string Person = "person";
    public const string Address = "address";
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
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string PartialFailure = "PartialFailure";
}

/// <summary>Batch status response including file-level details.</summary>
public record BatchStatusResponse(string BatchId, string Status, int ItemCount,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    List<BatchFileStatus> Files);

/// <summary>File-level status within a batch (e.g., person or address file).</summary>
public record BatchFileStatus(string FileType, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? ErrorMessage);
