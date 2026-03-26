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

    private readonly IMessageQueue messageQueue = Substitute.For<IMessageQueue>();
    private readonly IBatchPaymentStore batchPaymentStore = Substitute.For<IBatchPaymentStore>();
    private readonly DurableTaskClient durableClient = Substitute.For<DurableTaskClient>("test");
    private readonly FunctionContext context = new FakeFunctionContext(nameof(BatchProcessor.ProcessBatchQueue));

    private BatchProcessor CreateProcessor() => new(messageQueue, batchPaymentStore);

    [Fact]
    public async Task ValidMessage_StartsOrchestrationWithDeterministicId()
    {
        var queueMessage = new BatchQueueMessage("batch1");
        string messageText = JsonSerializer.Serialize(queueMessage, JsonOptions);
        batchPaymentStore.GetCallbackUrlAsync("batch1").Returns("http://localhost/callback");

        await CreateProcessor().ProcessBatchQueue(messageText, durableClient, context);

        await durableClient.Received(1).ScheduleNewOrchestrationInstanceAsync(
            nameof(BatchOrchestration),
            Arg.Is<BatchOrchestrationInput>(i => i.BatchId == "batch1" && i.CallbackUrl == "http://localhost/callback"),
            Arg.Is<StartOrchestrationOptions>(o => o.InstanceId == "batch-batch1"));
    }

    [Fact]
    public async Task NullMessage_ThrowsInvalidOperationException()
    {
        string messageText = "null";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateProcessor().ProcessBatchQueue(messageText, durableClient, context));
    }

    [Fact]
    public async Task MalformedJson_ThrowsJsonException()
    {
        string messageText = "not valid json";

        await Assert.ThrowsAsync<JsonException>(
            () => CreateProcessor().ProcessBatchQueue(messageText, durableClient, context));
    }

    [Fact]
    public async Task InstanceId_IncludesBatchId()
    {
        var queueMessage = new BatchQueueMessage("abc123");
        string messageText = JsonSerializer.Serialize(queueMessage, JsonOptions);
        batchPaymentStore.GetCallbackUrlAsync("abc123").Returns("http://localhost/callback");

        await CreateProcessor().ProcessBatchQueue(messageText, durableClient, context);

        await durableClient.Received(1).ScheduleNewOrchestrationInstanceAsync(
            nameof(BatchOrchestration),
            Arg.Any<BatchOrchestrationInput>(),
            Arg.Is<StartOrchestrationOptions>(o => o.InstanceId == "batch-abc123"));
    }
}
