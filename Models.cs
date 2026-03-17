namespace AzFunctions;

public record PersonData(string FirstName, string LastName, string DateOfBirth);
public record AddressData(string Street, string City, string State, string ZipCode);
public record SftpProcessRequest(string BatchId, string ItemId, PersonData Person, AddressData Address, string CallbackUrl);
public record SftpProcessResult(string BatchId, string ItemId, bool Succeeded, string? ErrorMessage);

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
