using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Sling.Core.Cookies;

/// <summary>
/// Turns one <c>Set-Cookie</c> header value into a stored cookie, applying RFC 6265
/// §5.2 (parsing) and §5.3 (the rules that decide whether to keep it at all).
/// </summary>
/// <remarks>
/// <para>
/// A rejected cookie is not an error the user needs to see - servers set cookies Sling
/// has no business storing all the time. It is reported anyway, because a session that
/// silently does not work is far worse to debug than one that says which cookie it
/// refused and why.
/// </para>
/// <para>
/// <strong>Known limitation, stated rather than papered over: there is no public suffix
/// list.</strong> RFC 6265 §5.3 step 5 says a <c>Domain</c> attribute that is a public
/// suffix must be rejected, which is what stops <c>evil.co.uk</c> setting a cookie for
/// <c>Domain=co.uk</c> and having it sent to every other <c>.co.uk</c> host. Shipping and
/// maintaining that list is a real dependency, and Sling does not carry one. What it does
/// instead: a single-label domain (<c>Domain=com</c>) is refused, and the jar is scoped
/// per environment, so the blast radius is one environment's requests rather than
/// everything the user has ever sent. Anyone relying on Sling to defend a browser-grade
/// boundary should know that it does not.
/// </para>
/// </remarks>
public static class CookieParser
{
    /// <summary>
    /// The largest <c>Set-Cookie</c> value that will be looked at. RFC 6265 §6.1 asks
    /// clients to support at least 4096 bytes per cookie; this is well past that and
    /// keeps a hostile server from making the parser do unbounded work.
    /// </summary>
    private const int MaxHeaderLength = 8192;

    /// <summary>
    /// Prefixes RFC 6265bis §4.1.3 gives a meaning to. A server opting into one is making
    /// a promise about the cookie, and a client that stores it without checking turns
    /// that promise into decoration.
    /// </summary>
    private const string SecurePrefix = "__Secure-";
    private const string HostPrefix = "__Host-";

    /// <summary>
    /// The <c>Expires</c> formats, after the day-of-week and the zone token have been
    /// stripped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neither the day name nor the zone is in these patterns, and that is the point.
    /// RFC 6265 §5.1.1 is an extraction algorithm rather than a format list: it takes the
    /// time, the day of month, the month and the year and <strong>ignores the day-of-week
    /// token entirely</strong>, because a server whose day name disagrees with its date is
    /// far more common than one whose date is wrong.
    /// </para>
    /// <para>
    /// Validating it, which is what a <c>ddd</c> in the pattern does, is wrong in both
    /// directions at once: a future expiry with a mismatched day name silently becomes a
    /// session cookie that never expires, and a server's own deletion - <c>Expires</c> in
    /// 1970, the standard way to log a user out - is ignored, so the session keeps
    /// travelling.
    /// </para>
    /// </remarks>
    private static readonly string[] ExpiresFormats =
    [
        "dd MMM yyyy HH:mm:ss",
        "dd-MMM-yyyy HH:mm:ss",
        "dd-MMM-yy HH:mm:ss",
        "d MMM yyyy HH:mm:ss",
        "MMM d HH:mm:ss yyyy",
    ];

    /// <summary>
    /// Zone tokens that mean UTC. RFC 6265 dates are always GMT; <c>UTC</c> turns up
    /// anyway, and an absent zone is read as UTC rather than as machine-local.
    /// </summary>
    private static readonly string[] UtcSuffixes = ["GMT", "UTC", "UT", "Z"];

    /// <summary>
    /// Parses <paramref name="header"/> as a cookie set by a response from
    /// <paramref name="requestUrl"/>.
    /// </summary>
    /// <param name="nowUtc">
    /// Taken as an argument rather than read from the clock, so expiry is testable and so
    /// every cookie in one response shares an instant.
    /// </param>
    /// <returns>
    /// True with <paramref name="cookie"/> set. False with <paramref name="reason"/>
    /// explaining the refusal in words a user can act on.
    /// </returns>
    public static bool TryParse(
        string header,
        Uri requestUrl,
        DateTimeOffset nowUtc,
        [NotNullWhen(true)] out Cookie? cookie,
        [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(requestUrl);

        cookie = null;

        if (string.IsNullOrWhiteSpace(header))
        {
            reason = "the Set-Cookie header was empty";
            return false;
        }

        if (header.Length > MaxHeaderLength)
        {
            reason = $"the Set-Cookie header is longer than {MaxHeaderLength.ToString(CultureInfo.InvariantCulture)} characters";
            return false;
        }

        var semicolon = header.IndexOf(';', StringComparison.Ordinal);
        var pair = semicolon < 0 ? header : header[..semicolon];
        var rest = semicolon < 0 ? string.Empty : header[(semicolon + 1)..];

        // RFC 6265 §5.2 step 2: a name-value pair with no '=' is not a cookie. Notably
        // this is a whole-string search, so 'Set-Cookie: flag' is refused rather than
        // stored as an empty-named cookie whose value is 'flag'.
        var equals = pair.IndexOf('=', StringComparison.Ordinal);
        if (equals < 0)
        {
            reason = "it has no '=' between a name and a value";
            return false;
        }

        var name = pair[..equals].Trim();
        var value = pair[(equals + 1)..].Trim();

        if (name.Length == 0)
        {
            reason = "the cookie name is empty";
            return false;
        }

        var attributes = ParseAttributes(rest);

        var host = Canonicalize(requestUrl.Host);
        var secure = SecureContext.Is(requestUrl);

        if (!TryResolveDomain(attributes.Domain, host, out var domain, out var hostOnly, out reason))
        {
            return false;
        }

        var path = ResolvePath(attributes.Path, requestUrl.AbsolutePath);
        var expires = ResolveExpiry(attributes, nowUtc);

        // A Secure cookie arriving from somewhere that is not a secure context is
        // refused, and refusing it costs nothing real: a correct client can never send
        // that cookie back to the origin that set it, so the only thing storing it
        // achieves is letting whoever controls that plain-HTTP response plant a cookie for
        // the HTTPS site of the same name. That is cookie forcing, and it is why RFC
        // 6265bis §5.5 added the rule. Loopback counts as secure - see SecureContext,
        // so a local development server issuing Secure cookies still works.
        if (attributes.Secure && !secure)
        {
            reason = "it is marked Secure but arrived over plain HTTP, where it could never be sent back";
            return false;
        }

        if (!TryCheckPrefix(name, attributes, hostOnly, path, out reason))
        {
            return false;
        }

        cookie = new Cookie(
            name,
            value,
            domain,
            path,
            hostOnly,
            attributes.Secure,
            attributes.HttpOnly,
            expires,
            nowUtc,
            nowUtc);

        reason = null;
        return true;
    }

    /// <summary>
    /// Lower-cases a host for comparison and strips the trailing dot of a fully-qualified
    /// name, so <c>API.Example.com.</c> and <c>api.example.com</c> are one domain.
    /// </summary>
    internal static string Canonicalize(string host) =>
        host.TrimEnd('.').ToLowerInvariant();

    /// <summary>
    /// RFC 6265 §5.1.3. <paramref name="host"/> is covered by <paramref name="domain"/>
    /// when they are equal, or when the host is a subdomain of it.
    /// </summary>
    /// <remarks>
    /// The dot in the suffix test is what stops <c>notexample.com</c> matching
    /// <c>example.com</c>. Checking <c>EndsWith(domain)</c> alone is the classic version
    /// of this bug and it hands every cookie to whoever registers the longer name.
    /// </remarks>
    internal static bool DomainMatches(string host, string domain) =>
        string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
        || (host.Length > domain.Length
            && host.EndsWith(domain, StringComparison.OrdinalIgnoreCase)
            && host[host.Length - domain.Length - 1] == '.'
            && !IsIpAddress(host));

    /// <summary>
    /// RFC 6265 §5.1.4. A cookie path covers a request path when they are equal, or when
    /// the cookie path is a prefix of it that ends at a path segment boundary.
    /// </summary>
    /// <remarks>
    /// The boundary test is the difference between this and a plain prefix match, and it
    /// is the reason Sling does not use the framework's container: a cookie scoped to
    /// <c>/foo</c> must not be sent to <c>/foobar</c>, which is a different resource that
    /// may well belong to someone else.
    /// </remarks>
    internal static bool PathMatches(string requestPath, string cookiePath)
    {
        if (string.Equals(requestPath, cookiePath, StringComparison.Ordinal))
        {
            return true;
        }

        if (!requestPath.StartsWith(cookiePath, StringComparison.Ordinal))
        {
            return false;
        }

        return cookiePath.EndsWith('/') || requestPath[cookiePath.Length] == '/';
    }

    /// <summary>
    /// RFC 6265 §5.1.4's default-path: the request path up to, but not including, its
    /// rightmost <c>/</c>.
    /// </summary>
    internal static string DefaultPath(string requestPath)
    {
        if (string.IsNullOrEmpty(requestPath) || requestPath[0] != '/')
        {
            return "/";
        }

        var lastSlash = requestPath.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : requestPath[..lastSlash];
    }

    /// <summary>
    /// True for a host that is a literal address rather than a name. Such a host can only
    /// ever set a host-only cookie - there is no subdomain of an address.
    /// </summary>
    private static bool IsIpAddress(string host) =>
        host.Contains(':', StringComparison.Ordinal)
        || (host.Length > 0 && char.IsAsciiDigit(host[^1]) && host.All(c => char.IsAsciiDigit(c) || c == '.'));

    private static bool TryResolveDomain(
        string? attribute,
        string host,
        out string domain,
        out bool hostOnly,
        [NotNullWhen(false)] out string? reason)
    {
        // No Domain attribute: the cookie belongs to the exact host that set it, and
        // nothing else. This is the safe default and the common case.
        if (string.IsNullOrEmpty(attribute))
        {
            domain = host;
            hostOnly = true;
            reason = null;
            return true;
        }

        // The leading dot is legacy syntax with no meaning of its own in RFC 6265 - a
        // Domain attribute always covers subdomains, dot or no dot.
        domain = Canonicalize(attribute.TrimStart('.'));
        hostOnly = false;

        if (domain.Length == 0)
        {
            reason = "its Domain attribute is empty";
            return false;
        }

        // Without a public suffix list this is the one thing that can still be said with
        // certainty: no single label is ever a registrable domain, so 'Domain=com' is
        // always an attempt at a supercookie. See the remark on this class for what this
        // does and does not cover.
        if (!domain.Contains('.', StringComparison.Ordinal))
        {
            reason = $"'{domain}' is a top-level domain, which no cookie may be scoped to";
            return false;
        }

        // RFC 6265 §5.3 step 6. A server may widen a cookie to its own parent domain and
        // no further; a response from api.example.com asking for Domain=example.org is
        // trying to set a cookie for someone else.
        if (!DomainMatches(host, domain))
        {
            reason = $"'{domain}' does not cover the host that set it";
            return false;
        }

        reason = null;
        return true;
    }

    private static string ResolvePath(string? attribute, string requestPath) =>
        string.IsNullOrEmpty(attribute) || attribute[0] != '/'
            ? DefaultPath(requestPath)
            : attribute;

    /// <summary>
    /// When the cookie expires, or null for a session cookie.
    /// </summary>
    /// <remarks>
    /// <c>Max-Age</c> beats <c>Expires</c> when both are present (RFC 6265 §5.3 step 3),
    /// because it does not depend on the two clocks agreeing. A zero or negative
    /// <c>Max-Age</c> is how a server deletes a cookie, and it arrives here as an expiry
    /// already in the past - which the jar treats as a removal.
    /// </remarks>
    private static DateTimeOffset? ResolveExpiry(Attributes attributes, DateTimeOffset nowUtc)
    {
        if (attributes.MaxAge is { } seconds)
        {
            return seconds <= 0
                ? DateTimeOffset.MinValue
                : SafeAdd(nowUtc, seconds);
        }

        return attributes.Expires;
    }

    /// <summary>
    /// Adds seconds without overflowing. A <c>Max-Age</c> of <see cref="long.MaxValue"/>
    /// is a perfectly ordinary thing for a hostile server to send, and
    /// <see cref="DateTimeOffset"/> throws on it.
    /// </summary>
    private static DateTimeOffset SafeAdd(DateTimeOffset from, long seconds)
    {
        var remaining = (DateTimeOffset.MaxValue - from).TotalSeconds;
        return seconds >= remaining ? DateTimeOffset.MaxValue : from.AddSeconds(seconds);
    }

    /// <summary>
    /// Enforces the <c>__Secure-</c> and <c>__Host-</c> name prefixes.
    /// </summary>
    /// <remarks>
    /// These exist so a server can state, in the cookie's own name, a property that
    /// survives being copied into a log or a proxy config. Honouring them is cheap;
    /// storing a <c>__Host-</c> cookie that is not host-only would mean the name is
    /// lying, and something downstream will believe it.
    /// </remarks>
    private static bool TryCheckPrefix(
        string name,
        Attributes attributes,
        bool hostOnly,
        string path,
        [NotNullWhen(false)] out string? reason)
    {
        // Case-insensitive, as RFC 6265bis §4.1.3 specifies. An ordinal comparison lets
        // '__host-sid' through carrying a name that promises what its attributes do not
        // deliver, which is the whole thing the prefixes exist to make impossible.
        if (name.StartsWith(HostPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!attributes.Secure || !hostOnly || !string.Equals(path, "/", StringComparison.Ordinal))
            {
                reason = $"'{HostPrefix}' names a cookie that must be Secure, have no Domain attribute and use Path=/";
                return false;
            }
        }
        else if (name.StartsWith(SecurePrefix, StringComparison.OrdinalIgnoreCase) && !attributes.Secure)
        {
            reason = $"'{SecurePrefix}' names a cookie that must be Secure";
            return false;
        }

        reason = null;
        return true;
    }

    private static Attributes ParseAttributes(string text)
    {
        var result = new Attributes();

        foreach (var part in text.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var equals = trimmed.IndexOf('=', StringComparison.Ordinal);
            var key = equals < 0 ? trimmed : trimmed[..equals].Trim();
            var value = equals < 0 ? string.Empty : trimmed[(equals + 1)..].Trim();

            // Attribute names are case-insensitive; the values of Domain and Path are not
            // (Domain is canonicalised later, Path is used verbatim).
            switch (key.ToLowerInvariant())
            {
                case "domain":
                    result.Domain = value;
                    break;

                case "path":
                    result.Path = value;
                    break;

                case "secure":
                    result.Secure = true;
                    break;

                case "httponly":
                    result.HttpOnly = true;
                    break;

                case "max-age":
                    if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds))
                    {
                        result.MaxAge = seconds;
                    }

                    break;

                case "expires":
                    if (TryParseExpires(value, out var expires))
                    {
                        result.Expires = expires;
                    }

                    break;

                default:
                    // Unknown attributes are ignored, which RFC 6265 §5.2 requires: it is
                    // how the format grows without every existing client rejecting the
                    // cookies of every server that adopts something new.
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Parses an <c>Expires</c> value, which servers spell several ways.
    /// </summary>
    /// <remarks>
    /// <see cref="CultureInfo.InvariantCulture"/> throughout: the day and month names in
    /// this field are English by specification, and parsing them under the machine's
    /// culture makes a cookie's lifetime depend on the user's regional settings.
    /// </remarks>
    private static bool TryParseExpires(string value, out DateTimeOffset expires)
    {
        const DateTimeStyles Styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        var stripped = StripZone(StripDayOfWeek(value));

        if (DateTimeOffset.TryParseExact(stripped, ExpiresFormats, CultureInfo.InvariantCulture, Styles, out expires))
        {
            return true;
        }

        // A last, lenient attempt, against the stripped text so a numeric offset that
        // survived stripping is still honoured. AssumeUniversal matters here rather than
        // being defensive noise: a value with no zone would otherwise be read as
        // machine-local, making a cookie's expiry depend on where the user is sitting.
        return DateTimeOffset.TryParse(stripped, CultureInfo.InvariantCulture, Styles, out expires);
    }

    /// <summary>
    /// Drops a leading day name, with or without its comma. RFC 6265 §5.1.1 never looks at
    /// it - see <see cref="ExpiresFormats"/>.
    /// </summary>
    private static string StripDayOfWeek(string value)
    {
        var trimmed = value.Trim();

        var comma = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0 && trimmed[..comma].All(char.IsAsciiLetter))
        {
            return trimmed[(comma + 1)..].Trim();
        }

        // The comma is optional in the asctime form, 'Sun Nov  6 08:49:37 1994'. Only a
        // token that is all letters and is followed by something starting with a digit is
        // taken as a day name, so 'Nov 6 08:49:37 1994' - which begins with a month,
        // keeps its first token.
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0 || !trimmed[..space].All(char.IsAsciiLetter))
        {
            return trimmed;
        }

        var rest = trimmed[(space + 1)..].TrimStart();
        return rest.Length > 0 && char.IsAsciiDigit(rest[0]) ? rest : trimmed;
    }

    /// <summary>
    /// Drops a trailing <c>GMT</c> or one of its spellings, so the patterns need not
    /// enumerate them and an absent zone parses the same way.
    /// </summary>
    private static string StripZone(string value)
    {
        foreach (var suffix in UtcSuffixes)
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return value[..^suffix.Length].TrimEnd();
            }
        }

        return value;
    }

    private sealed class Attributes
    {
        public string? Domain { get; set; }

        public string? Path { get; set; }

        public bool Secure { get; set; }

        public bool HttpOnly { get; set; }

        public long? MaxAge { get; set; }

        public DateTimeOffset? Expires { get; set; }
    }
}
