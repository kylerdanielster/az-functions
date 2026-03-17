namespace Company.Function;

public record PersonData(string FirstName, string LastName, string DateOfBirth);
public record AddressData(string Street, string City, string State, string ZipCode);
public record SftpProcessRequest(string BatchId, string ItemId, PersonData Person, AddressData Address, string CallbackUrl);
public record SftpProcessResult(string BatchId, string ItemId, bool Succeeded, string? ErrorMessage);

// Testing: response models for GetBatchStatus endpoint
public record BatchStatusResponse(string BatchId, string Status, int ItemCount,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, List<BatchItemStatus> Items);
public record BatchItemStatus(string ItemId, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? ErrorMessage);
