using AzFunctions.Tests.Helpers;

namespace AzFunctions.Tests;

public class CreateGLFileTests
{
    private readonly FunctionContext context = new FakeFunctionContext(nameof(SftpOrchestration.CreateGLFile));

    [Fact]
    public void SinglePayment_ProducesCorrectCsv()
    {
        var input = new SftpOrchestration.CreateGLFileInput("batch1",
        [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
                "1234567890", "021000021", "2026-03-15")
        ]);

        string csv = SftpOrchestration.CreateGLFile(input, context);

        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("PaymentId,PayorName,PayeeName,Amount,PaymentDate", lines[0]);
        Assert.Equal("pmt-000,John Doe,Acme Corp,1500.00,2026-03-15", lines[1]);
    }

    [Fact]
    public void ExcludesAccountNumber()
    {
        var input = new SftpOrchestration.CreateGLFileInput("batch1",
        [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
                "SENSITIVE_ACCT", "SENSITIVE_RTN", "2026-03-15")
        ]);

        string csv = SftpOrchestration.CreateGLFile(input, context);

        Assert.DoesNotContain("SENSITIVE_ACCT", csv);
    }

    [Fact]
    public void ExcludesRoutingNumber()
    {
        var input = new SftpOrchestration.CreateGLFileInput("batch1",
        [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m,
                "SENSITIVE_ACCT", "SENSITIVE_RTN", "2026-03-15")
        ]);

        string csv = SftpOrchestration.CreateGLFile(input, context);

        Assert.DoesNotContain("SENSITIVE_RTN", csv);
    }

    [Fact]
    public void HeaderDoesNotContainSensitiveColumns()
    {
        var input = new SftpOrchestration.CreateGLFileInput("batch1", []);

        string csv = SftpOrchestration.CreateGLFile(input, context);

        var header = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.DoesNotContain("AccountNumber", header);
        Assert.DoesNotContain("RoutingNumber", header);
    }

    [Fact]
    public void MultiplePayments_ProducesOneRowPerPayment()
    {
        var input = new SftpOrchestration.CreateGLFileInput("batch1",
        [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m, "1234567890", "021000021", "2026-03-15"),
            new PaymentData("pmt-001", "Jane Smith", "Globex Inc", 2750.50m, "9876543210", "021000089", "2026-03-14")
        ]);

        string csv = SftpOrchestration.CreateGLFile(input, context);

        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void EmptyPayments_ProducesHeaderOnly()
    {
        var input = new SftpOrchestration.CreateGLFileInput("batch1", []);

        string csv = SftpOrchestration.CreateGLFile(input, context);

        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Equal("PaymentId,PayorName,PayeeName,Amount,PaymentDate", lines[0]);
    }

    [Fact]
    public void FieldWithComma_IsQuoted()
    {
        var input = new SftpOrchestration.CreateGLFileInput("batch1",
        [
            new PaymentData("pmt-000", "Doe, John", "Acme Corp", 1500.00m,
                "1234567890", "021000021", "2026-03-15")
        ]);

        string csv = SftpOrchestration.CreateGLFile(input, context);

        Assert.Contains("\"Doe, John\"", csv);
    }

    [Fact]
    public void AmountFormatted_TwoDecimalPlaces()
    {
        var input = new SftpOrchestration.CreateGLFileInput("batch1",
        [
            new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500m,
                "1234567890", "021000021", "2026-03-15")
        ]);

        string csv = SftpOrchestration.CreateGLFile(input, context);

        Assert.Contains("1500.00", csv);
    }
}
