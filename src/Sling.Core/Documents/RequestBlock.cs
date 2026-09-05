namespace Sling.Core.Documents;

/// <summary>
/// One request in a document, exactly as written: variables are still <c>{{braced}}</c>
/// and nothing has been validated as a URL yet.
/// </summary>
/// <remarks>
/// Parsing and substitution are kept apart deliberately, and it is a security property
/// rather than tidiness (<c>Sling.md</c> §5.7). Because the structure - method, target,
/// each header name and value - is fixed before any value is substituted, a value that
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
/// <param name="Body">
/// The body in source order, or null when the request has none. A list rather than a
/// string because a <c>&lt; ./file</c> line inside it stands for bytes that are not in
/// the document at all - see <see cref="BodySegment"/>.
/// </param>
/// <param name="Auth">
/// The OAuth2 client-credentials grant a <c># @auth oauth2</c> block declared, or null.
/// Its values are still <c>{{braced}}</c>, like every other field here - which is what
/// keeps a client secret out of any diagnostic that quotes the grant.
/// </param>
public sealed record RequestBlock(
    string? Name,
    string? Title,
    string Method,
    string Target,
    string? Version,
    IReadOnlyList<HeaderField> Headers,
    IReadOnlyList<BodySegment>? Body,
    Auth.OAuth2Grant? Auth,
    int FirstLine,
    int StartLine,
    int EndLine)
{
    /// <summary>
    /// 1-based line of the <c>###</c> separator that introduced this request, or 0 when
    /// nothing above it did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately outside <see cref="FirstLine"/>, which begins below the separator. The
    /// separator ends the request above and titles the one below, so as far as parsing goes
    /// it belongs to neither - a diagnostic about it is not a diagnostic about either
    /// request, which is the distinction <see cref="FirstLine"/> exists to draw.
    /// </para>
    /// <para>
    /// It matters anyway, because a <em>reader</em> does not see it that way: the line below
    /// the <c>###</c> is where the request's text starts, and the <c>###</c> is where the
    /// request starts. Anything showing one request on its own needs the second answer, and
    /// deriving it by looking one line up would be a second copy of
    /// <see cref="Sling.Core.Parsing.HttpGrammar"/>'s separator rule.
    /// </para>
    /// </remarks>
    public int TitleLine { get; init; }

    /// <summary>
    /// The body's literal text, with every <c>&lt; ./file</c> import left out.
    /// </summary>
    /// <remarks>
    /// For showing and for testing, never for sending. An import contributes bytes this
    /// property cannot represent, so anything that sent it would drop them silently.
    /// Assembling a body that is actually sendable is
    /// <see cref="Sling.Core.Variables.RequestResolver"/>'s job, and it is the only place
    /// the two kinds of segment are put together.
    /// </remarks>
    public string? LiteralText =>
        Body is null ? null : string.Concat(Body.OfType<BodyText>().Select(s => s.Value));
}
