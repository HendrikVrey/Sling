namespace Sling.Core.Cookies;

/// <summary>
/// One stored cookie, in the shape RFC 6265 §5.3 describes.
/// </summary>
/// <remarks>
/// <para>
/// Sling keeps its own cookie type rather than using <c>System.Net.Cookie</c> and
/// <c>CookieContainer</c>, and the reason is <c>Sling.md</c> §5.6 - "the cookie jar
/// respects domain, path and <c>Secure</c>". <c>CookieContainer</c>'s path handling is a
/// <em>prefix</em> match, so a cookie scoped to <c>/foo</c> is sent to <c>/foobar</c>;
/// RFC 6265 §5.1.4 says it must not be. For a tool whose whole claim is that a staging
/// credential cannot reach production, shipping a known over-send would make the promise
/// false. The rules are small, and written here they are testable as pure functions.
/// </para>
/// <para>
/// This type also has to be enumerable and comparable so the jar can be scoped per
/// environment and inspected, which is awkward through a container that owns its own
/// storage.
/// </para>
/// </remarks>
/// <param name="Name">The cookie's name, exactly as the server wrote it.</param>
/// <param name="Value">
/// The value, exactly as the server wrote it - including surrounding quotes, which
/// RFC 6265 treats as part of the value rather than as quoting.
/// </param>
/// <param name="Domain">
/// The canonicalised host this cookie belongs to: lower-cased, with no leading dot. The
/// leading dot in a <c>Domain</c> attribute carries no meaning in RFC 6265; whether a
/// cookie covers subdomains is <paramref name="HostOnly"/>.
/// </param>
/// <param name="Path">The path prefix, always starting with <c>/</c>.</param>
/// <param name="HostOnly">
/// True when the response carried no <c>Domain</c> attribute, in which case the cookie
/// goes back only to the exact host that set it.
/// </param>
/// <param name="Secure">True when the cookie may only travel over HTTPS.</param>
/// <param name="HttpOnly">
/// True when the server marked it unavailable to scripts. Sling has no scripts, so this
/// changes nothing about sending - it is kept because dropping an attribute the server
/// set would make the jar a lossy record of what was actually agreed.
/// </param>
/// <param name="Expires">
/// When the cookie stops being valid, or null for a session cookie. Sling's jar lives
/// only as long as the process, so the distinction affects expiry alone.
/// </param>
/// <param name="CreatedUtc">
/// When the cookie was first stored. RFC 6265 §5.4 orders equal-length paths by this, and
/// §5.3 says a cookie replaced by a later <c>Set-Cookie</c> keeps its original creation
/// time - so the order requests see does not shuffle as a session refreshes itself.
/// </param>
/// <param name="LastAccessUtc">When the cookie was last sent, which is what eviction uses.</param>
public sealed record Cookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    bool HostOnly,
    bool Secure,
    bool HttpOnly,
    DateTimeOffset? Expires,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastAccessUtc)
{
    /// <summary>
    /// The triple that identifies a cookie. A <c>Set-Cookie</c> repeating it replaces the
    /// stored one rather than adding a second (RFC 6265 §5.3 step 11).
    /// </summary>
    public CookieKey Key => new(Name, Domain, Path);

    /// <summary>True when this cookie is no longer valid at <paramref name="nowUtc"/>.</summary>
    public bool HasExpired(DateTimeOffset nowUtc) => Expires is { } expires && expires <= nowUtc;
}

/// <summary>
/// What makes two cookies the same cookie: name, domain and path together.
/// </summary>
/// <remarks>
/// Name and path are ordinal - both are case-sensitive on the wire, and a server that
/// sets <c>Session</c> and <c>session</c> means two cookies. Domain is case-insensitive
/// because DNS is, and it is stored already lower-cased, so the comparer is belt and
/// braces against a key built from an un-canonicalised host.
/// </remarks>
public readonly record struct CookieKey(string Name, string Domain, string Path)
{
    public bool Equals(CookieKey other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Domain, other.Domain, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path, other.Path, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(
        Name.GetHashCode(StringComparison.Ordinal),
        Domain.GetHashCode(StringComparison.OrdinalIgnoreCase),
        Path.GetHashCode(StringComparison.Ordinal));
}
