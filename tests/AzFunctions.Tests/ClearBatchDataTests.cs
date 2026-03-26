using System.Net;
using AzFunctions.Tests.Helpers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AzFunctions.Tests;

public class ClearBatchDataTests
{
    private readonly IBatchTracker batchTracker = Substitute.For<IBatchTracker>();
    private readonly IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly FunctionContext context = new FakeFunctionContext(nameof(BatchCoordinator.ClearBatchData));

    private BatchCoordinator CreateCoordinator() => new(httpClientFactory, batchTracker);

    [Fact]
    public async Task ClearSucceeds_ReturnsDeletedCount()
    {
        batchTracker.ClearAllAsync().Returns(15);

        var req = new FakeHttpRequestData(context);
        var response = (FakeHttpResponseData)await CreateCoordinator().ClearBatchData(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = response.GetBodyString();
        Assert.Contains("15", body);
    }

    [Fact]
    public async Task ClearEmpty_ReturnsZero()
    {
        batchTracker.ClearAllAsync().Returns(0);

        var req = new FakeHttpRequestData(context);
        var response = (FakeHttpResponseData)await CreateCoordinator().ClearBatchData(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = response.GetBodyString();
        Assert.Contains("0", body);
    }

    [Fact]
    public async Task TrackerThrows_Returns500()
    {
        batchTracker.ClearAllAsync().ThrowsAsync(new Exception("Storage unavailable"));

        var req = new FakeHttpRequestData(context);
        var response = await CreateCoordinator().ClearBatchData(req, context);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
