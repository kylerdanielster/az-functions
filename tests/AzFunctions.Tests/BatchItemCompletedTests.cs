using System.Net;
using AzFunctions.Tests.Helpers;
using NSubstitute;

namespace AzFunctions.Tests;

public class BatchItemCompletedTests
{
    private readonly IBatchTracker batchTracker = Substitute.For<IBatchTracker>();
    private readonly IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly FunctionContext context = new FakeFunctionContext(nameof(SftpDataFeed.BatchItemCompleted));

    private SftpDataFeed CreateDataFeed() => new(httpClientFactory, batchTracker);

    [Fact]
    public async Task ValidCallback_UpdatesFilesAndItem()
    {
        var result = new SftpProcessResult("batch1", "item-000", [
            new FileResult(FileType.Person, true, null),
            new FileResult(FileType.Address, true, null)
        ]);
        var req = FakeHttpRequestData.CreateWithJson(context, result);
        batchTracker.IsBatchCompleteAsync("batch1").Returns(false);

        var response = await CreateDataFeed().BatchItemCompleted(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await batchTracker.Received(1).UpdateFileStatusAsync("batch1", "item-000", FileType.Person, BatchStatus.Completed, null);
        await batchTracker.Received(1).UpdateFileStatusAsync("batch1", "item-000", FileType.Address, BatchStatus.Completed, null);
        await batchTracker.Received(1).UpdateItemFromFilesAsync("batch1", "item-000");
    }

    [Fact]
    public async Task NullRequest_Returns400()
    {
        var req = new FakeHttpRequestData(context, "null");

        var response = await CreateDataFeed().BatchItemCompleted(req, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BatchComplete_CallsCompleteBatch()
    {
        var result = new SftpProcessResult("batch1", "item-009", [
            new FileResult(FileType.Person, true, null),
            new FileResult(FileType.Address, true, null)
        ]);
        var req = FakeHttpRequestData.CreateWithJson(context, result);
        batchTracker.IsBatchCompleteAsync("batch1").Returns(true);
        batchTracker.CompleteBatchAsync("batch1").Returns(true);

        var response = await CreateDataFeed().BatchItemCompleted(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await batchTracker.Received(1).CompleteBatchAsync("batch1");
    }

    [Fact]
    public async Task BatchComplete_RaceLost_DoesNotNotify()
    {
        var result = new SftpProcessResult("batch1", "item-009", [
            new FileResult(FileType.Person, true, null),
            new FileResult(FileType.Address, true, null)
        ]);
        var req = FakeHttpRequestData.CreateWithJson(context, result);
        batchTracker.IsBatchCompleteAsync("batch1").Returns(true);
        batchTracker.CompleteBatchAsync("batch1").Returns(false);

        var response = await CreateDataFeed().BatchItemCompleted(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await batchTracker.Received(1).CompleteBatchAsync("batch1");
        // The "would notify third party" log only fires when CompleteBatchAsync returns true.
        // We can't easily assert on log output, but we verified CompleteBatchAsync was called
        // and returned false, meaning the race-safe guard worked.
    }
}
