using System.Net;
using AzFunctions.Tests.Helpers;
using NSubstitute;

namespace AzFunctions.Tests;

public class ReceiveSftpRequestTests
{
    private readonly IMessageQueue messageQueue = Substitute.For<IMessageQueue>();
    private readonly FunctionContext context = new FakeFunctionContext(nameof(SftpProcessor.ReceiveSftpRequest));

    private SftpProcessor CreateProcessor() => new(messageQueue);

    [Fact]
    public async Task ValidRequest_Returns202AndQueuesMessage()
    {
        var body = new SftpBatchRequest("batch1", [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
                "1234567890", "021000021", "2026-03-15")
        ], "http://localhost/callback");
        var req = FakeHttpRequestData.CreateWithJson(context, body);

        var response = await CreateProcessor().ReceiveSftpRequest(req, context);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await messageQueue.Received(1).SendMessageAsync(Arg.Any<string>());
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
        var body = new SftpBatchRequest("", [
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
        var body = new SftpBatchRequest("batch1", [], "http://localhost/callback");
        var req = FakeHttpRequestData.CreateWithJson(context, body);

        var response = await CreateProcessor().ReceiveSftpRequest(req, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await messageQueue.DidNotReceive().SendMessageAsync(Arg.Any<string>());
    }
}
