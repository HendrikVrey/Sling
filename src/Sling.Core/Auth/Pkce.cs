using System.Security.Cryptography;
using System.Text;

namespace Sling.Core.Auth;

/// <summary>
/// Proof Key for Code Exchange, RFC 7636.
/// </summary>
/// <remarks>
/// <para>
/// <b>What replaces the client secret in the authorization-code flow.</b> A desktop
/// application cannot keep a secret - whatever it ships with is in the binary on every
/// machine that has it - so the code flow is protected instead by a value invented per
/// attempt: the authorization request carries a hash of it, and the token request carries the
/// value. An intercepted authorization code is useless without the one thing that never left
/// this process.
/// </para>
/// <para>
/// <c>S256</c> and never <c>plain</c>. RFC 7636 §4.2 permits <c>plain</c> only where the
/// client cannot compute SHA-256, which is not a situation .NET is ever in, and a client that
/// offers <c>plain</c> can be downgraded to it by a server that asks.
/// </para>
/// </remarks>
public static class Pkce
{
    /// <summary>
    /// How many random bytes a verifier is built from.
    /// </summary>
    /// <remarks>
    /// Thirty-two bytes becomes forty-three base64url characters, which is exactly the
    /// minimum length RFC 7636 §4.1 allows and 256 bits of entropy. The specification's upper
    /// bound is 128 characters; there is nothing to buy above this.
    /// </remarks>
    private const int VerifierBytes = 32;

    /// <summary>A fresh verifier and the challenge derived from it.</summary>
    /// <remarks>
    /// Generated together and used once. A verifier reused across two attempts would let an
    /// authorization code stolen from the first be redeemed against the second.
    /// </remarks>
    public static PkcePair Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(VerifierBytes));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        return new PkcePair(verifier, challenge);
    }

    /// <summary>
    /// An unguessable value for the <c>state</c> parameter.
    /// </summary>
    /// <remarks>
    /// A different concern from PKCE and needed as well as it. PKCE stops a stolen code being
    /// redeemed; <c>state</c> stops a code Sling never asked for being accepted, which is what
    /// a cross-site request forgery against the redirect looks like. Same generator, same
    /// entropy, separate value.
    /// </remarks>
    public static string State() => Base64Url(RandomNumberGenerator.GetBytes(VerifierBytes));

    /// <summary>
    /// Base64url without padding, which is the only encoding RFC 7636 §4.2 allows.
    /// </summary>
    /// <remarks>
    /// The characters it avoids are the ones that would otherwise have to be percent-encoded
    /// into a URL, which is the whole reason the specification asks for this spelling rather
    /// than ordinary base64.
    /// </remarks>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>One attempt's verifier and the challenge sent in its place.</summary>
/// <param name="Verifier">
/// The value that never leaves the process until the token request. A credential for the
/// length of one exchange, and treated as one.
/// </param>
/// <param name="Challenge">The SHA-256 of the verifier, base64url, which goes in the URL.</param>
public sealed record PkcePair(string Verifier, string Challenge)
{
    /// <summary>
    /// Overridden because the generated one prints the verifier, which is the half that is
    /// secret.
    /// </summary>
    public override string ToString() => "PKCE challenge " + Challenge;
}
