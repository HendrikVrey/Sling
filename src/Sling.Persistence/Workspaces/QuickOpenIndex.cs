using Sling.Core.Documents;
using Sling.Core.Navigation;
using Sling.Core.Parsing;

namespace Sling.Persistence.Workspaces;

/// <param name="Entries">Everything found, files before the requests inside them.</param>
/// <param name="Truncated">
/// True when a bound stopped the walk or the reading, so the caller can say the list is
/// partial rather than presenting it as complete.
/// </param>
public readonly record struct QuickOpenListing(IReadOnlyList<QuickOpenEntry> Entries, bool Truncated);

/// <summary>
/// Reads a workspace into the list Quick Open searches.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built on demand and thrown away, rather than kept in step.</b> The collections rail is
/// already a projection of a folder walk recomputed whenever it is drawn, for the reason
/// <c>Sling.md</c> §1 gives: nothing here is stored, so nothing can go stale, and a
/// <c>.http</c> file is a git artifact that changes without Sling. An index maintained
/// across a pull is an index that is wrong exactly when it matters, and the whole of this
/// takes a few tens of milliseconds on a real workspace.
/// </para>
/// <para>
/// <b>Bounded three ways, because it reads every file in a folder somebody chose.</b> The
/// files listed are already capped by <see cref="Workspace.RequestFiles"/>; on top of that a
/// file too large to be a request document is listed but not read, and the total read is
/// capped so pointing Sling at a checkout full of generated fixtures cannot turn one
/// keystroke into hundreds of megabytes.
/// </para>
/// <para>
/// It is not on the dispatcher's schedule to obey - the caller runs it off the thread - but
/// the bounds are what stop it being slow in the first place.
/// </para>
/// </remarks>
public static class QuickOpenIndex
{
    /// <summary>
    /// The largest file whose requests are read.
    /// </summary>
    /// <remarks>
    /// The same ceiling the rail parses a live buffer under. A document past it is still
    /// listed - somebody looking for the file by name finds it - and only its requests are
    /// missing, which is the half that costs a parse.
    /// </remarks>
    public const long MaxFileBytes = 256L * 1024;

    /// <summary>How much is read across the whole workspace before the rest is listed only.</summary>
    public const long MaxTotalBytes = 8L * 1024 * 1024;

    /// <summary>
    /// The most rows the index holds.
    /// </summary>
    /// <remarks>
    /// <b>The byte budget bounds the reading and not the answer, and those are different
    /// numbers.</b> Eight megabytes of documents whose every line is a <c>###</c> is on the
    /// order of a hundred thousand requests, and the search runs over the whole list on every
    /// keystroke, on the dispatcher. This is the bound on that, and it lives here beside the
    /// other two rather than in the window, so the three are read together.
    /// </remarks>
    public const int MaxEntries = 20_000;

    /// <summary>
    /// Lists every request file in <paramref name="workspace"/> and the requests in them.
    /// </summary>
    /// <param name="workspace">The folder to read.</param>
    /// <param name="openDocumentPath">
    /// The document the window has open, or null. Its requests come from
    /// <paramref name="openDocumentText"/> rather than from disk, so a request typed a moment
    /// ago and not yet saved is still something Quick Open can find.
    /// </param>
    /// <param name="openDocumentText">That document's buffer.</param>
    public static async Task<QuickOpenListing> BuildAsync(
        Workspace workspace,
        string? openDocumentPath,
        string? openDocumentText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var files = workspace.RequestFiles(out var truncated);
        var entries = new List<QuickOpenEntry>(files.Count * 4);

        var open = openDocumentPath is null ? null : Path.GetFullPath(openDocumentPath);
        var budget = MaxTotalBytes;

        foreach (var relative in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entries.Count >= MaxEntries)
            {
                truncated = true;
                break;
            }

            var full = Path.GetFullPath(Path.Combine(workspace.Root, relative));
            var collection = Path.GetDirectoryName(relative) ?? string.Empty;
            var name = Path.GetFileName(relative);

            entries.Add(new QuickOpenEntry(
                QuickOpenKind.Document,
                full,
                name,
                collection,
                Name: string.Empty,
                Method: string.Empty,
                Target: string.Empty,
                Line: 0));

            var isOpen = open is not null
                && string.Equals(full, open, StringComparison.OrdinalIgnoreCase);

            string? text;

            if (isOpen)
            {
                // From the buffer, and it costs nothing: a request added five seconds ago and
                // not yet saved is exactly the one somebody is about to go looking for.
                text = openDocumentText;
            }
            else
            {
                text = await TryReadAsync(full, budget, cancellationToken).ConfigureAwait(false);

                if (text is null)
                {
                    // Too large, unreadable, or past the budget. The file is listed either
                    // way; only its requests are missing.
                    truncated = true;
                    continue;
                }

                budget -= text.Length;
            }

            if (text is null)
            {
                continue;
            }

            foreach (var request in RequestDocumentParser.Parse(text).Requests)
            {
                if (entries.Count >= MaxEntries)
                {
                    truncated = true;
                    break;
                }

                entries.Add(new QuickOpenEntry(
                    QuickOpenKind.Request,
                    full,
                    name,
                    collection,
                    RequestNaming.Describe(request),
                    request.Method,
                    RequestNaming.Clamp(request.Target),
                    request.StartLine));
            }
        }

        return new QuickOpenListing(entries, truncated);
    }

    /// <summary>
    /// Reads one file, or answers null when it is not worth reading.
    /// </summary>
    /// <remarks>
    /// A file that cannot be read is not an error here. A workspace is a folder somebody
    /// pointed at and will hold files their account cannot open, ones a build is writing, and
    /// ones that vanish between the walk and the read - and a palette that refuses to open
    /// because of one of them is a palette that does not work on a real machine.
    /// </remarks>
    private static async Task<string?> TryReadAsync(string path, long budget, CancellationToken cancellationToken)
    {
        try
        {
            var length = new FileInfo(path).Length;

            if (length > MaxFileBytes || length > budget)
            {
                return null;
            }

            return await RequestFileStore.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
