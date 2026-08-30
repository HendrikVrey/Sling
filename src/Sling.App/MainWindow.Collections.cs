using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sling.App.Collections;
using Sling.App.Editor;
using Sling.Core.Documents;
using Sling.Core.Parsing;
using Sling.Persistence.Workspaces;

namespace Sling.App;

/// <summary>
/// The collections rail: folders, request files, and the requests inside them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is Postman's tree over Sling's artifacts, and the distinction is the whole
/// point.</b> <c>Sling.md</c> §1 refuses a collection <em>format</em>, and that still holds:
/// nothing here writes an index, a manifest or an ordering, and deleting Sling leaves a
/// folder of <c>.http</c> files that git, Rider and VS Code all still read. A collection
/// <em>is</em> a directory and an endpoint <em>is</em> a <c>###</c> block. The tree is a
/// projection of the folder walk, recomputed whenever it is drawn - an affordance, not a
/// format.
/// </para>
/// <para>
/// <b>Requests are read when a file is opened, not when the folder is.</b> A workspace is
/// very often a checkout with a few hundred request files in it, and parsing all of them to
/// draw a rail is work nobody asked for on the path that has to feel instant
/// (<c>Sling.md</c> §6). Each document node carries a placeholder child until its branch is
/// opened.
/// </para>
/// <para>
/// <b>Creating is all the rail does.</b> There is no rename and no delete,
/// <see cref="WorkspaceEditor"/> says why, and the short version is that both are file
/// manager operations with real consequences for a git working tree.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// The largest buffer the rail re-parses as you type, in characters.
    /// </summary>
    /// <remarks>
    /// Characters rather than UTF-8 bytes, and unlike <c>DocumentSizePolicy</c> that is the
    /// right measure here: this bounds the cost of a parse, which scales with the length the
    /// parser walks, not the memory the text occupies. The open dialog will take anything up
    /// to <see cref="RequestFileStore.MaxDocumentBytes"/> - past this ceiling the rail
    /// updates when the file is saved or reopened instead, which is a stale rail rather than
    /// a stuttering window.
    /// </remarks>
    private const int MaxLiveRefreshLength = 256 * 1024;

    /// <summary>
    /// The most request rows one document contributes to the rail.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxLiveRefreshLength"/> bounds the parse, which is the cheap half. A
    /// <c>TreeView</c> does not virtualise by default, so what actually costs is generating a
    /// container per row - and a document is allowed to be sixteen megabytes, which is tens
    /// of thousands of requests. A rail nobody could scroll is not worth freezing the window
    /// to draw, so the overflow is counted and named instead.
    /// </remarks>
    private const int MaxRequestRows = 500;

    /// <summary>How long a row's label may be.</summary>
    /// <remarks>
    /// The label is a <c>###</c> title or a target out of a file, which is untrusted content
    /// of no particular length. The rail is 250 px wide and trims, so this is about not
    /// handing the layout a megabyte-long string to measure. The command bar's send target
    /// uses the same cap for the same reason.
    /// </remarks>
    private const int MaxLabelLength = 160;

    private readonly ObservableCollection<CollectionItem> _collections = [];

    /// <summary>
    /// The folders and files the user had open, so a refresh does not collapse the tree.
    /// </summary>
    /// <remarks>
    /// Keyed by absolute path rather than by node, because the nodes are rebuilt from
    /// scratch every time - the tree is a projection of the walk, and holding onto the old
    /// objects to preserve a bit of view state is how a projection quietly becomes a model.
    /// </remarks>
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Re-reads the open document's requests once typing stops.
    /// </summary>
    /// <remarks>
    /// One-shot and restarted on each keystroke, not a poll: it is stopped in its own tick
    /// and never runs while the buffer is untouched. Adding a <c>###</c> and watching the
    /// rail not notice is the kind of small wrongness that makes a panel feel dead.
    /// </remarks>
    private readonly DispatcherTimer _requestRefresh = new()
    {
        Interval = TimeSpan.FromMilliseconds(400),
    };

    private IReadOnlyDictionary<string, Brush>? _methodBrushes;

    /// <summary>
    /// A row this code selected, which the tree has not told us about yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The <c>_rebuildingLists</c> flag does not cover a selection, and that was a real
    /// bug.</b> A row inside a collapsed branch has no <c>TreeViewItem</c>, so its
    /// <c>IsSelected</c> binding has nowhere to land - the tree applies it later, when the
    /// branch is expanded and containers are generated, long after the flag is back down.
    /// <see cref="OnCollectionSelected"/> then read a selection the code had made as a click,
    /// and moved the caret onto a stale request. In a tool whose whole interaction is "send
    /// what is under the caret", that is the wrong request going out.
    /// </para>
    /// <para>
    /// Keyed to the row rather than to a window of time, because the delay is unbounded,
    /// it lasts until the user opens that branch. Cleared the moment the user touches the
    /// tree, so a genuine click on the same row is never mistaken for the deferred one.
    /// </para>
    /// </remarks>
    private CollectionItem? _selectedFromCode;

    /// <summary>Pending name prompt, or null when the overlay is down.</summary>
    private TaskCompletionSource<string?>? _namePrompt;

    /// <summary>What the open prompt considers a usable name. Null means anything.</summary>
    private Func<string, string?>? _nameValidator;

    /// <summary>Wires the rail. Called once, from the constructor.</summary>
    private void InitializeCollections()
    {
        CollectionsTree.ItemsSource = _collections;

        // Handled on the TreeView rather than per container: containers for a collapsed
        // branch do not exist yet, so there is nothing to subscribe to until the moment the
        // subscription would have been needed.
        CollectionsTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnNodeExpanded));
        CollectionsTree.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler(OnNodeCollapsed));

        // A TreeViewItem selects on the LEFT button only, so a right-click leaves the
        // selection wherever it was - and every command on the context menu resolves its
        // target from the selection. Without this, right-clicking one collection and
        // choosing "New request file" created it in a different one.
        CollectionsTree.PreviewMouseRightButtonDown += OnTreeRightButtonDown;

        // Any real interaction invalidates a selection this code made but the tree has not
        // applied yet. See _selectedFromCode.
        CollectionsTree.PreviewMouseDown += OnTreeInteracted;
        CollectionsTree.PreviewKeyDown += OnTreeInteracted;

        // The rail follows the caret, which is a claim docs/collections.md makes: the
        // highlighted row is the request Ctrl+Enter would send. Nothing else fires on a
        // caret move - the idle timer is driven by text changes - so arrowing around a file
        // left the highlight where it was.
        RequestPane.TextArea.Caret.PositionChanged += OnCaretMoved;

        _requestRefresh.Tick += OnRequestRefreshTick;
    }

    /// <summary>Swaps the rail between its empty state and the tree.</summary>
    /// <remarks>
    /// Called from <see cref="SetWorkspace"/> rather than driven by a binding, because the
    /// rail has exactly two states and one of them is the startup state - a converter and a
    /// notifying property would be three moving parts for a boolean that changes once.
    /// </remarks>
    private void ShowWorkspaceRail(bool hasWorkspace)
    {
        EmptyRail.Visibility = hasWorkspace ? Visibility.Collapsed : Visibility.Visible;
        CollectionsTree.Visibility = hasWorkspace ? Visibility.Visible : Visibility.Collapsed;
        RailActions.Visibility = hasWorkspace ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e) => RunGuarded(OpenFolderAsync);

    private bool NamePromptIsOpen => _namePrompt is not null;

    /// <summary>The brush table, built against the pane colour the first time it is asked for.</summary>
    /// <remarks>
    /// Lazily, because building it reads the merged theme dictionaries and nothing on the
    /// startup path needs a rail - a window with no folder open has no tree at all.
    /// <para>
    /// Through <see cref="Page"/> rather than this window's own <c>Resources</c>. A
    /// <see cref="ResourceDictionary"/>'s indexer searches only itself and what it merges;
    /// it does not walk up the element tree, so asking the window for a theme key found
    /// nothing and silently took the fallback. The fallback and the theme's page colour
    /// happen to be the same today, which is exactly why this would have gone unnoticed the
    /// day they stopped being.
    /// </para>
    /// </remarks>
    private IReadOnlyDictionary<string, Brush> MethodBrushes =>
        _methodBrushes ??= MethodPalette.Build(Page);

    /// <summary>Rebuilds the tree from the folder on disk.</summary>
    private void RefreshCollections()
    {
        if (_workspace is null)
        {
            return;
        }

        var files = _workspace.RequestFiles(out var truncated);
        var root = Path.GetFullPath(_workspace.Root);

        _rebuildingLists = true;

        try
        {
            _collections.Clear();

            foreach (var entry in CollectionTree.Build(files))
            {
                _collections.Add(ToItem(entry, root));
            }
        }
        finally
        {
            _rebuildingLists = false;
        }

        if (truncated)
        {
            StatusLeft.Text = "That folder holds more request files than the rail will show. "
                + "Open a folder closer to the ones you want.";
        }
    }

    /// <summary>Turns one tree entry, and everything under it, into rail rows.</summary>
    private CollectionItem ToItem(CollectionEntry entry, string root)
    {
        // GetFullPath, not Combine alone: CollectionEntry uses '/' so its paths compare the
        // same on any machine, and the rest of the window compares absolute paths against
        // _documentPath with OrdinalIgnoreCase. A path carrying both separators matches
        // nothing.
        var path = Path.GetFullPath(Path.Combine(root, entry.RelativePath));

        var item = new CollectionItem(
            entry.Kind == CollectionEntryKind.Folder ? CollectionItemKind.Folder : CollectionItemKind.Document,
            entry.Name,
            path)
        {
            Hint = path,
        };

        if (entry.Kind == CollectionEntryKind.Folder)
        {
            foreach (var child in entry.Children)
            {
                item.Children.Add(ToItem(child, root));
            }

            item.IsExpanded = _expanded.Contains(path);
            return item;
        }

        // The document that is on screen is filled in from the buffer rather than from disk,
        // so the rail shows the requests as they are being edited rather than as they were
        // last saved.
        if (IsOpenDocument(path))
        {
            FillRequests(item, RequestPane.Text);
        }
        else
        {
            item.Children.Add(new CollectionItem(CollectionItemKind.Placeholder, "…", path));
        }

        item.IsExpanded = _expanded.Contains(path);
        return item;
    }

    /// <summary>Brings a document node's children into line with the requests in <paramref name="text"/>.</summary>
    /// <remarks>
    /// <b>Replaced only when they actually differ.</b> This runs on every idle tick after a
    /// keystroke, and a <c>TreeView</c> does not virtualise - clearing and refilling
    /// regenerates a container per row, which for a few hundred requests is a visible freeze
    /// every time typing pauses. Typing inside a body does not change the request set at all,
    /// which is the common case and now costs one comparison.
    /// </remarks>
    private void FillRequests(CollectionItem document, string text)
    {
        var desired = BuildRequestRows(document, RequestDocumentParser.Parse(text));

        document.IsLoaded = true;

        if (SameRows(document.Children, desired))
        {
            return;
        }

        document.Children.Clear();

        foreach (var row in desired)
        {
            document.Children.Add(row);
        }
    }

    private List<CollectionItem> BuildRequestRows(CollectionItem document, RequestDocument parsed)
    {
        var rows = new List<CollectionItem>(Math.Min(parsed.Requests.Count, MaxRequestRows) + 1);

        foreach (var request in parsed.Requests.Take(MaxRequestRows))
        {
            rows.Add(new CollectionItem(
                CollectionItemKind.Request,
                Describe(request),
                document.Path)
            {
                Method = request.Method,
                MethodBrush = MethodPalette.For(MethodBrushes, request.Method),
                Line = request.StartLine,
                Hint = Clamp(request.Target),
            });
        }

        if (parsed.Requests.Count == 0)
        {
            rows.Add(new CollectionItem(
                CollectionItemKind.Placeholder,
                "no requests yet",
                document.Path));
        }
        else if (parsed.Requests.Count > MaxRequestRows)
        {
            // Counted and named rather than silently dropped - a rail that stops at five
            // hundred without saying so is a rail that looks like the file ends there.
            var more = (parsed.Requests.Count - MaxRequestRows).ToString(CultureInfo.InvariantCulture);

            rows.Add(new CollectionItem(
                CollectionItemKind.Placeholder,
                $"+{more} more, not listed",
                document.Path));
        }

        return rows;
    }

    /// <summary>Whether the rows on screen already say what the new ones would.</summary>
    private static bool SameRows(ObservableCollection<CollectionItem> existing, List<CollectionItem> desired)
    {
        if (existing.Count != desired.Count)
        {
            return false;
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (existing[i].Kind != desired[i].Kind
                || existing[i].Line != desired[i].Line
                || !string.Equals(existing[i].Method, desired[i].Method, StringComparison.Ordinal)
                || !string.Equals(existing[i].Label, desired[i].Label, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string Clamp(string? text) =>
        text is null || text.Length <= MaxLabelLength ? text ?? string.Empty : text[..MaxLabelLength] + "…";

    /// <summary>What a request is called in the rail.</summary>
    /// <remarks>
    /// The <c>###</c> title first, because that is the line somebody wrote to describe it;
    /// then the <c># @name</c> handle, which exists for chaining rather than for reading;
    /// then the target, which is always there. A row labelled only by its line number would
    /// be a row nobody can pick out.
    /// </remarks>
    private static string Describe(RequestBlock request)
    {
        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            return Clamp(request.Title.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            return Clamp(request.Name);
        }

        return string.IsNullOrWhiteSpace(request.Target)
            ? "line " + request.StartLine.ToString(CultureInfo.InvariantCulture)
            : Clamp(request.Target);
    }

    private bool IsOpenDocument(string path) =>
        _documentPath is not null
            && string.Equals(Path.GetFullPath(_documentPath), path, StringComparison.OrdinalIgnoreCase);

    private void OnNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (e is not { OriginalSource: TreeViewItem { DataContext: CollectionItem item } })
        {
            return;
        }

        if (item.Path is not null)
        {
            _expanded.Add(item.Path);
        }

        if (item.Kind != CollectionItemKind.Document || item.IsLoaded || item.Path is null)
        {
            return;
        }

        // Set before the read rather than after it, so a second expand arriving while the
        // first is in flight does not start a second read of the same file.
        item.IsLoaded = true;

        RunGuarded(async () =>
        {
            try
            {
                var text = await RequestFileStore.ReadAsync(item.Path, CancellationToken.None)
                    .ConfigureAwait(true);

                FillRequests(item, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left unloaded so opening the branch again retries, and said out loud: a
                // branch that silently stays empty reads as a file with no requests in it.
                item.IsLoaded = false;
                StatusLeft.Text = $"Could not read '{item.Label}': {ex.Message}";
            }
        });
    }

    /// <summary>Selects the row under the pointer before the context menu opens on it.</summary>
    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Walked up from whatever was hit, because the hit is a TextBlock inside the row's
        // template rather than the container.
        for (var source = e.OriginalSource as DependencyObject;
             source is not null;
             source = VisualTreeHelper.GetParent(source))
        {
            if (source is TreeViewItem row)
            {
                row.IsSelected = true;
                return;
            }
        }

        // Right-clicked the empty space below the rows. The selection is cleared rather than
        // left behind, so "New collection" lands at the workspace root - which is what the
        // gesture means, and better than silently using whatever was clicked last.
        ClearTreeSelection();
    }

    private void OnTreeInteracted(object sender, RoutedEventArgs e) => _selectedFromCode = null;

    private void ClearTreeSelection()
    {
        _rebuildingLists = true;

        try
        {
            foreach (var item in Descendants(_collections))
            {
                item.IsSelected = false;
            }
        }
        finally
        {
            _rebuildingLists = false;
        }
    }

    private void OnNodeCollapsed(object sender, RoutedEventArgs e)
    {
        if (e is { OriginalSource: TreeViewItem { DataContext: CollectionItem { Path: { } path } } })
        {
            _expanded.Remove(path);
        }
    }

    /// <summary>
    /// Opens what was clicked: a file, or the request inside one.
    /// </summary>
    /// <remarks>
    /// A request row puts the caret on its request line, which is the only thing it needs to
    /// do - <c>Ctrl+Enter</c> already sends the request under the caret, so selecting an
    /// endpoint and sending it is two keystrokes without the rail knowing anything about
    /// sending.
    /// </remarks>
    private void OnCollectionSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_rebuildingLists || _workspace is null || e.NewValue is not CollectionItem item)
        {
            return;
        }

        // A selection this code made, arriving late because the row had no container when it
        // was set. Acting on it would move the caret to a request the user has since left.
        if (ReferenceEquals(item, _selectedFromCode))
        {
            _selectedFromCode = null;
            return;
        }

        if (item.Kind is CollectionItemKind.Folder or CollectionItemKind.Placeholder || item.Path is null)
        {
            return;
        }

        var path = item.Path;
        var line = item.Kind == CollectionItemKind.Request ? item.Line : 0;

        if (IsOpenDocument(path))
        {
            GoToLine(line);
            return;
        }

        RunGuarded(async () =>
        {
            if (!await ConfirmDiscardAsync().ConfigureAwait(true))
            {
                // Put the selection back on the file that is actually open, without the
                // restoration itself being read as a new choice.
                SelectOpenDocumentInTree();
                return;
            }

            await LoadDocumentAsync(path).ConfigureAwait(true);
            GoToLine(line);
        });
    }

    /// <summary>Puts the caret on a 1-based line and shows it.</summary>
    private void GoToLine(int line)
    {
        if (line <= 0 || line > RequestPane.Document.LineCount)
        {
            return;
        }

        RequestPane.TextArea.Caret.Line = line;
        RequestPane.TextArea.Caret.Column = 1;
        RequestPane.ScrollToLine(line);
        RequestPane.Focus();
    }

    /// <summary>
    /// Moves the tree's selection onto the open document without that looking like a click.
    /// </summary>
    private void SelectOpenDocumentInTree()
    {
        if (_workspace is null)
        {
            return;
        }

        _rebuildingLists = true;

        try
        {
            foreach (var item in Descendants(_collections))
            {
                item.IsSelected = item.Kind == CollectionItemKind.Document
                    && item.Path is not null
                    && IsOpenDocument(item.Path);

                if (!item.IsSelected)
                {
                    continue;
                }

                Reveal(item);
                _selectedFromCode = item;

                // And open the file's own branch. Its rows are already in memory - they came
                // from the buffer - and a rail that shows a selected file with its endpoints
                // hidden is a rail that looks like the file has none.
                item.IsExpanded = true;

                // Recorded here as well as in the Expanded handler: that only fires once a
                // container exists, and a row inside a branch nobody has opened has none,
                // so the next rebuild would collapse the file that is on screen.
                if (item.Path is not null)
                {
                    _expanded.Add(item.Path);
                }
            }
        }
        finally
        {
            _rebuildingLists = false;
        }
    }

    /// <summary>Expands every folder above <paramref name="target"/>.</summary>
    private void Reveal(CollectionItem target)
    {
        foreach (var branch in _collections)
        {
            RevealWithin(branch, target);
        }
    }

    private static bool RevealWithin(CollectionItem branch, CollectionItem target)
    {
        if (ReferenceEquals(branch, target))
        {
            return true;
        }

        foreach (var child in branch.Children)
        {
            if (!RevealWithin(child, target))
            {
                continue;
            }

            branch.IsExpanded = true;
            return true;
        }

        return false;
    }

    private static IEnumerable<CollectionItem> Descendants(IEnumerable<CollectionItem> items)
    {
        foreach (var item in items)
        {
            yield return item;

            foreach (var child in Descendants(item.Children))
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// The one document in the workspace, when there is exactly one.
    /// </summary>
    /// <remarks>
    /// For the folder-open shortcut: a folder holding a single request file has an obvious
    /// thing to open, and doing it saves the one click every single-file workspace would
    /// otherwise need.
    /// </remarks>
    private string? SingleDocumentPath()
    {
        var documents = Descendants(_collections)
            .Where(i => i.Kind == CollectionItemKind.Document)
            .Take(2)
            .ToList();

        return documents.Count == 1 ? documents[0].Path : null;
    }

    /// <summary>
    /// The folder a new thing goes into: the selected one, or the one holding the selected
    /// file, or the workspace root.
    /// </summary>
    /// <returns>A path relative to the root, or null for the root itself.</returns>
    private string? SelectedContainerRelative()
    {
        if (_workspace is null || CollectionsTree.SelectedItem is not CollectionItem { Path: { } path } item)
        {
            return null;
        }

        var folder = item.Kind == CollectionItemKind.Folder ? path : Path.GetDirectoryName(path);

        if (folder is null)
        {
            return null;
        }

        var relative = Path.GetRelativePath(_workspace.Root, folder);

        return relative is "." or "" ? null : relative;
    }

    private void OnNewCollection(object sender, RoutedEventArgs e) => RunGuarded(NewCollectionAsync);

    private void OnNewDocument(object sender, RoutedEventArgs e) => RunGuarded(NewDocumentFileAsync);

    private void OnNewRequest(object sender, RoutedEventArgs e) => RunGuarded(NewRequestAsync);

    private async Task NewCollectionAsync()
    {
        if (_workspace is null)
        {
            StatusLeft.Text = "Open a folder first with Ctrl+Shift+O. A collection is a folder in it.";
            return;
        }

        var parent = SelectedContainerRelative();

        var name = await PromptForNameAsync(
            "New collection",
            $"A folder in {parent ?? Path.GetFileName(_workspace.Root.TrimEnd(Path.DirectorySeparatorChar))}, "
                + "with a request file already in it.",
            ValidateSegment).ConfigureAwait(true);

        if (name is null)
        {
            return;
        }

        string relative;

        try
        {
            relative = await WorkspaceEditor
                .CreateCollectionAsync(_workspace, parent, name, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusLeft.Text = $"Could not create that collection: {ex.Message}";
            return;
        }

        await OpenCreatedAsync(relative, "Created ").ConfigureAwait(true);
    }

    private async Task NewDocumentFileAsync()
    {
        if (_workspace is null)
        {
            StatusLeft.Text = "Open a folder first with Ctrl+Shift+O.";
            return;
        }

        var parent = SelectedContainerRelative();

        var name = await PromptForNameAsync(
            "New request file",
            $"A .http file in {parent ?? Path.GetFileName(_workspace.Root.TrimEnd(Path.DirectorySeparatorChar))}.",
            ValidateStem).ConfigureAwait(true);

        if (name is null)
        {
            return;
        }

        string relative;

        try
        {
            relative = await WorkspaceEditor
                .CreateDocumentAsync(_workspace, parent, name, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusLeft.Text = $"Could not create that file: {ex.Message}";
            return;
        }

        await OpenCreatedAsync(relative, "Created ").ConfigureAwait(true);
    }

    /// <summary>Shows a newly created document in the rail and opens it.</summary>
    private async Task OpenCreatedAsync(string relative, string verb)
    {
        RefreshCollections();

        if (_workspace is null)
        {
            return;
        }

        var full = Path.GetFullPath(Path.Combine(_workspace.Root, relative));

        // The file is on disk either way, so a Cancel here must not undo it - it only means
        // "do not replace what I am editing", and saying where the new file went is what
        // stops that reading as a failure.
        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            StatusLeft.Text = $"{verb}{relative}. It is in the rail; the open file was left alone.";
            return;
        }

        await LoadDocumentAsync(full).ConfigureAwait(true);
        StatusLeft.Text = $"{verb}{relative}.";
    }

    /// <summary>
    /// Appends a request to the document on screen.
    /// </summary>
    /// <remarks>
    /// Into the buffer, not onto the disk. Saving is explicit everywhere else in Sling
    /// (<c>Sling.md</c> §8) and a rail button that writes to a git working tree behind the
    /// dirty marker would be the one place it is not.
    /// </remarks>
    private async Task NewRequestAsync()
    {
        // A file, not a buffer. An untitled document has nowhere for the request to be
        // saved, and on first run the buffer holds the seeded sample - which the constructor
        // deliberately left unmarked, so appending to it would ask the user to save text they
        // never wrote.
        if (_documentPath is null)
        {
            StatusLeft.Text = _workspace is null
                ? "Open or create a request file first."
                : "Open a request file first, or use + Collection to make one.";

            return;
        }

        var name = await PromptForNameAsync(
            "New request",
            $"Appended to {DocumentName}. Save with Ctrl+S when you are happy with it.",
            ValidateSegment).ConfigureAwait(true);

        if (name is null)
        {
            return;
        }

        // The document's own terminator, not a hard '\n'. A file loaded from a checkout with
        // CRLF endings would otherwise gain one LF line in the middle of it - invisible in
        // the editor, and a whole-file diff for whoever reviews it next.
        var text = RequestPane.Text;
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        // The whole buffer goes in, not a flag derived from it: whether the '###' would start
        // a line is the editor's business to ask and WorkspaceEditor's to answer, and that
        // split is what makes it testable against a parse of the result.
        var block = WorkspaceEditor.RequestBlockText(text, name, newLine);

        var offset = RequestPane.Document.TextLength;
        RequestPane.Document.Insert(offset, block);

        // The end of the request line, which is where the URL goes and therefore where
        // somebody who just named a request wants to be typing.
        RequestPane.CaretOffset = offset + block.Length - newLine.Length;
        RequestPane.ScrollToLine(RequestPane.TextArea.Caret.Line);
        RequestPane.Focus();

        RefreshOpenDocumentRequests();

        StatusLeft.Text = $"Added a request to {DocumentName}. Ctrl+S saves it.";
    }

    private static string? ValidateSegment(string typed) =>
        WorkspaceNames.TryToSegment(typed, out _, out var reason) ? null : reason;

    private static string? ValidateStem(string typed) =>
        WorkspaceNames.TryToDocumentStem(typed, out _, out var reason) ? null : reason;

    /// <summary>
    /// Asks for a name, and comes back with it or with null if the user backed out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The completion source runs its continuations asynchronously on purpose. Without it
    /// the button click that answers the prompt would run the whole rest of the command,
    /// a directory creation, a file write and a document load - inside the click handler,
    /// on the stack of the input event. That is the shape of the close-path hang that M3
    /// slice 1 shipped and had to fix.
    /// </para>
    /// <para>
    /// Validation happens here rather than in the persistence call so a rejected name leaves
    /// the prompt open with the text still in it. Being told what is wrong and having to
    /// retype it from memory is worse than not being told.
    /// </para>
    /// </remarks>
    private Task<string?> PromptForNameAsync(string title, string hint, Func<string, string?> validate)
    {
        if (NamePromptIsOpen)
        {
            return Task.FromResult<string?>(null);
        }

        CloseSettings();

        _nameValidator = validate;
        _namePrompt = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        NamePromptTitle.Text = title;
        NamePromptHint.Text = hint;
        NamePromptError.Visibility = Visibility.Collapsed;
        NamePromptBox.Text = string.Empty;

        Overlays.Reveal(NamePromptOverlay, NamePromptCard);

        NamePromptBox.Focus();

        return _namePrompt.Task;
    }

    private void OnNamePromptAccept(object sender, RoutedEventArgs e)
    {
        var typed = NamePromptBox.Text;

        if (_nameValidator?.Invoke(typed) is { } error)
        {
            NamePromptError.Text = error;
            NamePromptError.Visibility = Visibility.Visible;
            NamePromptBox.Focus();
            return;
        }

        CloseNamePrompt(typed);
    }

    private void OnNamePromptCancel(object sender, RoutedEventArgs e) => CloseNamePrompt(null);

    private void OnNamePromptKey(object sender, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key == Key.Enter && !e.IsRepeat)
        {
            e.Handled = true;
            OnNamePromptAccept(sender, e);
        }
    }

    private void CloseNamePrompt(string? answer)
    {
        var prompt = _namePrompt;

        _namePrompt = null;
        _nameValidator = null;

        Overlays.Hide(NamePromptOverlay);

        // Answered before focus moves, so the continuation cannot race a control that is on
        // its way out.
        prompt?.TrySetResult(answer);

        if (!_closed)
        {
            RequestPane.Focus();
        }
    }

    /// <summary>
    /// Puts a document's row back to an unread placeholder.
    /// </summary>
    /// <remarks>
    /// For the file being navigated away from. Its rows were filled from the buffer, which
    /// may hold edits that were just discarded - leaving them would show requests that are
    /// in no file, under a document nobody has open.
    /// </remarks>
    private void ResetRailDocument(string? path)
    {
        if (path is null)
        {
            return;
        }

        var full = Path.GetFullPath(path);

        var node = Descendants(_collections).FirstOrDefault(i =>
            i.Kind == CollectionItemKind.Document
                && i.Path is not null
                && string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase));

        if (node is null)
        {
            return;
        }

        _rebuildingLists = true;

        try
        {
            node.Children.Clear();
            node.Children.Add(new CollectionItem(CollectionItemKind.Placeholder, "…", node.Path));
            node.IsLoaded = false;
        }
        finally
        {
            _rebuildingLists = false;
        }
    }

    /// <summary>Re-reads the open document's requests from the buffer, if the rail shows it.</summary>
    /// <remarks>
    /// The open document is always worth filling, expanded or not - its rows come from
    /// memory, so there is no read to defer. The size ceiling is still honoured: the open
    /// dialog will take a document far larger than anything the parser should be run over on
    /// the dispatcher.
    /// </remarks>
    private void RefreshOpenDocumentRequests()
    {
        if (_workspace is null
            || _documentPath is null
            || RequestPane.Document.TextLength > MaxLiveRefreshLength)
        {
            return;
        }

        var node = OpenDocumentNode();

        if (node is null)
        {
            return;
        }

        _rebuildingLists = true;

        try
        {
            FillRequests(node, RequestPane.Text);
            HighlightCaretRow(node);
        }
        finally
        {
            _rebuildingLists = false;
        }
    }

    /// <summary>Moves the rail's highlight onto the request the caret is in.</summary>
    /// <remarks>
    /// The last request at or above the caret - the same "resolve backwards" rule
    /// <c>RequestDocument.BlockAtLine</c> uses to decide what <c>Ctrl+Enter</c> sends, so the
    /// highlighted row is always the one that would go.
    /// <para>
    /// The caller holds <c>_rebuildingLists</c>. It is not enough on its own for a row whose
    /// container does not exist yet, which is what <see cref="_selectedFromCode"/> is for.
    /// </para>
    /// </remarks>
    private void HighlightCaretRow(CollectionItem node)
    {
        var caret = RequestPane.TextArea.Caret.Line;

        var current = node.Children
            .Where(c => c.Kind == CollectionItemKind.Request && c.Line <= caret)
            .MaxBy(c => c.Line)
            ?? node.Children.FirstOrDefault(c => c.Kind == CollectionItemKind.Request);

        if (current is null || current.IsSelected)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            child.IsSelected = ReferenceEquals(child, current);
        }

        _selectedFromCode = current;
    }

    /// <summary>
    /// Follows the caret, without re-parsing.
    /// </summary>
    /// <remarks>
    /// Straight through rather than through the idle timer: moving the highlight reads rows
    /// that are already built, so it costs a scan of one document's children and there is
    /// nothing to defer. Re-parsing on every arrow key is what would need a timer, and it is
    /// also unnecessary - the caret moving cannot change what the requests are.
    /// </remarks>
    private void OnCaretMoved(object? sender, EventArgs e)
    {
        if (_closed || _rebuildingLists || _loadingDocument || _workspace is null || _documentPath is null)
        {
            return;
        }

        var node = OpenDocumentNode();

        if (node is null || !node.IsLoaded)
        {
            return;
        }

        _rebuildingLists = true;

        try
        {
            HighlightCaretRow(node);
        }
        finally
        {
            _rebuildingLists = false;
        }
    }

    private CollectionItem? OpenDocumentNode() =>
        Descendants(_collections).FirstOrDefault(i =>
            i.Kind == CollectionItemKind.Document && i.Path is not null && IsOpenDocument(i.Path));

    /// <summary>Restarts the idle refresh after a keystroke.</summary>
    /// <remarks>
    /// Deliberately <b>not</b> gated on there being a workspace any more. The tick now drives
    /// the command bar's send target as well as the rail, and that label has to be right in
    /// an untitled buffer - which is the state the application starts in and the state a
    /// pasted curl command lands in. <see cref="RefreshOpenDocumentRequests"/> still declines
    /// on its own when there is no rail to fill, so the tick costs one parse and nothing else.
    /// </remarks>
    private void QueueRequestRefresh()
    {
        if (_loadingDocument || RequestPane.Document.TextLength > MaxLiveRefreshLength)
        {
            return;
        }

        _requestRefresh.Stop();
        _requestRefresh.Start();
    }

    private void OnRequestRefreshTick(object? sender, EventArgs e)
    {
        _requestRefresh.Stop();

        if (_closed)
        {
            return;
        }

        RefreshOpenDocumentRequests();
        UpdateSendTarget(reparse: true);
    }
}
