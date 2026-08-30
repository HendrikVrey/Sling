namespace Sling.Persistence.Workspaces;

/// <summary>What one row in the collections rail stands for on disk.</summary>
public enum CollectionEntryKind
{
    /// <summary>A directory — what Sling calls a collection.</summary>
    Folder,

    /// <summary>A <c>.http</c> or <c>.rest</c> document.</summary>
    Document,
}

/// <summary>
/// One node of the collections tree.
/// </summary>
/// <param name="Name">The single path segment this node adds.</param>
/// <param name="RelativePath">
/// The whole path from the workspace root, with <c>/</c> separators regardless of platform
/// — it is joined back onto the root before it is used, and a stable separator is what
/// makes the tree comparable in a test.
/// </param>
/// <param name="Children">Folders first, then documents; each group ordered by name.</param>
public sealed record CollectionEntry(
    string Name,
    string RelativePath,
    CollectionEntryKind Kind,
    IReadOnlyList<CollectionEntry> Children);

/// <summary>
/// Builds the collections tree out of a flat list of request-file paths.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the whole of "there are no collections", made visible.</b> <c>Sling.md</c> §1
/// is unchanged: a collection is a folder of <c>.http</c> files, hierarchy is directories,
/// and sharing is <c>git push</c>. What was missing was a way to <em>see</em> that, which
/// is a different question from what the artifact is — Postman's tree is a good affordance
/// attached to a bad format, and there is no reason to give up the affordance along with
/// the format.
/// </para>
/// <para>
/// So this owns no state and invents no metadata. There is no collection file, no index and
/// no ordering to persist: the tree is a projection of what the walk found, recomputed
/// whenever the folder is re-read. Anything that had to be stored to make the rail work
/// would be a workspace format arriving through the back door.
/// </para>
/// <para>
/// <b>A folder holding no request files is not in the tree</b>, because the walk that feeds
/// this only reports files. That is the right answer for a checkout full of source
/// directories, and it is why creating a collection also creates a document inside it —
/// see <see cref="WorkspaceEditor"/>.
/// </para>
/// </remarks>
public static class CollectionTree
{
    /// <summary>
    /// Groups <paramref name="relativePaths"/> into folders and documents.
    /// </summary>
    /// <param name="relativePaths">
    /// Paths relative to the workspace root, as <see cref="Workspace.RequestFiles"/> returns
    /// them. Anything absolute, or that climbs above the root, is dropped: this is fed by a
    /// walk that cannot produce one, and a tree node whose path escapes the workspace would
    /// be a node every command in the rail then acts on.
    /// </param>
    public static IReadOnlyList<CollectionEntry> Build(IReadOnlyList<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);

        var root = new Node(string.Empty);

        foreach (var path in relativePaths)
        {
            var segments = Split(path);

            if (segments.Count == 0)
            {
                continue;
            }

            var folder = root;

            for (var i = 0; i < segments.Count - 1; i++)
            {
                folder = folder.Folder(segments[i]);
            }

            folder.Documents.Add(segments[^1]);
        }

        return root.ToEntries(string.Empty);
    }

    /// <summary>
    /// Splits a relative path, refusing anything that is not a plain descent.
    /// </summary>
    /// <remarks>
    /// An empty list means "do not put this in the tree". Both separators are accepted
    /// because the caller may be a test written with either.
    /// </remarks>
    private static List<string> Split(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return [];
        }

        var segments = path
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        return segments.Exists(s => s is "." or "..") ? [] : segments;
    }

    /// <summary>The mutable half, discarded once the immutable tree is built.</summary>
    private sealed class Node(string name)
    {
        private readonly Dictionary<string, Node> _folders = new(StringComparer.OrdinalIgnoreCase);

        public string Name { get; } = name;

        public List<string> Documents { get; } = [];

        public Node Folder(string segment)
        {
            // Keyed case-insensitively because Windows paths are, and the walk that feeds
            // this reports whatever casing the file system gave it — two spellings of one
            // directory would otherwise become two collections holding half the files each.
            if (!_folders.TryGetValue(segment, out var child))
            {
                child = new Node(segment);
                _folders[segment] = child;
            }

            return child;
        }

        public List<CollectionEntry> ToEntries(string prefix)
        {
            var entries = new List<CollectionEntry>();

            // Folders before documents, each group by name. The same order Postman, VS Code
            // and every file tree people already use put them in; a rail that sorts
            // differently is a rail whose contents move when a file is added.
            foreach (var folder in _folders.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var path = prefix + folder.Name;

                entries.Add(new CollectionEntry(
                    folder.Name,
                    path,
                    CollectionEntryKind.Folder,
                    folder.ToEntries(path + "/")));
            }

            foreach (var document in Documents.Order(StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new CollectionEntry(
                    document,
                    prefix + document,
                    CollectionEntryKind.Document,
                    []));
            }

            return entries;
        }
    }
}
