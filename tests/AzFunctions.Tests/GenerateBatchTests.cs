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
    public async Task SubmitSucceeds_CreatesBatchAndPayments()
    {
        Environment.SetEnvironmentVariable("PROCESSOR_BASE_URL", "http://localhost:7071");
        Environment.SetEnvironmentVariable("COORDINATOR_BASE_URL", "http://localhost:7071");

        batchTracker.GetQueuedPaymentsAsync(Arg.Any<string>()).Returns(
        [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m, "1234567890", "021000021", "2026-03-15")
        ]);

        var handler = new FakeHttpMessageHandler(HttpStatusCode.Accepted);
        var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var req = new FakeHttpRequestData(context);

        var response = await CreateDataFeed().TriggerDataFeed(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await batchTracker.Received(1).CreateBatchAsync(Arg.Any<string>(), 10);
        await batchTracker.Received(10).CreatePaymentAsync(Arg.Any<string>(), Arg.Any<PaymentData>());
        await batchTracker.Received(1).GetQueuedPaymentsAsync(Arg.Any<string>());
        // No failures — UpdateBatchStatusAsync should not be called
        await batchTracker.DidNotReceive().UpdateBatchStatusAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SubmitFails_MarksBatchAsError()
    {
        Environment.SetEnvironmentVariable("PROCESSOR_BASE_URL", "http://localhost:7071");
        Environment.SetEnvironmentVariable("COORDINATOR_BASE_URL", "http://localhost:7071");

        batchTracker.GetQueuedPaymentsAsync(Arg.Any<string>()).Returns(
        [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m, "1234567890", "021000021", "2026-03-15")
        ]);

        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, failAll: true);
        var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var req = new FakeHttpRequestData(context);

        var response = await CreateDataFeed().TriggerDataFeed(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await batchTracker.Received(1).UpdateBatchStatusAsync(Arg.Any<string>(), BatchStatus.Error);
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
