namespace Sling.Core.Navigation;

/// <summary>What one Quick Open row stands for.</summary>
public enum QuickOpenKind
{
    /// <summary>A <c>.http</c> file. Opening it shows the whole document.</summary>
    Document,

    /// <summary>One request inside one. Opening it narrows the pane to that request.</summary>
    Request,
}

/// <summary>
/// One thing Quick Open can take you to, and everything it can be found by.
/// </summary>
/// <param name="Kind">Whether this is a file or a request in one.</param>
/// <param name="Path">The document's absolute path. A request row carries its file's.</param>
/// <param name="File">The file's name, which is the commonest thing anyone types.</param>
/// <param name="Collection">
/// The folder holding the file, relative to the workspace root, or empty at the root.
/// Sling's word for a folder, so that typing the collection narrows to it.
/// </param>
/// <param name="Name">A request's <c>###</c> title or <c># @name</c>, empty on a file.</param>
/// <param name="Method">A request's verb, empty on a file.</param>
/// <param name="Target">A request's URL as written, empty on a file.</param>
/// <param name="Line">A request's 1-based request line, zero on a file.</param>
/// <remarks>
/// <para>
/// Every field here is one somebody would type looking for this row, which is the whole
/// design: a tree of five hundred rows is navigable by scrolling and nothing else, and the
/// things people actually remember about a request are scattered across four of its levels -
/// the collection it is in, the file it is in, what it is called, and what it hits.
/// </para>
/// <para>
/// The URL is carried <em>as written</em>, braces and all. It is what the document says and
/// therefore what somebody would type; the resolved form is a value that may hold a
/// credential, and a search index is not a place for one.
/// </para>
/// </remarks>
public sealed record QuickOpenEntry(
    QuickOpenKind Kind,
    string Path,
    string File,
    string Collection,
    string Name,
    string Method,
    string Target,
    int Line);
