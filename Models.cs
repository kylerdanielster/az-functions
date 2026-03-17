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

/// <summary>
/// Callback payload sent from the SFTP Processor (App 2) back to the Coordinator (App 1)
/// indicating whether a batch item was processed successfully.
/// </summary>
public record SftpProcessResult(string BatchId, string ItemId, bool Succeeded, string? ErrorMessage);

/// <summary>String constants for batch and item status values stored in Table Storage.</summary>
public static class BatchStatus
{
    public const string Queued = "Queued";
    public const string Processing = "Processing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string PartialFailure = "PartialFailure";
}

// Testing: response models for GetBatchStatus endpoint
public record BatchStatusResponse(string BatchId, string Status, int ItemCount,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, List<BatchItemStatus> Items);
public record BatchItemStatus(string ItemId, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? ErrorMessage);
