using System.Net;
using AzFunctions.Tests.Helpers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace AzFunctions.Tests;

public class SftpEndpointsTests
{
    private readonly ISftpClientFactory sftpClientFactory = Substitute.For<ISftpClientFactory>();
    private readonly FunctionContext context = new FakeFunctionContext("SftpEndpoints");

    private SftpEndpoints CreateEndpoints() => new(sftpClientFactory);

    // --- GetFile path traversal tests ---

    [Theory]
    [InlineData("..")]
    [InlineData("../etc/passwd")]
    [InlineData("foo/../bar")]
    public async Task GetFile_PathTraversal_Returns400(string fileName)
    {
        sftpClientFactory.RemotePath.Returns("/upload");

        var req = new FakeHttpRequestData(context);
        var response = await CreateEndpoints().GetFile(req, fileName, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("foo/bar")]
    [InlineData("sub/dir/file.csv")]
    public async Task GetFile_ForwardSlash_Returns400(string fileName)
    {
        sftpClientFactory.RemotePath.Returns("/upload");

        var req = new FakeHttpRequestData(context);
        var response = await CreateEndpoints().GetFile(req, fileName, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("foo\\bar")]
    public async Task GetFile_Backslash_Returns400(string fileName)
    {
        sftpClientFactory.RemotePath.Returns("/upload");

        var req = new FakeHttpRequestData(context);
        var response = await CreateEndpoints().GetFile(req, fileName, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetFile_ConnectionFails_Returns500()
    {
        sftpClientFactory.RemotePath.Returns("/upload");
        sftpClientFactory.CreateConnectedClientAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Connection refused"));

        var req = new FakeHttpRequestData(context);
        var response = await CreateEndpoints().GetFile(req, "valid.csv", context);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // --- ListFiles tests ---

    [Fact]
    public async Task ListFiles_ConnectionFails_Returns500()
    {
        sftpClientFactory.RemotePath.Returns("/upload");
        sftpClientFactory.CreateConnectedClientAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Connection refused"));

        var req = new FakeHttpRequestData(context);
        var response = await CreateEndpoints().ListFiles(req, context);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // --- DeleteAllFiles tests ---

    [Fact]
    public async Task DeleteAllFiles_ConnectionFails_Returns500()
    {
        sftpClientFactory.RemotePath.Returns("/upload");
        sftpClientFactory.CreateConnectedClientAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Connection refused"));

        var req = new FakeHttpRequestData(context);
        var response = await CreateEndpoints().DeleteAllFiles(req, context);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
