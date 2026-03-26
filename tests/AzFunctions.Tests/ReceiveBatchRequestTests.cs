using System.Net;
using System.Text.Json;
using AzFunctions.Tests.Helpers;
using NSubstitute;

namespace AzFunctions.Tests;

public class ReceiveBatchRequestTests
{
    private readonly IMessageQueue messageQueue = Substitute.For<IMessageQueue>();
    private readonly IBatchPaymentStore batchPaymentStore = Substitute.For<IBatchPaymentStore>();
    private readonly FunctionContext context = new FakeFunctionContext(nameof(BatchProcessor.ReceiveBatchRequest));

    private BatchProcessor CreateProcessor() => new(messageQueue, batchPaymentStore);

    [Fact]
    public async Task ValidRequest_Returns202AndStoresAndQueues()
    {
        var body = new BatchRequest("batch1", [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
                "1234567890", "021000021", "2026-03-15")
        ], "http://localhost/callback");
        var req = FakeHttpRequestData.CreateWithJson(context, body);

        var response = await CreateProcessor().ReceiveBatchRequest(req, context);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await batchPaymentStore.Received(1).StoreBatchAsync("batch1", "http://localhost/callback",
            Arg.Is<List<PaymentData>>(p => p.Count == 1 && p[0].PaymentId == "pmt-000"));
        await messageQueue.Received(1).SendMessageAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task ValidRequest_QueueMessageContainsOnlyBatchId()
    {
        var body = new BatchRequest("batch1", [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
                "1234567890", "021000021", "2026-03-15")
        ], "http://localhost/callback");
        var req = FakeHttpRequestData.CreateWithJson(context, body);

        string? capturedMessage = null;
        await messageQueue.SendMessageAsync(Arg.Do<string>(msg => capturedMessage = msg));

        await CreateProcessor().ReceiveBatchRequest(req, context);

        Assert.NotNull(capturedMessage);
        // Queue message should contain batchId but NOT payment data
        Assert.Contains("batch1", capturedMessage);
        Assert.DoesNotContain("pmt-000", capturedMessage);
        Assert.DoesNotContain("John Doe", capturedMessage);

        var deserialized = JsonSerializer.Deserialize<BatchQueueMessage>(capturedMessage,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.NotNull(deserialized);
        Assert.Equal("batch1", deserialized.BatchId);
    }

    [Fact]
    public async Task NullBody_Returns400()
    {
        var req = new FakeHttpRequestData(context, "null");

        var response = await CreateProcessor().ReceiveBatchRequest(req, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await messageQueue.DidNotReceive().SendMessageAsync(Arg.Any<string>());
        await batchPaymentStore.DidNotReceive().StoreBatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<List<PaymentData>>());
    }

    [Fact]
    public async Task MissingBatchId_Returns400()
    {
        var body = new BatchRequest("", [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
                "1234567890", "021000021", "2026-03-15")
        ], "http://localhost/callback");
        var req = FakeHttpRequestData.CreateWithJson(context, body);

        var response = await CreateProcessor().ReceiveBatchRequest(req, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await messageQueue.DidNotReceive().SendMessageAsync(Arg.Any<string>());
        await batchPaymentStore.DidNotReceive().StoreBatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<List<PaymentData>>());
    }

    [Fact]
    public async Task EmptyPayments_Returns400()
    {
        var body = new BatchRequest("batch1", [], "http://localhost/callback");
        var req = FakeHttpRequestData.CreateWithJson(context, body);

        var response = await CreateProcessor().ReceiveBatchRequest(req, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await messageQueue.DidNotReceive().SendMessageAsync(Arg.Any<string>());
        await batchPaymentStore.DidNotReceive().StoreBatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<List<PaymentData>>());
    }
}
