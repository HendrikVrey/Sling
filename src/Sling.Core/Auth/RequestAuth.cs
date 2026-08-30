using Sling.Core.Documents;

namespace Sling.Core.Auth;

/// <summary>Where a request's credential is declared.</summary>
public enum AuthOrigin
{
    /// <summary>Nowhere. The request sends no credential.</summary>
    None,

    /// <summary>A header written in the document.</summary>
    Header,

    /// <summary>A <c># @auth oauth2</c> block above the request.</summary>
    Grant,
}

/// <summary>
/// What kind of credential a request carries.
/// </summary>
/// <remarks>
/// The list is short on purpose: these are the schemes Sling can both read and write. A
/// header it does not recognise is <see cref="Unrecognized"/> rather than being guessed at,
/// and the panel shows it without offering to rewrite it - a tool that silently reinterprets
/// a header somebody wrote by hand is worse than one that admits it does not know.
/// </remarks>
public enum AuthScheme
{
    None,

    /// <summary><c>Authorization: Bearer …</c>, RFC 6750.</summary>
    Bearer,

    /// <summary><c>Authorization: Basic …</c>, RFC 7617.</summary>
    Basic,

    /// <summary>A key in a header of its own, the way most gateways spell it.</summary>
    ApiKeyHeader,

    /// <summary>An OAuth2 client-credentials grant Sling fetches and caches.</summary>
    ClientCredentials,

    /// <summary>An <c>Authorization</c> header whose scheme Sling does not write.</summary>
    Unrecognized,
}

/// <summary>
/// The auth in force for one request, as the document declares it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every value here is as written, with variables still <c>{{braced}}</c>.</b> That is
/// the same rule <see cref="OAuth2Grant"/> keeps and for the same reason: this is the shape
/// a panel and a diagnostic both display, and carrying the unresolved form on the type is
/// what makes it impossible to print a resolved secret from somewhere that only has this.
/// </para>
/// <para>
/// Nothing here is resolved against an environment, so nothing here knows whether the
/// variable it names has a value. <see cref="Variable"/> exists so the caller - which does
/// have the environment - can answer that without re-parsing the header.
/// </para>
/// </remarks>
/// <param name="Origin">Where the credential is declared.</param>
/// <param name="Scheme">What kind it is.</param>
/// <param name="Line">
/// The line it is declared on, 1-based; zero when there is none. For a grant this is the
/// <c># @auth</c> line, which is where a failure to fetch a token is reported.
/// </param>
/// <param name="HeaderName">The header carrying it, or null for a grant.</param>
/// <param name="Written">
/// The credential as written - the header value after the scheme, or null. Never resolved.
/// </param>
/// <param name="Variable">
/// The one variable <see cref="Written"/> consists of, when it is exactly one
/// <c>{{reference}}</c> and nothing else. Null when the value is a literal, or a mixture.
/// This is what lets a panel say the value comes from the environment, and what the
/// "define it" action is named after.
/// </param>
/// <param name="Grant">The grant, when <see cref="Origin"/> is <see cref="AuthOrigin.Grant"/>.</param>
public sealed record RequestAuthView(
    AuthOrigin Origin,
    AuthScheme Scheme,
    int Line,
    string? HeaderName,
    string? Written,
    string? Variable,
    OAuth2Grant? Grant)
{
    /// <summary>A request that sends no credential.</summary>
    public static RequestAuthView None { get; } =
        new(AuthOrigin.None, AuthScheme.None, 0, null, null, null, null);
}

/// <summary>
/// Reads the auth out of a parsed request.
/// </summary>
/// <remarks>
/// <para>
/// The fix for a friction that had nothing to do with the auth engine, which works: auth
/// can arrive from a header typed into the document, from a <c># @auth</c> block, or from a
/// variable resolved out of an environment file - and answering "what credential is this
/// request actually sending" meant reading three files. This answers it from the parse.
/// </para>
/// <para>
/// In <c>Sling.Core</c> rather than in the window because it is a property of the document,
/// not of the panel that shows it, and because it is the sort of thing that has to be tested
/// against a parse rather than against a screenshot.
/// </para>
/// </remarks>
public static class RequestAuth
{
    /// <summary>The header a credential is normally written in.</summary>
    public const string AuthorizationHeader = "Authorization";

    /// <summary>
    /// Header names treated as carrying an API key.
    /// </summary>
    /// <remarks>
    /// <b>A closed list, and it is deliberately not a guess.</b> There is no rule that makes
    /// a header a credential, so anything cleverer here - a name containing "key", say -
    /// would report an unrelated header as auth and, worse, offer to rewrite it. These are
    /// the spellings gateways actually use, and the one Sling itself writes is first.
    /// </remarks>
    public static IReadOnlyList<string> ApiKeyHeaders { get; } =
        ["X-API-Key", "X-Api-Token", "Api-Key", "ApiKey", "X-Auth-Token"];

    /// <summary>What auth <paramref name="block"/> declares.</summary>
    public static RequestAuthView Describe(RequestBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        // The grant wins when both are present, because it is what the sender does: the
        // token it fetches is put in the Authorization header, over whatever was written
        // there. A panel that reported the header would be naming the value that loses.
        if (block.Auth is { } grant)
        {
            return new RequestAuthView(
                AuthOrigin.Grant,
                AuthScheme.ClientCredentials,
                grant.Line,
                AuthorizationHeader,
                null,
                null,
                grant);
        }

        if (Find(block, AuthorizationHeader) is { } authorization)
        {
            var (scheme, credential) = ReadScheme(authorization.Value);

            return new RequestAuthView(
                AuthOrigin.Header,
                scheme,
                authorization.Line,
                authorization.Name,
                credential,
                SoleVariable(credential),
                null);
        }

        foreach (var name in ApiKeyHeaders)
        {
            if (Find(block, name) is not { } key)
            {
                continue;
            }

            return new RequestAuthView(
                AuthOrigin.Header,
                AuthScheme.ApiKeyHeader,
                key.Line,
                key.Name,
                key.Value,
                SoleVariable(key.Value),
                null);
        }

        return RequestAuthView.None;
    }

    /// <summary>
    /// The variable a value consists of, when it is exactly one <c>{{reference}}</c>.
    /// </summary>
    /// <remarks>
    /// Exactly one and nothing else, so <c>{{token}}</c> answers and <c>Bearer {{a}}{{b}}</c>
    /// does not. A partial answer here would be worse than none: it is what the panel says a
    /// credential "comes from", and naming one of two sources is a sentence that is wrong.
    /// </remarks>
    public static string? SoleVariable(string? written)
    {
        var value = written?.Trim();

        if (value is not { Length: > 4 }
            || !value.StartsWith("{{", StringComparison.Ordinal)
            || !value.EndsWith("}}", StringComparison.Ordinal))
        {
            return null;
        }

        var inner = value[2..^2].Trim();

        return inner.Length > 0
            && !inner.Contains('{', StringComparison.Ordinal)
            && !inner.Contains('}', StringComparison.Ordinal)
                ? inner
                : null;
    }

    /// <summary>
    /// Splits an <c>Authorization</c> value into its scheme and the credential after it.
    /// </summary>
    /// <remarks>
    /// The scheme is matched case-insensitively because RFC 9110 §11.1 says it is
    /// case-insensitive, and servers answer <c>bearer</c> as often as <c>Bearer</c>.
    /// </remarks>
    private static (AuthScheme Scheme, string? Credential) ReadScheme(string value)
    {
        var trimmed = value.Trim();
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);

        if (space <= 0)
        {
            // A value with no space is not a scheme and a credential. It may be a variable
            // standing for the whole header, which is a real thing people write.
            return (AuthScheme.Unrecognized, trimmed.Length == 0 ? null : trimmed);
        }

        var scheme = trimmed[..space];
        var credential = trimmed[(space + 1)..].Trim();

        if (scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return (AuthScheme.Bearer, credential);
        }

        return scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
            ? (AuthScheme.Basic, credential)
            : (AuthScheme.Unrecognized, trimmed);
    }

    private static HeaderField? Find(RequestBlock block, string name) =>
        block.Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
