using System.Windows;
using System.Windows.Input;
using Sling.App.Editor;
using Sling.Core.Documents;
using Sling.Core.Rendering;

namespace Sling.App;

/// <summary>
/// Diagnostics, in the pane they are about rather than only in the pane opposite it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here computes a diagnostic.</b> They are produced by the parse that already
/// runs on the idle tick to fill the collections rail and name the send target; this puts
/// them where they can be acted on. Before it, a request that would not send announced
/// itself as prose in the response pane - "error  line 14  '@name' needs a name" - and the
/// document it was about carried no sign of it at all, so the shortest route from reading the
/// message to fixing the line was to count down the editor by hand.
/// </para>
/// <para>
/// <b>Both directions, because either alone is half a feature.</b> A mark on the line says
/// which line, and a row that jumps says where from a list of eleven. The mapping between
/// the two is positional - the Nth rendered row is the Nth diagnostic in
/// <see cref="ResponseRenderer.InReadingOrder"/> - which is why the renderer flattens a
/// message to one line and why the window asks it for the order rather than sorting again.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>The marks on the request document, or null before the editor exists.</summary>
    private DiagnosticMarks? _diagnostics;

    /// <summary>
    /// The diagnostics the response pane is listing, in the order it listed them.
    /// </summary>
    /// <remarks>
    /// Empty whenever the pane holds anything else, which is what stops a double-click in a
    /// JSON body being read as a click on a row that is no longer there.
    /// </remarks>
    private IReadOnlyList<ParseDiagnostic> _listedDiagnostics = [];

    /// <summary>Wires the marks and the two ways into them. Called once, from the constructor.</summary>
    private void InitializeDiagnostics()
    {
        _diagnostics = new DiagnosticMarks(RequestPane, Application.Current?.Resources);

        // Preview, so the row is acted on before AvalonEdit's own double-click selects a
        // word under the pointer. Nothing is being selected here - the click means "take me
        // there" - and leaving a word highlighted in a pane the user has just left is noise.
        ResponsePane.PreviewMouseLeftButtonDown += OnResponseClicked;
        ResponsePane.PreviewKeyDown += OnResponseKeyDown;
    }

    private void RemoveDiagnosticHandlers()
    {
        ResponsePane.PreviewMouseLeftButtonDown -= OnResponseClicked;
        ResponsePane.PreviewKeyDown -= OnResponseKeyDown;

        _diagnostics?.Dispose();
        _diagnostics = null;
    }

    /// <summary>Puts the current parse's diagnostics onto the request document.</summary>
    /// <remarks>
    /// Fed from the send target's cached parse rather than from a parse of its own: that one
    /// is refreshed exactly where a re-parse is affordable - a load, and the idle tick after
    /// typing stops - and running a second one here would pay the cost the tick exists to
    /// avoid. The marks are therefore at most one idle interval out of date, which is the
    /// staleness the rail and the send-target label already accept; and unlike those two,
    /// these ride the document's own edits in between, so they stay on their text rather than
    /// on a line number.
    /// </remarks>
    private void RefreshDiagnosticMarks(IReadOnlyList<ParseDiagnostic>? diagnostics) =>
        _diagnostics?.Show(diagnostics);

    /// <summary>
    /// Puts a list of diagnostics in the response pane, and remembers what it listed.
    /// </summary>
    /// <param name="diagnostics">What to show. Rendered in reading order.</param>
    /// <param name="status">The status bar's line, which the hint is appended to.</param>
    /// <remarks>
    /// One method rather than the three call sites each rendering and setting the status,
    /// because the rows are only clickable while <see cref="_listedDiagnostics"/> agrees with
    /// what is in the buffer - and a fourth site that rendered without recording would be a
    /// list of rows that silently did nothing.
    /// </remarks>
    private void ShowDiagnostics(IReadOnlyList<ParseDiagnostic> diagnostics, string status)
    {
        var ordered = ResponseRenderer.InReadingOrder(diagnostics);

        ShowMessage(ResponseRenderer.RenderDiagnostics(diagnostics));

        _listedDiagnostics = ordered;

        StatusLeft.Text = ordered.Count > 0
            ? $"{status}  Double-click a line to go to it."
            : status;

        StatusRight.Text = string.Empty;
    }

    /// <summary>Forgets the listing, for anything that replaces what is in the pane.</summary>
    private void ForgetListedDiagnostics() => _listedDiagnostics = [];

    /// <summary>Goes to the line a clicked row names.</summary>
    private void OnResponseClicked(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.ClickCount != 2 || _listedDiagnostics.Count == 0)
        {
            return;
        }

        var position = ResponsePane.GetPositionFromPoint(e.GetPosition(ResponsePane));

        if (position is { } place && JumpToDiagnostic(place.Line))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// And from the keyboard, for the same rows.
    /// </summary>
    /// <remarks>
    /// The response pane is read-only, so Enter does nothing in it otherwise - and a list you
    /// can arrow through but not open is a list that sends you back to the mouse.
    /// </remarks>
    private void OnResponseKeyDown(object sender, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key != Key.Enter || e.IsRepeat || _listedDiagnostics.Count == 0)
        {
            return;
        }

        if (JumpToDiagnostic(ResponsePane.TextArea.Caret.Line))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Takes the caret to the document line the response pane's row on
    /// <paramref name="renderedLine"/> names.
    /// </summary>
    /// <returns>False when that row is not one of the listed diagnostics.</returns>
    private bool JumpToDiagnostic(int renderedLine)
    {
        if (renderedLine < 1 || renderedLine > _listedDiagnostics.Count)
        {
            return false;
        }

        RevealLine(_listedDiagnostics[renderedLine - 1].Line);
        return true;
    }

    /// <summary>
    /// Puts the caret on a line of the request document and makes sure it can be seen.
    /// </summary>
    /// <remarks>
    /// <b>The widening is not a courtesy.</b> While the pane is showing one request the rest
    /// of the file is collapsed, and scrolling to a collapsed line makes AvalonEdit walk back
    /// through <c>PreviousLine</c> looking for a visible one - which is the crash §23 spent a
    /// blocker on. A diagnostic in another request is exactly the case that reaches it, since
    /// the list in the response pane is the whole document's.
    /// </remarks>
    private void RevealLine(int line)
    {
        if (line <= 0 || line > RequestPane.Document.LineCount)
        {
            return;
        }

        if (IsNarrowed && HidesAnyLineIn(line, line))
        {
            ShowWholeFile();
        }

        GoToLine(line);
    }
}
