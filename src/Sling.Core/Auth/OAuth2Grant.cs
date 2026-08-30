namespace Sling.Core.Auth;

/// <summary>
/// Where the client's own credentials go when asking for a token.
/// </summary>
/// <remarks>
/// RFC 6749 §2.3.1 says an authorization server MUST support HTTP Basic and MAY also
/// accept the credentials in the form body. Basic is the default here because it is the
/// one every server is required to accept, and because it keeps the secret out of a body
/// that a proxy is more likely to log.
/// </remarks>
public enum ClientAuthPlacement
{
    /// <summary>An <c>Authorization: Basic</c> header, per RFC 6749 §2.3.1.</summary>
    BasicHeader,

    /// <summary><c>client_id</c> and <c>client_secret</c> as form fields.</summary>
    FormBody,
}

/// <summary>
/// An OAuth2 client-credentials grant, as written above a request and with its values
/// still <c>{{braced}}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Sling.md</c> §4 puts client-credentials in v1 because it is the flow a developer
/// actually meets when moving off Postman: a machine-to-machine API where every request
/// needs a bearer token that expires. The authorization-code flow is not here and is
/// documented as absent - it needs a browser, a redirect listener and a consent screen,
/// which is a different product.
/// </para>
/// <para>
/// This is built on chaining rather than beside it. Fetching a token and using it in the
/// next request <em>is</em> a chain; the directive exists because expressing it by hand
/// means writing a form-encoded POST and then knowing when the token went stale.
/// </para>
/// <para>
/// The values stay unresolved on purpose. A client secret belongs in
/// <c>http-client.private.env.json</c> and reaches the document as
/// <c>{{client_secret}}</c>, so keeping the braced form here is what lets a diagnostic
/// quote the grant without printing the secret - the rule
/// <see cref="Documents.ParseDiagnostic"/> holds everywhere else.
/// </para>
/// </remarks>
/// <param name="TokenUrl">The token endpoint, as written.</param>
/// <param name="ClientId">The client identifier, as written.</param>
/// <param name="ClientSecret">The client secret, as written.</param>
/// <param name="Scope">The requested scope, or null. Space-separated when there are several.</param>
/// <param name="Audience">
/// An <c>audience</c> form field, or null. Not in RFC 6749 - it is how Auth0 and several
/// others name which API the token is for, and omitting it makes Sling unable to talk to
/// them.
/// </param>
/// <param name="Placement">Where the client credentials go.</param>
/// <param name="Line">The line <c># @auth</c> was written on, for diagnostics.</param>
public sealed record OAuth2Grant(
    string TokenUrl,
    string ClientId,
    string ClientSecret,
    string? Scope,
    string? Audience,
    ClientAuthPlacement Placement,
    int Line);

/// <summary>
/// An <see cref="OAuth2Grant"/> with every variable substituted - the only shape a token
/// request is built from.
/// </summary>
/// <remarks>
/// Separate from the unresolved grant for exactly the reason
/// <see cref="Variables.ResolvedRequest"/> is separate from
/// <see cref="Documents.RequestBlock"/>: there is no way to construct one except through
/// resolution, so an unresolved <c>{{client_secret}}</c> cannot be sent to an
/// authorization server as a literal.
/// </remarks>
/// <param name="Line">
/// The line <c># @auth</c> was written on. Carried through resolution so a failure while
/// fetching the token is reported against the grant that asked for it, rather than
/// against the request line, which is not where the mistake is.
/// </param>
public sealed record ResolvedOAuth2Grant(
    Uri TokenUrl,
    string ClientId,
    string ClientSecret,
    string? Scope,
    string? Audience,
    ClientAuthPlacement Placement,
    int Line)
{
    /// <summary>
    /// What makes two grants the same grant, for the token cache.
    /// </summary>
    /// <remarks>
    /// Every field that changes which token comes back is in the key, and nothing else is.
    /// Leaving the scope out would hand a request asking for <c>orders.write</c> a cached
    /// token carrying only <c>orders.read</c> - which fails at the API with a message
    /// about permissions and no hint that the cache is the reason. The client secret is in
    /// it too, so rotating a secret takes effect at once rather than at the old token's
    /// expiry.
    /// </remarks>
    public TokenCacheKey CacheKey => new(TokenUrl.AbsoluteUri, ClientId, ClientSecret, Scope, Audience, Placement);

    /// <summary>
    /// Overridden because the compiler-generated version prints every property, and one of
    /// them is a client secret.
    /// </summary>
    /// <remarks>
    /// A record's <c>ToString</c> is the quietest way a credential reaches a screen: it
    /// needs nobody to have written the secret into a message, only for someone to have
    /// interpolated the object into one. The same applies to
    /// <see cref="TokenCacheKey"/> and to <see cref="OAuth2Token"/>.
    /// </remarks>
    public override string ToString() => $"client-credentials grant against {TokenUrl.AbsoluteUri}";
}

/// <summary>
/// The identity of a cached token. Held in memory for the life of the process and never
/// written anywhere.
/// </summary>
public readonly record struct TokenCacheKey(
    string TokenUrl,
    string ClientId,
    string ClientSecret,
    string? Scope,
    string? Audience,
    ClientAuthPlacement Placement)
{
    /// <inheritdoc cref="ResolvedOAuth2Grant.ToString"/>
    public override string ToString() => $"token for {ClientId} at {TokenUrl}";
}
