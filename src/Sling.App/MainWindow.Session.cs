using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sling.Persistence;
using Sling.Persistence.Settings;
using Sling.Persistence.Workspaces;

namespace Sling.App;

/// <summary>
/// Coming back to where you were: the last folder, the last file, the caret, and the split.
/// </summary>
/// <remarks>
/// <para>
/// <b>It removes a setup nobody should have to repeat, and it introduces no format to do
/// it.</b> Sling opens on an empty window, so every start meant opening the folder, finding
/// the file in the rail, and dragging the splitter back - three actions to get to where the
/// previous session ended, every day. What is remembered is deliberately only that: which
/// folder, which file, where the caret was and how wide the panes were, all of it in
/// <c>%LOCALAPPDATA%</c> beside the settings.
/// </para>
/// <para>
/// <b>Delete the file and nothing is lost but the convenience</b>, which is the test
/// <c>Sling.md</c> §1 sets for anything Sling remembers: the folder is still a folder of
/// <c>.http</c> files, every request still runs and every environment still resolves. This is
/// an accelerator over that text and not a fact about it, which is what makes it allowed
/// where a workspace manifest is not.
/// </para>
/// <para>
/// <b>A file named on the command line wins.</b> Somebody who double-clicked a request file
/// in Explorer has said which document they want more clearly than a session file can, and
/// opening the last one over it would be the association failing to honour itself.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private readonly SessionStore _sessionStore = new(LocalData.DefaultFolder);

    private SlingSession _session = SlingSession.None;

    /// <summary>
    /// Reads the session and applies the half that needs no disk.
    /// </summary>
    /// <remarks>
    /// The split goes on immediately rather than on the first frame, so the window is never
    /// briefly drawn at one ratio and then re-laid out at another.
    /// </remarks>
    private void InitializeSession()
    {
        _session = _sessionStore.Load();

        ApplySplit(_session.Split);
        RebuildRecentFolders();
    }

    /// <summary>
    /// Opens the folder and the file that were last on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the first load rather than in the constructor, for the reason the startup file is:
    /// the constructor cannot await, and reading a folder on the way to the first frame holds
    /// the window back for as long as the disk takes.
    /// </para>
    /// <para>
    /// <b>Anything that is no longer there is skipped in silence.</b> A folder that was
    /// deleted, moved or is on a share that is not mounted is the ordinary way a remembered
    /// path goes stale, and an error about it on startup would be a message about something
    /// nobody just did. What that leaves is the empty window Sling opened with before this
    /// existed.
    /// </para>
    /// </remarks>
    private async Task RestoreSessionAsync()
    {
        if (_session.WorkspaceRoot is not { Length: > 0 } root || !Directory.Exists(root))
        {
            return;
        }

        // Captured before SetWorkspace, which writes the session out through RememberFolder -
        // with the document not yet open, so the field would read null by the time it is
        // needed.
        var document = _session.DocumentPath;
        var line = _session.CaretLine;
        var column = _session.CaretColumn;

        try
        {
            SetWorkspace(Workspace.Open(root));
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException)
        {
            return;
        }

        // Only a document inside the folder that was restored. The two were saved together
        // and cannot normally disagree, but a hand-edited session file can say anything - and
        // opening a file from elsewhere here would rebuild the workspace around it, which is
        // the opposite of restoring the one that was remembered.
        if (document is not { Length: > 0 } path
            || !File.Exists(path)
            || _workspace?.Contains(path) != true)
        {
            return;
        }

        if (!await LoadDocumentAsync(path).ConfigureAwait(true))
        {
            return;
        }

        // Closed while the file was being read. Every other awaiting path in this window
        // checks, and this one runs on the way to the first frame, which is exactly when a
        // window can be shut before it has finished appearing.
        if (_closed || _documentPath is null || line <= 0)
        {
            return;
        }

        // After the load, because SetDocument puts the caret at the start of the buffer.
        RestoreCaret(line, column);
    }

    /// <summary>Puts the caret back where it was, as far as the file still allows.</summary>
    /// <remarks>
    /// Clamped rather than trusted: the file is a git artifact and may have lost a hundred
    /// lines since it was last open, and a caret past the end is an exception on the way to
    /// the first frame.
    /// </remarks>
    private void RestoreCaret(int line, int column)
    {
        var target = Math.Clamp(line, 1, RequestPane.Document.LineCount);
        var text = RequestPane.Document.GetLineByNumber(target);

        RequestPane.TextArea.Caret.Line = target;
        RequestPane.TextArea.Caret.Column = Math.Clamp(column < 1 ? 1 : column, 1, text.Length + 1);

        RequestPane.ScrollToLine(target);
    }

    /// <summary>Sets the two panes' share of the column.</summary>
    private void ApplySplit(double ratio)
    {
        var request = Math.Clamp(ratio, SlingSession.MinimumSplit, SlingSession.MaximumSplit);

        RequestColumn.Width = new GridLength(request, GridUnitType.Star);
        ResponseColumn.Width = new GridLength(1 - request, GridUnitType.Star);
    }

    /// <summary>The request column's share of the two, as the splitter left it.</summary>
    /// <remarks>
    /// Read off the star weights rather than off the rendered widths. A <c>GridSplitter</c>
    /// with <c>ResizeBehavior="PreviousAndNext"</c> rewrites both columns to star values, so
    /// the weights are the ratio - where the actual widths also carry whatever the window's
    /// current size and minimum widths did to them.
    /// </remarks>
    private double CurrentSplit()
    {
        var request = RequestColumn.Width.Value;
        var response = ResponseColumn.Width.Value;
        var total = request + response;

        return total > 0 ? request / total : 0.5;
    }

    /// <summary>Adds a folder to the recent list and writes the session out.</summary>
    /// <remarks>
    /// Written here as well as on the way out, so a machine that loses power keeps the list.
    /// A folder somebody opened once is worth remembering even if that session never ended
    /// cleanly, and the file is a few hundred bytes.
    /// </remarks>
    private void RememberFolder(string root)
    {
        _session = _session.WithRecent(root);

        RebuildRecentFolders();
        SaveSession();
    }

    /// <summary>Writes where the window is now.</summary>
    private void SaveSession()
    {
        _session = _session with
        {
            WorkspaceRoot = _workspace?.Root,
            DocumentPath = _documentPath,
            CaretLine = RequestPane.TextArea.Caret.Line,
            CaretColumn = RequestPane.TextArea.Caret.Column,
            Split = CurrentSplit(),
        };

        // The result is deliberately not reported. This runs on the way out of the
        // application and whenever a folder is opened, and a status bar sentence about a
        // preferences file nobody asked to write would be noise on both paths.
        _sessionStore.Save(_session);
    }

    /// <summary>Refills the Recent folders submenu from the session.</summary>
    /// <remarks>
    /// A greyed entry rather than an empty submenu when there are none. An empty menu tells
    /// somebody the feature is broken; "Nothing yet" tells them there is nothing to show,
    /// which is the true answer to the question they asked by opening it.
    /// </remarks>
    private void RebuildRecentFolders()
    {
        RecentFoldersMenu.Items.Clear();

        if (_session.RecentFolders.Count == 0)
        {
            RecentFoldersMenu.Items.Add(new MenuItem { Header = "Nothing yet", IsEnabled = false });
            return;
        }

        foreach (var folder in _session.RecentFolders)
        {
            var item = new MenuItem
            {
                // The folder's own name reads, and the path disambiguates two checkouts that
                // share one. Both, because on a machine with three copies of the same
                // repository the name alone is three identical rows.
                Header = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)),
                InputGestureText = folder,
                ToolTip = folder,
            };

            // Captured rather than read back off the sender, so a rebuild between the menu
            // opening and the click cannot re-point the item at a different folder.
            var target = folder;

            item.Click += (_, _) => RunGuarded(() => OpenFolderAsync(target));

            RecentFoldersMenu.Items.Add(item);
        }
    }
}
