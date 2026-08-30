using System.Text;

namespace Sling.Persistence.Workspaces;

/// <summary>
/// Keeps the secrets file out of the repository, by writing the <c>.gitignore</c> entry
/// itself rather than trusting the user to.
/// </summary>
/// <remarks>
/// <para>
/// <c>Sling.md</c> §5.1 makes this structural: a committed bearer token is <em>the</em>
/// known failure mode of <c>.http</c> files in the wild, and an instruction in a README
/// is not a defence against it. The moment a workspace contains a private environment
/// file, the ignore entry has to exist, whether Sling created the file or found it.
/// </para>
/// <para>
/// Strictly additive. The file is appended to and never rewritten, reordered or pruned,
/// this is somebody's repository, and the one thing worse than a missing entry here is
/// silently removing a rule that was keeping something else out.
/// </para>
/// </remarks>
public static class GitIgnoreGuard
{
    private const string FileName = ".gitignore";

    private const string Heading = "# Sling - the environment file that holds secrets. Never commit this.";

    /// <summary>
    /// Ensures <paramref name="root"/>'s <c>.gitignore</c> covers <paramref name="patterns"/>.
    /// </summary>
    /// <returns>The patterns that had to be added; empty when nothing needed doing.</returns>
    /// <remarks>
    /// A plain line-by-line comparison, not gitignore semantics. Deciding whether some
    /// existing rule already matches a path means implementing gitignore's precedence,
    /// negation and directory rules - and being wrong about that in the permissive
    /// direction is precisely the failure this exists to prevent. A duplicate entry in a
    /// gitignore file costs nothing.
    /// </remarks>
    public static IReadOnlyList<string> EnsureIgnored(string root, IReadOnlyList<string> patterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(patterns);

        var path = Path.Combine(root, FileName);
        var existing = File.Exists(path)
            ? File.ReadAllLines(path).Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal)
            : [];

        var missing = patterns.Where(p => !existing.Contains(p)).ToArray();
        if (missing.Length == 0)
        {
            return [];
        }

        var addition = new StringBuilder();

        // A file that does not end in a newline would otherwise get the heading welded on
        // to its last rule, turning that rule into something git no longer honours.
        if (File.Exists(path) && new FileInfo(path).Length > 0 && !EndsWithNewline(path))
        {
            addition.Append('\n');
        }

        addition.Append('\n').Append(Heading).Append('\n');
        foreach (var pattern in missing)
        {
            addition.Append(pattern).Append('\n');
        }

        File.AppendAllText(path, addition.ToString());
        return missing;
    }

    private static bool EndsWithNewline(string path)
    {
        using var stream = File.OpenRead(path);
        stream.Seek(-1, SeekOrigin.End);

        return stream.ReadByte() is '\n' or '\r';
    }
}
