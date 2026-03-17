using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AzFunctions.Tests;

public class SftpOrchestrationTests
{
    [Fact]
    public async Task CallbackFailure_DoesNotFailOrchestration()
    {
        var context = Substitute.For<TaskOrchestrationContext>();
        context.InstanceId.Returns("sftp-batch1-item-000");
        context.CreateReplaySafeLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        context.GetInput<SftpProcessRequest>().Returns(new SftpProcessRequest(
            "batch1", "item-000",
            new PersonData("John", "Doe", "1990-01-01"),
            new AddressData("123 Main St", "Springfield", "IL", "62701"),
            "http://localhost/callback"));

        // File content creation succeeds
        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreatePersonFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("First Name: John\nLast Name: Doe\nDate of Birth: 1990-01-01");
        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreateAddressFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("Street: 123 Main St\nCity: Springfield\nState: IL\nZip Code: 62701");

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
        context.InstanceId.Returns("sftp-batch1-item-000");
        context.CreateReplaySafeLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        context.GetInput<SftpProcessRequest>().Returns(new SftpProcessRequest(
            "batch1", "item-000",
            new PersonData("John", "Doe", "1990-01-01"),
            new AddressData("123 Main St", "Springfield", "IL", "62701"),
            "http://localhost/callback"));

        // File content creation succeeds
        string personContent = "First Name: John\nLast Name: Doe\nDate of Birth: 1990-01-01";
        string addressContent = "Street: 123 Main St\nCity: Springfield\nState: IL\nZip Code: 62701";

        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreatePersonFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns(personContent);
        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreateAddressFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns(addressContent);

        // Person upload fails, address upload succeeds.
        // NSubstitute matches on the UploadFileInput passed to the activity.
        var personInput = new SftpOrchestration.UploadFileInput("person_sftp-batch1-item-000.txt", personContent);
        var addressInput = new SftpOrchestration.UploadFileInput("address_sftp-batch1-item-000.txt", addressContent);

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
