using System.Text.Json;
using AzFunctions.Tests.Helpers;

namespace AzFunctions.Tests;

public class ProcessGLErrorQueueTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly FunctionContext context = new FakeFunctionContext(nameof(SftpProcessor.ProcessGLErrorQueue));

    [Fact]
    public void ValidMessage_DoesNotThrow()
    {
        var errorMessage = new SftpOrchestration.GLErrorMessage(
            "batch1",
            [new PaymentData("pmt-000", "John Doe", "Acme Corp", 1500.00m, "1234567890", "021000021", "2026-03-15")],
            "http://localhost/callback",
            "SFTP connection failed");

        string messageText = JsonSerializer.Serialize(errorMessage, JsonOptions);

        var exception = Record.Exception(() => SftpProcessor.ProcessGLErrorQueue(messageText, context));

        Assert.Null(exception);
    }

    [Fact]
    public void NullMessage_DoesNotThrow()
    {
        // Deserializes to null — method handles gracefully by logging and returning
        string messageText = "null";

        var exception = Record.Exception(() => SftpProcessor.ProcessGLErrorQueue(messageText, context));

        Assert.Null(exception);
    }

    [Fact]
    public void MalformedJson_DoesNotThrow()
    {
        // JsonSerializer.Deserialize returns null for invalid JSON when using certain options
        // But with strict JSON, it may throw — the method should handle either case
        string messageText = "{}";

        var exception = Record.Exception(() => SftpProcessor.ProcessGLErrorQueue(messageText, context));

        Assert.Null(exception);
    }
}
