using Sling.Core.Documents;
using Sling.Core.Redaction;
using Sling.Core.Variables;

namespace Sling.Core.History;

/// <summary>One header as a history entry stores it: the name, and a value already redacted.</summary>
public sealed record HistoryHeader(string Name, string Value);

/// <summary>
/// One completed exchange, recorded in a form that is safe to keep on disk.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A sealed class with a private constructor, and that is the design.</strong>
/// <c>Sling.md</c> §5.4 says redaction must happen where it cannot be forgotten at a call
/// site; the way to deliver that is not a rule but a type nobody can build without
/// supplying a <see cref="Redactor"/>. <see cref="Record"/> is the only route in.
/// </para>
/// <para>
/// <strong>No bodies. History records the exchange, not the payload.</strong> A login
/// response body <em>is</em> the token, and redacting an arbitrary body means recognising
/// credentials in JSON, XML, form encoding and whatever else a server sends — a guess that
/// fails silently in the direction that matters. Storing no body at all cannot leak one,
/// and the question history answers in practice is "what did I send, when, and what came
/// back", which needs the status and the timing rather than the payload. The bodies of the
/// current session are in the response pane, where they belong and where they are not at
/// rest.
/// </para>
/// </remarks>
public sealed class HistoryEntry
{
    private HistoryEntry(
        DateTimeOffset sentUtc,
        string method,
        string url,
        int statusCode,
        string reasonPhrase,
        TimeSpan elapsed,
        long requestBodyBytes,
        long responseBodyBytes,
        string? environmentName,
        IReadOnlyList<HistoryHeader> requestHeaders,
        IReadOnlyList<HistoryHeader> responseHeaders)
    {
        SentUtc = sentUtc;
        Method = method;
        Url = url;
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        Elapsed = elapsed;
        RequestBodyBytes = requestBodyBytes;
        ResponseBodyBytes = responseBodyBytes;
        EnvironmentName = environmentName;
        RequestHeaders = requestHeaders;
        ResponseHeaders = responseHeaders;
    }

    /// <summary>When the exchange completed, in UTC.</summary>
    public DateTimeOffset SentUtc { get; }

    public string Method { get; }

    /// <summary>The final URL, redacted. The fragment is not kept — it is never sent.</summary>
    public string Url { get; }

    public int StatusCode { get; }

    public string ReasonPhrase { get; }

    public TimeSpan Elapsed { get; }

    /// <summary>Bytes of request body sent. Zero when there was none.</summary>
    public long RequestBodyBytes { get; }

    /// <summary>Bytes of response body held, which is the read cap when one was hit.</summary>
    public long ResponseBodyBytes { get; }

    /// <summary>
    /// The environment selected when this ran, or null. Recorded because "which
    /// deployment did that go to" is the question a history is most often opened to
    /// answer, and the URL alone does not say when a base URL is shared.
    /// </summary>
    public string? EnvironmentName { get; }

    public IReadOnlyList<HistoryHeader> RequestHeaders { get; }

    public IReadOnlyList<HistoryHeader> ResponseHeaders { get; }

    /// <summary>
    /// Records one exchange. The only way to make a <see cref="HistoryEntry"/>.
    /// </summary>
    /// <param name="redactor">
    /// Required, not optional, and there is no overload without it. Passing
    /// <see cref="Redactor.WithoutKnownSecrets"/> is a decision the caller has to type out.
    /// </param>
    public static HistoryEntry Record(
        ResolvedRequest request,
        ResponseSnapshot response,
        DateTimeOffset sentUtc,
        string? environmentName,
        Redactor redactor)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(redactor);

        return new HistoryEntry(
            sentUtc,
            request.Method,

            // The response's final URL rather than the request's: after a redirect they
            // differ, and the one worth recording is where the bytes actually came from.
            redactor.Url(response.FinalUrl),
            response.StatusCode,
            redactor.Text(response.ReasonPhrase),
            response.Elapsed,
            request.Body?.LongLength ?? 0,
            response.BodyByteCount,
            environmentName,
            [.. request.Headers.Select(h => new HistoryHeader(h.Name, redactor.HeaderValue(h.Name, h.Value)))],
            [.. response.Headers.Select(h => new HistoryHeader(h.Name, redactor.HeaderValue(h.Name, h.Value)))]);
    }

    /// <summary>
    /// Rebuilds an entry read back from disk.
    /// </summary>
    /// <remarks>
    /// A second route in, and it is safe for the reason the first one is not obviously so:
    /// what it reads was redacted before it was ever written. Deliberately named for
    /// reading rather than given as a constructor overload, so nothing reaches for it to
    /// skip <see cref="Record"/>.
    /// </remarks>
    public static HistoryEntry FromStorage(
        DateTimeOffset sentUtc,
        string method,
        string url,
        int statusCode,
        string reasonPhrase,
        TimeSpan elapsed,
        long requestBodyBytes,
        long responseBodyBytes,
        string? environmentName,
        IReadOnlyList<HistoryHeader> requestHeaders,
        IReadOnlyList<HistoryHeader> responseHeaders) =>
        new(
            sentUtc,
            method,
            url,
            statusCode,
            reasonPhrase,
            elapsed,
            requestBodyBytes,
            responseBodyBytes,
            environmentName,
            requestHeaders,
            responseHeaders);
}
