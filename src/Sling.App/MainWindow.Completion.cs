using ICSharpCode.AvalonEdit.CodeCompletion;
using Sling.App.Editor;
using Sling.Core.Parsing;

namespace Sling.App;

/// <summary>
/// Completion in the request pane.
/// </summary>
/// <remarks>
/// <para>
/// The window itself is AvalonEdit's; what lives here is when to open one and what to tell
/// it, and the fact that only one may be open at a time. Everything it offers is read from
/// the document and the selected environment at the moment it opens, so there is nothing
/// indexed and nothing to keep in step.
/// </para>
/// <para>
/// <c>Ctrl+Space</c> only, and deliberately not on every keystroke. A list that appears
/// while somebody is typing a URL is a list that swallows the next Enter, and this document
/// is mostly URLs.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>The completion window on screen, or null.</summary>
    /// <remarks>
    /// Held so a second <c>Ctrl+Space</c> replaces the first rather than stacking a window on
    /// top of one that still has the keyboard - and so it can be closed when the window is.
    /// </remarks>
    private CompletionWindow? _completion;

    /// <summary>
    /// Offers what could be typed at the caret.
    /// </summary>
    /// <remarks>
    /// Nothing happens when the caret is somewhere with nothing to say - inside a header
    /// value, say. An empty list shown as an empty popup is worse than no popup: it reads as
    /// the feature being broken rather than as there being nothing to offer.
    /// </remarks>
    private void ShowCompletion()
    {
        // Only where it means anything. The response pane is read-only and the chord there
        // would open a list over a body nobody can edit.
        if (!RequestPane.TextArea.IsKeyboardFocusWithin)
        {
            return;
        }

        CloseCompletion();

        var document = RequestDocumentParser.Parse(RequestPane.Text);
        var variables = _environments.Select(_selectedEnvironment).VariableNames;

        if (RequestCompletion.Build(RequestPane.TextArea, document, variables) is not { } window)
        {
            StatusLeft.Text = "Nothing to complete here.";
            return;
        }

        _completion = window;

        // AvalonEdit closes the window itself on most exits; this is what keeps the field
        // from holding one that is already gone.
        window.Closed += OnCompletionClosed;
        window.Show();
    }

    private void OnCompletionClosed(object? sender, EventArgs e)
    {
        if (sender is CompletionWindow window)
        {
            window.Closed -= OnCompletionClosed;
        }

        _completion = null;
    }

    private void CloseCompletion()
    {
        _completion?.Close();
        _completion = null;
    }
}
