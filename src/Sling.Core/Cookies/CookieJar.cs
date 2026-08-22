using System.Text;

namespace Sling.Core.Cookies;

/// <summary>
/// The cookies one environment has collected, and the rules deciding which of them a
/// given request may carry.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One jar per environment</strong> (<c>Sling.md</c> §5.6). A cookie set by
/// staging must not be sent to production, and the reliable way to guarantee that is for
/// the two never to share storage — not for a check somewhere to remember to compare
/// environments. Sling holds a jar per selected environment and swaps the whole thing
/// when the selection changes.
/// </para>
/// <para>
/// <strong>Memory only. Cookies are never written to disk.</strong> §5.6 says persisted
/// cookies are secrets and must live with the secrets; the strongest reading of that
/// requirement, and the one taken here, is not to persist them at all. A session cookie
/// in an API client exists to carry a login across the requests of one working session,
/// and keeping it after the process exits buys a small convenience in exchange for a
/// credential sitting at rest in a file. Revisitable, and it would need encryption at
/// rest if it is ever revisited.
/// </para>
/// <para>
/// Locked, like <c>ResponseStore</c> and for the same reason: sending runs
/// <c>ConfigureAwait(false)</c> throughout, so cookies are stored on whatever pool thread
/// the response arrived on.
/// </para>
/// </remarks>
public sealed class CookieJar
{
    /// <summary>
    /// The total cookie ceiling. RFC 6265 §6.1 asks for at least 3000; there is no reason
    /// to hold more, and a bound is what stops a hostile or merely enthusiastic server
    /// growing the jar without limit.
    /// </summary>
    private const int MaxCookies = 3000;

    /// <summary>Per-domain ceiling, the RFC's suggested 50.</summary>
    private const int MaxPerDomain = 50;

    private readonly Lock _gate = new();
    private readonly Dictionary<CookieKey, Cookie> _cookies = [];

    /// <summary>How many cookies are held, expired ones included until they are swept.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _cookies.Count;
            }
        }
    }

    /// <summary>
    /// Stores what a response set, and reports anything refused.
    /// </summary>
    /// <param name="requestUrl">The URL the response came from, which decides the default scope.</param>
    /// <param name="setCookieValues">Every <c>Set-Cookie</c> header on the response, in order.</param>
    /// <param name="nowUtc">The instant the response arrived.</param>
    /// <returns>
    /// A sentence per refused cookie. Empty when everything was stored, which is the
    /// common case — a refusal is worth surfacing because a session that silently does
    /// not work is the hardest kind to debug.
    /// </returns>
    public IReadOnlyList<string> Store(
        Uri requestUrl,
        IEnumerable<string> setCookieValues,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(requestUrl);
        ArgumentNullException.ThrowIfNull(setCookieValues);

        var refusals = new List<string>();

        foreach (var header in setCookieValues)
        {
            if (!CookieParser.TryParse(header, requestUrl, nowUtc, out var cookie, out var reason))
            {
                refusals.Add($"A cookie from {requestUrl.Host} was not stored: {reason}.");
                continue;
            }

            lock (_gate)
            {
                StoreOne(cookie, nowUtc);
            }
        }

        return refusals;
    }

    /// <summary>
    /// The <c>Cookie</c> header value for <paramref name="requestUrl"/>, or null when no
    /// stored cookie applies.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty string on purpose: an empty <c>Cookie</c> header is a
    /// header the request did not have, and sending one changes what the server sees.
    /// </remarks>
    public string? Header(Uri requestUrl, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(requestUrl);

        var host = CookieParser.Canonicalize(requestUrl.Host);
        var path = requestUrl.AbsolutePath;

        // A secure context rather than strictly HTTPS, matching the rule the parser applies
        // when the cookie was set. Loopback counts: a local development server issuing
        // Secure session cookies has to be able to hold a session, and nothing addressed to
        // 127.0.0.1 reaches a wire anyone can read.
        var secure = SecureContext.Is(requestUrl);

        List<Cookie> matches;

        lock (_gate)
        {
            SweepExpired(nowUtc);

            matches = _cookies.Values
                .Where(c => Applies(c, host, path, secure))
                .ToList();

            if (matches.Count == 0)
            {
                return null;
            }

            // Sending is an access, and eviction is by least-recently-used — so a cookie
            // that is actually in play must not be the one dropped when the jar fills.
            foreach (var match in matches)
            {
                _cookies[match.Key] = match with { LastAccessUtc = nowUtc };
            }
        }

        // RFC 6265 §5.4 step 2: longer paths first, then oldest first. Servers are not
        // supposed to depend on the order, and some do — the specified order is the one
        // they were written against.
        matches.Sort(static (left, right) =>
        {
            var byPath = right.Path.Length.CompareTo(left.Path.Length);
            return byPath != 0 ? byPath : left.CreatedUtc.CompareTo(right.CreatedUtc);
        });

        var builder = new StringBuilder();

        foreach (var cookie in matches)
        {
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(cookie.Name).Append('=').Append(cookie.Value);
        }

        return builder.ToString();
    }

    /// <summary>Every cookie held, expired ones swept first. For showing the user.</summary>
    public IReadOnlyList<Cookie> Snapshot(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            SweepExpired(nowUtc);
            return [.. _cookies.Values.OrderBy(c => c.Domain, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal)];
        }
    }

    /// <summary>Empties the jar.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _cookies.Clear();
        }
    }

    /// <summary>
    /// RFC 6265 §5.4 step 1: the three tests that decide whether a stored cookie belongs
    /// on this request.
    /// </summary>
    private static bool Applies(Cookie cookie, string host, string path, bool secure)
    {
        var domainOk = cookie.HostOnly
            ? string.Equals(host, cookie.Domain, StringComparison.OrdinalIgnoreCase)
            : CookieParser.DomainMatches(host, cookie.Domain);

        return domainOk
            && CookieParser.PathMatches(path, cookie.Path)
            && (!cookie.Secure || secure);
    }

    /// <summary>
    /// Stores one cookie, replacing any cookie with the same name, domain and path.
    /// </summary>
    /// <remarks>
    /// The replacement keeps the <em>original</em> creation time (RFC 6265 §5.3 step 11).
    /// That is not a detail: creation time breaks ties in send order, so a session that
    /// refreshes its own cookie would otherwise reshuffle the header on every request.
    /// </remarks>
    private void StoreOne(Cookie cookie, DateTimeOffset nowUtc)
    {
        // A server deletes a cookie by setting it with an expiry in the past. Storing it
        // and letting the sweep find it later would work; removing it here means the
        // deletion takes effect on the very next request rather than depending on when a
        // sweep happens to run.
        if (cookie.HasExpired(nowUtc))
        {
            _cookies.Remove(cookie.Key);
            return;
        }

        if (_cookies.TryGetValue(cookie.Key, out var existing))
        {
            _cookies[cookie.Key] = cookie with { CreatedUtc = existing.CreatedUtc };
            return;
        }

        SweepExpired(nowUtc);
        EvictIfFull(cookie.Domain);

        _cookies[cookie.Key] = cookie;
    }

    private void SweepExpired(DateTimeOffset nowUtc)
    {
        // Materialised before removing: mutating a dictionary while enumerating it throws,
        // and the sweep is over a few thousand entries at worst.
        foreach (var key in _cookies.Where(entry => entry.Value.HasExpired(nowUtc)).Select(entry => entry.Key).ToList())
        {
            _cookies.Remove(key);
        }
    }

    /// <summary>
    /// Makes room for one more cookie, dropping the least recently used.
    /// </summary>
    /// <remarks>
    /// Both ceilings matter and they catch different things. The per-domain one stops one
    /// chatty host filling the jar and evicting every other host's session; the total one
    /// stops a walk through a thousand hosts doing the same thing a domain at a time.
    /// </remarks>
    private void EvictIfFull(string domain)
    {
        var forDomain = _cookies.Values
            .Where(c => string.Equals(c.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (forDomain.Count >= MaxPerDomain)
        {
            Evict(forDomain, forDomain.Count - MaxPerDomain + 1);
        }

        if (_cookies.Count >= MaxCookies)
        {
            Evict([.. _cookies.Values], _cookies.Count - MaxCookies + 1);
        }
    }

    private void Evict(List<Cookie> candidates, int howMany)
    {
        foreach (var victim in candidates.OrderBy(c => c.LastAccessUtc).Take(howMany))
        {
            _cookies.Remove(victim.Key);
        }
    }
}
