using System.Text;

namespace Sling.Persistence.Workspaces;

/// <summary>
/// Creating a collection or a request document inside an open workspace.
/// </summary>
/// <remarks>
/// <para>
/// Two operations, and both are creations. <b>There is deliberately no rename and no
/// delete here.</b> Renaming a folder breaks every <c>&lt; ./file</c> body import that
/// pointed into it and moves the document that may be open and unsaved; deleting one
/// destroys a git artifact with no recycle bin behind it on a <c>net10.0</c> target. Both
/// are ordinary operations in a file manager, where the user has undo, history and time to
/// think — and neither is what a rail is for. Sling's answer to "rename this collection" is
/// that it is a directory and there is already a tool for that.
/// </para>
/// <para>
/// <b>Nothing is ever overwritten.</b> A document is created with
/// <see cref="FileMode.CreateNew"/> rather than after a <see cref="File.Exists"/> check, so
/// a file that appears between the two is refused by the file system rather than replaced —
/// the check and the write are one operation. Replacing a request file somebody wrote by
/// hand is not recoverable from inside Sling.
/// </para>
/// </remarks>
public static class WorkspaceEditor
{
    /// <summary>The document a new collection is seeded with.</summary>
    /// <remarks>
    /// A collection is a directory, and <see cref="Workspace.RequestFiles"/> only reports
    /// files — so an empty directory is invisible to the rail that just created it. Seeding
    /// one document is what makes a new collection something you can see and immediately
    /// type into, and it is also what gives the "new request" command somewhere to append.
    /// </remarks>
    public const string SeedDocumentName = "requests";

    /// <summary>
    /// The encoding documents are written in.
    /// </summary>
    /// <remarks>
    /// Constructed rather than <see cref="Encoding.UTF8"/>, whose singleton emits a byte
    /// order mark — and a <c>.http</c> file that starts with one is a file whose first
    /// request line does not parse in half the tools that read the format. The same reason
    /// <see cref="RequestFileStore"/> and <see cref="ImportStore"/> do it.
    /// </remarks>
    private static readonly UTF8Encoding FileEncoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Creates a collection under <paramref name="parentRelative"/>, and one document in it.
    /// </summary>
    /// <param name="parentRelative">
    /// Where to put it, relative to the workspace root; null or empty means the root itself.
    /// </param>
    /// <param name="typedName">What the user typed. Reduced to one segment first.</param>
    /// <returns>
    /// The path of the seeded document, relative to the root, so the caller can open it.
    /// </returns>
    /// <exception cref="ArgumentException">The name reduces to nothing usable.</exception>
    /// <exception cref="IOException">
    /// The collection already exists, the parent is not inside the workspace, or the write
    /// failed.
    /// </exception>
    public static async Task<string> CreateCollectionAsync(
        Workspace workspace,
        string? parentRelative,
        string typedName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!WorkspaceNames.TryToSegment(typedName, out var segment, out var reason))
        {
            throw new ArgumentException(reason, nameof(typedName));
        }

        var parent = ResolveContainer(workspace, parentRelative);
        var folder = Path.Combine(parent, segment);

        if (Directory.Exists(folder) || File.Exists(folder))
        {
            throw new IOException($"There is already something called '{segment}' there.");
        }

        Directory.CreateDirectory(folder);

        var document = Path.Combine(folder, SeedDocumentName + WorkspaceNames.DocumentExtension);

        try
        {
            await CreateAsync(document, SeedText(segment), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Unwound, because a half-made collection is worse than none: the caller reports
            // that it could not be created, and an empty directory left behind then makes the
            // next attempt at the same name fail with "there is already something called
            // that". Only ever removed when it is empty, so a directory that somehow already
            // held something survives.
            try
            {
                Directory.Delete(folder, recursive: false);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }

        return Path.GetRelativePath(workspace.Root, document);
    }

    /// <summary>
    /// Creates a request document under <paramref name="parentRelative"/>.
    /// </summary>
    /// <returns>Its path relative to the workspace root.</returns>
    /// <exception cref="ArgumentException">The name reduces to nothing usable.</exception>
    /// <exception cref="IOException">
    /// The document already exists, the parent is not inside the workspace, or the write
    /// failed.
    /// </exception>
    public static async Task<string> CreateDocumentAsync(
        Workspace workspace,
        string? parentRelative,
        string typedName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!WorkspaceNames.TryToDocumentStem(typedName, out var stem, out var reason))
        {
            throw new ArgumentException(reason, nameof(typedName));
        }

        var parent = ResolveContainer(workspace, parentRelative);
        var document = Path.Combine(parent, stem + WorkspaceNames.DocumentExtension);

        await CreateAsync(document, SeedText(stem), cancellationToken).ConfigureAwait(false);

        return Path.GetRelativePath(workspace.Root, document);
    }

    /// <summary>
    /// The text one new request is appended to a document as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built here rather than in the window, because the name has to go through
    /// <see cref="WorkspaceNames"/> before it reaches the text and that rule lives in this
    /// project. <b>It is the same hazard the Postman importer's descriptions were:</b>
    /// generated text is fed straight back through the <c>.http</c> parser, so what matters
    /// is not only what a value may contain but what the parser reads at the <em>start of a
    /// line</em> — <c># @name login</c> is a directive, not documentation. The segment rule's
    /// whitelist has no <c>@</c>, <c>#</c> or newline in it, so the injection is impossible
    /// by construction rather than by escaping.
    /// </para>
    /// <para>
    /// The target is left as a bare scheme. A placeholder host would be a request that can
    /// be sent by accident, and a real one is a request that goes somewhere nobody asked
    /// for.
    /// </para>
    /// </remarks>
    /// <param name="existingText">
    /// What the document already holds. Only its ending is read, and reading it here rather
    /// than passing a flag is the point: the caller cannot get the separator wrong.
    /// </param>
    /// <param name="newLine">
    /// The document's own terminator. A file loaded from a checkout with CRLF endings would
    /// otherwise gain one LF line in the middle of it — invisible in the editor, and a
    /// whole-file diff for whoever reviews it next.
    /// </param>
    /// <exception cref="ArgumentException">The name reduces to nothing usable.</exception>
    public static string RequestBlockText(string existingText, string typedName, string newLine)
    {
        ArgumentNullException.ThrowIfNull(existingText);
        ArgumentException.ThrowIfNullOrEmpty(newLine);

        if (!WorkspaceNames.TryToSegment(typedName, out var segment, out var reason))
        {
            throw new ArgumentException(reason, nameof(typedName));
        }

        return Separator(existingText, newLine)
            + "### " + segment + newLine
            + "GET https://" + newLine;
    }

    /// <summary>
    /// What has to go between the document and the block appended to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two separate questions, and asking only one of them was a bug.</b> The first is
    /// whether the <c>###</c> would start a line at all — <c>IsSeparator</c> tests
    /// <c>StartsWith("###")</c> with no trim, so appending to a document ending in a space
    /// produces <c>"   ### orders"</c>, which parses as a <em>comment</em> and silently
    /// discards the name the user just typed. The second is whether a blank line is wanted,
    /// which is only about readability.
    /// </para>
    /// <para>
    /// An earlier version asked "is there any content?" and answered both from it, so a
    /// buffer holding nothing but whitespace got no separator at all.
    /// </para>
    /// </remarks>
    private static string Separator(string existingText, string newLine)
    {
        if (existingText.Length == 0)
        {
            return string.Empty;
        }

        var atLineStart = existingText.EndsWith('\n');
        var hasContent = existingText.TrimEnd().Length > 0;

        if (!hasContent)
        {
            return atLineStart ? string.Empty : newLine;
        }

        // One blank line above a request that follows another, so the separator is visible
        // rather than butted against the line before it.
        return atLineStart ? newLine : newLine + newLine;
    }

    /// <summary>
    /// Resolves a rail path to a directory inside the workspace, or refuses it.
    /// </summary>
    /// <remarks>
    /// The path comes from the tree, which Sling built from its own walk — so this is a
    /// second line rather than the only one, in the same spirit as <see cref="ImportStore"/>
    /// re-checking a containment that <c>FileNames</c> had already made impossible. The two
    /// would have to be wrong in the same way on the same day.
    /// <para>
    /// The link check is not decoration either: a junction planted inside the workspace is
    /// how a "create a folder here" that looked contained lands somewhere else entirely.
    /// </para>
    /// </remarks>
    private static string ResolveContainer(Workspace workspace, string? parentRelative)
    {
        var root = Path.GetFullPath(workspace.Root);

        if (string.IsNullOrWhiteSpace(parentRelative))
        {
            return root;
        }

        if (Path.IsPathRooted(parentRelative))
        {
            throw new IOException("A collection is created by its path inside the workspace.");
        }

        var resolved = Path.GetFullPath(Path.Combine(root, parentRelative));

        if (!WorkspacePaths.IsWithin(root, resolved))
        {
            throw new IOException($"'{parentRelative}' is outside the workspace.");
        }

        if (!Directory.Exists(resolved))
        {
            throw new IOException($"'{parentRelative}' is not a folder in this workspace any more.");
        }

        if (!WorkspacePaths.DirectoryChainStaysWithin(new DirectoryInfo(resolved), root, out var reason))
        {
            throw new IOException($"'{parentRelative}' cannot be written to: {reason}.");
        }

        return resolved;
    }

    /// <summary>Writes a file that must not already exist.</summary>
    private static async Task CreateAsync(string path, string text, CancellationToken cancellationToken)
    {
        // CreateNew, so "does it exist?" and "write it" are one operation the file system
        // decides rather than two this method races between.
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);

        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(FileEncoding.GetBytes(text), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>What a freshly created document holds.</summary>
    /// <remarks>
    /// One request and one line of orientation. It is the first thing somebody arriving from
    /// Postman sees after clicking "new collection", and an empty file would leave them
    /// looking for the form that is not coming.
    /// </remarks>
    private static string SeedText(string segment) =>
        "# " + segment + "\n"
            + "# Ctrl+Enter sends the request under the caret. '###' starts another one.\n"
            + "\n"
            + "### " + segment + "\n"
            + "GET https://\n";
}
