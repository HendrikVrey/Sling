using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Sling.App.Editor;
using Sling.App.Environments;
using Sling.Core.Documents;
using Sling.Persistence.Environments;
using Sling.Persistence.Workspaces;

namespace Sling.App;

/// <summary>
/// The environment card: reading the two environment files, and writing values into them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The finding this exists to fix was the largest one in Sling's auth story.</b> A
/// credential could not be created from inside the application at all - the docs said so
/// outright - so the first-run path for a bearer token was to alt-tab to another editor and
/// hand-write JSON whose environment names had to match a second file that had also been
/// hand-written. Everything else about auth here was smaller than that.
/// </para>
/// <para>
/// The card is a view onto the files, exactly as the collections rail is a view onto the
/// folder. It is rebuilt from disk each time it opens and holds nothing of its own, so
/// deleting Sling leaves two JSON files that Rider and Visual Studio open unchanged.
/// </para>
/// <para>
/// <b>There is no delete and no move between the two files.</b> Both mean taking lines out
/// of somebody's repository, which belongs in a text editor where there is undo - the same
/// decision the rail made about renaming a collection. A variable's secret flag is
/// therefore settled once it exists, and the toggle goes dead and says why rather than
/// offering a move it could only half-perform.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>The label the picker uses for <see cref="EnvironmentSet.SharedName"/>.</summary>
    /// <remarks>
    /// Named rather than shown raw: <c>$shared</c> is a reserved key in a file format, not
    /// something anybody would guess the meaning of from a list.
    /// </remarks>
    private const string SharedLabel = "$shared  (every environment)";

    /// <summary>
    /// The colour of something that stops the save.
    /// </summary>
    /// <remarks>
    /// The same value the name prompt's error uses. Frozen because it is shared by every
    /// message this card shows, and an unfrozen brush handed to several elements is a
    /// dependency object with several parents.
    /// </remarks>
    private static readonly SolidColorBrush RefusalBrush = Frozen(Color.FromRgb(0xF4, 0x87, 0x71));

    /// <summary>The colour of something worth saying that is not a refusal.</summary>
    private static readonly SolidColorBrush NoteBrush = Frozen(Color.FromRgb(0x9D, 0xAE, 0xBA));

    /// <summary>Which environment the card is showing. Independent of the selected one.</summary>
    /// <remarks>
    /// Deliberately separate from <c>_selectedEnvironment</c>. Editing production's client
    /// id while sending against staging is an ordinary thing to want, and coupling the two
    /// would change what the next request resolves against as a side effect of opening a
    /// card.
    /// </remarks>
    private string _editingEnvironment = EnvironmentSet.SharedName;

    /// <summary>What the card is showing, in the order the list draws it.</summary>
    private IReadOnlyList<EnvironmentRow> _environmentRows = [];

    /// <summary>The variable the form is changing, or null when it is adding a new one.</summary>
    private EnvironmentRow? _editingRow;

    /// <summary>Guards the form refresh against the control changes it makes itself.</summary>
    private bool _updatingEnvironmentsForm;

    private bool EnvironmentsAreOpen => EnvironmentsOverlay.Visibility == Visibility.Visible;

    /// <summary>The variable a diagnostic named and could not find, and where it was used.</summary>
    private (string Name, bool Credential)? _missingVariable;

    private void OnEnvironmentsClicked(object sender, RoutedEventArgs e) => ShowEnvironments();

    /// <summary>
    /// Offers to define the variable a run could not resolve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It already knows the name and the line. Naming something it could fix and then not
    /// offering to is what made this the sharpest dead end in the application: the fix was
    /// read it, leave Sling, guess the shape of a file nobody has written yet, come back.
    /// </para>
    /// <para>
    /// The first one only. A run can fail on several names and a status bar holds one
    /// sentence; offering the first is the same choice the summary already makes, and after
    /// it is defined the next send names the next one.
    /// </para>
    /// </remarks>
    private void OfferMissingVariable(IEnumerable<ParseDiagnostic> diagnostics)
    {
        var missing = diagnostics.FirstOrDefault(d => d.MissingVariable is { Length: > 0 });

        _missingVariable = missing?.MissingVariable is { } name
            ? (name, missing.LooksLikeCredential)
            : null;

        StatusAction.Content = _missingVariable is { } offer ? $"Define {offer.Name}" : string.Empty;
        StatusAction.Visibility = _missingVariable is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnStatusAction(object sender, RoutedEventArgs e)
    {
        if (_missingVariable is not { } missing)
        {
            return;
        }

        ShowEnvironments(missing.Name, missing.Credential);
    }

    private void OnCloseEnvironments(object sender, RoutedEventArgs e) => CloseEnvironments();

    /// <summary>
    /// Opens the card, optionally with a name already filled in.
    /// </summary>
    /// <param name="name">
    /// A variable to start on. This is how the missing-variable diagnostic hands its work
    /// over: it already knows the name, and a message that names something it could define
    /// and then does not is a dead end with better manners.
    /// </param>
    /// <param name="secret">
    /// Whether a new variable defaults to the gitignored file. True when the name came from
    /// an <c>Authorization</c> header or an auth directive, because a name missing from one
    /// of those is a credential far more often than not.
    /// </param>
    private void ShowEnvironments(string? name = null, bool secret = false)
    {
        if (EnsureWorkspace("Choose the folder these environments belong to") is null)
        {
            return;
        }

        // Read from disk on the way in rather than trusted from the last activation: this
        // card is where they are about to be written, and editing a stale copy of a file
        // somebody has open elsewhere is how an edit lands on top of theirs.
        ReloadEnvironments();

        var root = _workspace?.Root ?? string.Empty;
        EnvironmentsPath.Text = root;
        EnvironmentsPath.ToolTip = root;

        // The environment being sent against is the one to start on, when there is one.
        _editingEnvironment = _selectedEnvironment ?? EnvironmentSet.SharedName;
        _editingRow = null;

        EnvironmentsReveal.IsChecked = false;
        EnvironmentsName.Text = name ?? string.Empty;
        EnvironmentsValue.Text = string.Empty;
        EnvironmentsSecret.IsChecked = secret;

        RebuildEnvironmentsCard();

        Overlays.Reveal(EnvironmentsOverlay, EnvironmentsCard);

        // The value box when the name arrived with the request: the name is already answered
        // and the value is the only thing left to type.
        if (string.IsNullOrEmpty(name))
        {
            EnvironmentsName.Focus();
        }
        else
        {
            EnvironmentsValue.Focus();
        }
    }

    private void CloseEnvironments()
    {
        Overlays.Hide(EnvironmentsOverlay);

        // Nothing of the value survives the card. It is a credential often enough that
        // leaving it in a control for the next open to show is not worth the convenience.
        EnvironmentsValue.Text = string.Empty;
        _editingRow = null;
    }

    /// <summary>Fills the picker and the list from the environments just read.</summary>
    private void RebuildEnvironmentsCard()
    {
        _rebuildingLists = true;

        try
        {
            var names = new List<string> { EnvironmentSet.SharedName };
            names.AddRange(_environments.Names);

            // The environment being edited may be in neither file yet - it is created by the
            // first value written into it - so it goes in the list on its own account.
            if (!names.Contains(_editingEnvironment, StringComparer.Ordinal))
            {
                names.Add(_editingEnvironment);
            }

            EnvironmentsPicker.ItemsSource = names.Select(Label).ToList();
            EnvironmentsPicker.SelectedItem = Label(_editingEnvironment);
        }
        finally
        {
            _rebuildingLists = false;
        }

        RefreshEnvironmentsList();
        UpdateEnvironmentsForm();
    }

    private void RefreshEnvironmentsList()
    {
        var reveal = EnvironmentsReveal.IsChecked == true;

        _environmentRows =
        [
            .. _environments.Entries
                .Where(e => string.Equals(e.Environment, _editingEnvironment, StringComparison.Ordinal))
                .Select(e => new EnvironmentRow(e, reveal)),
        ];

        EnvironmentsList.ItemsSource = _environmentRows;
        EnvironmentsEmpty.Visibility = _environmentRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEnvironmentsEnvironmentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingLists || EnvironmentsPicker.SelectedItem is not string label)
        {
            return;
        }

        _editingEnvironment = Unlabel(label);
        _editingRow = null;

        EnvironmentsValue.Text = string.Empty;

        RefreshEnvironmentsList();
        UpdateEnvironmentsForm();
    }

    private void OnEnvironmentsRevealToggled(object sender, RoutedEventArgs e)
    {
        if (EnvironmentsAreOpen)
        {
            RefreshEnvironmentsList();
        }
    }

    /// <summary>Starts changing the variable whose row was clicked.</summary>
    /// <remarks>
    /// A hidden secret's value is deliberately not copied into the box. Filling it would put
    /// on screen the exact thing the mask is there to keep off it, and by a route that never
    /// touches the toggle deciding whether it should be.
    /// </remarks>
    private void OnEnvironmentsRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EnvironmentRow row })
        {
            return;
        }

        _editingRow = row;

        EnvironmentsName.Text = row.Name;
        EnvironmentsSecret.IsChecked = row.Secret;
        EnvironmentsValue.Text = row.Secret && EnvironmentsReveal.IsChecked != true
            ? string.Empty
            : row.Value;

        UpdateEnvironmentsForm();

        EnvironmentsValue.Focus();
        EnvironmentsValue.SelectAll();
    }

    private void OnEnvironmentsFormChanged(object sender, TextChangedEventArgs e)
    {
        // A name typed over the one that was clicked is a different variable, so the row it
        // came from stops governing anything. Without this, renaming in the box would write
        // the new name into whichever file the old one happened to live in.
        if (ReferenceEquals(sender, EnvironmentsName)
            && _editingRow is { } row
            && !string.Equals(EnvironmentsName.Text, row.Name, StringComparison.Ordinal))
        {
            _editingRow = null;
        }

        UpdateEnvironmentsForm();
    }

    private void OnEnvironmentsSecretToggled(object sender, RoutedEventArgs e) => UpdateEnvironmentsForm();

    private void OnEnvironmentsValueKey(object sender, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key == Key.Enter && !e.IsRepeat && EnvironmentsSaveButton.IsEnabled)
        {
            e.Handled = true;
            RunGuarded(SaveEnvironmentValueAsync);
        }
    }

    private void OnEnvironmentsSave(object sender, RoutedEventArgs e) => RunGuarded(SaveEnvironmentValueAsync);

    /// <summary>
    /// Recomputes what the form will accept, and says why when it will not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An empty value is refused rather than written, because writing one is a clear - the
    /// one operation on this card that destroys something - and it would be reachable by
    /// clicking a masked row and pressing Save without typing anything.
    /// </para>
    /// <para>
    /// Only the name rule stops a save. The note about a variable that already exists is
    /// orientation rather than an objection: changing its value is exactly what the form is
    /// for, and it is the <em>file</em> the value lives in that is settled.
    /// </para>
    /// </remarks>
    private void UpdateEnvironmentsForm()
    {
        if (_updatingEnvironmentsForm)
        {
            return;
        }

        _updatingEnvironmentsForm = true;

        try
        {
            var name = EnvironmentsName.Text.Trim();
            var existing = _environmentRows.FirstOrDefault(
                r => string.Equals(r.Name, name, StringComparison.Ordinal));

            // The flag is a property of the variable once it exists, because moving one
            // between the files means deleting a line from the file it is leaving.
            EnvironmentsSecret.IsEnabled = existing is null;

            if (existing is not null)
            {
                EnvironmentsSecret.IsChecked = existing.Secret;
            }

            var named = name.Length > 0;
            var refused = named && !EnvironmentEditor.IsWritableName(name, out var reason) ? reason : null;

            EnvironmentsSaveButton.IsEnabled =
                named && refused is null && EnvironmentsValue.Text.Length > 0;

            var note = refused ?? Note(existing, name);

            EnvironmentsError.Text = note ?? string.Empty;
            EnvironmentsError.Foreground = refused is null ? NoteBrush : RefusalBrush;
            EnvironmentsError.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _updatingEnvironmentsForm = false;
        }
    }

    /// <summary>What to say about a variable that is already in one of the files.</summary>
    private static string? Note(EnvironmentRow? existing, string name) => existing switch
    {
        null => null,

        { Secret: true } => $"'{name}' is already a secret. Its value can be changed here; moving it "
            + $"out of {Workspace.PrivateEnvironmentFileName} means editing that file.",

        _ => $"'{name}' is already in {Workspace.SharedEnvironmentFileName}. Its value can be changed "
            + "here; making it a secret means removing it from that file first.",
    };

    private async Task SaveEnvironmentValueAsync()
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        var name = EnvironmentsName.Text.Trim();
        var value = EnvironmentsValue.Text;
        var secret = EnvironmentsSecret.IsChecked == true;

        EnvironmentWrite written;

        try
        {
            written = await EnvironmentEditor
                .SetAsync(workspace, _editingEnvironment, name, value, secret, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidDataException)
        {
            EnvironmentsError.Text = ex.Message;
            EnvironmentsError.Foreground = RefusalBrush;
            EnvironmentsError.Visibility = Visibility.Visible;
            return;
        }

        // Read back rather than patched in memory: the card is a view onto the files, and
        // the only way to be sure what is in them is to have read them.
        ReloadEnvironments();

        _editingRow = null;
        EnvironmentsValue.Text = string.Empty;

        RebuildEnvironmentsCard();

        EnvironmentsName.Focus();
        EnvironmentsName.SelectAll();

        StatusLeft.Text = Describe(written);
    }

    /// <summary>What the status bar says after a value is written.</summary>
    /// <remarks>
    /// The <c>.gitignore</c> half is not decoration. Sling has just edited somebody's
    /// repository, and saying so out loud matters as much as doing it.
    /// </remarks>
    private static string Describe(EnvironmentWrite written)
    {
        var wrote = $"Wrote '{written.Name}' to {written.Environment} in {written.FileName}.";

        return written.IgnoreEntriesAdded.Count == 0
            ? wrote
            : wrote
                + $" Added {string.Join(" and ", written.IgnoreEntriesAdded.Select(a => $"'{a}'"))}"
                + " to .gitignore.";
    }

    private void OnNewEnvironment(object sender, RoutedEventArgs e) => RunGuarded(NewEnvironmentAsync);

    private async Task NewEnvironmentAsync()
    {
        var name = await PromptForNameAsync(
            "New environment",
            "A name for a deployment - dev, staging, prod. It exists once it has a value in it.",
            ValidateEnvironmentName).ConfigureAwait(true);

        if (name is null)
        {
            return;
        }

        // Nothing is written yet, deliberately. An environment is a key in a JSON file, so
        // an empty one is punctuation somebody has to delete later; the first value written
        // into it is what creates it.
        _editingEnvironment = name.Trim();
        _editingRow = null;

        EnvironmentsValue.Text = string.Empty;

        RebuildEnvironmentsCard();
        EnvironmentsName.Focus();
    }

    /// <summary>
    /// Checks an environment name.
    /// </summary>
    /// <remarks>
    /// Looser than a variable's, because an environment name is never referenced as
    /// <c>{{name}}</c> - it is a key in a JSON object and a label in a picker. What it must
    /// not be is blank, or <c>$shared</c>, which already means something.
    /// </remarks>
    private string? ValidateEnvironmentName(string typed)
    {
        var name = typed?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            return "An environment needs a name.";
        }

        if (name.Length > 64)
        {
            return "That name is too long for an environment.";
        }

        if (string.Equals(name, EnvironmentSet.SharedName, StringComparison.Ordinal))
        {
            return $"'{EnvironmentSet.SharedName}' already exists - it is the one whose values "
                + "underlie every other environment.";
        }

        if (name.Any(char.IsControl))
        {
            return "An environment name cannot hold control characters.";
        }

        return _environments.Names.Contains(name, StringComparer.Ordinal)
            ? $"There is already an environment called '{name}'."
            : null;
    }

    private static string Label(string environment) =>
        string.Equals(environment, EnvironmentSet.SharedName, StringComparison.Ordinal)
            ? SharedLabel
            : environment;

    private static string Unlabel(string label) =>
        string.Equals(label, SharedLabel, StringComparison.Ordinal)
            ? EnvironmentSet.SharedName
            : label;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        return brush;
    }
}
