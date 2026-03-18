using System.Net;
using AzFunctions.Tests.Helpers;
using NSubstitute;

namespace AzFunctions.Tests;

public class GenerateBatchTests : IDisposable
{
    private readonly IBatchTracker batchTracker = Substitute.For<IBatchTracker>();
    private readonly IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly FunctionContext context = new FakeFunctionContext(nameof(SftpDataFeed.TriggerDataFeed));

    private readonly string? originalProcessorUrl;
    private readonly string? originalCoordinatorUrl;

    public GenerateBatchTests()
    {
        originalProcessorUrl = Environment.GetEnvironmentVariable("PROCESSOR_BASE_URL");
        originalCoordinatorUrl = Environment.GetEnvironmentVariable("COORDINATOR_BASE_URL");
        Environment.SetEnvironmentVariable("PROCESSOR_BASE_URL", "http://localhost:7071");
        Environment.SetEnvironmentVariable("COORDINATOR_BASE_URL", "http://localhost:7071");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PROCESSOR_BASE_URL", originalProcessorUrl);
        Environment.SetEnvironmentVariable("COORDINATOR_BASE_URL", originalCoordinatorUrl);
    }

    private SftpDataFeed CreateDataFeed() => new(httpClientFactory, batchTracker);

    private static List<PaymentData> CreateTestPayments(int count = 10) =>
        Enumerable.Range(0, count).Select(i =>
            new PaymentData($"pmt-{i:D3}", $"Payor {i}", $"Payee {i}", 1000m + i,
                $"ACCT{i:D6}", $"RTN{i:D6}", $"2026-03-{15 + (i % 15):D2}")).ToList();

    [Fact]
    public async Task SubmitSucceeds_CreatesBatchAndPayments()
    {
        var payments = CreateTestPayments(10);
        batchTracker.GetQueuedPaymentsAsync(Arg.Any<string>()).Returns(payments);

        var handler = new FakeHttpMessageHandler(HttpStatusCode.Accepted);
        var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var req = new FakeHttpRequestData(context);

        var response = await CreateDataFeed().TriggerDataFeed(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await batchTracker.Received(1).CreateBatchAsync(Arg.Any<string>(), 10);
        await batchTracker.Received(10).CreatePaymentAsync(Arg.Any<string>(), Arg.Any<PaymentData>());
        await batchTracker.Received(1).GetQueuedPaymentsAsync(Arg.Any<string>());
        // Successful submit sets status to Processing
        await batchTracker.Received(1).UpdateBatchStatusAsync(Arg.Any<string>(), BatchStatus.Processing);
    }

    [Fact]
    public async Task SubmitFails_MarksBatchAsError()
    {
        var payments = CreateTestPayments(10);
        batchTracker.GetQueuedPaymentsAsync(Arg.Any<string>()).Returns(payments);

        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, failAll: true);
        var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var req = new FakeHttpRequestData(context);

        var response = await CreateDataFeed().TriggerDataFeed(req, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await batchTracker.Received(1).UpdateBatchStatusAsync(Arg.Any<string>(), BatchStatus.Error);
        // Failed submit should not set Processing
        await batchTracker.DidNotReceive().UpdateBatchStatusAsync(Arg.Any<string>(), BatchStatus.Processing);
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
