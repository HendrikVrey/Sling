using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Sling.Core.Variables;
using Sling.Persistence.Environments;
using Sling.Persistence.Workspaces;

namespace Sling.App;

/// <summary>
/// The document side of the window: which file is open, which folder it came from, which
/// environment is selected, and saving.
/// </summary>
/// <remarks>
/// <para>
/// Saving is explicit here, unlike Etch, which auto-saves continuously and has no save
/// command at all. The difference is what the file is: an Etch buffer is scratch that
/// belongs to Etch, and a <c>.http</c> file is a git artifact that belongs to a
/// repository. Rewriting one on every keystroke would move the working tree under a
/// reviewer mid-diff.
/// </para>
/// <para>
/// A folder rather than a workspace format. There is no index, no metadata file and
/// nothing for Sling to own - the folder is very often a checkout of the API's own
/// repository, with the request files beside the code they exercise.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Shown in the picker for "resolve nothing from an environment".</summary>
    private const string NoEnvironmentLabel = "no environment";

    private const string DocumentFilter =
        "HTTP request files (*.http;*.rest)|*.http;*.rest|All files (*.*)|*.*";

    private Workspace? _workspace;
    private EnvironmentSet _environments = EnvironmentSet.Empty;
    private string? _selectedEnvironment;

    /// <summary>The file in the request pane, or null while it is an unsaved buffer.</summary>
    private string? _documentPath;

    /// <summary>The file Sling was launched with, until it has been opened.</summary>
    private string? _pendingStartupFile;

    private bool _dirty;

    /// <summary>True while the pane is being filled from disk, so it is not marked dirty.</summary>
    private bool _loadingDocument;

    /// <summary>True while a picker or the file list is being repopulated in code.</summary>
    private bool _rebuildingLists;

    /// <summary>
    /// Set once the unsaved-work question has been answered, so the <c>Close</c> that
    /// follows it is not asked again.
    /// </summary>
    private bool _closeConfirmed;

    /// <summary>
    /// The environment problems already reported, so alt-tabbing back does not overwrite
    /// the status bar with a message the user has read.
    /// </summary>
    private string _reportedProblems = string.Empty;

    /// <summary>Wires the document side. Called once, from the constructor.</summary>
    private void InitializeWorkspace()
    {
        InitializeCollections();

        RequestPane.TextChanged += OnRequestTextChanged;

        // Environment files are edited outside Sling - that is the point of them being
        // plain JSON beside the requests. Re-reading them when the window comes forward
        // covers the whole of that workflow without a file watcher, and two small files
        // is not a cost worth a mechanism.
        Activated += OnWindowActivated;

        UpdateTitle();

        // Deferred to Loaded rather than done here. The constructor cannot await, and
        // reading a file on the way to the first frame would hold the window back for as
        // long as the disk takes; by Loaded there is a dispatcher running, which is what
        // RunGuarded needs in order to report a failure into the status bar.
        _pendingStartupFile = App.StartupFile;
        if (_pendingStartupFile is not null)
        {
            Loaded += OnLoadedOpenStartupDocument;
        }
    }

    /// <summary>Opens the command-line file once, on the first load.</summary>
    private void OnLoadedOpenStartupDocument(object sender, RoutedEventArgs e)
    {
        // Unsubscribed first. Loaded fires again whenever the window is re-attached to a
        // visual tree, and re-opening the file then would discard whatever had been typed
        // since - silently, because this path deliberately does not ask about unsaved work.
        Loaded -= OnLoadedOpenStartupDocument;

        if (_pendingStartupFile is not { } path)
        {
            return;
        }

        _pendingStartupFile = null;
        RunGuarded(() => OpenStartupDocumentAsync(path));
    }

    /// <summary>
    /// Opens the file Sling was launched with, and adopts its folder as the workspace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The folder matters as much as the file. A <c>.http</c> document resolves
    /// <c>{{variables}}</c> from the environment files beside it and reads
    /// <c>&lt; ./body.json</c> imports relative to the workspace root, so opening the
    /// file on its own would give a document that parses and then fails to send.
    /// Explorer hands over one path; the directory it came from is the rest of the answer.
    /// </para>
    /// <para>
    /// Folder before file, matching <see cref="OpenFolderAsync"/>: setting the workspace
    /// rebuilds the rail and reloads the environments, so doing it afterwards would clear
    /// the selection the load had just made.
    /// </para>
    /// <para>
    /// No unsaved-work question, deliberately. This runs on the way to the first frame,
    /// against an empty buffer, so there is nothing of the user's to discard and a prompt
    /// would be a question about their own click.
    /// </para>
    /// </remarks>
    private async Task OpenStartupDocumentAsync(string path)
    {
        if (Path.GetDirectoryName(path) is { } directory)
        {
            try
            {
                SetWorkspace(Workspace.Open(directory));
            }
            catch (DirectoryNotFoundException)
            {
                // A file whose parent cannot be enumerated - a race, or a link Sling is not
                // allowed to walk. The document is still worth opening on its own.
            }
        }

        await LoadDocumentAsync(path).ConfigureAwait(true);
    }

    /// <summary>
    /// Handles the document chords. Returns true when the key was one of them.
    /// </summary>
    /// <remarks>
    /// Resolved on the window's tunnelling pass with the rest of the keymap, for the
    /// reason given on <see cref="OnPreviewKeyDown"/>: an <c>InputBinding</c> is matched
    /// after the focused editor has already had the key.
    /// </remarks>
    private bool TryHandleDocumentKey(KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers is not (ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            return false;
        }

        var shift = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.N:
                RunGuarded(shift ? NewRequestAsync : NewDocumentAsync);
                return true;

            case Key.O:
                RunGuarded(shift ? OpenFolderAsync : OpenDocumentAsync);
                return true;

            case Key.I when !shift:
                RunGuarded(ImportPostmanAsync);
                return true;

            case Key.S:
                RunGuarded(shift ? () => SaveAsAsync() : () => SaveAsync());
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Starts <paramref name="work"/> and makes sure a failure is seen.
    /// </summary>
    /// <remarks>
    /// Every document command is started from a key handler and its task is discarded, so
    /// without this an exception nobody anticipated vanishes completely: no message, no
    /// dialog, and a command that silently does nothing every time it is pressed. Each
    /// method already maps the failures it knows about - this is for the ones nobody has
    /// met yet, which is the whole category that reaches a last-resort catch.
    /// </remarks>
    private async void RunGuarded(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusLeft.Text = $"That did not work: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Everything outside the document that resolving it needs.
    /// </summary>
    /// <remarks>
    /// Built on the dispatcher and handed to the runner, rather than read from fields on
    /// whatever thread the send ends up on. Nothing here is thread-safe and it does not
    /// need to be: a snapshot taken when the user pressed send is also the right answer
    /// semantically, because switching environment mid-flight should not change what the
    /// request in flight resolved to.
    /// </remarks>
    private ResolutionContext CreateResolutionContext()
    {
        var directory = _documentPath is null ? null : Path.GetDirectoryName(_documentPath);

        return new ResolutionContext
        {
            Environment = _environments.Select(_selectedEnvironment),
            Files = directory is null
                ? NoRequestFiles.Instance
                : new WorkspaceFileSource(directory, _workspace?.Root),
        };
    }

    private void OnRequestTextChanged(object? sender, EventArgs e)
    {
        // Before the _dirty short-circuit, deliberately: every keystroke after the first is
        // one this returns early on, and those are exactly the ones that add the '###' the
        // rail is meant to notice.
        QueueRequestRefresh();

        // Above the short-circuit for the same reason, and one sharper: emptying a document
        // that was already dirty has to bring the empty state back, and that is a keystroke
        // the early return would swallow.
        UpdateEmptyState();

        if (_loadingDocument || _dirty)
        {
            return;
        }

        _dirty = true;
        UpdateTitle();
    }

    private void OnWindowActivated(object? sender, EventArgs e) => ReloadEnvironments();

    private void OnEnvironmentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingLists || EnvironmentPicker.SelectedItem is not string label)
        {
            return;
        }

        SelectEnvironment(string.Equals(label, NoEnvironmentLabel, StringComparison.Ordinal) ? null : label);

        StatusLeft.Text = _selectedEnvironment is null
            ? "No environment selected. Variables resolve from the document only."
            : $"Environment '{_selectedEnvironment}' selected. Earlier responses were forgotten.";
    }

    /// <summary>
    /// Changes the environment in force, dropping anything resolved under the old one.
    /// </summary>
    /// <remarks>
    /// The one place the selection changes, because forgetting what the old one produced
    /// is not optional and there are three ways to arrive here: the combo box, an
    /// environment that vanished from the file, and an environment file that stopped
    /// defining any. A token fetched against staging is a valid-looking bearer token, and a
    /// chained request that reused it after a switch would send it to production.
    /// <para>
    /// Three things are dropped and they are the same hazard three times over: stored
    /// responses, cached access tokens, and the cookie jar. <c>Sling.md</c> §5.6 scopes the
    /// jar per environment for exactly this reason - a session cookie carried across a
    /// switch is a credential the receiving server cannot tell was meant for somewhere
    /// else.
    /// </para>
    /// </remarks>
    private void SelectEnvironment(string? name)
    {
        if (string.Equals(name, _selectedEnvironment, StringComparison.Ordinal))
        {
            return;
        }

        _selectedEnvironment = name;
        _runner.ForgetSession();
        ResetCookieJar();
    }

    private async Task NewDocumentAsync()
    {
        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return;
        }

        SetDocument(string.Empty, path: null);
        StatusLeft.Text = ReadyHint;
    }

    private async Task OpenDocumentAsync()
    {
        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Open a request file",
            Filter = DocumentFilter,
            InitialDirectory = _workspace?.Root ?? string.Empty,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await LoadDocumentAsync(dialog.FileName).ConfigureAwait(true);
        }
    }

    private async Task OpenFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Open a folder of request files",
            InitialDirectory = _workspace?.Root ?? string.Empty,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SetWorkspace(Workspace.Open(dialog.FolderName));
        }
        catch (DirectoryNotFoundException ex)
        {
            StatusLeft.Text = ex.Message;
            return;
        }

        // A folder with exactly one request file has an obvious thing to open, and doing
        // it saves the one click that every single-file workspace would otherwise need.
        if (SingleDocumentPath() is { } only && await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            await LoadDocumentAsync(only).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Makes sure there is a workspace, asking for a folder when there is not.
    /// </summary>
    /// <returns>
    /// The workspace, or null when the user backed out or the folder could not be opened.
    /// Returned rather than left for the caller to read off the field, so the compiler
    /// carries the "there is one now" through the rest of the method.
    /// </returns>
    /// <remarks>
    /// A creation command that answers "open a folder first" is a dead end wearing the
    /// clothes of a message: the user has said what they want, and the tool knows the one
    /// thing missing and can ask for it. The same shape as the missing-variable diagnostic
    /// offering to define the variable rather than naming the three files it could go in.
    /// </remarks>
    private Workspace? EnsureWorkspace(string title)
    {
        if (_workspace is not null)
        {
            return _workspace;
        }

        var dialog = new OpenFolderDialog { Title = title };

        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        try
        {
            SetWorkspace(Workspace.Open(dialog.FolderName));
        }
        catch (DirectoryNotFoundException ex)
        {
            StatusLeft.Text = ex.Message;
            return null;
        }

        // Deliberately without the single-document convenience OpenFolderAsync applies. The
        // user asked to create something, and opening some other file underneath them on
        // the way there would replace the buffer they are about to write into.
        return _workspace;
    }

    /// <summary>Opens a folder, and drops everything that belonged to the last one.</summary>
    /// <remarks>
    /// A different folder is a different set of APIs. Leaving this to
    /// <see cref="ReloadEnvironments"/> was not enough: it resets only when the selected
    /// environment's <em>values</em> change, and two folders that define no environments at
    /// all both select an empty set - so opening the second kept the first one's cookies
    /// and tokens, in a window whose file rail said it was somewhere else.
    /// </remarks>
    private void SetWorkspace(Workspace workspace)
    {
        _workspace = workspace;
        _runner.ForgetSession();
        ResetCookieJar();

        ShowWorkspaceRail(hasWorkspace: true);
        FilesLabel.Text = Path.GetFileName(workspace.Root.TrimEnd(Path.DirectorySeparatorChar)).ToUpperInvariant();
        FilesLabel.ToolTip = workspace.Root;

        // A different folder is a different tree; carrying the last one's open branches over
        // would expand paths that no longer exist and collapse the ones that do.
        _expanded.Clear();

        RefreshCollections();
        SelectOpenDocumentInTree();
        ReloadEnvironments();
    }

    private async Task LoadDocumentAsync(string path)
    {
        try
        {
            var text = await RequestFileStore.ReadAsync(path, CancellationToken.None).ConfigureAwait(true);
            SetDocument(text, path);
        }
        catch (IOException ex)
        {
            StatusLeft.Text = $"Could not open '{Path.GetFileName(path)}': {ex.Message}";
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusLeft.Text = $"Could not open '{Path.GetFileName(path)}': {ex.Message}";
            return;
        }

        StatusLeft.Text = ReadyHint;
    }

    /// <summary>Puts text in the pane and records where it came from.</summary>
    /// <remarks>
    /// Every path that replaces the document goes through here, which is why the stored
    /// responses are forgotten here rather than at the call sites. Opening a file did it
    /// and <see cref="NewDocumentAsync"/> did not, so <c>Ctrl+N</c> left the previous
    /// file's <c>login</c> response live - and a fresh buffer's
    /// <c>{{login.response.body.$.access_token}}</c> resolved against it and sent that
    /// token wherever the new document pointed. Request names are per-file; a response
    /// store that outlives the file is a store keyed by the wrong thing.
    /// </remarks>
    private void SetDocument(string text, string? path)
    {
        // The outgoing file's rail rows were filled from the buffer, and the buffer is about
        // to be replaced - including, on a discarded edit, by text that was never on disk.
        ResetRailDocument(_documentPath);

        _loadingDocument = true;

        try
        {
            RequestPane.Text = text;
            RequestPane.CaretOffset = 0;
        }
        finally
        {
            _loadingDocument = false;
        }

        _documentPath = path;
        _dirty = false;
        _runner.ForgetSession();
        ResetCookieJar();

        // Before the selection, so the rail has this file's requests under it by the time
        // the row is revealed rather than a placeholder that resolves a moment later.
        RefreshOpenDocumentRequests();

        SelectOpenDocumentInTree();
        UpdateTitle();

        // A whole new buffer, so the cached parse behind the send target is worthless. This
        // is a load rather than a keystroke, so the reparse is affordable and the label is
        // right the moment the file appears.
        UpdateSendTarget(reparse: true);
    }

    /// <summary>Saves, asking where to put it if the document has never been saved.</summary>
    /// <param name="adoptWorkspace">
    /// False on the closing path. Saving an untitled buffer normally adopts its folder as
    /// the workspace, which enumerates a tree and may edit a <c>.gitignore</c> - none of
    /// which anyone wants happening on the way out of the application.
    /// </param>
    private async Task<bool> SaveAsync(bool adoptWorkspace = true)
    {
        if (_documentPath is null)
        {
            return await SaveAsAsync(adoptWorkspace).ConfigureAwait(true);
        }

        return await WriteAsync(_documentPath).ConfigureAwait(true);
    }

    private async Task<bool> SaveAsAsync(bool adoptWorkspace = true)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save this request file",
            Filter = DocumentFilter,
            DefaultExt = ".http",
            AddExtension = true,
            FileName = _documentPath is null ? "requests.http" : Path.GetFileName(_documentPath),
            InitialDirectory = _workspace?.Root ?? string.Empty,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        if (!await WriteAsync(dialog.FileName).ConfigureAwait(true))
        {
            return false;
        }

        if (!adoptWorkspace)
        {
            return true;
        }

        // Saving into the open workspace adds a file to it; saving outside one is how a
        // folder gets opened without ever using the folder command.
        if (_workspace is null)
        {
            var directory = Path.GetDirectoryName(dialog.FileName);
            if (directory is not null)
            {
                SetWorkspace(Workspace.Open(directory));
            }
        }
        else
        {
            RefreshCollections();
        }

        SelectOpenDocumentInTree();
        return true;
    }

    private async Task<bool> WriteAsync(string path)
    {
        // Both captured before the await, and the version is the load-bearing half: the
        // write is not instant, and a keystroke landing during it sets _dirty again.
        // Clearing the flag unconditionally afterwards would mark the document clean with
        // that edit neither on disk nor flagged - so closing the window would discard it
        // silently, which is the one outcome the dirty marker exists to prevent.
        var text = RequestPane.Text;
        var saved = RequestPane.Document.Version;

        try
        {
            await RequestFileStore.SaveAsync(path, text, CancellationToken.None).ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            StatusLeft.Text = $"Could not save: {ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusLeft.Text = $"Could not save: {ex.Message}";
            return false;
        }

        _documentPath = path;

        var editedDuringWrite = !saved.BelongsToSameDocumentAs(RequestPane.Document.Version)
            || saved.CompareAge(RequestPane.Document.Version) != 0;

        _dirty = editedDuringWrite;
        UpdateTitle();

        StatusLeft.Text = editedDuringWrite
            ? $"Saved {Path.GetFileName(path)}, but it changed while it was being written - save again."
            : $"Saved {Path.GetFileName(path)}.";

        return true;
    }

    /// <summary>
    /// Asks about unsaved work. True means carry on with whatever prompted the question.
    /// </summary>
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_dirty)
        {
            return true;
        }

        var answer = await AskAboutUnsavedAsync("before leaving this file").ConfigureAwait(true);

        return answer switch
        {
            UnsavedChoice.Save => await SaveAsync().ConfigureAwait(true),
            UnsavedChoice.Discard => true,
            _ => false,
        };
    }

    /// <summary>
    /// Re-reads both environment files, and re-checks that the secrets one is ignored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs on every window activation, because the environment files are plain JSON
    /// beside the requests and editing them in another editor is the expected workflow.
    /// </para>
    /// <para>
    /// The <c>.gitignore</c> check belongs here rather than only on folder-open, and the
    /// reason is specific to this slice: nothing in it <em>creates</em> a secrets file, so
    /// the only way a user gets one is by writing it themselves - after the folder is
    /// already open. Checking on open alone covered every case except the one that
    /// actually happens.
    /// </para>
    /// </remarks>
    private void ReloadEnvironments()
    {
        if (_workspace is null)
        {
            return;
        }

        var before = _environments.Select(_selectedEnvironment);

        _environments = EnvironmentStore.Load(_workspace);
        RebuildEnvironmentPicker();

        // A name can keep pointing at a different deployment without the selection ever
        // changing - someone edits 'base' in the file from staging to production and
        // alt-tabs back. That has to invalidate what was fetched under the old value for
        // the same reason switching environments does.
        if (!before.HasSameValuesAs(_environments.Select(_selectedEnvironment)))
        {
            _runner.ForgetSession();
            ResetCookieJar();
        }

        // Sling.md §5.1. Saying so out loud matters as much as doing it, because this
        // edits somebody's repository. Cheap when there is nothing to do - one File.Exists.
        var added = EnvironmentStore.ProtectSecrets(_workspace);
        if (added.Count > 0)
        {
            StatusLeft.Text =
                $"Added {string.Join(" and ", added.Select(a => $"'{a}'"))} to .gitignore - "
                    + "this folder has a secrets file that was not ignored.";
            return;
        }

        // Reported once per distinct set of problems. Without that, every alt-tab back
        // overwrites whatever the status bar was saying - a send result, or "Saved
        // requests.http." - with a message the user has already read.
        var problems = string.Join('\n', _environments.Problems);
        if (problems.Length > 0 && !string.Equals(problems, _reportedProblems, StringComparison.Ordinal))
        {
            StatusLeft.Text = _environments.Problems.Count == 1
                ? _environments.Problems[0]
                : $"{_environments.Problems[0]}  (+{(_environments.Problems.Count - 1).ToString(CultureInfo.InvariantCulture)} more)";
        }

        _reportedProblems = problems;
    }

    private void RebuildEnvironmentPicker()
    {
        // A workspace with no environment file gets no picker at all rather than a picker
        // with one entry meaning "off" - an empty control invites the question of what is
        // missing.
        if (_environments.Names.Count == 0)
        {
            EnvironmentPicker.Visibility = Visibility.Collapsed;
            SelectEnvironment(null);

            // A file holding only '$shared' defines no selectable environment but its
            // values are still in force. With no picker there is nothing on screen to say
            // so, and a variable resolving from an invisible source is worse than one that
            // does not resolve.
            var shared = _environments.Select(null).Count;
            if (shared > 0)
            {
                StatusLeft.Text = $"{shared.ToString(CultureInfo.InvariantCulture)} shared variable"
                    + (shared == 1 ? " is" : "s are")
                    + $" in force from {Workspace.SharedEnvironmentFileName}.";
            }

            return;
        }

        // A selection that no longer exists - the environment was renamed or removed in
        // the file since it was chosen - must not survive as a name nothing resolves. It
        // goes through SelectEnvironment because arriving here is the same transition as
        // picking a different one from the combo box, and the stored responses have to be
        // dropped either way.
        if (_selectedEnvironment is not null
            && !_environments.Names.Contains(_selectedEnvironment, StringComparer.Ordinal))
        {
            SelectEnvironment(null);
        }

        _rebuildingLists = true;

        try
        {
            EnvironmentPicker.ItemsSource = new[] { NoEnvironmentLabel }.Concat(_environments.Names).ToList();
            EnvironmentPicker.SelectedItem = _selectedEnvironment ?? NoEnvironmentLabel;
            EnvironmentPicker.Visibility = Visibility.Visible;
        }
        finally
        {
            _rebuildingLists = false;
        }
    }

    private string DocumentName => _documentPath is null ? "Untitled" : Path.GetFileName(_documentPath);

    private void UpdateTitle()
    {
        var marker = _dirty ? " •" : string.Empty;

        Title = $"{DocumentName}{marker} - Sling";
        RequestLabel.Text = $"{DocumentName.ToUpperInvariant()}{marker}";
        RequestLabel.ToolTip = _documentPath;

        // The Save button's enabled state is the dirty marker in another form, so it is
        // recomputed here rather than at each of the several places that set _dirty - one of
        // which would eventually be forgotten.
        UpdateToolbar();
    }

    /// <summary>
    /// Gives unsaved work a chance to be kept before the window goes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cancel the close, save, then close again - rather than blocking on the save here.
    /// <see cref="CancelEventArgs.Cancel"/> is read the moment this returns, so the close
    /// has to be called off while the answer is still unknown and re-issued once it is.
    /// </para>
    /// <para>
    /// <strong>Blocking is not an option, and the obvious reason it looks like one is
    /// wrong.</strong> An earlier version called <c>SaveAsync().GetAwaiter().GetResult()</c>
    /// on the grounds that the write underneath uses <c>ConfigureAwait(false)</c>. It does
    /// - but <see cref="WriteAsync"/>'s own await does not, so its continuation is posted
    /// to the dispatcher that <c>GetResult</c> is blocking. That is a guaranteed hang on
    /// "save before closing? yes", which destroys exactly the work the prompt exists to
    /// protect. <c>ConfigureAwait(false)</c> two layers down buys nothing; what matters is
    /// every await between here and the I/O.
    /// </para>
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_closeConfirmed || !_dirty)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        RunGuarded(async () =>
        {
            var answer = await AskAboutUnsavedAsync("before closing").ConfigureAwait(true);

            if (answer == UnsavedChoice.Cancel)
            {
                return;
            }

            // A failed save, or a dismissed Save As dialog, must not close the window:
            // that would throw away the work the user just asked to keep.
            if (answer == UnsavedChoice.Save
                && !await SaveAsync(adoptWorkspace: false).ConfigureAwait(true))
            {
                return;
            }

            _closeConfirmed = true;
            Close();
        });

        base.OnClosing(e);
    }
}
