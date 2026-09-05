using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Sling.App.Editor;

/// <summary>
/// One run of lines a narrowed request pane is not drawing, and the notice that stands in
/// for it.
/// </summary>
/// <param name="Section">
/// The collapsed run, held as the editor's own section rather than as a pair of line
/// numbers: the section's lines move with every edit, so the notice stays in the right
/// place between one recomputation and the next.
/// </param>
/// <param name="Label">What the notice says. Recomputed whenever the narrowing is.</param>
internal sealed record HiddenRun(CollapsedLineSection Section, string Label);

/// <summary>
/// Draws the notice that stands in for the lines a narrowed request pane is hiding.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not decoration; without it the editor throws.</b> Collapsing lines only tells
/// the height tree not to give them any height. A visual line still ends at its own line's
/// end, so the next line the renderer reaches is the first collapsed one, and building a
/// visual line from a collapsed line is an <see cref="InvalidOperationException"/>. What
/// carries the renderer over the run is an element wide enough to span it, which is exactly
/// what AvalonEdit's own folding does and the reason folding is the only thing in the
/// library that calls <c>CollapseLines</c>.
/// </para>
/// <para>
/// It earns its place twice over, because a pane that silently drops two thirds of a file
/// looks like a shorter file. The only other clue is a jump in the line numbers, which is
/// evidence rather than a message.
/// </para>
/// </remarks>
internal sealed class HiddenLineRenderer : VisualLineElementGenerator
{
    private readonly List<HiddenRun> _runs = [];
    private readonly Brush _brush;

    /// <param name="brush">
    /// The notice's colour. Passed in rather than chosen here, so every colour decision in
    /// the request pane stays in one place.
    /// </param>
    public HiddenLineRenderer(Brush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);

        _brush = brush.IsFrozen || !brush.CanFreeze ? brush : Frozen(brush);
    }

    /// <summary>Replaces the runs being hidden. Empty means the pane shows everything.</summary>
    public void SetRuns(IEnumerable<HiddenRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        _runs.Clear();
        _runs.AddRange(runs);
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var first = -1;

        foreach (var run in _runs)
        {
            if (Bounds(run) is not ({ } start, _) || start < startOffset)
            {
                continue;
            }

            if (first < 0 || start < first)
            {
                first = start;
            }
        }

        return first;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        foreach (var run in _runs)
        {
            if (Bounds(run) is ({ } start, { } end) && start == offset)
            {
                return new Notice(run.Label, end - start, _brush);
            }
        }

        return null;
    }

    /// <summary>
    /// Where the notice starts and ends, in the document as it is right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It begins at the <b>end of the line above</b> the run, because that is the last
    /// offset a visible line reaches: an element starting on the first hidden line would
    /// never be asked for, since the renderer never gets there.
    /// </para>
    /// <para>
    /// It ends at the end of the run's last line, before that line's terminator. An element
    /// that stopped inside a terminator is rejected by name, and one that stopped short of
    /// the line end would leave the following line collapsed and unreachable.
    /// </para>
    /// <para>
    /// Null when the run has nothing above it, which happens when a file opens straight
    /// into a request that is not the first one. There is no visible line to hang a notice
    /// on, and none is needed: the renderer starts below the run of its own accord.
    /// </para>
    /// </remarks>
    private static (int Start, int End)? Bounds(HiddenRun run)
    {
        var section = run.Section;

        if (!section.IsCollapsed)
        {
            return null;
        }

        var first = section.Start;
        var last = section.End;

        if (first is null || last is null || first.IsDeleted || last.IsDeleted)
        {
            return null;
        }

        var above = first.PreviousLine;

        if (above is null)
        {
            return null;
        }

        var start = above.EndOffset;
        var end = last.EndOffset;

        return end > start ? (start, end) : null;
    }

    private static Brush Frozen(Brush brush)
    {
        var copy = brush.Clone();
        copy.Freeze();

        return copy;
    }

    /// <summary>The notice itself, drawn in its own colour rather than the document's.</summary>
    /// <remarks>
    /// The colour is applied in <see cref="CreateTextRun"/> because that is the first moment
    /// the element has any run properties: <c>VisualLine</c> hands every element a fresh copy
    /// of the global ones after construction, so anything set in the constructor would be
    /// overwritten by them.
    /// </remarks>
    private sealed class Notice(string text, int documentLength, Brush brush)
        : FormattedTextElement(text, documentLength)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            TextRunProperties.SetForegroundBrush(brush);

            return base.CreateTextRun(startVisualColumn, context);
        }
    }
}
