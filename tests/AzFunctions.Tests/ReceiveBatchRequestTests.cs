using System.Net;
using System.Text.Json;
using AzFunctions.Tests.Helpers;
using NSubstitute;

namespace AzFunctions.Tests;

public class ReceiveBatchRequestTests
{
    private readonly IMessageQueue messageQueue = Substitute.For<IMessageQueue>();
    private readonly FunctionContext context = new FakeFunctionContext(nameof(BatchProcessor.ReceiveSftpRequest));

    private BatchProcessor CreateProcessor() => new(messageQueue);

    [Fact]
    public async Task ValidRequest_Returns202AndQueuesMessage()
    {
        var body = new BatchRequest("batch1", [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
                "1234567890", "021000021", "2026-03-15")
        ], "http://localhost/callback");
        var req = FakeHttpRequestData.CreateWithJson(context, body);

        var response = await CreateProcessor().ReceiveSftpRequest(req, context);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await messageQueue.Received(1).SendMessageAsync(Arg.Is<string>(msg =>
            msg.Contains("batch1") &&
            msg.Contains("pmt-000") &&
            msg.Contains("http://localhost/callback")));
    }

    [Fact]
    public async Task ValidRequest_QueueMessageContainsAllPaymentFields()
    {
        var body = new BatchRequest("batch1", [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
                "1234567890", "021000021", "2026-03-15")
        ], "http://localhost/callback");
        var req = FakeHttpRequestData.CreateWithJson(context, body);

        string? capturedMessage = null;
        await messageQueue.SendMessageAsync(Arg.Do<string>(msg => capturedMessage = msg));

        await CreateProcessor().ReceiveSftpRequest(req, context);

        Assert.NotNull(capturedMessage);
        var deserialized = JsonSerializer.Deserialize<BatchRequest>(capturedMessage,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.NotNull(deserialized);
        Assert.Equal("batch1", deserialized.BatchId);
        Assert.Equal("http://localhost/callback", deserialized.CallbackUrl);
        Assert.Single(deserialized.Payments);
        Assert.Equal("pmt-000", deserialized.Payments[0].PaymentId);
        Assert.Equal(1500.00m, deserialized.Payments[0].Amount);
    }

    [Fact]
    public async Task NullBody_Returns400()
    {
        var req = new FakeHttpRequestData(context, "null");

        var response = await CreateProcessor().ReceiveSftpRequest(req, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await messageQueue.DidNotReceive().SendMessageAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task MissingBatchId_Returns400()
    {
        var body = new BatchRequest("", [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
                "1234567890", "021000021", "2026-03-15")
        ], "http://localhost/callback");
        var req = FakeHttpRequestData.CreateWithJson(context, body);

        var response = await CreateProcessor().ReceiveSftpRequest(req, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await messageQueue.DidNotReceive().SendMessageAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task EmptyPayments_Returns400()
    {
        var body = new BatchRequest("batch1", [], "http://localhost/callback");
        var req = FakeHttpRequestData.CreateWithJson(context, body);

        var response = await CreateProcessor().ReceiveSftpRequest(req, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await messageQueue.DidNotReceive().SendMessageAsync(Arg.Any<string>());
    }
}
