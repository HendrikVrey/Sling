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
public sealed record ResolvedRequest(
    string? Name,
    string Method,
    Uri Url,
    IReadOnlyList<HeaderField> Headers,
    byte[]? Body,
    string? Version);
