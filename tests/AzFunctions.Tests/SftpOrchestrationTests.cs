using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AzFunctions.Tests;

public class SftpOrchestrationTests
{
    private static List<BatchItem> CreateTestItems() =>
    [
        new BatchItem("item-000",
            new PersonData("John", "Doe", "1990-01-01"),
            new AddressData("123 Main St", "Springfield", "IL", "62701")),
        new BatchItem("item-001",
            new PersonData("Jane", "Smith", "1985-06-15"),
            new AddressData("456 Oak Ave", "Chicago", "IL", "60601"))
    ];

    [Fact]
    public async Task CallbackFailure_DoesNotFailOrchestration()
    {
        var context = Substitute.For<TaskOrchestrationContext>();
        context.InstanceId.Returns("sftp-batch1");
        context.CreateReplaySafeLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        context.GetInput<SftpBatchRequest>().Returns(new SftpBatchRequest(
            "batch1", CreateTestItems(), "http://localhost/callback"));

        // File content creation succeeds
        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreatePersonFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("ItemId,FirstName,LastName,DateOfBirth\nitem-000,John,Doe,1990-01-01\n");
        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreateAddressFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("ItemId,Street,City,State,ZipCode\nitem-000,123 Main St,Springfield,IL,62701\n");

        // Uploads succeed
        context.CallActivityAsync<string>(nameof(SftpOrchestration.UploadFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("Uploaded.");

        // Callback fails
        context.CallActivityAsync(nameof(SftpOrchestration.SendCallback), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .ThrowsAsync(new TaskFailedException("SendCallback", 1, new ApplicationException("Connection refused")));

        // Should NOT throw — callback failure is isolated
        string result = await SftpOrchestration.RunOrchestrator(context);

        Assert.Contains("Uploaded 2 files", result);
    }

    [Fact]
    public async Task SingleFileUploadFailure_DoesNotBlockOtherFile()
    {
        var context = Substitute.For<TaskOrchestrationContext>();
        context.InstanceId.Returns("sftp-batch1");
        context.CreateReplaySafeLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        context.GetInput<SftpBatchRequest>().Returns(new SftpBatchRequest(
            "batch1", CreateTestItems(), "http://localhost/callback"));

        // File content creation succeeds
        string personContent = "ItemId,FirstName,LastName,DateOfBirth\nitem-000,John,Doe,1990-01-01\n";
        string addressContent = "ItemId,Street,City,State,ZipCode\nitem-000,123 Main St,Springfield,IL,62701\n";

        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreatePersonFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns(personContent);
        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreateAddressFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns(addressContent);

        // Person upload fails, address upload succeeds.
        var personInput = new SftpOrchestration.UploadFileInput("person_batch1.csv", personContent);
        var addressInput = new SftpOrchestration.UploadFileInput("address_batch1.csv", addressContent);

        context.CallActivityAsync<string>(nameof(SftpOrchestration.UploadFile), personInput, Arg.Any<TaskOptions>())
            .ThrowsAsync(new TaskFailedException("UploadFile", 1, new ApplicationException("SFTP connection failed")));
        context.CallActivityAsync<string>(nameof(SftpOrchestration.UploadFile), addressInput, Arg.Any<TaskOptions>())
            .Returns("Uploaded.");

        // Callback succeeds
        context.CallActivityAsync(nameof(SftpOrchestration.SendCallback), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns(Task.CompletedTask);

        string result = await SftpOrchestration.RunOrchestrator(context);

        Assert.Contains("Partial failure", result);
        Assert.Contains(FileType.Person, result);
    }
}
