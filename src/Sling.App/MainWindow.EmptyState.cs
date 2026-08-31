using System.Runtime.InteropServices;
using System.Windows;
using Sling.Import.Curl;

namespace Sling.App;

/// <summary>
/// The pane under the editor, and the only thing that decides whether it is there.
/// </summary>
/// <remarks>
/// <para>
/// Sling used to seed two GitHub requests into the buffer on every launch. Removing them
/// and leaving an empty editor would have repeated the collapsed-rail mistake one pane
/// over: on first run the whole feature was invisible and the way to reach it was a chord
/// nobody had been told about. A panel that is absent explains less than an empty one.
/// </para>
/// <para>
/// So the seeded text became an empty state that names the concept and offers the three
/// ways in, with the example itself one quiet click away. The difference that matters is
/// consent: nothing arrives in the buffer unless it was asked for, which is what made the
/// sample wrong rather than merely unnecessary.
/// </para>
/// <para>
/// It was a card floating over the editor's canvas until Hendrik reported how it looked
/// (2026-08-31). It is now a pane of its own below the editor, which is what makes its
/// visibility a layout question rather than a decoration one.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// Shows or hides the getting-started pane, which is purely a function of whether the
    /// buffer holds anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One character is enough to take it down, whitespace included, and it comes straight
    /// back if that character is deleted. Hendrik asked for exactly that, and the layout
    /// agrees with him: the pane takes real height off the editor now rather than floating
    /// over it, so a document somebody has started typing into must not still be sharing
    /// the column with a panel headed "Nothing open".
    /// </para>
    /// <para>
    /// That is a change from the floating card, which treated a buffer of pure whitespace as
    /// still empty. A card that hid on a stray space would have looked like a glitch; a pane
    /// that gives the editor its space back does not.
    /// </para>
    /// </remarks>
    private void UpdateEmptyState()
    {
        var empty = RequestPane.Document.TextLength == 0;

        GettingStarted.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        // The row as well as the element in it. A star-sized row keeps its share of the
        // column whether or not its child is visible, so hiding the card on its own left
        // the editor stopping two thirds of the way down over a band of nothing.
        GettingStartedRow.Height = empty ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    /// <summary>
    /// Puts the chaining example in the buffer, on request.
    /// </summary>
    /// <remarks>
    /// Marked dirty like any other edit, and that is the whole difference from the seeded
    /// sample: this text is in the buffer because somebody asked for it, so being asked
    /// whether to save it is right rather than a question about work they never did.
    /// </remarks>
    private void OnLoadExampleClicked(object sender, RoutedEventArgs e)
    {
        RequestPane.Document.Insert(0, ExampleDocument);
        RequestPane.CaretOffset = RequestPane.Document.TextLength;
        RequestPane.Focus();

        StatusLeft.Text = "Ctrl+Enter on the second request sends the first one too - that is chaining.";
    }

    /// <summary>
    /// Converts whatever curl command is on the clipboard into the buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same conversion the paste handler runs, reached by a button because pasting is
    /// only discoverable to somebody who already suspects it works. <see cref="CurlImport"/>
    /// answers "not curl" rather than guessing, and this says so instead of putting the
    /// clipboard's contents in the pane - a button that dumps whatever was copied last into
    /// a document is a worse outcome than one that declines.
    /// </para>
    /// <para>
    /// The clipboard is another process's to lock, so reading it throws often enough to be
    /// ordinary rather than exceptional.
    /// </para>
    /// </remarks>
    private void OnPasteCurlClicked(object sender, RoutedEventArgs e)
    {
        string? copied;

        try
        {
            copied = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (ExternalException ex)
        {
            // Another application is holding the clipboard open. Nothing to do about it
            // except say so - it clears on its own.
            StatusLeft.Text = $"The clipboard could not be read: {ex.Message}";
            return;
        }

        if (string.IsNullOrWhiteSpace(copied))
        {
            StatusLeft.Text = "There is nothing on the clipboard. Copy a curl command and press this again.";
            return;
        }

        var result = CurlImport.Convert(copied);

        if (!result.Recognized || result.Http.Length == 0)
        {
            StatusLeft.Text = "That is not a curl command, so nothing was pasted. "
                + "Most tools and API docs offer a 'copy as curl'.";
            return;
        }

        RequestPane.Document.Insert(RequestPane.CaretOffset, result.Http);
        RequestPane.CaretOffset = RequestPane.Document.TextLength;
        RequestPane.Focus();

        StatusLeft.Text = result.Notes.Count == 0
            ? "Converted a curl command."
            : $"Converted a curl command - {Plural(result.Notes.Count)} noted in the document.";
    }
}
