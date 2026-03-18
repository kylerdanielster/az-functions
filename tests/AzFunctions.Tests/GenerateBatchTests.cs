using System.Net;
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
    public async Task SubmitSucceeds_CreatesBatchFilesAndPayments()
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
        await batchTracker.Received(1).CreateFileAsync(Arg.Any<string>(), FileType.Payment);
        await batchTracker.Received(1).CreateFileAsync(Arg.Any<string>(), FileType.GeneralLedger);
        await batchTracker.Received(10).CreatePaymentAsync(Arg.Any<string>(), Arg.Any<string>());
        // No failures — CompleteBatchFromResultsAsync should not be called
        await batchTracker.DidNotReceive().CompleteBatchFromResultsAsync(Arg.Any<string>(), Arg.Any<List<FileResult>>());
    }

    [Fact]
    public async Task SubmitFails_MarksBothFilesFailed()
    {
        Environment.SetEnvironmentVariable("PROCESSOR_BASE_URL", "http://localhost:7071");
        Environment.SetEnvironmentVariable("COORDINATOR_BASE_URL", "http://localhost:7071");

        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, failAll: true);
        var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var req = new FakeHttpRequestData(context);

        var response = await CreateDataFeed().TriggerDataFeed(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await batchTracker.Received(1).CompleteBatchFromResultsAsync(Arg.Any<string>(),
            Arg.Is<List<FileResult>>(f =>
                f.Count == 2 &&
                !f[0].Succeeded && f[0].FileType == FileType.Payment &&
                !f[1].Succeeded && f[1].FileType == FileType.GeneralLedger));
    }
}

internal class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode defaultStatus;
    private readonly bool failAll;

    public FakeHttpMessageHandler(HttpStatusCode defaultStatus, bool failAll = false)
    {
        this.defaultStatus = defaultStatus;
        this.failAll = failAll;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (failAll)
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
