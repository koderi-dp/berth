using System.Net;
using System.Text;

namespace Berth.Tests.Engine;

/// <summary>An engine stand-in: records each request and answers from a table keyed by path and query.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.Ordinal);

    public List<(string Method, string PathAndQuery)> Requests { get; } = [];

    /// <summary>Answers requests for <paramref name="pathAndQuery"/> with this status and body.</summary>
    public StubHandler On(string pathAndQuery, HttpStatusCode status, string body)
    {
        _answers[pathAndQuery] = (status, body);
        return this;
    }

    public StubHandler On(string pathAndQuery, string body) => On(pathAndQuery, HttpStatusCode.OK, body);

    /// <summary>A <c>/version</c> answer that reports this API version.</summary>
    public StubHandler WithVersion(string apiVersion) => On(
        "/version",
        $$"""{"Version":"29.9.3","ApiVersion":"{{apiVersion}}","MinAPIVersion":"1.40","Os":"linux","Arch":"amd64"}""");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pathAndQuery = request.RequestUri!.PathAndQuery;
        Requests.Add((request.Method.Method, pathAndQuery));

        var (status, body) = _answers.TryGetValue(pathAndQuery, out var answer)
            ? answer
            : (HttpStatusCode.NotFound, """{"message":"page not found"}""");

        return Task.FromResult(new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
