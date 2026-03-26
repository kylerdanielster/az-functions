using System.Net;
using AzFunctions.Tests.Helpers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AzFunctions.Tests;

public class BatchCompletedTests
{
    private readonly IBatchTracker batchTracker = Substitute.For<IBatchTracker>();
    private readonly IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly FunctionContext context = new FakeFunctionContext(nameof(BatchCoordinator.BatchCompleted));

    private BatchCoordinator CreateDataFeed() => new(httpClientFactory, batchTracker);

    [Fact]
    public async Task ProcessedCallback_UpdatesBatchStatus()
    {
        var callback = new BatchCallback("batch1", BatchStatus.Processed);
        var req = FakeHttpRequestData.CreateWithJson(context, callback);

        var response = await CreateDataFeed().BatchCompleted(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await batchTracker.Received(1).UpdateBatchStatusAsync("batch1", BatchStatus.Processed);
    }

    [Fact]
    public async Task NullRequest_Returns400()
    {
        var req = new FakeHttpRequestData(context, "null");

        var response = await CreateDataFeed().BatchCompleted(req, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ErrorCallback_UpdatesBatchStatus()
    {
        var callback = new BatchCallback("batch1", BatchStatus.Error);
        var req = FakeHttpRequestData.CreateWithJson(context, callback);

        var response = await CreateDataFeed().BatchCompleted(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await batchTracker.Received(1).UpdateBatchStatusAsync("batch1", BatchStatus.Error);
    }

    [Fact]
    public async Task ProcessingCallback_UpdatesBatchStatus()
    {
        var callback = new BatchCallback("batch1", BatchStatus.Processing);
        var req = FakeHttpRequestData.CreateWithJson(context, callback);

        var response = await CreateDataFeed().BatchCompleted(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await batchTracker.Received(1).UpdateBatchStatusAsync("batch1", BatchStatus.Processing);
    }

    [Fact]
    public async Task TrackerThrows_PropagatesException()
    {
        batchTracker.UpdateBatchStatusAsync("batch1", BatchStatus.Processed)
            .ThrowsAsync(new Exception("Storage unavailable"));

        var callback = new BatchCallback("batch1", BatchStatus.Processed);
        var req = FakeHttpRequestData.CreateWithJson(context, callback);

        await Assert.ThrowsAsync<Exception>(() => CreateDataFeed().BatchCompleted(req, context));
    }
}
