namespace AzFunctions.Tests;

public class CsvEscapeTests
{
    [Fact]
    public void NullField_ReturnsEmptyString()
    {
        string result = BatchOrchestration.CsvEscape(null!);
        Assert.Equal("", result);
    }

    [Fact]
    public void EmptyField_ReturnsEmptyString()
    {
        string result = BatchOrchestration.CsvEscape("");
        Assert.Equal("", result);
    }

    [Fact]
    public void SimpleField_ReturnedUnchanged()
    {
        string result = BatchOrchestration.CsvEscape("hello");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void FieldWithComma_IsQuoted()
    {
        string result = BatchOrchestration.CsvEscape("Doe, John");
        Assert.Equal("\"Doe, John\"", result);
    }

    [Fact]
    public void FieldWithDoubleQuote_IsEscapedAndQuoted()
    {
        string result = BatchOrchestration.CsvEscape("John \"JD\" Doe");
        Assert.Equal("\"John \"\"JD\"\" Doe\"", result);
    }

    [Fact]
    public void FieldWithNewline_IsQuoted()
    {
        string result = BatchOrchestration.CsvEscape("Line1\nLine2");
        Assert.Equal("\"Line1\nLine2\"", result);
    }

    [Fact]
    public void FieldWithCommaAndQuote_IsEscapedAndQuoted()
    {
        string result = BatchOrchestration.CsvEscape("Doe, \"JD\"");
        Assert.Equal("\"Doe, \"\"JD\"\"\"", result);
    }
}
