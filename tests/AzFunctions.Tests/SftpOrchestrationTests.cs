using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AzFunctions.Tests;

public class SftpOrchestrationTests
{
    private static List<PaymentData> CreateTestPayments() =>
    [
        new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
            "1234567890", "021000021", "2026-03-15"),
        new PaymentData("pmt-001", "Jane Smith", "Globex Inc", 2750.50m,
            "9876543210", "021000089", "2026-03-14")
    ];

    [Fact]
    public async Task CallbackFailure_DoesNotFailOrchestration()
    {
        var context = Substitute.For<TaskOrchestrationContext>();
        context.InstanceId.Returns("sftp-batch1");
        context.CreateReplaySafeLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        context.GetInput<SftpBatchRequest>().Returns(new SftpBatchRequest(
            "batch1", CreateTestPayments(), "http://localhost/callback"));

        // File content creation succeeds
        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreatePaymentFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("PaymentId,PayorName,PayeeName,Amount,AccountNumber,RoutingNumber,PaymentDate\npmt-000,John Doe,Acme Corp,1500.00,1234567890,021000021,2026-03-15\n");
        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreateGLFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("PaymentId,PayorName,PayeeName,Amount,PaymentDate\npmt-000,John Doe,Acme Corp,1500.00,2026-03-15\n");

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
            "batch1", CreateTestPayments(), "http://localhost/callback"));

        // File content creation succeeds
        string paymentContent = "PaymentId,PayorName,PayeeName,Amount,AccountNumber,RoutingNumber,PaymentDate\npmt-000,John Doe,Acme Corp,1500.00,1234567890,021000021,2026-03-15\n";
        string glContent = "PaymentId,PayorName,PayeeName,Amount,PaymentDate\npmt-000,John Doe,Acme Corp,1500.00,2026-03-15\n";

        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreatePaymentFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns(paymentContent);
        context.CallActivityAsync<string>(nameof(SftpOrchestration.CreateGLFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns(glContent);

        // Payment upload fails, GL upload succeeds.
        var paymentInput = new SftpOrchestration.UploadFileInput("payment_batch1.csv", paymentContent);
        var glInput = new SftpOrchestration.UploadFileInput("gl_batch1.csv", glContent);

        context.CallActivityAsync<string>(nameof(SftpOrchestration.UploadFile), paymentInput, Arg.Any<TaskOptions>())
            .ThrowsAsync(new TaskFailedException("UploadFile", 1, new ApplicationException("SFTP connection failed")));
        context.CallActivityAsync<string>(nameof(SftpOrchestration.UploadFile), glInput, Arg.Any<TaskOptions>())
            .Returns("Uploaded.");

        // Callback succeeds
        context.CallActivityAsync(nameof(SftpOrchestration.SendCallback), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns(Task.CompletedTask);

        string result = await SftpOrchestration.RunOrchestrator(context);

        Assert.Contains("Partial failure", result);
        Assert.Contains(FileType.Payment, result);
    }
}
