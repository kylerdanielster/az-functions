using System.Text.Json;
using AzFunctions.Tests.Helpers;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using NSubstitute;

namespace AzFunctions.Tests;

public class ProcessBatchQueueTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly DurableTaskClient durableClient = Substitute.For<DurableTaskClient>("test");
    private readonly FunctionContext context = new FakeFunctionContext(nameof(BatchProcessor.ProcessSftpQueue));

    [Fact]
    public async Task ValidMessage_StartsOrchestrationWithDeterministicId()
    {
        var request = new BatchRequest("batch1",
        [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
                "1234567890", "021000021", "2026-03-15")
        ], "http://localhost/callback");

        string messageText = JsonSerializer.Serialize(request, JsonOptions);

        await BatchProcessor.ProcessSftpQueue(messageText, durableClient, context);

        await durableClient.Received(1).ScheduleNewOrchestrationInstanceAsync(
            nameof(BatchOrchestration),
            Arg.Any<BatchRequest>(),
            Arg.Is<StartOrchestrationOptions>(o => o.InstanceId == "sftp-batch1"));
    }

    [Fact]
    public async Task NullMessage_ThrowsInvalidOperationException()
    {
        string messageText = "null";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => BatchProcessor.ProcessSftpQueue(messageText, durableClient, context));
    }

    [Fact]
    public async Task MalformedJson_ThrowsJsonException()
    {
        string messageText = "not valid json";

        await Assert.ThrowsAsync<JsonException>(
            () => BatchProcessor.ProcessSftpQueue(messageText, durableClient, context));
    }

    [Fact]
    public async Task InstanceId_IncludesBatchId()
    {
        var request = new BatchRequest("abc123",
        [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
                "1234567890", "021000021", "2026-03-15")
        ], "http://localhost/callback");

        string messageText = JsonSerializer.Serialize(request, JsonOptions);

        await BatchProcessor.ProcessSftpQueue(messageText, durableClient, context);

        await durableClient.Received(1).ScheduleNewOrchestrationInstanceAsync(
            nameof(BatchOrchestration),
            Arg.Any<BatchRequest>(),
            Arg.Is<StartOrchestrationOptions>(o => o.InstanceId == "sftp-abc123"));
    }
}
