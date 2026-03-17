using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace AzFunctions.Tests.Helpers;

public class FakeHttpRequestData : HttpRequestData
{
    private readonly FunctionContext context;
    private readonly MemoryStream bodyStream;

    public FakeHttpRequestData(FunctionContext context, string? jsonBody = null)
        : base(context)
    {
        this.context = context;
        bodyStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonBody ?? ""));
    }

    public override Stream Body => bodyStream;
    public override HttpHeadersCollection Headers { get; } = [];
    public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = [];
    public override Uri Url { get; } = new("https://localhost/api/test");
    public override IEnumerable<ClaimsIdentity> Identities { get; } = [];
    public override string Method { get; } = "POST";

    public override HttpResponseData CreateResponse()
    {
        return new FakeHttpResponseData(context);
    }

    public static FakeHttpRequestData CreateWithJson<T>(FunctionContext context, T body)
    {
        string json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        return new FakeHttpRequestData(context, json);
    }
}

public class FakeHttpResponseData : HttpResponseData
{
    public FakeHttpResponseData(FunctionContext context) : base(context)
    {
        Body = new MemoryStream();
    }

    public override HttpStatusCode StatusCode { get; set; }
    public override HttpHeadersCollection Headers { get; set; } = [];
    public override Stream Body { get; set; }
    public override HttpCookies Cookies { get; }= null!;

    public string GetBodyString()
    {
        Body.Position = 0;
        using var reader = new StreamReader(Body, leaveOpen: true);
        return reader.ReadToEnd();
    }
}
