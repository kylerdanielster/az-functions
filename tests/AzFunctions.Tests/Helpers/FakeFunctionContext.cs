using System.Collections.Immutable;
using System.Text.Json;
using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AzFunctions.Tests.Helpers;

public class FakeFunctionContext : FunctionContext
{
    private readonly FunctionDefinition functionDefinition;

    public FakeFunctionContext(string functionName = "TestFunction")
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.Configure<WorkerOptions>(options =>
        {
            options.Serializer = new JsonObjectSerializer(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            });
        });
        InstanceServices = services.BuildServiceProvider();
        functionDefinition = new FakeFunctionDefinition(functionName);
    }

    public override string InvocationId { get; } = Guid.NewGuid().ToString();
    public override string FunctionId { get; } = Guid.NewGuid().ToString();
    public override TraceContext TraceContext { get; } = null!;
    public override BindingContext BindingContext { get; } = null!;
    public override RetryContext RetryContext { get; } = null!;
    public override IServiceProvider InstanceServices { get; set; }
    public override FunctionDefinition FunctionDefinition => functionDefinition;
    public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();
    public override IInvocationFeatures Features { get; } = null!;
}

internal class FakeFunctionDefinition : FunctionDefinition
{
    public FakeFunctionDefinition(string name)
    {
        Name = name;
    }

    public override string PathToAssembly { get; } = "";
    public override string EntryPoint { get; } = "";
    public override string Id { get; } = Guid.NewGuid().ToString();
    public override string Name { get; }
    public override IImmutableDictionary<string, BindingMetadata> InputBindings { get; } =
        ImmutableDictionary<string, BindingMetadata>.Empty;
    public override IImmutableDictionary<string, BindingMetadata> OutputBindings { get; } =
        ImmutableDictionary<string, BindingMetadata>.Empty;
    public override ImmutableArray<FunctionParameter> Parameters { get; } = [];
}
