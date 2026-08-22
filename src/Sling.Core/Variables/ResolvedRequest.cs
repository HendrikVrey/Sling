using Sling.Core.Auth;
using Sling.Core.Documents;

namespace Sling.Core.Variables;

/// <summary>
/// A request with every variable substituted and every field validated — the only shape
/// <c>Sling.Http</c> will send.
/// </summary>
/// <remarks>
/// Making this a separate type from <see cref="RequestBlock"/> is what stops an
/// unresolved request reaching the network by accident: the sender takes one of these
/// and there is no way to construct it except through
/// <see cref="RequestResolver.Resolve"/>, which is where §5.7's injection checks live.
/// </remarks>
/// <param name="Name">The <c>@name</c> the response should be stored under, if any.</param>
/// <param name="Body">
/// The body exactly as it will go on the wire, or null when there is none.
/// </param>
/// <param name="Version">The HTTP version the document asked for, if it asked.</param>
/// <remarks>
/// <para>
/// <paramref name="Body"/> is bytes rather than text because a <c>&lt; ./logo.png</c>
/// import puts arbitrary bytes in the middle of a body, and a PNG does not survive being
/// decoded to a string and re-encoded. Text written in the document is UTF-8 encoded on
/// its way in here, which is what it would have been anyway.
/// </para>
/// <para>
/// An array rather than a <see cref="ReadOnlyMemory{T}"/>, which was the first attempt
/// and is quietly wrong for an <em>optional</em> body. There is an implicit conversion
/// from an array to a memory, so in <c>hasBody ? bytes : null</c> the null literal
/// converts through <c>byte[]</c> and wraps into an <em>empty</em> memory: the expression
/// yields a non-null <c>ReadOnlyMemory&lt;byte&gt;?</c> of length zero, with no warning.
/// "No body" then silently becomes "a body of zero bytes" — a different request, carrying
/// <c>Content-Length: 0</c> and a content object on a <c>GET</c>. Caught by an existing
/// redirect test; nothing in the type system was going to.
/// </para>
/// </remarks>
/// <param name="Auth">
/// The resolved OAuth2 grant this request needs a token from, or null. Not applied here:
/// the token is not known until it has been fetched, so <c>Sling.Http</c> obtains it and
/// adds the <c>Authorization</c> header — which is also what makes the token exchange
/// visible as an exchange of its own.
/// </param>
/// <param name="FollowRedirects">
/// Whether a 3xx may move this request to another URL. True for everything the document
/// asks for; false for the OAuth2 token exchange.
/// </param>
/// <remarks>
/// <para>
/// <paramref name="FollowRedirects"/> lives on the request rather than on the sender's
/// options, and that is the whole point of it. A token request carries the client secret
/// — in the body under <c>client-auth body</c>, where no credential-header rule reaches it
/// — and its URL was checked once, before it was sent, for being HTTPS. A single 307 to
/// another host would hand that secret over, defeat the HTTPS check in one hop, and let
/// whoever answered mint the bearer token attached to the user's real request. Carrying
/// the refusal on the request means it travels with it, instead of being something the
/// code that builds the request has to remember at the call site that sends it.
/// </para>
/// </remarks>
public sealed record ResolvedRequest(
    string? Name,
    string Method,
    Uri Url,
    IReadOnlyList<HeaderField> Headers,
    byte[]? Body,
    string? Version,
    ResolvedOAuth2Grant? Auth = null,
    bool FollowRedirects = true);
