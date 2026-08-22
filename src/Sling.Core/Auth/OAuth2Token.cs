using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Sling.Core.Parsing;

namespace Sling.Core.Auth;

/// <summary>
/// An access token, already checked for anything that could not safely go in a header.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The validation is the reason this type exists.</strong> An access token is a
/// string taken out of a response body — untrusted input, exactly like a chained
/// <c>{{login.response.body.$.token}}</c> value, and <c>Sling.md</c> §5.7 applies to it
/// unchanged. A token containing CR or LF appended to <c>Authorization: Bearer </c> would
/// add headers of its own to every request the grant covers. Constructing one is only
/// possible through <see cref="TryCreate"/>, so there is no route from a JSON body to a
/// header that skips the check.
/// </para>
/// <para>
/// Tokens live in memory only, for the life of the process. Nothing writes one to disk:
/// history stores them redacted, and there is no token cache file.
/// </para>
/// </remarks>
public sealed class OAuth2Token
{
    /// <summary>
    /// How long before the stated expiry a token is treated as spent.
    /// </summary>
    /// <remarks>
    /// Covers the flight time of the request the token is about to be used on, plus any
    /// disagreement between this machine's clock and the authorization server's. Without
    /// it a token fetched with one second left is dutifully cached and then rejected.
    /// </remarks>
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);

    private OAuth2Token(string accessToken, string tokenType, DateTimeOffset? expiresUtc)
    {
        AccessToken = accessToken;
        TokenType = tokenType;
        ExpiresUtc = expiresUtc;
    }

    /// <summary>The token itself. A credential — never log, display or persist it.</summary>
    public string AccessToken { get; }

    /// <summary>
    /// The scheme for the <c>Authorization</c> header, normalised to the casing RFC 6750
    /// uses. <c>Bearer</c> in practice; a server that answers <c>bearer</c> means the same
    /// thing and some gateways compare the scheme case-sensitively.
    /// </summary>
    public string TokenType { get; }

    /// <summary>
    /// When the token stops being valid, or null when the server did not say.
    /// </summary>
    /// <remarks>
    /// Null is not "never expires" — it is "unknown", and it is handled as such: a token
    /// with no stated lifetime is used once and not cached. RFC 6749 §5.1 only recommends
    /// <c>expires_in</c>, and guessing an hour for a server that meant five minutes
    /// produces a run of confusing 401s in the middle of a session.
    /// </remarks>
    public DateTimeOffset? ExpiresUtc { get; }

    /// <summary>The value to put in an <c>Authorization</c> header.</summary>
    public string HeaderValue => $"{TokenType} {AccessToken}";

    /// <summary>True when this token may still be reused at <paramref name="nowUtc"/>.</summary>
    public bool IsUsableAt(DateTimeOffset nowUtc) =>
        ExpiresUtc is { } expires && nowUtc + ExpiryMargin < expires;

    /// <summary>
    /// Builds a token, refusing anything that could not go in a header.
    /// </summary>
    /// <param name="error">
    /// Why it was refused, naming the offending code point rather than the value. An
    /// access token is the single most sensitive string in the process and a diagnostic is
    /// not a place to print one.
    /// </param>
    public static bool TryCreate(
        string? accessToken,
        string? tokenType,
        DateTimeOffset? expiresUtc,
        [NotNullWhen(true)] out OAuth2Token? token,
        [NotNullWhen(false)] out string? error)
    {
        token = null;

        if (string.IsNullOrEmpty(accessToken))
        {
            error = "the authorization server returned an empty access token";
            return false;
        }

        if (!accessToken.All(HttpSyntax.IsLegalHeaderValueChar))
        {
            error = "the access token contains "
                + HttpSyntax.DescribeFirstIllegal(accessToken, HttpSyntax.IsLegalHeaderValueChar)
                + ", which cannot go in a header";
            return false;
        }

        var scheme = NormalizeTokenType(tokenType);

        // The scheme is a header token, so the same rules as a header name apply. A server
        // answering with a token_type of 'Bearer\r\nX-Admin: 1' is the shape this refuses.
        if (!HttpSyntax.IsToken(scheme))
        {
            error = $"'{scheme}' is not a usable token type";
            return false;
        }

        token = new OAuth2Token(accessToken, scheme, expiresUtc);
        error = null;
        return true;
    }

    /// <summary>
    /// Reads an RFC 6749 §5.1 token response, or the §5.2 error that came instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walked by hand rather than deserialised, the same way environment files are: the
    /// interesting failures are all "the server sent something else", and walking turns
    /// each of them into a sentence rather than an exception naming a type nobody has
    /// heard of. It also keeps the project free of source generation, which it needs to
    /// stay AOT-compatible.
    /// </para>
    /// <para>
    /// <c>expires_in</c> is a JSON number by specification, and several real servers send
    /// it as a string. Both are read, because refusing the string form would mean the
    /// feature does not work against those servers for a reason the user cannot fix.
    /// </para>
    /// </remarks>
    /// <param name="nowUtc">The instant the response arrived, which <c>expires_in</c> is relative to.</param>
    public static bool TryParseResponse(
        string json,
        DateTimeOffset nowUtc,
        [NotNullWhen(true)] out OAuth2Token? token,
        [NotNullWhen(false)] out string? error)
    {
        token = null;

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json ?? string.Empty);
        }
        catch (JsonException ex)
        {
            error = $"the token response is not JSON ({ex.Message})";
            return false;
        }
        catch (ArgumentException ex)
        {
            // Parse transcodes to UTF-8 first, so a lone surrogate arrives here rather
            // than as a JsonException.
            error = $"the token response is not valid text ({ex.Message})";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "the token response is not a JSON object";
                return false;
            }

            // RFC 6749 §5.2. Reported before the missing-token message, because "invalid
            // client" is the answer the user needs and "no access_token field" is a
            // description of the same response that helps nobody.
            if (TryReadString(root, "error", out var code))
            {
                var described = TryReadString(root, "error_description", out var description)
                    ? $"{code}: {description}"
                    : code;

                error = $"the authorization server refused the grant ({described})";
                return false;
            }

            if (!TryReadString(root, "access_token", out var accessToken))
            {
                error = "the token response has no 'access_token'";
                return false;
            }

            TryReadString(root, "token_type", out var tokenType);

            var expires = TryReadExpiresIn(root, out var seconds)
                ? SafeAdd(nowUtc, seconds)
                : (DateTimeOffset?)null;

            return TryCreate(accessToken, tokenType, expires, out token, out error);
        }
    }

    /// <summary>
    /// Upper-cases the first letter and lower-cases the rest, so <c>bearer</c> and
    /// <c>BEARER</c> both become <c>Bearer</c>. Defaults to <c>Bearer</c>, which RFC 6750
    /// makes the only type this grant realistically produces.
    /// </summary>
    private static string NormalizeTokenType(string? tokenType)
    {
        if (string.IsNullOrWhiteSpace(tokenType))
        {
            return "Bearer";
        }

        var trimmed = tokenType.Trim();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
    }

    private static bool TryReadString(JsonElement root, string name, [NotNullWhen(true)] out string? value)
    {
        value = null;

        // ValueKind is checked before GetString: it throws for anything that is not a
        // string or null, and a server sending access_token as a number would otherwise
        // take down the send rather than produce a message.
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryReadExpiresIn(JsonElement root, out long seconds)
    {
        seconds = 0;

        if (!root.TryGetProperty("expires_in", out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            // TryGetInt64 throws when the element is not a Number — the Try only suppresses
            // a malformed number — so the kind has to be checked first.
            JsonValueKind.Number => element.TryGetInt64(out seconds) && seconds > 0,
            JsonValueKind.String => long.TryParse(
                element.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out seconds) && seconds > 0,
            _ => false,
        };
    }

    /// <summary>
    /// Adds seconds without overflowing. An <c>expires_in</c> of <see cref="long.MaxValue"/>
    /// is an ordinary thing for a broken server to send, and <see cref="DateTimeOffset"/>
    /// throws on it.
    /// </summary>
    private static DateTimeOffset SafeAdd(DateTimeOffset from, long seconds)
    {
        var remaining = (DateTimeOffset.MaxValue - from).TotalSeconds;
        return seconds >= remaining ? DateTimeOffset.MaxValue : from.AddSeconds(seconds);
    }

    /// <inheritdoc cref="ResolvedOAuth2Grant.ToString"/>
    public override string ToString() =>
        $"{TokenType} token expiring {ExpiresUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "(unstated)"}";
}
