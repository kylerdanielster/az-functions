using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace Company.Function;

public record PersonData(string FirstName, string LastName, string DateOfBirth);
public record AddressData(string Street, string City, string State, string ZipCode);
public record CreateFileInput<T>(string InstanceId, T Data);

public static class SftpOrchestration
{
    private const string PersonReceivedEvent = "PersonReceived";
    private const string AddressReceivedEvent = "AddressReceived";
    private static readonly TimeSpan SftpTimeout = TimeSpan.FromSeconds(30);
    [Function(nameof(SftpOrchestration))]
    public static async Task<string> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ILogger logger = context.CreateReplaySafeLogger(nameof(SftpOrchestration));
        string id = context.InstanceId;

        logger.LogInformation("[SFTP] Orchestration {id} started — waiting for person and address data.", id);
        var personTask = context.WaitForExternalEvent<PersonData>(PersonReceivedEvent);
        var addressTask = context.WaitForExternalEvent<AddressData>(AddressReceivedEvent);
        await Task.WhenAll(personTask, addressTask);

        PersonData person = personTask.Result;
        AddressData address = addressTask.Result;
        logger.LogInformation("[SFTP] Orchestration {id} — received both person ({first} {last}) and address ({street}, {city}).",
            id, person.FirstName, person.LastName, address.Street, address.City);

        // Create both files in parallel
        logger.LogInformation("[SFTP] Orchestration {id} — creating files...", id);
        var personFileTask = context.CallActivityAsync<string>(nameof(CreatePersonFile), new CreateFileInput<PersonData>(id, person));
        var addressFileTask = context.CallActivityAsync<string>(nameof(CreateAddressFile), new CreateFileInput<AddressData>(id, address));
        await Task.WhenAll(personFileTask, addressFileTask);

        string personFilePath = personFileTask.Result;
        string addressFilePath = addressFileTask.Result;

        // Upload only after both files are ready
        logger.LogInformation("[SFTP] Orchestration {id} — uploading 2 files to SFTP server...", id);
        string result = await context.CallActivityAsync<string>(
            nameof(UploadFiles), new[] { personFilePath, addressFilePath });

        logger.LogInformation("[SFTP] Orchestration {id} — complete!", id);
        return result;
    }

    [Function(nameof(CreatePersonFile))]
    public static string CreatePersonFile([ActivityTrigger] CreateFileInput<PersonData> input, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(CreatePersonFile));
        var person = input.Data;
        string path = Path.Combine(Path.GetTempPath(), $"person_{input.InstanceId}.txt");
        string content = $"First Name: {person.FirstName}\nLast Name: {person.LastName}\nDate of Birth: {person.DateOfBirth}";
        File.WriteAllText(path, content);
        logger.LogInformation("[SFTP] Created person file at {path}.", path);
        return path;
    }

    [Function(nameof(CreateAddressFile))]
    public static string CreateAddressFile([ActivityTrigger] CreateFileInput<AddressData> input, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(CreateAddressFile));
        var address = input.Data;
        string path = Path.Combine(Path.GetTempPath(), $"address_{input.InstanceId}.txt");
        string content = $"Street: {address.Street}\nCity: {address.City}\nState: {address.State}\nZip Code: {address.ZipCode}";
        File.WriteAllText(path, content);
        logger.LogInformation("[SFTP] Created address file at {path}.", path);
        return path;
    }

    [Function(nameof(UploadFiles))]
    public static string UploadFiles([ActivityTrigger] string[] filePaths, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(UploadFiles));

        string host = Environment.GetEnvironmentVariable("SFTP_HOST")
            ?? throw new InvalidOperationException("SFTP_HOST not configured.");
        int port = int.Parse(Environment.GetEnvironmentVariable("SFTP_PORT") ?? "22");
        string username = Environment.GetEnvironmentVariable("SFTP_USERNAME")
            ?? throw new InvalidOperationException("SFTP_USERNAME not configured.");
        string password = Environment.GetEnvironmentVariable("SFTP_PASSWORD")
            ?? throw new InvalidOperationException("SFTP_PASSWORD not configured.");
        string remotePath = Environment.GetEnvironmentVariable("SFTP_REMOTE_PATH") ?? "/upload";

        using var client = new SftpClient(host, port, username, password);
        client.OperationTimeout = SftpTimeout;
        client.Connect();
        logger.LogInformation("[SFTP] Connected to SFTP server {host}:{port}.", host, port);

        foreach (string localPath in filePaths)
        {
            string remoteFilePath = $"{remotePath}/{Path.GetFileName(localPath)}";
            using var fileStream = File.OpenRead(localPath);
            client.UploadFile(fileStream, remoteFilePath);
            logger.LogInformation("[SFTP] Uploaded {fileName} to {remote}.", Path.GetFileName(localPath), remoteFilePath);
        }

        foreach (string localPath in filePaths)
        {
            try { File.Delete(localPath); }
            catch (Exception ex) { logger.LogWarning(ex, "[SFTP] Failed to delete temp file {path}.", localPath); }
        }

        return $"Uploaded {filePaths.Length} files to {host}:{remotePath}.";
    }

    // --- HTTP Triggers ---

    [Function("SftpOrchestration_Start")]
    public static async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sftp/start")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("SftpOrchestration_Start");
        string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(SftpOrchestration));
        logger.LogInformation("[SFTP] Orchestration {id} created.", instanceId);
        return await client.CreateCheckStatusResponseAsync(req, instanceId);
    }

    [Function("SftpOrchestration_Person")]
    public static async Task<HttpResponseData> SubmitPerson(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sftp/person/{instanceId}")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient client,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("SftpOrchestration_Person");
        var person = await req.ReadFromJsonAsync<PersonData>();
        if (person is null)
        {
            var badRequest = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid or missing person data.");
            return badRequest;
        }

        await client.RaiseEventAsync(instanceId, PersonReceivedEvent, person);
        logger.LogInformation("[SFTP] Orchestration {id} — person data received ({first} {last}).",
            instanceId, person.FirstName, person.LastName);

        var response = req.CreateResponse(System.Net.HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new { instanceId, @event = "PersonReceived" });
        return response;
    }

    [Function("SftpOrchestration_Address")]
    public static async Task<HttpResponseData> SubmitAddress(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sftp/address/{instanceId}")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient client,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("SftpOrchestration_Address");
        var address = await req.ReadFromJsonAsync<AddressData>();
        if (address is null)
        {
            var badRequest = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid or missing address data.");
            return badRequest;
        }

        await client.RaiseEventAsync(instanceId, AddressReceivedEvent, address);
        logger.LogInformation("[SFTP] Orchestration {id} — address data received ({street}, {city}).",
            instanceId, address.Street, address.City);

        var response = req.CreateResponse(System.Net.HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new { instanceId, @event = "AddressReceived" });
        return response;
    }

    // --- SFTP Server Inspection Endpoints ---

    [Function("SftpOrchestration_ListFiles")]
    public static async Task<HttpResponseData> ListFiles(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sftp/files")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("SftpOrchestration_ListFiles");

        string host = Environment.GetEnvironmentVariable("SFTP_HOST")
            ?? throw new InvalidOperationException("SFTP_HOST not configured.");
        int port = int.Parse(Environment.GetEnvironmentVariable("SFTP_PORT") ?? "22");
        string username = Environment.GetEnvironmentVariable("SFTP_USERNAME")
            ?? throw new InvalidOperationException("SFTP_USERNAME not configured.");
        string password = Environment.GetEnvironmentVariable("SFTP_PASSWORD")
            ?? throw new InvalidOperationException("SFTP_PASSWORD not configured.");
        string remotePath = Environment.GetEnvironmentVariable("SFTP_REMOTE_PATH") ?? "/upload";

        using var client = new SftpClient(host, port, username, password);
        client.OperationTimeout = SftpTimeout;
        client.Connect();

        var files = client.ListDirectory(remotePath)
            .Where(f => !f.IsDirectory)
            .Select(f => new { name = f.Name, size = f.Length, modified = f.LastWriteTime })
            .ToList();

        logger.LogInformation("[SFTP] Listed {count} files on SFTP server.", files.Count);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await response.WriteAsJsonAsync(files);
        return response;
    }

    [Function("SftpOrchestration_GetFile")]
    public static async Task<HttpResponseData> GetFile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sftp/files/{fileName}")] HttpRequestData req,
        string fileName,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("SftpOrchestration_GetFile");

        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
        {
            var badRequest = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid file name.");
            return badRequest;
        }

        string host = Environment.GetEnvironmentVariable("SFTP_HOST")
            ?? throw new InvalidOperationException("SFTP_HOST not configured.");
        int port = int.Parse(Environment.GetEnvironmentVariable("SFTP_PORT") ?? "22");
        string username = Environment.GetEnvironmentVariable("SFTP_USERNAME")
            ?? throw new InvalidOperationException("SFTP_USERNAME not configured.");
        string password = Environment.GetEnvironmentVariable("SFTP_PASSWORD")
            ?? throw new InvalidOperationException("SFTP_PASSWORD not configured.");
        string remotePath = Environment.GetEnvironmentVariable("SFTP_REMOTE_PATH") ?? "/upload";

        string remoteFilePath = $"{remotePath}/{fileName}";

        using var client = new SftpClient(host, port, username, password);
        client.OperationTimeout = SftpTimeout;
        client.Connect();

        if (!client.Exists(remoteFilePath))
        {
            var notFound = req.CreateResponse(System.Net.HttpStatusCode.NotFound);
            await notFound.WriteStringAsync($"File not found: {fileName}");
            return notFound;
        }

        using var memoryStream = new MemoryStream();
        client.DownloadFile(remoteFilePath, memoryStream);

        string content = System.Text.Encoding.UTF8.GetString(memoryStream.ToArray());
        logger.LogInformation("[SFTP] Read file {fileName} ({length} bytes) from SFTP server.", fileName, content.Length);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "text/plain");
        await response.WriteStringAsync(content);
        return response;
    }
}
