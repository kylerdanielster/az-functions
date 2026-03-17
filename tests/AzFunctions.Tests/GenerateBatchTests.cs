using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AzFunctions.Tests.Helpers;
using NSubstitute;

namespace AzFunctions.Tests;

public class GenerateBatchTests
{
    private readonly IBatchTracker batchTracker = Substitute.For<IBatchTracker>();
    private readonly IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly FunctionContext context = new FakeFunctionContext(nameof(SftpDataFeed.TriggerDataFeed));

    private SftpDataFeed CreateDataFeed() => new(httpClientFactory, batchTracker);

    [Fact]
    public async Task AllItemsSucceed_Creates10Items()
    {
        Environment.SetEnvironmentVariable("PROCESSOR_BASE_URL", "http://localhost:7071");
        Environment.SetEnvironmentVariable("COORDINATOR_BASE_URL", "http://localhost:7071");

        var handler = new FakeHttpMessageHandler(HttpStatusCode.Accepted);
        var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var req = new FakeHttpRequestData(context);

        var response = await CreateDataFeed().TriggerDataFeed(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await batchTracker.Received(1).CreateBatchAsync(Arg.Any<string>(), 10);
        await batchTracker.Received(10).CreateItemAsync(Arg.Any<string>(), Arg.Any<string>());
        await batchTracker.Received(20).CreateFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        // No failures — CompleteBatchAsync should not be called during submission
        await batchTracker.DidNotReceive().CompleteBatchAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task MidBatchFailure_MarksFailedFilesAndContinues()
    {
        Environment.SetEnvironmentVariable("PROCESSOR_BASE_URL", "http://localhost:7071");
        Environment.SetEnvironmentVariable("COORDINATOR_BASE_URL", "http://localhost:7071");

        // First 3 succeed, 4th fails, rest succeed
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Accepted, failOnRequest: 3);
        var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var req = new FakeHttpRequestData(context);

        var response = await CreateDataFeed().TriggerDataFeed(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The failed item should have its files marked as Failed
        await batchTracker.Received(1).UpdateFileStatusAsync(
            Arg.Any<string>(), "item-003", FileType.Person, BatchStatus.Failed, "Submission failed");
        await batchTracker.Received(1).UpdateFileStatusAsync(
            Arg.Any<string>(), "item-003", FileType.Address, BatchStatus.Failed, "Submission failed");
        await batchTracker.Received(1).UpdateItemFromFilesAsync(Arg.Any<string>(), "item-003");
        // Batch should NOT be completed since 9 items succeeded
        await batchTracker.DidNotReceive().CompleteBatchAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task AllItemsFail_CompletesBatchImmediately()
    {
        Environment.SetEnvironmentVariable("PROCESSOR_BASE_URL", "http://localhost:7071");
        Environment.SetEnvironmentVariable("COORDINATOR_BASE_URL", "http://localhost:7071");

        // All requests fail
        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, failAll: true);
        var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        batchTracker.CompleteBatchAsync(Arg.Any<string>()).Returns(true);

        var req = new FakeHttpRequestData(context);

        var response = await CreateDataFeed().TriggerDataFeed(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // All 10 items should have failed files
        await batchTracker.Received(10).UpdateFileStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), FileType.Person, BatchStatus.Failed, "Submission failed");
        await batchTracker.Received(10).UpdateFileStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), FileType.Address, BatchStatus.Failed, "Submission failed");
        // Batch should be completed immediately since zero succeeded
        await batchTracker.Received(1).CompleteBatchAsync(Arg.Any<string>());
    }
}

internal class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode defaultStatus;
    private readonly int? failOnRequest;
    private readonly bool failAll;
    private int requestCount;

    public FakeHttpMessageHandler(HttpStatusCode defaultStatus, int? failOnRequest = null, bool failAll = false)
    {
        this.defaultStatus = defaultStatus;
        this.failOnRequest = failOnRequest;
        this.failAll = failAll;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int current = requestCount++;

        if (failAll || current == failOnRequest)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Server error")
            });
        }

        return Task.FromResult(new HttpResponseMessage(defaultStatus)
        {
            Content = new StringContent("{}")
        });
    }
}
