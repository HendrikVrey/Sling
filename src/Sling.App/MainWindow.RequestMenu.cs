using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Sling.App;

/// <summary>
/// The request pane's context menu.
/// </summary>
/// <remarks>
/// <para>
/// Raised as a defect once and left: right-clicking the buffer you read offered the whole
/// transform catalogue, and right-clicking the buffer you <em>write</em> did nothing at all.
/// Both of the things this adds had a chord and nothing else, which is a keymap rather than
/// an interface.
/// </para>
/// <para>
/// Deliberately short. The response menu is long because a transform catalogue is long; this
/// holds the two commands that are about the request under the caret, the editing commands
/// anybody expects from a text box, and nothing that already has a button on the command bar.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private void InstallRequestContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += OnRequestMenuOpening;

        // The TextArea, not the TextEditor. AvalonEdit's editing and navigation commands are
        // registered onto an input handler attached to the TextArea, and every CanExecute in
        // them opens with 'target as TextArea' - so a menu whose command target is the editor
        // renders Copy and Select all permanently greyed out.
        RequestPane.TextArea.ContextMenu = menu;
    }

    private void OnRequestMenuOpening(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        menu.Items.Clear();

        menu.Items.Add(Action("Auth for this request…", "Ctrl+Alt+A", ShowAuth));
        menu.Items.Add(Action("Environments and secrets…", "Ctrl+E", () => ShowEnvironments()));

        menu.Items.Add(new Separator());

        menu.Items.Add(Command("Cut", ApplicationCommands.Cut));
        menu.Items.Add(Command("Copy", ApplicationCommands.Copy));
        menu.Items.Add(Command("Paste", ApplicationCommands.Paste));
        menu.Items.Add(Command("Select all", ApplicationCommands.SelectAll));

        menu.Items.Add(new Separator());

        // The focus goes back to the editor first. Completion is anchored to the caret and
        // refuses to open when the keyboard focus is elsewhere, which while a menu is up it
        // always is.
        menu.Items.Add(Action("Complete here", "Ctrl+Space", () =>
        {
            RequestPane.Focus();
            ShowCompletion();
        }));
    }

    /// <summary>A row that runs a method rather than a routed command, naming its chord.</summary>
    private static MenuItem Action(string header, string gesture, Action run)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture };
        item.Click += (_, _) => run();

        return item;
    }
}
