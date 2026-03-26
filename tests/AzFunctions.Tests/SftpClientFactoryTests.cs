namespace AzFunctions.Tests;

public class SftpClientFactoryTests : IDisposable
{
    private readonly string? originalHost;
    private readonly string? originalPort;
    private readonly string? originalUsername;
    private readonly string? originalPassword;
    private readonly string? originalRemotePath;

    public SftpClientFactoryTests()
    {
        originalHost = Environment.GetEnvironmentVariable("SFTP_HOST");
        originalPort = Environment.GetEnvironmentVariable("SFTP_PORT");
        originalUsername = Environment.GetEnvironmentVariable("SFTP_USERNAME");
        originalPassword = Environment.GetEnvironmentVariable("SFTP_PASSWORD");
        originalRemotePath = Environment.GetEnvironmentVariable("SFTP_REMOTE_PATH");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SFTP_HOST", originalHost);
        Environment.SetEnvironmentVariable("SFTP_PORT", originalPort);
        Environment.SetEnvironmentVariable("SFTP_USERNAME", originalUsername);
        Environment.SetEnvironmentVariable("SFTP_PASSWORD", originalPassword);
        Environment.SetEnvironmentVariable("SFTP_REMOTE_PATH", originalRemotePath);
    }

    private void SetAllRequired()
    {
        Environment.SetEnvironmentVariable("SFTP_HOST", "localhost");
        Environment.SetEnvironmentVariable("SFTP_PORT", "2222");
        Environment.SetEnvironmentVariable("SFTP_USERNAME", "testuser");
        Environment.SetEnvironmentVariable("SFTP_PASSWORD", "testpass");
    }

    [Fact]
    public void MissingHost_Throws()
    {
        Environment.SetEnvironmentVariable("SFTP_HOST", null);
        Environment.SetEnvironmentVariable("SFTP_PORT", "22");
        Environment.SetEnvironmentVariable("SFTP_USERNAME", "user");
        Environment.SetEnvironmentVariable("SFTP_PASSWORD", "pass");

        var ex = Assert.Throws<InvalidOperationException>(() => new SftpClientFactory());
        Assert.Contains("SFTP_HOST", ex.Message);
    }

    [Fact]
    public void MissingUsername_Throws()
    {
        Environment.SetEnvironmentVariable("SFTP_HOST", "localhost");
        Environment.SetEnvironmentVariable("SFTP_PORT", "22");
        Environment.SetEnvironmentVariable("SFTP_USERNAME", null);
        Environment.SetEnvironmentVariable("SFTP_PASSWORD", "pass");

        var ex = Assert.Throws<InvalidOperationException>(() => new SftpClientFactory());
        Assert.Contains("SFTP_USERNAME", ex.Message);
    }

    [Fact]
    public void MissingPassword_Throws()
    {
        Environment.SetEnvironmentVariable("SFTP_HOST", "localhost");
        Environment.SetEnvironmentVariable("SFTP_PORT", "22");
        Environment.SetEnvironmentVariable("SFTP_USERNAME", "user");
        Environment.SetEnvironmentVariable("SFTP_PASSWORD", null);

        var ex = Assert.Throws<InvalidOperationException>(() => new SftpClientFactory());
        Assert.Contains("SFTP_PASSWORD", ex.Message);
    }

    [Fact]
    public void InvalidPort_Throws()
    {
        Environment.SetEnvironmentVariable("SFTP_HOST", "localhost");
        Environment.SetEnvironmentVariable("SFTP_PORT", "not-a-number");
        Environment.SetEnvironmentVariable("SFTP_USERNAME", "user");
        Environment.SetEnvironmentVariable("SFTP_PASSWORD", "pass");

        var ex = Assert.Throws<InvalidOperationException>(() => new SftpClientFactory());
        Assert.Contains("SFTP_PORT", ex.Message);
    }

    [Fact]
    public void DefaultRemotePath_UsedWhenNotSet()
    {
        SetAllRequired();
        Environment.SetEnvironmentVariable("SFTP_REMOTE_PATH", null);

        var factory = new SftpClientFactory();

        Assert.Equal("/upload", factory.RemotePath);
    }

    [Fact]
    public void CustomRemotePath_UsedWhenSet()
    {
        SetAllRequired();
        Environment.SetEnvironmentVariable("SFTP_REMOTE_PATH", "/custom/path");

        var factory = new SftpClientFactory();

        Assert.Equal("/custom/path", factory.RemotePath);
    }
}
