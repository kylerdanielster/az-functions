using System.Text.Json;
using AzFunctions.Tests.Helpers;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using NSubstitute;

namespace AzFunctions.Tests;

public class ProcessSftpQueueTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly DurableTaskClient durableClient = Substitute.For<DurableTaskClient>("test");
    private readonly FunctionContext context = new FakeFunctionContext(nameof(SftpProcessor.ProcessSftpQueue));

    [Fact]
    public async Task ValidMessage_StartsOrchestrationWithDeterministicId()
    {
        var request = new SftpBatchRequest("batch1",
        [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
                "1234567890", "021000021", "2026-03-15")
        ], "http://localhost/callback");

        string messageText = JsonSerializer.Serialize(request, JsonOptions);

        await SftpProcessor.ProcessSftpQueue(messageText, durableClient, context);

        await durableClient.Received(1).ScheduleNewOrchestrationInstanceAsync(
            nameof(SftpOrchestration),
            Arg.Any<SftpBatchRequest>(),
            Arg.Is<StartOrchestrationOptions>(o => o.InstanceId == "sftp-batch1"));
    }

    [Fact]
    public async Task NullMessage_ThrowsInvalidOperationException()
    {
        string messageText = "null";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SftpProcessor.ProcessSftpQueue(messageText, durableClient, context));
    }

    [Fact]
    public async Task MalformedJson_ThrowsJsonException()
    {
        string messageText = "not valid json";

        await Assert.ThrowsAsync<JsonException>(
            () => SftpProcessor.ProcessSftpQueue(messageText, durableClient, context));
    }

    [Fact]
    public async Task InstanceId_IncludesBatchId()
    {
        var request = new SftpBatchRequest("abc123",
        [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
                "1234567890", "021000021", "2026-03-15")
        ], "http://localhost/callback");

        string messageText = JsonSerializer.Serialize(request, JsonOptions);

        await SftpProcessor.ProcessSftpQueue(messageText, durableClient, context);

        await durableClient.Received(1).ScheduleNewOrchestrationInstanceAsync(
            nameof(SftpOrchestration),
            Arg.Any<SftpBatchRequest>(),
            Arg.Is<StartOrchestrationOptions>(o => o.InstanceId == "sftp-abc123"));
    }
}
