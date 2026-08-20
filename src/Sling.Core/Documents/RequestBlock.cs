namespace Sling.Core.Documents;

/// <summary>
/// One request in a document, exactly as written: variables are still <c>{{braced}}</c>
/// and nothing has been validated as a URL yet.
/// </summary>
/// <remarks>
/// Parsing and substitution are kept apart deliberately, and it is a security property
/// rather than tidiness (<c>Sling.md</c> §5.7). Because the structure — method, target,
/// each header name and value — is fixed before any value is substituted, a value that
/// arrives from a response body cannot add a header or rewrite the request line. It can
/// only ever land inside the one field that referenced it.
/// </remarks>
/// <param name="Name">From a <c># @name login</c> line; the handle other requests chain against.</param>
/// <param name="Title">The text after the <c>###</c> separator, if any. Documentation only.</param>
/// <param name="Method">Upper-cased verb. <c>GET</c> when the document gave only a URL.</param>
/// <param name="Target">The request target as written, variables unresolved.</param>
/// <param name="Version">From a trailing <c>HTTP/1.1</c>, if the line carried one.</param>
/// <param name="FirstLine">
/// 1-based line where this request's text begins, including the <c># @name</c> and
/// comment lines above the request line. Distinct from <paramref name="StartLine"/>
/// because a diagnostic about <c># @name</c> belongs to this request, and filtering by
/// <paramref name="StartLine"/> alone silently discarded every one of them.
/// </param>
/// <param name="StartLine">1-based line of the request line itself.</param>
/// <param name="EndLine">1-based line of the last line belonging to this request.</param>
public sealed record RequestBlock(
    string? Name,
    string? Title,
    string Method,
    string Target,
    string? Version,
    IReadOnlyList<HeaderField> Headers,
    string? Body,
    int FirstLine,
    int StartLine,
    int EndLine);
