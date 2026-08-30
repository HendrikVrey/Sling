using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sling.Core.Auth;

/// <summary>
/// What a cached token is <em>for</em>, with the secret left out.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TokenCacheKey"/> answers "is this the same grant" and holds the client secret
/// in order to. This answers "which grant was it", which is a question a panel and a token
/// store both need and neither is entitled to a secret for.
/// </para>
/// <para>
/// The split is what lets a token be written to disk at all: the store keeps this and a
/// fingerprint, so a client secret is never written even inside an encrypted blob.
/// </para>
/// </remarks>
/// <param name="TokenUrl">The endpoint the token came from.</param>
/// <param name="ClientId">Which client asked. Half of the identity, and not a secret.</param>
/// <param name="Scope">What was asked for, or null.</param>
/// <param name="Audience">Which API it is for, or null.</param>
public sealed record TokenIdentity(string TokenUrl, string ClientId, string? Scope, string? Audience)
{
    /// <inheritdoc cref="ResolvedOAuth2Grant.ToString"/>
    public override string ToString() => $"token for {ClientId} at {TokenUrl}";
}

/// <summary>
/// A cached token in the form it can be written down as.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one type in Sling that carries an access token somewhere other than
/// memory</b>, so everything about it is arranged around that. It is identified by a
/// fingerprint rather than by the grant, so the client secret is not in it; and its
/// <see cref="ToString"/> is overridden, because a record's generated one prints every
/// property and one of them is a bearer credential.
/// </para>
/// <para>
/// It is a shape, not a store. Reading and writing one is <c>Sling.Persistence</c>'s job,
/// where the encryption is; rebuilding a token from one goes through
/// <see cref="OAuth2Token.TryCreate"/> like every other route, so a store somebody has
/// edited cannot put a newline into an <c>Authorization</c> header.
/// </para>
/// </remarks>
/// <param name="Fingerprint">
/// <see cref="TokenCacheKey.Fingerprint"/>: what decides whether a stored token belongs to
/// the grant being sent. Rotating the client secret changes it, so the old token stops
/// matching at once rather than at its expiry.
/// </param>
/// <param name="AccessToken">The credential. Never logged, displayed, or written unencrypted.</param>
public sealed record PersistedToken(
    string Fingerprint,
    TokenIdentity Identity,
    string AccessToken,
    string TokenType,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset FetchedUtc)
{
    /// <inheritdoc cref="ResolvedOAuth2Grant.ToString"/>
    public override string ToString() =>
        $"stored {TokenType} token for {Identity.ClientId}, expiring "
            + ExpiresUtc.ToString("u", CultureInfo.InvariantCulture);
}

/// <summary>Turns a cache key into the fingerprint everything else identifies it by.</summary>
/// <remarks>
/// <para>
/// A hash rather than the fields, and the reason is the one field that is a secret. Every
/// field that changes which token comes back has to take part - including the client secret,
/// so that rotating one takes effect immediately - and a fingerprint is the only form of
/// "all of them" that can be written to disk or held in a dictionary without keeping the
/// secret alive alongside it.
/// </para>
/// <para>
/// SHA-256 because a collision here would hand one grant's token to another, which is the
/// same class of mistake as sending a staging token to production. A shorter hash would be
/// smaller and would be a place for that to happen.
/// </para>
/// </remarks>
public static class TokenFingerprint
{
    /// <summary>The separator between fields, chosen because none of them may contain it.</summary>
    /// <remarks>
    /// A null character rather than a comma or a colon: a token URL holds colons and a scope
    /// holds spaces, and a separator a field can contain is a separator two different grants
    /// can hash the same way.
    /// </remarks>
    private const char Separator = '\0';

    /// <summary>The fingerprint of <paramref name="key"/>, as lowercase hex.</summary>
    public static string Of(TokenCacheKey key)
    {
        var joined = string.Join(
            Separator,
            key.TokenUrl,
            key.ClientId,
            key.ClientSecret,
            key.Scope ?? string.Empty,
            key.Audience ?? string.Empty,
            key.Placement.ToString());

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }
}
