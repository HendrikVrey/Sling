using System.Diagnostics.CodeAnalysis;

namespace Sling.Persistence.Workspaces;

/// <summary>
/// The containment rules every path in a workspace is held to.
/// </summary>
/// <remarks>
/// <para>
/// Shared rather than duplicated, because the two callers guard the same boundary from
/// opposite directions: <see cref="WorkspaceFileSource"/> decides which files a document
/// may <em>read</em>, and <see cref="WorkspaceEditor"/> decides where a new collection may
/// be <em>written</em>. A workspace whose read boundary and write boundary disagree is a
/// workspace where a file can be created somewhere it could never be read from — or,
/// worse, the other way round.
/// </para>
/// <para>
/// Everything here is lexical except <see cref="DirectoryChainStaysWithin"/>, which has to
/// touch the file system: a reparse point is invisible to <see cref="Path.GetFullPath(string)"/>.
/// </para>
/// </remarks>
internal static class WorkspacePaths
{
    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="root"/> or sits beneath it.
    /// </summary>
    /// <remarks>
    /// Compared through <see cref="Path.GetRelativePath"/> rather than by string prefix:
    /// a prefix test says <c>C:\work\api-secrets</c> is inside <c>C:\work\api</c>, which
    /// is the classic way this check is got wrong. Case-insensitive because Windows file
    /// names are.
    /// </remarks>
    internal static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);

        return !Path.IsPathRooted(relative)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(relative, "..", StringComparison.Ordinal);
    }

    internal static bool IsSamePath(string left, string right) =>
        string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether every directory from <paramref name="start"/> up to <paramref name="root"/>
    /// is reached without a link leaving the workspace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The leaf is not enough, and checking only the leaf was a real hole.</strong>
    /// <see cref="FileSystemInfo.ResolveLinkTarget"/> inspects the entry it is called on,
    /// and <see cref="Path.GetFullPath(string)"/> is purely lexical — neither follows a
    /// reparse point on an <em>intermediate</em> component. So a directory symlink or
    /// junction anywhere above a file was invisible to both, and
    /// <c>&lt; ./fixtures/Users/me/.ssh/id_rsa</c> through a committed
    /// <c>fixtures -&gt; ../../</c> link read straight out of the workspace.
    /// </para>
    /// <para>
    /// The walk is bounded by the root, and by Windows' own limit on how many reparse
    /// points it will traverse.
    /// </para>
    /// </remarks>
    internal static bool DirectoryChainStaysWithin(
        DirectoryInfo? start,
        string root,
        [NotNullWhen(false)] out string? reason)
    {
        for (var directory = start;
             directory is not null && !IsSamePath(directory.FullName, root);
             directory = directory.Parent)
        {
            if (directory.ResolveLinkTarget(returnFinalTarget: true) is { } hop
                && !IsWithin(root, Path.GetFullPath(hop.FullName)))
            {
                reason = $"it is reached through '{directory.Name}', which is a link that leaves "
                    + "the workspace";
                return false;
            }
        }

        reason = null;
        return true;
    }
}
