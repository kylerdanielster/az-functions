namespace AzFunctions;

/// <summary>Person demographic data included in a payment batch item.</summary>
public record PersonData(string FirstName, string LastName, string DateOfBirth);

/// <summary>Address data included in a payment batch item.</summary>
public record AddressData(string Street, string City, string State, string ZipCode);

/// <summary>
/// Request payload sent from the Coordinator (App 1) to the SFTP Processor (App 2).
/// Contains person/address data for a single batch item, plus the callback URL for completion notification.
/// </summary>
public record SftpProcessRequest(string BatchId, string ItemId, PersonData Person, AddressData Address, string CallbackUrl);

/// <summary>String constants for file types tracked per batch item.</summary>
public static class FileType
{
    public const string Person = "person";
    public const string Address = "address";
}

/// <summary>Per-file result reported by the SFTP Processor in the callback payload.</summary>
public record FileResult(string FileType, bool Succeeded, string? ErrorMessage);

/// <summary>
/// Callback payload sent from the SFTP Processor (App 2) back to the Coordinator (App 1)
/// with per-file results for a batch item.
/// </summary>
public record SftpProcessResult(string BatchId, string ItemId, List<FileResult> Files);

/// <summary>String constants for batch and item status values stored in Table Storage.</summary>
public static class BatchStatus
{
    public const string Queued = "Queued";
    public const string Processing = "Processing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string PartialFailure = "PartialFailure";
}

/// <summary>Batch status response including item-level and file-level details.</summary>
public record BatchStatusResponse(string BatchId, string Status, int ItemCount,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    List<BatchItemStatus> Items, List<BatchFileStatus> Files);

/// <summary>Item-level status within a batch. Status is derived from its file statuses.</summary>
public record BatchItemStatus(string ItemId, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? ErrorMessage);

/// <summary>File-level status within a batch item (e.g., person or address file).</summary>
public record BatchFileStatus(string ItemId, string FileType, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? ErrorMessage);
