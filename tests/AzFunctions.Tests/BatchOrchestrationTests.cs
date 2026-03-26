using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AzFunctions.Tests;

public class BatchOrchestrationTests
{
    private static List<PaymentData> CreateTestPayments() =>
    [
        new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
            "1234567890", "021000021", "2026-03-15"),
        new PaymentData("pmt-001", "Jane Smith", "Globex Inc", 2750.50m,
            "9876543210", "021000089", "2026-03-14")
    ];

    private static TaskOrchestrationContext CreateMockContext()
    {
        var context = Substitute.For<TaskOrchestrationContext>();
        context.InstanceId.Returns("batch-batch1");
        context.CreateReplaySafeLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        context.GetInput<BatchRequest>().Returns(new BatchRequest(
            "batch1", CreateTestPayments(), "http://localhost/callback"));
        return context;
    }

    [Fact]
    public async Task FullSuccess_SendsProcessedCallback()
    {
        var context = CreateMockContext();

        // File content creation succeeds
        context.CallActivityAsync<string>(nameof(BatchOrchestration.CreatePaymentFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("payment csv content");
        context.CallActivityAsync<string>(nameof(BatchOrchestration.CreateGLFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("gl csv content");

        // Uploads succeed
        context.CallActivityAsync<string>(nameof(BatchOrchestration.UploadFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("Uploaded.");

        // Capture callback inputs
        var callbackInputs = new List<BatchOrchestration.SendCallbackInput>();
        context.CallActivityAsync(nameof(BatchOrchestration.SendCallback), Arg.Do<object>(o =>
        {
            if (o is BatchOrchestration.SendCallbackInput input) callbackInputs.Add(input);
        }), Arg.Any<TaskOptions>())
            .Returns(Task.CompletedTask);

        string result = await BatchOrchestration.RunOrchestrator(context);

        Assert.Contains("Uploaded 2 files", result);

        // Only one callback (Processed) — Processing is set by App 1
        Assert.Single(callbackInputs);
        Assert.Equal("batch1", callbackInputs[0].Callback.BatchId);
        Assert.Equal(BatchStatus.Processed, callbackInputs[0].Callback.Status);
    }

    [Fact]
    public async Task PaymentUploadFails_SendsErrorCallback_NoGLAttempt()
    {
        var context = CreateMockContext();

        // Payment file content creation succeeds
        context.CallActivityAsync<string>(nameof(BatchOrchestration.CreatePaymentFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("payment csv content");

        // Payment upload fails
        context.CallActivityAsync<string>(nameof(BatchOrchestration.UploadFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .ThrowsAsync(new TaskFailedException("UploadFile", 1, new ApplicationException("SFTP connection failed")));

        // Capture callback inputs
        var callbackInputs = new List<BatchOrchestration.SendCallbackInput>();
        context.CallActivityAsync(nameof(BatchOrchestration.SendCallback), Arg.Do<object>(o =>
        {
            if (o is BatchOrchestration.SendCallbackInput input) callbackInputs.Add(input);
        }), Arg.Any<TaskOptions>())
            .Returns(Task.CompletedTask);

        string result = await BatchOrchestration.RunOrchestrator(context);

        Assert.Contains("Payment file upload failed", result);

        // GL file should never be created
        await context.DidNotReceive().CallActivityAsync<string>(nameof(BatchOrchestration.CreateGLFile),
            Arg.Any<object>(), Arg.Any<TaskOptions>());

        // Only one callback (Error) with correct payload
        Assert.Single(callbackInputs);
        Assert.Equal("batch1", callbackInputs[0].Callback.BatchId);
        Assert.Equal(BatchStatus.Error, callbackInputs[0].Callback.Status);
    }

    [Fact]
    public async Task GLUploadFails_QueuesError_NoCallback()
    {
        var context = CreateMockContext();

        // File content creation succeeds
        context.CallActivityAsync<string>(nameof(BatchOrchestration.CreatePaymentFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("payment csv content");
        context.CallActivityAsync<string>(nameof(BatchOrchestration.CreateGLFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("gl csv content");

        // Payment upload succeeds, GL upload fails
        var paymentInput = new BatchOrchestration.UploadFileInput("payment_batch1.csv", "payment csv content");
        var glInput = new BatchOrchestration.UploadFileInput("gl_batch1.csv", "gl csv content");

        context.CallActivityAsync<string>(nameof(BatchOrchestration.UploadFile), paymentInput, Arg.Any<TaskOptions>())
            .Returns("Uploaded.");
        context.CallActivityAsync<string>(nameof(BatchOrchestration.UploadFile), glInput, Arg.Any<TaskOptions>())
            .ThrowsAsync(new TaskFailedException("UploadFile", 1, new ApplicationException("SFTP connection failed")));

        // Callbacks succeed
        context.CallActivityAsync(nameof(BatchOrchestration.SendCallback), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns(Task.CompletedTask);

        string result = await BatchOrchestration.RunOrchestrator(context);

        Assert.Contains("GL file upload failed", result);

        // No callbacks — Processing is set by App 1, and no Processed after GL failure
        await context.DidNotReceive().CallActivityAsync(nameof(BatchOrchestration.SendCallback),
            Arg.Any<object>(), Arg.Any<TaskOptions>());

        // GL error queued
        await context.Received(1).CallActivityAsync(nameof(BatchOrchestration.SendToGLErrorQueue),
            Arg.Any<object>(), Arg.Any<TaskOptions>());
    }

    [Fact]
    public async Task CallbackFailure_DoesNotFailOrchestration()
    {
        var context = CreateMockContext();

        // File content creation succeeds
        context.CallActivityAsync<string>(nameof(BatchOrchestration.CreatePaymentFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("payment csv content");
        context.CallActivityAsync<string>(nameof(BatchOrchestration.CreateGLFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("gl csv content");

        // Uploads succeed
        context.CallActivityAsync<string>(nameof(BatchOrchestration.UploadFile), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .Returns("Uploaded.");

        // Callbacks fail
        context.CallActivityAsync(nameof(BatchOrchestration.SendCallback), Arg.Any<object>(), Arg.Any<TaskOptions>())
            .ThrowsAsync(new TaskFailedException("SendCallback", 1, new ApplicationException("Connection refused")));

        // Should NOT throw — callback failure is isolated
        string result = await BatchOrchestration.RunOrchestrator(context);

        Assert.Contains("Uploaded 2 files", result);
    }
}
