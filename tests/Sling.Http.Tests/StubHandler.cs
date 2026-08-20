using System.Net;
using System.Net.Http.Headers;

namespace Sling.Http.Tests;

/// <summary>
/// A handler that answers from a script instead of a socket, and records what it was
/// asked for.
/// </summary>
/// <remarks>
/// The redirect and credential-stripping rules can only be tested by inspecting the
/// second request, which means the test has to be the server. Every request is captured
/// before its content is read, because a <see cref="HttpRequestMessage"/> is disposed
/// with the exchange and its content stream is not readable afterwards.
/// </remarks>
internal sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<CapturedRequest> _requests = [];

    public IReadOnlyList<CapturedRequest> Requests => _requests;

    /// <summary>A response with a body and, optionally, headers of the form "Name: value".</summary>
    public static HttpResponseMessage Ok(string body, string contentType = "application/json") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
        };

    public static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(string.Empty),
        };

        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);

        var index = _requests.Count;
        _requests.Add(new CapturedRequest(request.Method.Method, request.RequestUri!, headers, body));

        return respond(request, index);
    }

    internal sealed record CapturedRequest(
        string Method,
        Uri Url,
        IReadOnlyDictionary<string, string> Headers,
        string? Body)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }
}
