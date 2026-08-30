using System.Text;
using Sling.Core.Documents;
using Sling.Core.Variables;

namespace Sling.Core.Auth;

/// <summary>
/// Builds the token request an <see cref="ResolvedOAuth2Grant"/> implies.
/// </summary>
/// <remarks>
/// <para>
/// It produces a <see cref="ResolvedRequest"/> rather than something bespoke, which means
/// the token exchange goes down the same path as every other request: the same redirect
/// policy, the same cross-origin credential stripping, the same timeout, the same body
/// cap - and it lands in <c>RunResult.Exchanges</c>, so a call Sling made on the user's
/// behalf is visible like every other one.
/// </para>
/// <para>
/// The request is built entirely from percent-encoded fields, so a client secret
/// containing an ampersand cannot add a form field and one containing a newline cannot
/// add a header. That is <c>Sling.md</c> §5.7's rule reached structurally rather than by
/// checking: there is no point at which a value is concatenated into syntax it could
/// break.
/// </para>
/// </remarks>
public static class OAuth2TokenRequest
{
    private static readonly UTF8Encoding FormEncoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Builds the <c>client_credentials</c> POST for <paramref name="grant"/>.</summary>
    public static ResolvedRequest Build(ResolvedOAuth2Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        var form = new StringBuilder("grant_type=client_credentials");

        Append(form, "scope", grant.Scope);
        Append(form, "audience", grant.Audience);

        // Every header is anchored to the '# @auth' line rather than to line 0: these
        // headers are not in the document, but the directive that caused them is, and that
        // is the line a person can act on.
        var headers = new List<HeaderField>
        {
            new("Content-Type", "application/x-www-form-urlencoded", grant.Line),

            // Asking for JSON explicitly. RFC 6749 §5.1 requires a JSON body, and a
            // gateway in front of the authorization server may still content-negotiate
            // its way to something else if nothing says otherwise.
            new("Accept", "application/json", grant.Line),
        };

        if (grant.Placement == ClientAuthPlacement.BasicHeader)
        {
            headers.Add(new HeaderField("Authorization", BasicCredential(grant), grant.Line));
        }
        else
        {
            Append(form, "client_id", grant.ClientId);
            Append(form, "client_secret", grant.ClientSecret);
        }

        return new ResolvedRequest(
            // Null, so the token response is never stored under a name. A response in the
            // chain store is substitutable into any later request, and the one response in
            // the process that must not be casually interpolated is this one.
            Name: null,
            Method: "POST",
            Url: grant.TokenUrl,
            Headers: headers,
            Body: FormEncoding.GetBytes(form.ToString()),
            Version: null,
            Auth: null,

            // The one request in Sling that refuses to be redirected. Its URL was checked
            // for being HTTPS before it was built, and that check covers exactly one hop;
            // under 'client-auth body' the client secret is the body, which 307 and 308
            // carry across an origin change untouched. Refusing the redirect is what makes
            // the HTTPS rule a property of where the secret actually goes rather than of
            // where it was first addressed.
            FollowRedirects: false);
    }

    /// <summary>
    /// The <c>Authorization: Basic</c> value for a client-credentials grant.
    /// </summary>
    /// <remarks>
    /// RFC 6749 §2.3.1 says the identifier and secret are form-urlencoded <em>before</em>
    /// being joined with a colon and base64-encoded. Skipping that step is invisible for
    /// the usual alphanumeric secret and wrong for one containing a colon, a plus or a
    /// space - where it produces a credential the server splits in the wrong place and
    /// reports as invalid, with the true cause two layers down.
    /// </remarks>
    private static string BasicCredential(ResolvedOAuth2Grant grant)
    {
        var credential = $"{Uri.EscapeDataString(grant.ClientId)}:{Uri.EscapeDataString(grant.ClientSecret)}";
        return "Basic " + Convert.ToBase64String(FormEncoding.GetBytes(credential));
    }

    /// <summary>
    /// Appends one form field, percent-encoded.
    /// </summary>
    /// <remarks>
    /// <see cref="Uri.EscapeDataString(string)"/> writes a space as <c>%20</c> rather than
    /// as <c>+</c>. Both decode to a space under every form-urlencoded reader, and
    /// <c>%20</c> is unambiguous in a way <c>+</c> is not - which matters here because a
    /// scope is a space-separated list and a literal <c>+</c> inside a scope value would
    /// otherwise be indistinguishable from a separator.
    /// </remarks>
    private static void Append(StringBuilder form, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        form.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
    }
}
