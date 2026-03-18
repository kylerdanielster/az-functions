using System.Net;
using AzFunctions.Tests.Helpers;
using NSubstitute;

namespace AzFunctions.Tests;

public class BatchCompletedTests
{
    private readonly IBatchTracker batchTracker = Substitute.For<IBatchTracker>();
    private readonly IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly FunctionContext context = new FakeFunctionContext(nameof(SftpDataFeed.BatchCompleted));

    private SftpDataFeed CreateDataFeed() => new(httpClientFactory, batchTracker);

    [Fact]
    public async Task ValidCallback_CallsCompleteBatchFromResults()
    {
        var result = new SftpBatchResult("batch1", [
            new FileResult(FileType.Person, true, null),
            new FileResult(FileType.Address, true, null)
        ]);
        var req = FakeHttpRequestData.CreateWithJson(context, result);

        var response = await CreateDataFeed().BatchCompleted(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await batchTracker.Received(1).CompleteBatchFromResultsAsync("batch1",
            Arg.Is<List<FileResult>>(f => f.Count == 2));
    }

    [Fact]
    public async Task NullRequest_Returns400()
    {
        var req = new FakeHttpRequestData(context, "null");

        var response = await CreateDataFeed().BatchCompleted(req, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PartialFailure_CallsCompleteBatchFromResults()
    {
        var result = new SftpBatchResult("batch1", [
            new FileResult(FileType.Person, true, null),
            new FileResult(FileType.Address, false, "SFTP connection failed")
        ]);
        var req = FakeHttpRequestData.CreateWithJson(context, result);

        var response = await CreateDataFeed().BatchCompleted(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await batchTracker.Received(1).CompleteBatchFromResultsAsync("batch1",
            Arg.Is<List<FileResult>>(f => f.Count == 2 && !f[1].Succeeded));
    }
}
