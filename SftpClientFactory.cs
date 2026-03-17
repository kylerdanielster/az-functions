using Renci.SshNet;

namespace AzFunctions;

/// <summary>
/// Creates connected SFTP clients using configuration from environment variables.
/// Used by the SFTP Processor (App 2) for file upload and server inspection.
/// </summary>
public interface ISftpClientFactory
{
    string RemotePath { get; }
    SftpClient CreateConnectedClient();
}

/// <summary>
/// Reads SFTP connection settings (host, port, username, password, remote path) from environment
/// variables at startup and creates connected <see cref="SftpClient"/> instances on demand.
/// Registered as a singleton in DI. Part of the SFTP Processor (App 2).
/// </summary>
public class SftpClientFactory : ISftpClientFactory
{
    private static readonly TimeSpan SftpTimeout = TimeSpan.FromSeconds(30);

    private readonly string host;
    private readonly int port;
    private readonly string username;
    private readonly string password;
    private readonly string remotePath;

    public string RemotePath => remotePath;

    public SftpClientFactory()
    {
        host = Environment.GetEnvironmentVariable("SFTP_HOST")
            ?? throw new InvalidOperationException("SFTP_HOST not configured.");

        string portValue = Environment.GetEnvironmentVariable("SFTP_PORT") ?? "22";
        if (!int.TryParse(portValue, out port))
            throw new InvalidOperationException($"SFTP_PORT is not a valid integer: '{portValue}'.");

        username = Environment.GetEnvironmentVariable("SFTP_USERNAME")
            ?? throw new InvalidOperationException("SFTP_USERNAME not configured.");
        password = Environment.GetEnvironmentVariable("SFTP_PASSWORD")
            ?? throw new InvalidOperationException("SFTP_PASSWORD not configured.");
        remotePath = Environment.GetEnvironmentVariable("SFTP_REMOTE_PATH") ?? "/upload";
    }

    public SftpClient CreateConnectedClient()
    {
        var client = new SftpClient(host, port, username, password);
        client.OperationTimeout = SftpTimeout;
        client.Connect();
        return client;
    }
}
