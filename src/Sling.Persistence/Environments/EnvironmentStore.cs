using Sling.Persistence.Workspaces;

namespace Sling.Persistence.Environments;

/// <summary>
/// Loads a workspace's two environment files, and keeps the secrets one out of git.
/// </summary>
public static class EnvironmentStore
{
    /// <summary>
    /// The ignore entries a workspace holding secrets must have.
    /// </summary>
    /// <remarks>
    /// Both spellings: the exact file name, and the pattern that also covers the
    /// per-user variants people end up with (<c>http-client.private.env.json.bak</c> and
    /// friends). Erring wide is free here - an over-broad ignore entry hides a file
    /// someone would have had to justify committing anyway.
    /// </remarks>
    public static IReadOnlyList<string> IgnoreEntries { get; } =
        [Workspace.PrivateEnvironmentFileName, "http-client.private.env.json*"];

    /// <summary>Reads both files for <paramref name="workspace"/>.</summary>
    public static EnvironmentSet Load(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return new EnvironmentSet(
            EnvironmentFile.Load(workspace.SharedEnvironmentFile),
            EnvironmentFile.Load(workspace.PrivateEnvironmentFile));
    }

    /// <summary>
    /// Makes sure the secrets file is ignored, if the workspace has one.
    /// </summary>
    /// <returns>
    /// The ignore entries that had to be added, so the caller can tell the user its
    /// repository was just modified. Empty when there was nothing to do - including when
    /// there is no secrets file, because a repository with no secrets in it should not
    /// have entries written into its <c>.gitignore</c> on Sling's say-so.
    /// </returns>
    /// <remarks>
    /// Called when a workspace is opened rather than only when Sling writes the file
    /// itself. The dangerous case is precisely the one Sling did not create: a secrets
    /// file that arrived by hand, in a repository whose <c>.gitignore</c> has never heard
    /// of it, one <c>git add -A</c> away from being public.
    /// </remarks>
    public static IReadOnlyList<string> ProtectSecrets(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!File.Exists(workspace.PrivateEnvironmentFile))
        {
            return [];
        }

        try
        {
            return GitIgnoreGuard.EnsureIgnored(workspace.Root, IgnoreEntries);
        }
        catch (IOException)
        {
            // A read-only checkout, or a .gitignore someone has open in another editor.
            // Failing to harden is not a reason to fail to open the workspace, and the
            // caller has nothing useful to do about it either.
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
