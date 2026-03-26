using Azure.Data.Tables;
using Azure.Storage.Queues;
using AzFunctions;
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

builder.Services.AddSingleton<ISftpClientFactory, SftpClientFactory>();

builder.Services.AddSingleton<IBatchTracker>(sp =>
{
    string connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
        ?? throw new InvalidOperationException("AzureWebJobsStorage not configured.");
    var tableClient = new TableClient(connectionString, "BatchTracking");
    tableClient.CreateIfNotExists();
    return new TableBatchTracker(tableClient);
});

builder.Services.AddSingleton<IMessageQueue>(sp =>
{
    string connectionString = Environment.GetEnvironmentVariable("BatchStorageConnection")
        ?? throw new InvalidOperationException("BatchStorageConnection not configured.");
    var queueClient = new QueueClient(connectionString, BatchProcessor.QueueName, new QueueClientOptions
    {
        MessageEncoding = QueueMessageEncoding.Base64
    });
    queueClient.CreateIfNotExists();
    return new StorageQueueClient(queueClient);
});

builder.Services.AddSingleton<IGLErrorQueue>(sp =>
{
    string connectionString = Environment.GetEnvironmentVariable("BatchStorageConnection")
        ?? throw new InvalidOperationException("BatchStorageConnection not configured.");
    var queueClient = new QueueClient(connectionString, BatchProcessor.GLErrorQueueName, new QueueClientOptions
    {
        MessageEncoding = QueueMessageEncoding.Base64
    });
    queueClient.CreateIfNotExists();
    return new GLErrorQueueClient(queueClient);
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
    Console.WriteLine("    ./test-batch-orchestration.sh");
    Console.WriteLine();
});

app.Run();
