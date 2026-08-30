using System.Text;

namespace Sling.Core.Auth;

/// <summary>
/// The URL the browser is sent to, for the authorization-code flow.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and separate from the round trip that uses it, so the one thing worth checking about
/// this flow without a browser can be checked: that every parameter RFC 6749 §4.1.1 and RFC
/// 7636 §4.3 require is there, spelled the way they spell it, and percent-encoded.
/// </para>
/// <para>
/// Built by appending encoded pairs rather than by formatting a string, so a scope containing
/// an ampersand cannot add a parameter and a client id containing one cannot replace one.
/// </para>
/// </remarks>
public static class OAuth2AuthorizeRequest
{
    /// <summary>
    /// Where to send the browser for <paramref name="grant"/>.
    /// </summary>
    /// <param name="challenge">The PKCE challenge. Its verifier stays in this process.</param>
    /// <param name="state">
    /// The value the callback has to echo back. Without it, any code arriving at the loopback
    /// listener would be accepted, including one an attacker sent there.
    /// </param>
    /// <exception cref="ArgumentException">The grant is not an authorization-code grant.</exception>
    public static Uri Build(ResolvedOAuth2Grant grant, string challenge, string state)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrEmpty(challenge);
        ArgumentException.ThrowIfNullOrEmpty(state);

        if (grant is not { AuthorizeUrl: { } authorize, RedirectUri: { } redirect })
        {
            throw new ArgumentException(
                "An authorization URL can only be built for a grant that has one.",
                nameof(grant));
        }

        var query = new StringBuilder();

        // Any query the authorization URL was written with is kept. Several providers put a
        // tenant, a connection or an audience there, and dropping it would send the browser
        // somewhere that looks right and answers differently.
        if (authorize.Query.Length > 1)
        {
            query.Append(authorize.Query.AsSpan(1)).Append('&');
        }

        Append(query, "response_type", "code");
        Append(query, "client_id", grant.ClientId);
        Append(query, "redirect_uri", redirect.AbsoluteUri);
        Append(query, "state", state);
        Append(query, "code_challenge", challenge);

        // S256 and never 'plain'. A client that offers plain can be asked for plain, which
        // removes the only thing protecting the code.
        Append(query, "code_challenge_method", "S256");

        Append(query, "scope", grant.Scope);
        Append(query, "audience", grant.Audience);

        return new UriBuilder(authorize) { Query = query.ToString() }.Uri;
    }

    private static void Append(StringBuilder query, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (query.Length > 0 && query[^1] != '&')
        {
            query.Append('&');
        }

        query.Append(name).Append('=').Append(Uri.EscapeDataString(value));
    }
}
