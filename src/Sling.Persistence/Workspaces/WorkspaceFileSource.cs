using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Sling.Core.Variables;

namespace Sling.Persistence.Workspaces;

/// <summary>
/// Supplies the bytes for a <c>&lt; ./file</c> body import, and decides which files a
/// document is allowed to ask for.
/// </summary>
/// <remarks>
/// <para>
/// Containment is the whole job. A <c>.http</c> file is something people share, paste
/// from a colleague or generate from an imported Postman collection, so
/// <c>&lt; C:\Users\me\.ssh\id_rsa</c> followed by a <c>POST</c> to somewhere else is a
/// perfectly ordinary request document — and Sling would send it without hesitating.
/// Imports therefore resolve inside the workspace and nowhere else.
/// </para>
/// <para>
/// Three rules, and each one exists because the other two do not cover it: the path must
/// be relative, so a document cannot simply name a file elsewhere; the resolved path must
/// sit under the root, so <c>../../..</c> cannot walk out; and the <em>final</em> path
/// after following links must also sit under the root, so a symlink planted in the
/// workspace cannot point out of it.
/// </para>
/// <para>
/// This is a bright line rather than a prompt. A dialog asking "allow this file?" is
/// answered yes by everyone, which is why it is not offered — the way to send a file
/// outside the workspace is to move it in, which is a decision made in a file manager
/// with time to think.
/// </para>
/// </remarks>
public sealed class WorkspaceFileSource : IRequestFileSource
{
    /// <summary>
    /// The cap on a single import. Well above any payload someone is debugging by hand,
    /// and far below the point where reading it stops being survivable.
    /// </summary>
    private const long MaxFileBytes = 32L * 1024 * 1024;

    private readonly string _documentDirectory;
    private readonly string _root;

    /// <summary>
    /// Creates a source for a saved document.
    /// </summary>
    /// <param name="documentDirectory">The folder the <c>.http</c> file lives in.</param>
    /// <param name="workspaceRoot">
    /// The opened workspace, when the document is inside one. Imports may reach anywhere
    /// beneath it — a shared <c>fixtures/</c> folder beside the requests is the ordinary
    /// arrangement. With no workspace, the document's own folder is the boundary.
    /// </param>
    public WorkspaceFileSource(string documentDirectory, string? workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentDirectory);

        _documentDirectory = Path.GetFullPath(documentDirectory);
        _root = workspaceRoot is null ? _documentDirectory : Path.GetFullPath(workspaceRoot);

        // A workspace that does not contain the document is not this document's boundary.
        // It happens whenever a file is opened from outside the open folder, and treating
        // it as the root would widen the boundary to a tree the document is not in.
        if (!WorkspacePaths.IsWithin(_root, _documentDirectory))
        {
            _root = _documentDirectory;
        }
    }

    public bool TryRead(
        string path,
        [NotNullWhen(true)] out byte[]? bytes,
        [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(path);

        bytes = null;

        if (path.Length == 0)
        {
            reason = "no file is named";
            return false;
        }

        if (Path.IsPathRooted(path))
        {
            reason = "an import must be written relative to the request file, so the document "
                + "works for whoever opens it next. Absolute paths are refused";
            return false;
        }

        string resolved;

        try
        {
            resolved = Path.GetFullPath(Path.Combine(_documentDirectory, path));
        }
        catch (ArgumentException)
        {
            reason = "that is not a usable path";
            return false;
        }
        catch (PathTooLongException)
        {
            // GetFullPath throws this as well as ArgumentException, and it escaped the
            // interface — whose contract is that failures come back as text. It surfaced
            // as the window's generic "Sling could not complete the request."
            reason = "that path is too long";
            return false;
        }

        if (!WorkspacePaths.IsWithin(_root, resolved))
        {
            reason = $"it is outside '{_root}'. An import may only read files inside the workspace";
            return false;
        }

        // The workspace's own secrets file sits at the root by definition, so containment
        // alone lets a document read it — a shorter path to the credential than the
        // private key this class was written to refuse.
        if (IsEnvironmentFile(resolved))
        {
            reason = "it is an environment file. Reference its values as {{name}} instead — a "
                + "request file that could read the secrets file could also post it somewhere";
            return false;
        }

        return TryReadContained(resolved, out bytes, out reason);
    }

    /// <summary>
    /// Whether the path names one of the environment files, at any depth.
    /// </summary>
    /// <remarks>
    /// By file name rather than by full path, and both files rather than only the private
    /// one: a nested folder may be somebody else's workspace, and refusing the committed
    /// file too makes the rule "environment files are not body content" instead of a list
    /// of what happens to be sensitive today.
    /// </remarks>
    private static bool IsEnvironmentFile(string resolved)
    {
        var name = Path.GetFileName(resolved);

        return name.Equals(Workspace.PrivateEnvironmentFileName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(Workspace.SharedEnvironmentFileName, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryReadContained(
        string resolved,
        [NotNullWhen(true)] out byte[]? bytes,
        [NotNullWhen(false)] out string? reason)
    {
        bytes = null;

        try
        {
            var info = new FileInfo(resolved);

            if (!info.Exists)
            {
                reason = "there is no such file";
                return false;
            }

            if (!IsReachedWithoutLeaving(info, out reason))
            {
                return false;
            }

            if (info.Length > MaxFileBytes)
            {
                reason = $"it is larger than {(MaxFileBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB";
                return false;
            }

            bytes = File.ReadAllBytes(resolved);
            reason = null;
            return true;
        }
        catch (IOException ex)
        {
            reason = ex.Message;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Whether the file is reached without any link along the way leaving the workspace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The leaf is not enough, and checking only the leaf was a real hole.</strong>
    /// <see cref="FileSystemInfo.ResolveLinkTarget"/> inspects the entry it is called on,
    /// and <see cref="Path.GetFullPath(string)"/> is purely lexical — neither follows a
    /// reparse point on an <em>intermediate</em> component. So a directory symlink or
    /// junction anywhere above the file was invisible to both, and
    /// <c>&lt; ./fixtures/Users/me/.ssh/id_rsa</c> through a committed
    /// <c>fixtures -&gt; ../../</c> link read straight out of the workspace.
    /// </para>
    /// <para>
    /// Every component from the file up to the root is therefore checked. The walk is
    /// bounded by the root, and by Windows' own limit on how many reparse points it will
    /// traverse.
    /// </para>
    /// <para>
    /// <strong>What this cannot see:</strong> a hard link. It is a second name for the
    /// same data with nothing marking it as derived, and no managed API distinguishes one
    /// from an ordinary file. Creating one requires having already written inside the
    /// workspace, which is a larger privilege than this check is defending against.
    /// </para>
    /// </remarks>
    private bool IsReachedWithoutLeaving(FileInfo file, [NotNullWhen(false)] out string? reason)
    {
        if (file.ResolveLinkTarget(returnFinalTarget: true) is { } target
            && !WorkspacePaths.IsWithin(_root, Path.GetFullPath(target.FullName)))
        {
            reason = "it is a link to a file outside the workspace";
            return false;
        }

        return WorkspacePaths.DirectoryChainStaysWithin(file.Directory, _root, out reason);
    }
}
