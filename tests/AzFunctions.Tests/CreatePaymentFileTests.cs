using AzFunctions.Tests.Helpers;
using NSubstitute;

namespace AzFunctions.Tests;

public class CreatePaymentFileTests
{
    private readonly IBatchPaymentStore batchPaymentStore = Substitute.For<IBatchPaymentStore>();
    private readonly FunctionContext context = new FakeFunctionContext(nameof(BatchOrchestration.CreatePaymentFile));

    private BatchOrchestration CreateOrchestration() => new(
        Substitute.For<IHttpClientFactory>(),
        Substitute.For<ISftpClientFactory>(),
        Substitute.For<IGLErrorQueue>(),
        batchPaymentStore);

    private void SetupPayments(params PaymentData[] payments)
    {
        batchPaymentStore.GetPaymentsAsync("batch1").Returns(payments.ToList());
    }

    [Fact]
    public async Task SinglePayment_ProducesCorrectCsv()
    {
        SetupPayments(new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
            "1234567890", "021000021", "2026-03-15"));

        string csv = await CreateOrchestration().CreatePaymentFile("batch1", context);

        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("PaymentId,PayorName,PayeeName,Amount,AccountNumber,RoutingNumber,PaymentDate", lines[0]);
        Assert.Equal("pmt-000,John Doe,Acme Corp,1500.00,1234567890,021000021,2026-03-15", lines[1]);
    }

    [Fact]
    public async Task MultiplePayments_ProducesOneRowPerPayment()
    {
        SetupPayments(
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m, "1234567890", "021000021", "2026-03-15"),
            new PaymentData("pmt-001", "Jane Smith", "Globex Inc", 2750.50m, "9876543210", "021000089", "2026-03-14"));

        string csv = await CreateOrchestration().CreatePaymentFile("batch1", context);

        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // header + 2 data rows
    }

    [Fact]
    public async Task EmptyPayments_ProducesHeaderOnly()
    {
        SetupPayments();

        string csv = await CreateOrchestration().CreatePaymentFile("batch1", context);

        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Equal("PaymentId,PayorName,PayeeName,Amount,AccountNumber,RoutingNumber,PaymentDate", lines[0]);
    }

    [Fact]
    public async Task IncludesAccountAndRoutingNumbers()
    {
        SetupPayments(new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
            "SENSITIVE_ACCT", "SENSITIVE_RTN", "2026-03-15"));

        string csv = await CreateOrchestration().CreatePaymentFile("batch1", context);

        Assert.Contains("SENSITIVE_ACCT", csv);
        Assert.Contains("SENSITIVE_RTN", csv);
    }

    [Fact]
    public async Task FieldWithComma_IsQuoted()
    {
        SetupPayments(new PaymentData("pmt-000", "Doe, John", "Acme Corp", 1500.00m,
            "1234567890", "021000021", "2026-03-15"));

        string csv = await CreateOrchestration().CreatePaymentFile("batch1", context);

        Assert.Contains("\"Doe, John\"", csv);
    }

    [Fact]
    public async Task FieldWithDoubleQuotes_IsEscaped()
    {
        SetupPayments(new PaymentData("pmt-000", "John \"JD\" Doe", "Acme Corp", 1500.00m,
            "1234567890", "021000021", "2026-03-15"));

        string csv = await CreateOrchestration().CreatePaymentFile("batch1", context);

        Assert.Contains("\"John \"\"JD\"\" Doe\"", csv);
    }

    [Fact]
    public async Task FieldWithNewline_IsQuoted()
    {
        SetupPayments(new PaymentData("pmt-000", "John\nDoe", "Acme Corp", 1500.00m,
            "1234567890", "021000021", "2026-03-15"));

        string csv = await CreateOrchestration().CreatePaymentFile("batch1", context);

        Assert.Contains("\"John\nDoe\"", csv);
    }

    [Fact]
    public async Task AmountFormatted_TwoDecimalPlaces()
    {
        SetupPayments(new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500m,
            "1234567890", "021000021", "2026-03-15"));

        string csv = await CreateOrchestration().CreatePaymentFile("batch1", context);

        Assert.Contains("1500.00", csv);
    }
}
