using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sling.App.Collections;
using Sling.App.Editor;
using Sling.Core.Navigation;
using Sling.Persistence.Workspaces;

namespace Sling.App;

/// <summary>
/// Quick open: <c>Ctrl+P</c>, and everything in the workspace by typing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rail is a tree, and a tree is only navigable by scrolling.</b> A workspace of a few
/// hundred request files is an ordinary checkout, and the collections rail lists two thousand
/// of them - so finding the one request somebody half-remembers means expanding folders until
/// it appears. What they do remember is spread over four levels of that tree: which
/// collection, which file, what the request is called, and what it hits. All four are what
/// this searches, and any of them will do.
/// </para>
/// <para>
/// <b>It stores nothing, exactly like the rail.</b> The list is read from the folder each
/// time the palette opens and thrown away when it closes, which is <c>Sling.md</c> §1's rule
/// rather than a shortcut: an index kept across a <c>git pull</c> is an index that is wrong
/// precisely when somebody most needs it, and reading the folder takes a few tens of
/// milliseconds. The open document's requests come from the buffer rather than from disk, so
/// one typed a moment ago and not yet saved is still findable.
/// </para>
/// <para>
/// <b>Opening a row goes through the same method the rail's rows do.</b> A palette with its
/// own copy of "open the file, confirm unsaved work, narrow to the request" is a second copy
/// that eventually stops confirming.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>The most rows the list shows at once.</summary>
    /// <remarks>
    /// A list nobody will scroll to the bottom of does not need to be complete, and it does
    /// need to be fast to rebuild: this is replaced on every keystroke. Typing one more
    /// character is a better way to reach the two hundredth match than scrolling to it.
    /// </remarks>
    private const int MaxQuickOpenRows = 200;

    /// <summary>Everything the workspace holds, read when the palette opened.</summary>
    private IReadOnlyList<QuickOpenEntry> _quickOpen = [];

    /// <summary>True when a bound stopped the read, so the hint can say so.</summary>
    private bool _quickOpenPartial;

    /// <summary>
    /// Cancels the folder walk when the palette is closed or the window goes.
    /// </summary>
    /// <remarks>
    /// The walk reads every request file in a folder somebody chose. Escape has to stop it,
    /// or a palette opened and dismissed on a large checkout leaves a read running with
    /// nothing waiting for it - and opening the palette twice in a row would have two.
    /// </remarks>
    private CancellationTokenSource? _quickOpenWalk;

    private bool QuickOpenIsOpen => QuickOpenOverlay.Visibility == Visibility.Visible;

    /// <summary>
    /// Opens the palette and fills it.
    /// </summary>
    /// <remarks>
    /// The card goes up before the folder is read, and the list arrives into it. The other
    /// way round would mean a chord that visibly does nothing for as long as the disk takes,
    /// which on a cold checkout is exactly long enough to be pressed again.
    /// </remarks>
    private void ShowQuickOpen()
    {
        if (QuickOpenIsOpen)
        {
            return;
        }

        if (_workspace is not { } workspace)
        {
            StatusLeft.Text = "Open a folder first - quick open searches the files in it.";
            return;
        }

        _quickOpen = [];
        _quickOpenPartial = false;

        QuickOpenBox.Text = string.Empty;
        QuickOpenList.ItemsSource = null;
        QuickOpenHint.Text = "Reading the folder …";

        Overlays.Reveal(QuickOpenOverlay, QuickOpenCard);
        QuickOpenBox.Focus();

        var path = _documentPath;
        var buffer = path is null ? null : RequestPane.Text;

        var walk = new CancellationTokenSource();

        _quickOpenWalk = walk;

        RunGuarded(async () =>
        {
            try
            {
                var listing = await Task
                    .Run(() => QuickOpenIndex.BuildAsync(workspace, path, buffer, walk.Token), walk.Token)
                    .ConfigureAwait(true);

                // The palette can have been closed, or the folder changed, while the walk ran.
                if (_closed || !QuickOpenIsOpen || !ReferenceEquals(_workspace, workspace))
                {
                    return;
                }

                _quickOpen = listing.Entries;
                _quickOpenPartial = listing.Truncated;

                RefreshQuickOpen();
            }
            catch (OperationCanceledException)
            {
                // The palette was closed while the folder was being read.
            }
            finally
            {
                if (ReferenceEquals(_quickOpenWalk, walk))
                {
                    _quickOpenWalk = null;
                }

                walk.Dispose();
            }
        });
    }

    private void CloseQuickOpen()
    {
        // Before the card goes down, so a walk that is still running stops rather than
        // finishing into a palette nobody is looking at.
        CancelQuickOpenWalk();

        Overlays.Hide(QuickOpenOverlay);

        // The list can hold a few hundred rows over a workspace that may since have been
        // closed, and nothing else refers to it once the card is down.
        QuickOpenList.ItemsSource = null;
        _quickOpen = [];

        if (!_closed)
        {
            RequestPane.Focus();
        }
    }

    /// <summary>Stops a folder walk that nothing is waiting for any more.</summary>
    private void CancelQuickOpenWalk()
    {
        var walk = _quickOpenWalk;

        _quickOpenWalk = null;

        // Cancel only. The task that owns it disposes it, so cancelling from here cannot
        // race a walk that is still reading its token.
        try
        {
            walk?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It finished between the read and the call.
        }
    }

    private void OnQuickOpenTextChanged(object sender, TextChangedEventArgs e) => RefreshQuickOpen();

    /// <summary>Re-ranks against what has been typed and rebuilds the list.</summary>
    private void RefreshQuickOpen()
    {
        if (_closed || !QuickOpenIsOpen)
        {
            return;
        }

        var matches = QuickOpenSearch.Rank(_quickOpen, QuickOpenBox.Text, MaxQuickOpenRows);

        QuickOpenList.ItemsSource = matches.Select(ToRow).ToList();

        // The first row, always. The palette is used by typing three characters and pressing
        // Enter, and a list with nothing selected makes that two keystrokes longer for no
        // reason anybody could state.
        if (QuickOpenList.Items.Count > 0)
        {
            QuickOpenList.SelectedIndex = 0;
        }

        QuickOpenHint.Text = Hint(matches.Count);
    }

    private QuickOpenRow ToRow(QuickOpenEntry entry) => new()
    {
        Entry = entry,
        Label = entry.Kind == QuickOpenKind.Request ? entry.Name : entry.File,
        Detail = Detail(entry),
        Method = entry.Method,
        MethodBrush = MethodPalette.For(MethodBrushes, entry.Method),
    };

    /// <summary>The dim second line: where the row is, and what it hits.</summary>
    /// <remarks>
    /// Both, because the two questions somebody has about a candidate row are "is that the
    /// one in the collection I meant" and "is that the URL I meant" - and a list that answers
    /// neither is a list whose rows have to be opened to be told apart.
    /// </remarks>
    private static string Detail(QuickOpenEntry entry)
    {
        var where = entry.Collection.Length == 0
            ? entry.File
            : entry.Collection + System.IO.Path.DirectorySeparatorChar + entry.File;

        return entry.Kind == QuickOpenKind.Request && entry.Target.Length > 0
            ? $"{where}  ·  {entry.Target}"
            : where;
    }

    private string Hint(int shown)
    {
        if (_quickOpen.Count == 0)
        {
            return "Nothing to search - this folder holds no request files.";
        }

        if (shown == 0)
        {
            return "No matches. Every word has to appear somewhere in the row.";
        }

        var text = shown >= MaxQuickOpenRows
            ? $"First {MaxQuickOpenRows.ToString(CultureInfo.CurrentCulture)} matches.  ↑↓ to move, Enter to open."
            : "↑↓ to move, Enter to open.";

        // Said out loud rather than left as a short list that looks complete. A workspace
        // that hit a bound is one where the row somebody wants may be the missing one.
        return _quickOpenPartial
            ? text + "  Some files were too large to read, so their requests are not listed."
            : text;
    }

    /// <summary>
    /// Answers the keys the palette owns while it is up.
    /// </summary>
    /// <remarks>
    /// Reached from <see cref="TryHandleModalKey"/> with the rest of the overlays, so the
    /// arrows and Enter are resolved on the window's tunnelling pass before the text box has
    /// them. Everything not named here falls through and is typed.
    /// </remarks>
    private bool TryHandleQuickOpenKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseQuickOpen();
                return true;

            // The chord that opened it closes it, which is the reading anybody has of a key
            // that put a panel on screen.
            case Key.P when e.KeyboardDevice.Modifiers == ModifierKeys.Control:
                CloseQuickOpen();
                return true;

            case Key.Down:
                MoveQuickOpenSelection(1);
                return true;

            case Key.Up:
                MoveQuickOpenSelection(-1);
                return true;

            case Key.Enter when !e.IsRepeat:
                OpenQuickOpenSelection();
                return true;

            default:
                return false;
        }
    }

    /// <summary>Moves the selection and keeps it on screen.</summary>
    /// <remarks>
    /// Clamped rather than wrapped. A list that jumps from the last row to the first is a
    /// list where holding Down never tells you that you have reached the end.
    /// </remarks>
    private void MoveQuickOpenSelection(int delta)
    {
        var count = QuickOpenList.Items.Count;

        if (count == 0)
        {
            return;
        }

        var next = Math.Clamp(QuickOpenList.SelectedIndex + delta, 0, count - 1);

        QuickOpenList.SelectedIndex = next;
        QuickOpenList.ScrollIntoView(QuickOpenList.Items[next]);
    }

    private void OnQuickOpenDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Only on a row. A double-click on the empty space below the last one would otherwise
        // open whatever happens to be selected, which is not what was clicked.
        if (e.OriginalSource is DependencyObject source && Row(source) is not null)
        {
            OpenQuickOpenSelection();
        }
    }

    private static ListBoxItem? Row(DependencyObject? hit)
    {
        for (var source = hit; source is not null; source = System.Windows.Media.VisualTreeHelper.GetParent(source))
        {
            if (source is ListBoxItem row)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>Opens whatever is selected, and closes the palette.</summary>
    /// <remarks>
    /// The card comes down first. Opening a document confirms unsaved work, which puts a
    /// second overlay up - and two modal cards on screen at once is a state where Escape
    /// means two things.
    /// </remarks>
    private void OpenQuickOpenSelection()
    {
        if (QuickOpenList.SelectedItem is not QuickOpenRow row)
        {
            return;
        }

        var entry = row.Entry;

        CloseQuickOpen();

        // The rail's own method, so the palette cannot drift into a different idea of what
        // opening a request means - it confirms unsaved work, loads the file if it is not the
        // one on screen, and narrows the pane to the request.
        OpenDocumentAt(entry.Path, entry.Kind == QuickOpenKind.Request ? entry.Line : 0);
    }
}
