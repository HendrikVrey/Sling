using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Etch.Core.Abstractions;
using Etch.Core.Palette;
using Sling.App.Editor;

namespace Sling.App;

/// <summary>
/// The transform menu over the response body — the milestone's payoff, and the thing no
/// other HTTP client does.
/// </summary>
public partial class MainWindow
{
    private ContextMenu? _responseMenu;

    /// <summary>
    /// Builds the response pane's context menu and attaches it.
    /// </summary>
    /// <remarks>
    /// <b>Attached to the <c>TextArea</c>, not to the <c>TextEditor</c>.</b> AvalonEdit's
    /// <c>TextEditor</c> has no command bindings of its own: <c>EditingCommandHandler</c>
    /// and <c>CaretNavigationCommandHandler</c> register onto an input handler attached to
    /// the <c>TextArea</c>, and every one of their <c>CanExecute</c> methods opens with
    /// <c>target as TextArea</c> and does nothing otherwise. A menu whose command target
    /// is the editor renders Copy and Select All permanently greyed out — a failure that
    /// looks like a WPF defect and gets debugged as one.
    /// </remarks>
    private void InstallResponseContextMenu()
    {
        _responseMenu = new ContextMenu();
        _responseMenu.Opened += OnResponseMenuOpening;

        ResponsePane.TextArea.ContextMenu = _responseMenu;
    }

    /// <summary>
    /// Rebuilds the menu each time it opens, because what it should offer depends on what
    /// the buffer currently is.
    /// </summary>
    /// <remarks>
    /// Rebuilt rather than refreshed. The rows change identity as well as state — a JSON
    /// body offers four JSON transforms, a JWT offers "Decode JWT" — so keeping a fixed
    /// set of items and toggling their enabled state would mean maintaining a menu of
    /// every transform Sling has, mostly disabled.
    /// </remarks>
    private void OnResponseMenuOpening(object sender, RoutedEventArgs e)
    {
        if (_responseMenu is not { } menu)
        {
            return;
        }

        menu.Items.Clear();

        menu.Items.Add(Command("Copy", ApplicationCommands.Copy));
        menu.Items.Add(Command("Select all", ApplicationCommands.SelectAll));
        menu.Items.Add(new Separator());

        AddSuggested(menu);

        menu.Items.Add(new Separator());
        menu.Items.Add(AllTransformsSubmenu());
    }

    /// <summary>
    /// The transforms that apply to what is in the buffer right now.
    /// </summary>
    /// <remarks>
    /// When nothing is recognised the row says so rather than leaving a gap. An empty
    /// region between two separators reads as a bug; "Nothing obvious for this body" reads
    /// as an answer, and the full list is directly below it.
    /// </remarks>
    private void AddSuggested(ContextMenu menu)
    {
        var suggested = BodyTransforms.Suggested(_detection, _recentTransformIds);

        if (suggested.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Nothing obvious for this body", IsEnabled = false });
            return;
        }

        foreach (var transform in suggested)
        {
            menu.Items.Add(TransformItem(transform));
        }
    }

    /// <summary>Everything else, grouped the way the catalogue groups itself.</summary>
    private MenuItem AllTransformsSubmenu()
    {
        var all = new MenuItem { Header = "All transforms" };

        foreach (var group in BodyTransforms.All
            .GroupBy(static transform => transform.Category)
            .OrderBy(static group => group.Key))
        {
            var category = new MenuItem { Header = group.Key.ToString() };

            foreach (var transform in group.OrderBy(static t => t.Name, StringComparer.CurrentCulture))
            {
                category.Items.Add(TransformItem(transform));
            }

            all.Items.Add(category);
        }

        return all;
    }

    private MenuItem TransformItem(ITransform transform)
    {
        var item = new MenuItem { Header = transform.Name };

        // Fire-and-forget, because a Click handler cannot be awaited. Everything inside
        // RunTransformAsync is inside a try, so nothing escapes into the void.
        item.Click += (_, _) => _ = RunTransformAsync(transform);

        return item;
    }

    private static MenuItem Command(string header, RoutedUICommand command) =>
        new() { Header = header, Command = command };

    /// <summary>
    /// How long a transform may run before it is abandoned.
    /// </summary>
    /// <remarks>
    /// A bound rather than a tuning knob. The transport caps a body at sixteen mebibytes
    /// and the menu is offered on all of it, so "this one is going to take a while" is a
    /// reachable state and needs an exit that is not the task manager.
    /// </remarks>
    private static readonly TimeSpan TransformTimeout = TimeSpan.FromSeconds(20);

    private bool _transforming;

    /// <summary>
    /// Applies a transform to the body and re-reads what is left.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On a background thread, and that is a requirement rather than a nicety.</b>
    /// <c>ITransform</c>'s own contract says the application layer runs these off the UI
    /// thread; a sixteen-mebibyte minified JSON body run through "Format JSON" on the
    /// dispatcher freezes the window for seconds with no progress and no way out, which is
    /// the same mistake <see cref="Editor.ResponseSyntax.RefreshFoldings"/> goes to real
    /// trouble to avoid one file away.
    /// </para>
    /// <para>
    /// The document is only touched back on the UI thread, after the await, because
    /// AvalonEdit's <c>TextDocument</c> has thread affinity. The buffer is re-read at that
    /// point rather than captured beforehand — see <see cref="Editor.BodyTransforms"/>.
    /// </para>
    /// <para>
    /// The re-read afterwards is what makes transforms chain: base64 → JSON → sort keys is
    /// three clicks in one buffer, because after each one the pane asks again what it is
    /// holding and offers the next obvious thing.
    /// </para>
    /// </remarks>
    private async Task RunTransformAsync(ITransform transform)
    {
        // One at a time. Two transforms racing to rewrite the same buffer would interleave
        // their edits, and the second would be computed from text the first had already
        // replaced.
        if (_transforming)
        {
            return;
        }

        _transforming = true;

        try
        {
            using var timeout = new CancellationTokenSource(TransformTimeout);

            StatusLeft.Text = $"{transform.Name} …";

            var outcome = await BodyTransforms
                .ApplyAsync(ResponsePane, transform, timeout.Token)
                .ConfigureAwait(true);

            if (_closed)
            {
                return;
            }

            if (outcome.Applied)
            {
                // The re-analysis is not here: ResponsePane.TextChanged does it, so that
                // an undo gets the same treatment as an apply. Doing it in both places
                // would be two answers to one question.
                _recentTransformIds = PaletteRanking.Remember(_recentTransformIds, transform.Id);
            }
            else if (outcome.ErrorOffset is { } offset)
            {
                // The transform said where it gave up. Putting the caret there is the whole
                // value of that number — a message naming a line still leaves the user to
                // go and find it, and on a minified body the whole payload is line one.
                ResponsePane.Select(offset, 0);
                ResponsePane.TextArea.Caret.BringCaretToView();
            }

            StatusLeft.Text = outcome.Message;
        }
        catch (OperationCanceledException)
        {
            StatusLeft.Text = $"{transform.Name} took too long and was abandoned.";
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Last resort, and deliberately broad. A transform is contractually supposed to
            // report bad input as a failed result rather than by throwing, so anything that
            // arrives here is a contract violation or something nobody has met yet — and
            // this runs from a click handler, so an escape would take the process down and
            // lose the buffer with it. A wrong-looking message beats a dead window.
            StatusLeft.Text = $"{transform.Name} failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _transforming = false;
        }
    }
}
