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
        var body = new SftpProcessRequest("batch1", "item-000",
            new PersonData("John", "Doe", "1990-01-01"),
            new AddressData("123 Main St", "Springfield", "IL", "62701"),
            "http://localhost/callback");
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
        var body = new SftpProcessRequest("", "item-000",
            new PersonData("John", "Doe", "1990-01-01"),
            new AddressData("123 Main St", "Springfield", "IL", "62701"),
            "http://localhost/callback");
        var req = FakeHttpRequestData.CreateWithJson(context, body);

        var response = await CreateProcessor().ReceiveSftpRequest(req, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await messageQueue.DidNotReceive().SendMessageAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task MissingPerson_Returns400()
    {
        // Serialize manually to produce null Person
        var json = """{"batchId":"batch1","itemId":"item-000","person":null,"address":{"street":"123","city":"X","state":"IL","zipCode":"62701"},"callbackUrl":"http://localhost/callback"}""";
        var req = new FakeHttpRequestData(context, json);

        var response = await CreateProcessor().ReceiveSftpRequest(req, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await messageQueue.DidNotReceive().SendMessageAsync(Arg.Any<string>());
    }
}
