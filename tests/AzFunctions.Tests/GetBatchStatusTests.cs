using System.Net;
using Azure.Data.Tables;
using AzFunctions.Tests.Helpers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AzFunctions.Tests;

public class GetBatchStatusTests
{
    private readonly IBatchTracker batchTracker = Substitute.For<IBatchTracker>();
    private readonly IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly FunctionContext context = new FakeFunctionContext(nameof(BatchCoordinator.GetBatchStatus));

    private BatchCoordinator CreateDataFeed() => new(httpClientFactory, batchTracker);

    [Fact]
    public async Task BatchNotFound_Returns404()
    {
        batchTracker.GetBatchAsync("nonexistent").Returns((TableEntity?)null);

        var req = new FakeHttpRequestData(context);

        var response = await CreateDataFeed().GetBatchStatus(req, "nonexistent", context);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BatchFound_Returns200WithStatus()
    {
        var batchEntity = new TableEntity("batch", "batch1")
        {
            ["Status"] = BatchStatus.Processing,
            ["PaymentCount"] = 2,
            ["CreatedAt"] = DateTimeOffset.UtcNow
        };
        batchTracker.GetBatchAsync("batch1").Returns(batchEntity);

        var paymentEntities = new List<TableEntity>
        {
            new("batch1", "pmt-000")
            {
                ["Status"] = BatchStatus.Queued,
                ["CreatedAt"] = DateTimeOffset.UtcNow
            },
            new("batch1", "pmt-001")
            {
                ["Status"] = BatchStatus.Queued,
                ["CreatedAt"] = DateTimeOffset.UtcNow
            }
        };
        batchTracker.GetBatchPaymentsAsync("batch1").Returns(paymentEntities);

        var req = new FakeHttpRequestData(context);

        var response = await CreateDataFeed().GetBatchStatus(req, "batch1", context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BatchFound_PaymentsOrderedByPaymentId()
    {
        var batchEntity = new TableEntity("batch", "batch1")
        {
            ["Status"] = BatchStatus.Queued,
            ["PaymentCount"] = 2,
            ["CreatedAt"] = DateTimeOffset.UtcNow
        };
        batchTracker.GetBatchAsync("batch1").Returns(batchEntity);

        // Return payments out of order
        var paymentEntities = new List<TableEntity>
        {
            new("batch1", "pmt-001")
            {
                ["Status"] = BatchStatus.Queued,
                ["CreatedAt"] = DateTimeOffset.UtcNow
            },
            new("batch1", "pmt-000")
            {
                ["Status"] = BatchStatus.Queued,
                ["CreatedAt"] = DateTimeOffset.UtcNow
            }
        };
        batchTracker.GetBatchPaymentsAsync("batch1").Returns(paymentEntities);

        var req = new FakeHttpRequestData(context);
        var response = (FakeHttpResponseData)await CreateDataFeed().GetBatchStatus(req, "batch1", context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Verify response was written (payments should be ordered)
        string body = response.GetBodyString();
        int pmt000Pos = body.IndexOf("pmt-000");
        int pmt001Pos = body.IndexOf("pmt-001");
        Assert.True(pmt000Pos < pmt001Pos, "Payments should be ordered by PaymentId");
    }

    [Fact]
    public async Task CompletedBatch_IncludesCompletedAt()
    {
        var completedAt = DateTimeOffset.UtcNow;
        var batchEntity = new TableEntity("batch", "batch1")
        {
            ["Status"] = BatchStatus.Processed,
            ["PaymentCount"] = 1,
            ["CreatedAt"] = DateTimeOffset.UtcNow.AddMinutes(-5),
            ["CompletedAt"] = completedAt
        };
        batchTracker.GetBatchAsync("batch1").Returns(batchEntity);
        batchTracker.GetBatchPaymentsAsync("batch1").Returns(new List<TableEntity>());

        var req = new FakeHttpRequestData(context);
        var response = (FakeHttpResponseData)await CreateDataFeed().GetBatchStatus(req, "batch1", context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = response.GetBodyString();
        Assert.Contains("completedAt", body);
    }

    [Fact]
    public async Task TrackerThrows_Returns500()
    {
        batchTracker.GetBatchAsync("batch1").ThrowsAsync(new Exception("Storage unavailable"));

        var req = new FakeHttpRequestData(context);

        var response = await CreateDataFeed().GetBatchStatus(req, "batch1", context);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
