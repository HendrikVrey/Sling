using System.Diagnostics;
using System.Net;
using System.Text;
using Sling.Core.Documents;
using Sling.Core.Variables;

namespace Sling.Http;

/// <summary>
/// Sends one resolved request and returns what came back.
/// </summary>
/// <remarks>
/// <para>
/// The whole of Sling's contact with the network is this class. That concentration is
/// the point (<c>Sling.md</c> §3): the redirect and TLS rules in §5 can only be got
/// wrong in one file, so they can be audited by reading one file.
/// </para>
/// <para>
/// Redirects are followed by hand with <see cref="SocketsHttpHandler.AllowAutoRedirect"/>
/// off. The handler's own redirect support cannot express "drop the credentials when the
/// origin changes", and sending an <c>Authorization</c> header to whatever host a 302
/// nominates is a real, shipped bug in more than one HTTP client.
/// </para>
/// </remarks>
public sealed class RequestSender : IDisposable
{
    private const char ByteOrderMark = (char)0xFEFF;
    private const int ReadChunkBytes = 81920;

    private static readonly UTF8Encoding DefaultEncoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Headers that authenticate the request rather than describe it, and that must not
    /// survive a hop to a different origin.
    /// </summary>
    private static readonly string[] CredentialHeaders = ["Authorization", "Cookie", "Proxy-Authorization"];

    /// <summary>Headers that describe a body, and are meaningless once one is dropped.</summary>
    private static readonly string[] BodyHeaders = ["Content-Type", "Content-Length", "Content-Encoding", "Transfer-Encoding"];

    private readonly HttpClient _client;
    private readonly SendOptions _options;

    public RequestSender(SendOptions? options = null)
        : this(CreateHandler(), options)
    {
    }

    internal RequestSender(HttpMessageHandler handler, SendOptions? options = null)
    {
        _options = options ?? new SendOptions();

        // The timeout is enforced with a linked token instead, so a timeout is
        // distinguishable from the user cancelling and the elapsed time reported is the
        // time the whole chain of hops actually took.
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// The handler every send goes through, with the security-relevant switches set
    /// explicitly rather than left at their defaults.
    /// </summary>
    /// <remarks>
    /// TLS is deliberately untouched: no <c>SslOptions</c> are assigned, so certificate
    /// validation stays on with the platform's own trust store. <c>Sling.md</c> §5.3
    /// allows a bypass only per request and only with loud indication, which means there
    /// is nothing here to switch off globally — the safest way to hold that line is for
    /// the code that could weaken it not to exist.
    /// </remarks>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,

        // The cookie jar arrives in M3 and is scoped per environment. Until it does,
        // nothing stores or replays a cookie: an implicit process-wide jar is exactly the
        // mechanism that would carry a staging cookie to production.
        UseCookies = false,

        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public async Task<ResponseSnapshot> SendAsync(ResolvedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);

        var url = request.Url;
        var method = request.Method;
        var headers = request.Headers.ToList();
        var body = request.Body is null ? null : DefaultEncoding.GetBytes(request.Body);
        var trail = new List<Uri>();

        for (var hop = 0; ; hop++)
        {
            using var message = BuildMessage(method, url, headers, body, request.Version);
            using var response = await _client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            var next = RedirectTarget(response, url);
            if (next is null || hop >= _options.MaxRedirects)
            {
                // Handing back the 3xx when the budget runs out is deliberate: the user
                // sees the status and the trail and can decide, which is more useful than
                // an exception saying a number was exceeded.
                return await SnapshotAsync(response, url, trail, stopwatch, deadline.Token).ConfigureAwait(false);
            }

            if (!IsSameOrigin(url, next))
            {
                headers = Without(headers, CredentialHeaders);
            }

            (method, body, headers) = FollowRedirect(response.StatusCode, method, body, headers);

            url = next;
            trail.Add(next);
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Two URLs share an origin when scheme, host and port all match — the same rule the
    /// web platform uses. Host comparison is case-insensitive because DNS is; scheme is
    /// ordinal because <see cref="Uri"/> has already lower-cased it.
    /// </summary>
    internal static bool IsSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.Ordinal)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    /// <summary>
    /// Where a redirect points, or null if this response is not one Sling will follow.
    /// </summary>
    /// <remarks>
    /// A <c>Location</c> naming a scheme other than http or https is not followed. A
    /// redirect is the server choosing Sling's next request, and that choice must not be
    /// able to turn a network call into something else.
    /// </remarks>
    internal static Uri? RedirectTarget(HttpResponseMessage response, Uri current)
    {
        if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
        {
            return null;
        }

        var location = response.Headers.Location;
        if (location is null)
        {
            return null;
        }

        if (!location.IsAbsoluteUri)
        {
            if (!Uri.TryCreate(current, location, out var absolute))
            {
                return null;
            }

            location = absolute;
        }

        var isWeb = location.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.Ordinal)
            || location.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal);

        return isWeb ? location : null;
    }

    /// <summary>
    /// Applies the method and body rewriting a redirect status implies: 303 always
    /// becomes GET, 301 and 302 turn a POST into one by long-standing practice, and 307
    /// and 308 exist precisely to say "repeat exactly what you sent".
    /// </summary>
    internal static (string Method, byte[]? Body, List<HeaderField> Headers) FollowRedirect(
        HttpStatusCode status,
        string method,
        byte[]? body,
        List<HeaderField> headers)
    {
        var isSafe = method is "GET" or "HEAD";
        var becomesGet = (int)status is 301 or 302 or 303 && !isSafe;

        if (!becomesGet)
        {
            return (method, body, headers);
        }

        return ("GET", null, Without(headers, BodyHeaders));
    }

    private static List<HeaderField> Without(List<HeaderField> headers, string[] names) =>
        headers
            .Where(h => !names.Contains(h.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

    private static HttpRequestMessage BuildMessage(
        string method,
        Uri url,
        List<HeaderField> headers,
        byte[]? body,
        string? version)
    {
        var message = new HttpRequestMessage(HttpMethod.Parse(method), url);

        if (TryParseVersion(version, out var parsed))
        {
            message.Version = parsed;

            // OrLower: the document asking for HTTP/2 is a preference, not a demand. A
            // server that only speaks 1.1 should answer, not fail the handshake.
            message.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }

        if (body is not null)
        {
            message.Content = new ByteArrayContent(body);
        }

        foreach (var header in headers)
        {
            // Without validation: the document is the authority on what to send. A user
            // debugging a server that mishandles a malformed header needs to be able to
            // send that header. TryAddWithoutValidation returns false for content headers,
            // which belong on the content object rather than the request.
            if (message.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                continue;
            }

            // A content header with no body to describe. Previously the null-conditional
            // swallowed it and the header simply never left the process — so a GET
            // carrying Content-Type sent no Content-Type, silently, in a method whose
            // comment claims the document decides. An empty body is what the document
            // actually described.
            message.Content ??= new ByteArrayContent([]);
            message.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        return message;
    }

    private static bool TryParseVersion(string? version, out Version parsed)
    {
        parsed = HttpVersion.Version11;

        if (version is null)
        {
            return false;
        }

        var digits = version.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
            ? version["HTTP/".Length..]
            : version;

        // "HTTP/2" is how the wire spells it; Version needs two components.
        if (!digits.Contains('.', StringComparison.Ordinal))
        {
            digits += ".0";
        }

        if (!Version.TryParse(digits, out var wanted))
        {
            return false;
        }

        parsed = wanted;
        return true;
    }

    private async Task<ResponseSnapshot> SnapshotAsync(
        HttpResponseMessage response,
        Uri finalUrl,
        List<Uri> trail,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var (bytes, truncated) = await ReadCappedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        var body = ResolveEncoding(response.Content.Headers.ContentType?.CharSet).GetString(bytes);
        if (body.Length > 0 && body[0] == ByteOrderMark)
        {
            body = body[1..];
        }

        var headers = response.Headers
            .Concat(response.Content.Headers)
            .SelectMany(h => h.Value.Select(value => new ResponseHeader(h.Key, value)))
            .ToList();

        return new ResponseSnapshot(
            (int)response.StatusCode,
            response.ReasonPhrase ?? string.Empty,
            response.Version.ToString(),
            headers,
            body,
            bytes.Length,
            truncated,
            stopwatch.Elapsed,
            finalUrl,
            trail);
    }

    /// <summary>
    /// Reads at most <see cref="SendOptions.MaxBodyBytes"/>, reporting whether there was
    /// more. Reads one byte past the cap on purpose: that is the only way to tell a body
    /// that exactly fills the cap from one that overflows it.
    /// </summary>
    private async Task<(byte[] Bytes, bool Truncated)> ReadCappedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var cap = _options.MaxBodyBytes;

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkBytes];

        while (buffer.Length <= cap)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return (buffer.ToArray(), false);
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        return (bytes[..(int)Math.Min(cap, int.MaxValue)], true);
    }

    /// <summary>
    /// The encoding named by <c>Content-Type</c>, or UTF-8. An unknown or unsupported
    /// charset falls back rather than throwing: a body decoded imperfectly is far more
    /// useful than an exception where a response should be.
    /// </summary>
    private static Encoding ResolveEncoding(string? charSet)
    {
        if (string.IsNullOrWhiteSpace(charSet))
        {
            return DefaultEncoding;
        }

        try
        {
            return Encoding.GetEncoding(charSet.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            return DefaultEncoding;
        }
    }
}
