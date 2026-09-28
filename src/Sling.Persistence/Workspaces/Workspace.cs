namespace Sling.Persistence.Workspaces;

/// <summary>
/// A folder of <c>.http</c> files, opened rather than owned.
/// </summary>
/// <remarks>
/// <para>
/// Sling has no workspace format, no index and no metadata file. A workspace is whatever
/// folder the user pointed at - very often a checkout of the API's own repository, with
/// the request files sitting beside the code they exercise. That is the whole of
/// <c>Sling.md</c> §1's "a collection becomes a folder of <c>.http</c> files": hierarchy
/// is directories, sharing is <c>git push</c>, and review is a normal diff.
/// </para>
/// <para>
/// The folder is what environments and body imports are resolved against, which is the
/// only reason the concept exists at all.
/// </para>
/// </remarks>
public sealed class Workspace
{
    /// <summary>The environment file that is meant to be committed.</summary>
    public const string SharedEnvironmentFileName = "http-client.env.json";

    /// <summary>
    /// The environment file that holds the secrets and must never be committed.
    /// </summary>
    /// <remarks>
    /// Both names are the convention Rider and Visual Studio 2022 already use, for the
    /// same reason <c>.http</c> itself was chosen (<c>Sling.md</c> §2): someone arriving
    /// from either tool keeps the environments they already have, and someone leaving
    /// takes them along.
    /// </remarks>
    public const string PrivateEnvironmentFileName = "http-client.private.env.json";

    /// <summary>Extensions treated as request documents.</summary>
    private static readonly string[] RequestExtensions = [".http", ".rest"];

    /// <summary>
    /// Directories never walked. Build output and package caches hold thousands of files
    /// and no request the user wrote; <c>.git</c> can hold more objects than the rest of
    /// the tree combined.
    /// </summary>
    private static readonly string[] SkippedDirectories =
        [".git", ".svn", ".hg", ".vs", ".idea", "node_modules", "bin", "obj", "packages", "target"];

    /// <summary>
    /// A ceiling on the listing rather than on the tree.
    /// </summary>
    /// <remarks>
    /// Someone will open their home directory by accident, and the file list is a flat
    /// list in a side rail - past a few hundred entries it has stopped being usable well
    /// before it stops being cheap. Stopping is reported, never silent.
    /// </remarks>
    private const int MaxListedFiles = 2000;

    /// <summary>
    /// A ceiling on directories walked, separate from the one on files found.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxListedFiles"/> bounds the wrong thing on its own: a tree with a
    /// million directories and three <c>.http</c> files never reaches it, and the walk runs
    /// on the dispatcher. Somebody will point this at their home directory by accident.
    /// </remarks>
    private const int MaxWalkedDirectories = 20_000;

    private Workspace(string root) => Root = root;

    /// <summary>The absolute, normalised path of the opened folder.</summary>
    public string Root { get; }

    public string SharedEnvironmentFile => Path.Combine(Root, SharedEnvironmentFileName);

    public string PrivateEnvironmentFile => Path.Combine(Root, PrivateEnvironmentFileName);

    /// <summary>
    /// Opens <paramref name="folder"/>, which must exist.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The folder is not there.</exception>
    public static Workspace Open(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var root = Path.GetFullPath(folder);

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"There is no folder at '{root}'.");
        }

        return new Workspace(root);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is this folder or sits inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The question the window asks before deciding whether a document may keep resolving
    /// against this workspace. A <c>.http</c> file reads its <c>{{variables}}</c> from the
    /// environment files at <see cref="Root"/> and its <c>&lt; ./body.json</c> imports from
    /// inside it, so a document that is not in here resolves against a folder that has
    /// nothing to do with it - which is the same wrong answer whether it arrived through
    /// <c>Ctrl+O</c> or through Save As.
    /// </para>
    /// <para>
    /// Through the same containment rule the read and write boundaries use, rather than a
    /// third copy of it: a string prefix test says <c>C:\work\api-secrets</c> is inside
    /// <c>C:\work\api</c>.
    /// </para>
    /// </remarks>
    public bool Contains(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return WorkspacePaths.IsWithin(Root, Path.GetFullPath(path));
    }

    /// <summary>
    /// Every request file under the root, as paths relative to it, in a stable order.
    /// </summary>
    /// <param name="truncated">
    /// True when <see cref="MaxListedFiles"/> stopped the walk, so the caller can say so
    /// rather than presenting a partial list as a complete one.
    /// </param>
    public IReadOnlyList<string> RequestFiles(out bool truncated)
    {
        var found = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue(Root);

        truncated = false;

        // Directories already walked, so a junction pointing back at an ancestor is visited
        // once rather than to the depth of Windows' reparse-point limit. Without it a
        // self-referencing link listed the same file sixty-four times, at
        // requests\loop\requests\loop\… - not a hang, but sixty-four wrong answers.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Root };

        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();

            foreach (var file in EnumerateSafely(() => Directory.EnumerateFiles(directory)))
            {
                if (!RequestExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (found.Count == MaxListedFiles)
                {
                    truncated = true;
                    break;
                }

                found.Add(Path.GetRelativePath(Root, file));
            }

            if (truncated)
            {
                break;
            }

            foreach (var child in EnumerateSafely(() => Directory.EnumerateDirectories(directory)))
            {
                var name = Path.GetFileName(child);

                if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || name.StartsWith('.'))
                {
                    continue;
                }

                if (seen.Count >= MaxWalkedDirectories)
                {
                    truncated = true;
                    break;
                }

                // Keyed by the link's target where there is one, so two routes to the same
                // directory are one entry.
                var identity = new DirectoryInfo(child).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? child;

                if (seen.Add(identity))
                {
                    pending.Enqueue(child);
                }
            }

            if (truncated)
            {
                break;
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>
    /// Runs an enumeration, treating a folder that cannot be read as an empty one.
    /// </summary>
    /// <remarks>
    /// A tree the user pointed at will contain directories their account cannot open, and
    /// a file list that throws instead of skipping them is a file list that does not work
    /// on a real machine. The enumeration is materialised here on purpose: the exception
    /// from <see cref="Directory.EnumerateFiles(string)"/> surfaces during iteration, not
    /// at the call, so a <c>try</c> around the call alone would catch nothing.
    /// </remarks>
    private static List<string> EnumerateSafely(Func<IEnumerable<string>> enumerate)
    {
        try
        {
            return [.. enumerate()];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            // Deleted between being listed and being walked.
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }
}
