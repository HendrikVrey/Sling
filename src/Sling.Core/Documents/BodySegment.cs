namespace Sling.Core.Documents;

/// <summary>
/// One piece of a request body as written in the document.
/// </summary>
/// <remarks>
/// <para>
/// A body is a sequence rather than a string because of <c>&lt; ./file</c>. The reference
/// dialect has no separate multipart syntax: a multipart body is written out in full,
/// with a <c>&lt; ./file</c> line standing in for each part's content. So supporting the
/// import line correctly — including in the middle of a body, and including bytes that
/// are not text — <em>is</em> supporting multipart.
/// </para>
/// <para>
/// The segments carry no file contents. <c>Sling.Core</c> never touches the disk;
/// <see cref="Sling.Core.Variables.IRequestFileSource"/> is how the bytes arrive, and
/// where the rules about which files a document may read are enforced.
/// </para>
/// </remarks>
public abstract record BodySegment;

/// <summary>Literal body text, still holding any <c>{{references}}</c> it was written with.</summary>
public sealed record BodyText(string Value) : BodySegment;

/// <summary>
/// A <c>&lt; ./payload.json</c> line: the named file's contents belong here.
/// </summary>
/// <param name="Path">
/// The path as written, which may itself contain <c>{{references}}</c>. Always resolved
/// relative to the document — an absolute path is refused, because a request file is
/// something people share.
/// </param>
/// <param name="Interpolate">
/// True for the <c>&lt;@</c> form, which reads the file as text and substitutes variables
/// inside it. The plain <c>&lt;</c> form copies the bytes through untouched, which is the
/// only form that can carry a PNG.
/// </param>
/// <param name="Encoding">
/// The charset named by <c>&lt;@utf16 ./file</c>, or null for UTF-8. Only ever set on the
/// <c>&lt;@</c> form, because that is the only spelling the pattern reaches it through.
/// </param>
/// <param name="Line">1-based line the import was written on, so a failure points at it.</param>
/// <param name="AsWritten">
/// The path with its <c>{{references}}</c> still braced, which is what any diagnostic
/// quotes.
/// </param>
/// <remarks>
/// <paramref name="AsWritten"/> exists because <paramref name="Path"/> is substituted, and
/// a substituted path routinely contains a secret — <c>&lt; ./{{token}}.json</c> resolves
/// before anything can fail on it. Reporting the resolved form would put a credential on
/// screen, which <see cref="ParseDiagnostic"/> promises never happens. The same split as
/// <c>RequestResolver.TryBuildUrl</c>'s <c>asWritten</c> parameter, for the same reason.
/// </remarks>
public sealed record BodyFile(string Path, bool Interpolate, string? Encoding, int Line, string AsWritten)
    : BodySegment
{
    public BodyFile(string path, bool interpolate, string? encoding, int line)
        : this(path, interpolate, encoding, line, path)
    {
    }
}
