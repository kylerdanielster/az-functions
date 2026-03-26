using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AzFunctions;

/// <summary>
/// TEST/DEBUG ENDPOINTS: SFTP server inspection.
/// Lists, reads, and deletes files on the SFTP server. These endpoints interact directly
/// with the SFTP server via <see cref="ISftpClientFactory"/> and are not part of the batch processing pipeline.
/// </summary>
public class SftpEndpoints(ISftpClientFactory sftpClientFactory)
{
    // TEST/DEBUG ENDPOINT — Deletes all files from the SFTP server's remote upload directory.
    [Function("Sftp_DeleteAllFiles")]
    public async Task<HttpResponseData> DeleteAllFiles(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sftp/files")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("Sftp_DeleteAllFiles");

        try
        {
            using var client = await sftpClientFactory.CreateConnectedClientAsync();

            var files = client.ListDirectory(sftpClientFactory.RemotePath)
                .Where(f => !f.IsDirectory)
                .ToList();

            foreach (var file in files)
            {
                client.DeleteFile($"{sftpClientFactory.RemotePath}/{file.Name}");
            }

            logger.LogInformation("[SFTP] Deleted {count} files from SFTP server.", files.Count);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { deleted = files.Count });
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SFTP] Failed to delete files from SFTP server.");
            var error = req.CreateResponse(HttpStatusCode.InternalServerError);
            await error.WriteStringAsync("Failed to delete files from SFTP server.");
            return error;
        }
    }

    // TEST/DEBUG ENDPOINT — Lists all files on the SFTP server with name, size, and last modified time.
    [Function("Sftp_ListFiles")]
    public async Task<HttpResponseData> ListFiles(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sftp/files")] HttpRequestData req,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("Sftp_ListFiles");

        try
        {
            using var client = await sftpClientFactory.CreateConnectedClientAsync();

            var files = client.ListDirectory(sftpClientFactory.RemotePath)
                .Where(f => !f.IsDirectory)
                .Select(f => new { name = f.Name, size = f.Length, modified = f.LastWriteTime })
                .ToList();

            logger.LogInformation("[SFTP] Listed {count} files on SFTP server.", files.Count);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(files);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SFTP] Failed to list files on SFTP server.");
            var error = req.CreateResponse(HttpStatusCode.InternalServerError);
            await error.WriteStringAsync("Failed to list files from SFTP server.");
            return error;
        }
    }

    // TEST/DEBUG ENDPOINT — Returns the contents of a specific file from the SFTP server.
    [Function("Sftp_GetFile")]
    public async Task<HttpResponseData> GetFile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sftp/files/{fileName}")] HttpRequestData req,
        string fileName,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("Sftp_GetFile");

        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid file name.");
            return badRequest;
        }

        try
        {
            string remoteFilePath = $"{sftpClientFactory.RemotePath}/{fileName}";

            using var client = await sftpClientFactory.CreateConnectedClientAsync();

            if (!client.Exists(remoteFilePath))
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync($"File not found: {fileName}");
                return notFound;
            }

            using var memoryStream = new MemoryStream();
            client.DownloadFile(remoteFilePath, memoryStream);

            string content = System.Text.Encoding.UTF8.GetString(memoryStream.ToArray());
            logger.LogInformation("[SFTP] Read file {fileName} ({length} bytes) from SFTP server.", fileName, content.Length);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/plain");
            await response.WriteStringAsync(content);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SFTP] Failed to read file {fileName} from SFTP server.", fileName);
            var error = req.CreateResponse(HttpStatusCode.InternalServerError);
            await error.WriteStringAsync("Failed to read file from SFTP server.");
            return error;
        }
    }
}
