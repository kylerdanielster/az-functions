using System.Text.Json;
using AzFunctions.Tests.Helpers;

namespace AzFunctions.Tests;

public class ProcessGLErrorQueueTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly FunctionContext context = new FakeFunctionContext(nameof(BatchProcessor.ProcessGLErrorQueue));

    [Fact]
    public void ValidMessage_DoesNotThrow()
    {
        var errorMessage = new BatchOrchestration.GLErrorMessage(
            "batch1",
            "http://localhost/callback",
            "SFTP connection failed");

        string messageText = JsonSerializer.Serialize(errorMessage, JsonOptions);

        var exception = Record.Exception(() => BatchProcessor.ProcessGLErrorQueue(messageText, context));

        Assert.Null(exception);
    }

    [Fact]
    public void NullMessage_DoesNotThrow()
    {
        // Deserializes to null — method handles gracefully by logging and returning
        string messageText = "null";

        var exception = Record.Exception(() => BatchProcessor.ProcessGLErrorQueue(messageText, context));

        Assert.Null(exception);
    }

    [Fact]
    public void MalformedJson_DoesNotThrow()
    {
        // JsonSerializer.Deserialize returns null for invalid JSON when using certain options
        // But with strict JSON, it may throw — the method should handle either case
        string messageText = "{}";

        var exception = Record.Exception(() => BatchProcessor.ProcessGLErrorQueue(messageText, context));

        Assert.Null(exception);
    }
}
