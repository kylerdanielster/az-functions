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

        // File creation succeeds
        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreatePersonFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("/tmp/person_sftp-batch1-item-000.txt");
        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreateAddressFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("/tmp/address_sftp-batch1-item-000.txt");

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

        // File creation succeeds
        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreatePersonFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("/tmp/person_sftp-batch1-item-000.txt");
        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreateAddressFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("/tmp/address_sftp-batch1-item-000.txt");

        // First upload (person) fails, second (address) succeeds
        string personPath = "/tmp/person_sftp-batch1-item-000.txt";
        string addressPath = "/tmp/address_sftp-batch1-item-000.txt";

        context.CallActivityAsync<string>(nameof(SftpOrchestration.UploadFile), personPath, Arg.Any<TaskOptions>())
            .ThrowsAsync(new TaskFailedException("UploadFile", 1, new ApplicationException("SFTP connection failed")));
        context.CallActivityAsync<string>(nameof(SftpOrchestration.UploadFile), addressPath, Arg.Any<TaskOptions>())
            .Returns("Uploaded.");

        // Callback succeeds
        context.CallActivityAsync(nameof(SftpOrchestration.SendCallback), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns(Task.CompletedTask);

        // Cleanup for person file
        context.CallActivityAsync(nameof(SftpOrchestration.CleanupTempFiles), Arg.Any<object>())
            .Returns(Task.CompletedTask);

        string result = await SftpOrchestration.RunOrchestrator(context);

        Assert.Contains("Partial failure", result);
        Assert.Contains(FileType.Person, result);
    }
}
