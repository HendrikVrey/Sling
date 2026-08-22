namespace Sling.Core;

/// <summary>
/// Whether a URL is somewhere a credential may safely travel.
/// </summary>
/// <remarks>
/// <para>
/// HTTPS anywhere, or plain HTTP to this machine. It is the same rule browsers use for a
/// "secure context", and it is here rather than written out twice because two copies of
/// it would eventually disagree — which is exactly what happened before this type existed:
/// the OAuth2 token endpoint accepted <c>http://localhost</c> while the cookie jar refused
/// a <c>Secure</c> cookie from the same origin, so a local development server could
/// authenticate but could not hold a session.
/// </para>
/// <para>
/// <see cref="Uri.IsLoopback"/> covers <c>localhost</c>, the whole of 127.0.0.0/8 and
/// <c>::1</c> — the full set rather than the three spellings people remember. Nothing
/// addressed to a loopback address leaves the machine, so there is no wire for anything to
/// be read off.
/// </para>
/// </remarks>
internal static class SecureContext
{
    public static bool Is(Uri url) =>
        url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal)
        || (url.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.Ordinal) && url.IsLoopback);
}
