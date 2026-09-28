using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Sling.Core.Documents;

namespace Sling.App.Editor;

/// <summary>
/// One diagnostic, anchored to the text it is about.
/// </summary>
/// <remarks>
/// <b>A segment rather than a line number, and that is the load-bearing choice here.</b>
/// The parse behind these runs on an idle tick, so between two of them the document has been
/// edited and a remembered line number describes a document that no longer exists - which
/// for a mark drawn under text means an underline that has crept onto the wrong line and a
/// tooltip explaining something else. A <see cref="TextSegment"/> in a collection bound to
/// the document is moved by the document itself on every edit, so the mark stays on its text
/// until the next parse replaces it.
/// </remarks>
internal sealed class DiagnosticMark : TextSegment
{
    internal required DiagnosticSeverity Severity { get; init; }

    internal required string Message { get; init; }

    /// <summary>The line it was raised on, kept for the tooltip.</summary>
    internal required int Line { get; init; }
}

/// <summary>
/// Draws a squiggle under every stretch of text a diagnostic was raised on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The diagnostics existed and were unreachable.</b> They were rendered as prose in the
/// <em>other</em> pane, as lines reading "error  line 14  …", so acting on one meant reading
/// a number out of a sentence and counting down the document by hand. The information was
/// never missing; the connection between it and the text it was about was.
/// </para>
/// <para>
/// Under the text rather than over it, at <see cref="KnownLayer.Selection"/>. A mark drawn
/// on top of glyphs makes the thing it is complaining about harder to read.
/// </para>
/// </remarks>
internal sealed class DiagnosticRenderer : IBackgroundRenderer
{
    /// <summary>The wavelength of one squiggle tooth, in device-independent pixels.</summary>
    /// <remarks>
    /// A squiggle whose teeth are wider than a character reads as a border, and one whose
    /// teeth are a pixel across reads as a straight line at any scaling that is not exactly
    /// 100%. Four sits between the two at the pane's own font size.
    /// </remarks>
    private const double Wavelength = 4.0;

    private const double Amplitude = 1.4;

    /// <summary>How far above the bottom of the text's own box the squiggle sits.</summary>
    private const double Drop = 1.0;

    private readonly Dictionary<DiagnosticSeverity, Pen> _pens = [];

    internal DiagnosticRenderer(
        IReadOnlyDictionary<DiagnosticSeverity, Brush> brushes,
        TextDocument document)
    {
        ArgumentNullException.ThrowIfNull(brushes);

        Marks = new TextSegmentCollection<DiagnosticMark>(document);

        foreach (var severity in new[] { DiagnosticSeverity.Error, DiagnosticSeverity.Warning })
        {
            var pen = new Pen(DiagnosticPalette.For(brushes, severity), 1.0);

            // Frozen because this is drawn on every layout pass of a scrolling document,
            // and an unfrozen pen is checked for changes each time it is used.
            pen.Freeze();
            _pens[severity] = pen;
        }
    }

    /// <summary>The marks on screen, moved by the document as it is edited.</summary>
    internal TextSegmentCollection<DiagnosticMark> Marks { get; }

    /// <inheritdoc />
    public KnownLayer Layer => KnownLayer.Selection;

    /// <inheritdoc />
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(textView);
        ArgumentNullException.ThrowIfNull(drawingContext);

        if (Marks.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0)
        {
            return;
        }

        var view = textView.VisualLines;
        var first = view[0].FirstDocumentLine.Offset;
        var last = view[^1].LastDocumentLine.EndOffset;

        foreach (var mark in Marks.FindOverlappingSegments(first, Math.Max(0, last - first)))
        {
            var pen = _pens.TryGetValue(mark.Severity, out var found)
                ? found
                : _pens[DiagnosticSeverity.Error];

            // GetRectsForSegment answers nothing for a line the pane is hiding, which is
            // what makes this correct while one request is being shown: a mark on a
            // collapsed line simply is not drawn.
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, mark))
            {
                DrawSquiggle(drawingContext, pen, rect);
            }
        }
    }

    private static void DrawSquiggle(DrawingContext drawingContext, Pen pen, Rect rect)
    {
        if (rect.Width < Wavelength)
        {
            return;
        }

        var y = rect.Bottom - Drop;
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(rect.Left, y), isFilled: false, isClosed: false);

            var up = true;

            for (var x = rect.Left + (Wavelength / 2); x < rect.Right; x += Wavelength / 2)
            {
                context.LineTo(new Point(x, up ? y - Amplitude : y), isStroked: true, isSmoothJoin: false);
                up = !up;
            }
        }

        geometry.Freeze();

        drawingContext.DrawGeometry(brush: null, pen, geometry);
    }
}

/// <summary>
/// The strip left of the line numbers, carrying a dot on every line with a diagnostic.
/// </summary>
/// <remarks>
/// <para>
/// <b>A squiggle answers "what is wrong with this line" and a margin answers "is anything
/// wrong near here".</b> They are different questions, and a squiggle can only be seen on a
/// line that is already legible, so on a dense document the marks are easiest to find in the
/// one column that carries nothing else.
/// </para>
/// <para>
/// It draws what is on screen, like every other margin in the editor. A map of the whole
/// document scaled onto the scrollbar is a different feature and a better one, and AvalonEdit
/// has no seam for it.
/// </para>
/// </remarks>
internal sealed class DiagnosticMargin : AbstractMargin
{
    private const double Strip = 9.0;

    private const double Radius = 2.5;

    private readonly DiagnosticRenderer _renderer;

    private readonly IReadOnlyDictionary<DiagnosticSeverity, Brush> _brushes;

    internal DiagnosticMargin(DiagnosticRenderer renderer, IReadOnlyDictionary<DiagnosticSeverity, Brush> brushes)
    {
        _renderer = renderer;
        _brushes = brushes;
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(Strip, 0);

    /// <inheritdoc />
    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        // Scrolling does not change the marks, it changes which of them are on screen - and
        // a margin that does not redraw on that is one whose dots stay where the text used
        // to be.
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        }

        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        if (TextView is not { VisualLinesValid: true } view || _renderer.Marks.Count == 0)
        {
            return;
        }

        foreach (var line in view.VisualLines)
        {
            var start = line.FirstDocumentLine.Offset;
            var length = Math.Max(0, line.LastDocumentLine.EndOffset - start);

            var here = _renderer.Marks.FindOverlappingSegments(start, length);

            if (here.Count == 0)
            {
                continue;
            }

            // The worst wins the dot. One column cannot show two answers, and a line that
            // will not send matters more than one that will send with a remark.
            var worst = here.Max(m => m.Severity);

            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextMiddle)
                - view.VerticalOffset;

            drawingContext.DrawEllipse(
                DiagnosticPalette.For(_brushes, worst),
                pen: null,
                new Point(Strip / 2, y),
                Radius,
                Radius);
        }
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();
}

/// <summary>
/// Everything that puts diagnostics onto an editor: the marks, the margin and the tooltip.
/// </summary>
/// <remarks>
/// <para>
/// One object so the three cannot come apart - they are installed together, refreshed
/// together and taken off together, and a window that removed the renderer but left the
/// margin subscribed to the text view would hold the editor alive.
/// </para>
/// <para>
/// The segment collection is bound to the editor's document once and never rebound, which is
/// sound because Sling never swaps that document: <c>SetDocument</c> assigns
/// <c>TextEditor.Text</c>, which writes into the document already there. That is the same
/// property the fold manager depends on.
/// </para>
/// </remarks>
internal sealed class DiagnosticMarks : IDisposable
{
    private readonly TextEditor _editor;

    private readonly DiagnosticRenderer _renderer;

    private readonly DiagnosticMargin _margin;

    private readonly ToolTip _tooltip = new();

    private bool _disposed;

    internal DiagnosticMarks(TextEditor editor, ResourceDictionary? resources)
    {
        ArgumentNullException.ThrowIfNull(editor);

        _editor = editor;

        var brushes = DiagnosticPalette.Build(resources);

        _renderer = new DiagnosticRenderer(brushes, editor.Document);
        _margin = new DiagnosticMargin(_renderer, brushes);

        editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);

        // Ahead of the line numbers, so a dot and the number it belongs to read as one thing
        // rather than the mark being buried against the text.
        editor.TextArea.LeftMargins.Insert(0, _margin);

        editor.TextArea.TextView.MouseHover += OnMouseHover;
        editor.TextArea.TextView.MouseHoverStopped += OnMouseHoverStopped;
        editor.Document.Changed += OnDocumentChanged;
    }

    /// <summary>
    /// Replaces the marks with <paramref name="diagnostics"/>.
    /// </summary>
    /// <remarks>
    /// Whole rather than incremental. The parse behind it produces the complete list every
    /// time, so reconciling would mean inventing a diff to avoid rebuilding a few dozen
    /// segments - and a reconciliation that got it wrong would leave a mark under text
    /// nothing is complaining about, which is the failure that costs the feature its
    /// credibility.
    /// </remarks>
    internal void Show(IReadOnlyList<ParseDiagnostic>? diagnostics)
    {
        if (_disposed)
        {
            return;
        }

        _renderer.Marks.Clear();

        foreach (var diagnostic in diagnostics ?? [])
        {
            if (SegmentFor(diagnostic) is { } mark)
            {
                _renderer.Marks.Add(mark);
            }
        }

        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        _margin.InvalidateVisual();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _editor.TextArea.TextView.MouseHover -= OnMouseHover;
        _editor.TextArea.TextView.MouseHoverStopped -= OnMouseHoverStopped;
        _editor.Document.Changed -= OnDocumentChanged;

        _editor.TextArea.TextView.BackgroundRenderers.Remove(_renderer);
        _editor.TextArea.LeftMargins.Remove(_margin);

        _tooltip.IsOpen = false;
    }

    /// <summary>
    /// The stretch of text one diagnostic is about: its line, without the indentation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The line rather than a column range, because a <see cref="ParseDiagnostic"/> carries
    /// only a line - which is the honest granularity of a format whose smallest unit of
    /// meaning is a line.
    /// </para>
    /// <para>
    /// Leading and trailing whitespace comes off so the squiggle sits under the text rather
    /// than running out into the margin, and a blank line gets no mark at all: a squiggle
    /// under nothing is a mark nobody can connect to anything.
    /// </para>
    /// </remarks>
    private DiagnosticMark? SegmentFor(ParseDiagnostic diagnostic)
    {
        var document = _editor.Document;

        if (diagnostic.Line <= 0 || diagnostic.Line > document.LineCount)
        {
            return null;
        }

        var line = document.GetLineByNumber(diagnostic.Line);
        var text = document.GetText(line);

        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        var end = text.Length;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        if (end == start)
        {
            return null;
        }

        return new DiagnosticMark
        {
            Severity = diagnostic.Severity,
            Message = diagnostic.Message,
            Line = diagnostic.Line,
            StartOffset = line.Offset + start,
            Length = end - start,
        };
    }

    /// <summary>Takes the tooltip down when the text under it moves.</summary>
    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e) => _tooltip.IsOpen = false;

    /// <summary>
    /// Shows what a mark says, on hover.
    /// </summary>
    /// <remarks>
    /// The half that makes a squiggle worth drawing. A line marked as wrong with no way to
    /// find out why is a line that sends the reader to the other pane to look it up - which
    /// is the round trip this whole feature exists to remove.
    /// </remarks>
    private void OnMouseHover(object sender, MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_disposed || _renderer.Marks.Count == 0)
        {
            return;
        }

        var view = _editor.TextArea.TextView;

        if (view.GetPositionFloor(e.GetPosition(view) + view.ScrollOffset) is not { } place)
        {
            return;
        }

        var here = _renderer.Marks.FindSegmentsContaining(_editor.Document.GetOffset(place.Location));

        if (here.Count == 0)
        {
            return;
        }

        _tooltip.PlacementTarget = _editor;
        _tooltip.Content = string.Join(
            '\n',
            here.Select(m => $"{Word(m.Severity)}  line {m.Line.ToString(CultureInfo.CurrentCulture)}  {m.Message}"));

        _tooltip.IsOpen = true;
        e.Handled = true;
    }

    private void OnMouseHoverStopped(object sender, MouseEventArgs e) => _tooltip.IsOpen = false;

    private static string Word(DiagnosticSeverity severity) =>
        severity == DiagnosticSeverity.Error ? "error" : "warning";
}
