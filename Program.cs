using Azure.Data.Tables;
using Company.Function;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddHttpClient()
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services.AddSingleton<IBatchTracker>(sp =>
{
    string connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
        ?? throw new InvalidOperationException("AzureWebJobsStorage not configured.");
    var tableClient = new TableClient(connectionString, "BatchTracking");
    tableClient.CreateIfNotExists();
    return new TableBatchTracker(tableClient);
});

var app = builder.Build();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine();
    Console.WriteLine("  Trigger data feed:");
    Console.WriteLine("    curl -s -X POST http://localhost:7071/api/datafeed/trigger | python3 -m json.tool");
    Console.WriteLine();
    Console.WriteLine("  Check batch status:");
    Console.WriteLine("    curl -s http://localhost:7071/api/batch/{batchId} | python3 -m json.tool");
    Console.WriteLine();
    Console.WriteLine("  Run E2E test:");
    Console.WriteLine("    ./test-sftp-orchestration.sh");
    Console.WriteLine();
});

app.Run();
