using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Sling.App.Collections;
using Sling.App.Editor;
using Sling.Core.Documents;
using Sling.Core.Parsing;

namespace Sling.App;

/// <summary>
/// Narrowing the request pane to one request, and putting the whole file back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The buffer always holds the whole document.</b> Only what is drawn changes: the runs
/// of lines outside the request are collapsed through
/// <see cref="TextView.CollapseLines(DocumentLine, DocumentLine)"/>, the same mechanism
/// folding uses. Nothing else in the window has to know this feature exists - Save writes
/// the file, <c>Run all</c> runs the file, a chain still finds the request it depends on,
/// and the parser still sees every line. Loading a smaller <em>document</em> instead would
/// have meant a second copy of the text with a second set of offsets to keep in step, which
/// is a class of bug this codebase has already paid for once.
/// </para>
/// <para>
/// <b>Leaving the request shows the file again, and that is a safety property rather than a
/// convenience.</b> <c>Ctrl+A</c> selects the whole document, hidden lines included, so
/// typing over that selection would silently destroy requests nobody could see. Every way
/// out of the narrowed region - the caret leaving it, a selection reaching past it, a
/// <c>Ctrl+F</c> match further down the file - puts the file back on screen first, so what
/// is about to be replaced is visible while it happens.
/// </para>
/// <para>
/// <b>Escape is deliberately not bound to this.</b> It belongs to AvalonEdit's find bar and
/// to cancelling a send, and the window resolves keys on its tunnelling pass - so taking it
/// here would take it from the find bar, which is the defect §17 spent a section fixing.
/// The chip in the header and the rail's own <c>All requests</c> row are the two ways back.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// The collapsed runs, so they can be handed back. Empty whenever the whole file shows.
    /// </summary>
    private readonly List<CollapsedLineSection> _collapsed = [];

    /// <summary>
    /// A point inside the request being shown, or null when the whole file is.
    /// </summary>
    /// <remarks>
    /// An anchor rather than a line number, because the number moves: inserting a line
    /// above the request shifts it, and a stored number would then name a different
    /// request. The anchor is what the document itself keeps in step, which is the same
    /// reason the collapsed runs are held as sections rather than as line pairs.
    /// <para>
    /// It is deliberately not the caret. The caret is allowed to sit in the file's
    /// <c>@variables</c>, which a narrowed pane still shows, and resolving the shown request
    /// from there would jump the pane to the first request in the file.
    /// </para>
    /// </remarks>
    private TextAnchor? _focusAnchor;

    /// <summary>True while this file is moving the caret or the collapsed runs itself.</summary>
    private bool _focusUpdating;

    /// <summary>Draws the notice standing in for each hidden run. See its own remarks.</summary>
    private HiddenLineRenderer? _hiddenLines;

    /// <summary>Whether the pane is showing one request rather than the whole file.</summary>
    private bool IsNarrowed => _focusAnchor is not null;

    /// <summary>Wires the renderer and the two events that end a narrowing.</summary>
    /// <remarks>Called once, from the constructor.</remarks>
    private void InitializeRequestFocus()
    {
        // Dimmed against the pane's own text rather than given a colour of its own: the
        // notice is a remark about the document, not part of it, and the rail says the same
        // thing about its placeholder rows with the same number.
        var brush = RequestPane.Foreground?.Clone() ?? Brushes.Gray.Clone();
        brush.Opacity = 0.45;

        _hiddenLines = new HiddenLineRenderer(brush);
        RequestPane.TextArea.TextView.ElementGenerators.Add(_hiddenLines);

        RequestPane.TextArea.Caret.PositionChanged += OnCaretMovedInNarrowedPane;
        RequestPane.TextArea.SelectionChanged += OnSelectionChangedInNarrowedPane;
    }

    /// <summary>Shows only the request whose request line is <paramref name="startLine"/>.</summary>
    /// <remarks>
    /// Takes a line rather than a <see cref="RequestBlock"/> on purpose: the row that was
    /// clicked may have been built from a parse of the file on disk, and by the time this
    /// runs the buffer is what matters. A line survives that; a block from another parse
    /// would not.
    /// </remarks>
    private void ShowOnlyRequest(int startLine)
    {
        if (startLine <= 0 || startLine > RequestPane.Document.LineCount)
        {
            ShowWholeFile();
            return;
        }

        // Same ceiling the rail parses under, and for the same reason: this narrowing is
        // recomputed on the idle tick after every keystroke, so it has to be affordable at
        // the size the document actually is. A file past the ceiling has no rail rows built
        // from the buffer either, so saying so is the whole of the answer.
        if (RequestPane.Document.TextLength > MaxLiveRefreshLength)
        {
            ShowWholeFile();
            StatusLeft.Text = "This file is too large to show one request at a time.";
            return;
        }

        // The previous narrowing goes before the caret moves, and it is a crash rather than
        // a tidiness. The request being switched TO is very often one the last one was
        // hiding, and scrolling to a collapsed line makes AvalonEdit walk back through
        // PreviousLine until it finds a visible one. It is also what stops the rules below
        // reading the caret's arrival as a reason to undo the narrowing being asked for:
        // with nothing collapsed, there is nothing for them to object to.
        Uncollapse();

        // A selection left over from before - Ctrl+A, or a drag - would span the lines about
        // to be hidden, so the next keystroke would replace requests nobody can see. That is
        // the state the rules below exist to prevent, and narrowing must not create it.
        RequestPane.TextArea.ClearSelection();

        _focusAnchor = RequestPane.Document.CreateAnchor(
            RequestPane.Document.GetLineByNumber(startLine).Offset);

        // Survives a deletion so that removing the request being shown leaves an anchor that
        // still answers rather than one that throws on every read. AfterInsertion so typing
        // at the very start of the request keeps the anchor inside it.
        _focusAnchor.SurviveDeletion = true;
        _focusAnchor.MovementType = AnchorMovementType.AfterInsertion;

        // The caret goes in before anything is hidden. A caret left outside the visible runs
        // would be answered by the rule below by undoing the narrowing that had just been
        // asked for.
        GoToLine(startLine);
        RefreshRequestFocus();
    }

    /// <summary>Puts the whole document back on screen.</summary>
    private void ShowWholeFile()
    {
        _focusAnchor = null;

        Uncollapse();
        UpdateFocusChrome(null, 0, 0);
    }

    /// <summary>
    /// Recomputes the narrowing against the buffer as it now is.
    /// </summary>
    /// <remarks>
    /// Called after the idle re-parse, because the request being shown may have grown, moved
    /// or stopped existing since the last one. The collapsed runs themselves ride the
    /// document's own lines in between, so the picture is only ever briefly approximate.
    /// </remarks>
    private void RefreshRequestFocus()
    {
        if (_closed)
        {
            return;
        }

        if (!IsNarrowed)
        {
            // The rows are rebuilt whenever the requests change, which takes the mark with
            // them - so the whole-file case has to put it back too, or the rail stops saying
            // anything about what is on screen after the first keystroke.
            MarkShownRow(null);
            return;
        }

        var document = RequestDocumentParser.Parse(RequestPane.Text);
        var block = document.BlockAtLine(_focusAnchor!.Line);

        if (block is null)
        {
            // Every request in the file has been deleted. There is nothing to show one of.
            ShowWholeFile();
            return;
        }

        var view = RequestView.Of(document, block, RequestPane.Document.LineCount);

        _focusUpdating = true;

        try
        {
            Uncollapse();

            var runs = new List<HiddenRun>(2);

            foreach (var range in view.Hidden())
            {
                var section = RequestPane.TextArea.TextView.CollapseLines(
                    RequestPane.Document.GetLineByNumber(range.First),
                    RequestPane.Document.GetLineByNumber(range.Last));

                _collapsed.Add(section);
                runs.Add(new HiddenRun(section, DescribeHiddenRun(document, range)));
            }

            _hiddenLines!.SetRuns(runs);

            // Collapsing registers with the height tree and nothing else. AvalonEdit's own
            // folding calls this straight afterwards for the same reason: without it the
            // cached visual lines stay exactly as they were and the lines go on being drawn.
            RequestPane.TextArea.TextView.Redraw();

            UpdateFocusChrome(block, IndexOf(document, block), document.Requests.Count);
        }
        finally
        {
            _focusUpdating = false;
        }
    }

    /// <summary>Hands every collapsed run back to the editor.</summary>
    private void Uncollapse()
    {
        if (_collapsed.Count == 0)
        {
            return;
        }

        foreach (var section in _collapsed)
        {
            // A section can already be gone - replacing the document deletes the lines it
            // was anchored to, and the height tree nulls out a section whose ends have both
            // been removed. Uncollapsing one of those is a no-op rather than a throw, so this
            // check is about saying what is meant rather than about avoiding an exception.
            if (section.IsCollapsed)
            {
                section.Uncollapse();
            }
        }

        _collapsed.Clear();
        _hiddenLines!.SetRuns([]);

        // Same reason as the collapse: the height tree knows, and the cached visual lines do
        // not until they are told to go again.
        RequestPane.TextArea.TextView.Redraw();
    }

    /// <summary>What the notice standing in for a hidden run says.</summary>
    /// <remarks>
    /// Named apart from the rail's own <c>Describe</c>, which names a request. Two private
    /// statics on one partial class that both describe something and mean different things
    /// is a collision waiting for whoever reads the second one first.
    /// </remarks>
    /// <remarks>
    /// Requests where there are any, because that is the unit the rail and the chip both
    /// count in. A run of blank lines below the last request holds none, and saying "0
    /// requests" about it would be true and useless, so that one counts lines instead.
    /// </remarks>
    private static string DescribeHiddenRun(RequestDocument document, LineRange range)
    {
        var requests = document.Requests.Count(r => range.Contains(r.StartLine));

        var what = requests > 0
            ? requests.ToString(CultureInfo.CurrentCulture) + (requests == 1 ? " request" : " requests")
            : range.Count.ToString(CultureInfo.CurrentCulture) + (range.Count == 1 ? " line" : " lines");

        return "    ··· " + what + " hidden ···";
    }

    /// <summary>
    /// The chip in the header and the accent mark in the rail, which say the same thing in
    /// two places.
    /// </summary>
    /// <param name="block">The request being shown, or null for the whole file.</param>
    /// <param name="ordinal">Its 1-based position in the document.</param>
    /// <param name="total">How many requests the document holds.</param>
    private void UpdateFocusChrome(RequestBlock? block, int ordinal, int total)
    {
        if (block is null || ordinal <= 0)
        {
            FocusChip.Visibility = Visibility.Collapsed;
            FocusChip.Content = string.Empty;
            FocusChip.ToolTip = null;
        }
        else
        {
            var position = ordinal.ToString(CultureInfo.CurrentCulture);
            var count = total.ToString(CultureInfo.CurrentCulture);

            // Deliberately short. The header's other four controls are Auto-width, so every
            // character here comes out of the file name beside it and, once that is gone, out
            // of the last button - which on a 760 px window was clipped through the middle.
            // What it does belongs in the tooltip; that it is a link is visible from its
            // colour, and the rail's own "All requests" row is the other way back.
            FocusChip.Visibility = Visibility.Visible;
            FocusChip.Content = $"{position} of {count}";
            FocusChip.ToolTip = $"Showing request {position} of {count}. The rest of the file is "
                + "still here and still saves. Click to show all of it.";
        }

        MarkShownRow(block?.StartLine);
    }

    /// <summary>Moves the rail's "this is on screen" mark onto the right row.</summary>
    /// <remarks>
    /// Every row in the tree rather than the open document's, because a document whose rows
    /// were built and then left behind by a switch would otherwise keep a mark that says
    /// something untrue about a pane showing a different file.
    /// </remarks>
    private void MarkShownRow(int? startLine)
    {
        foreach (var item in Descendants(_collections))
        {
            var mine = item.Path is not null && IsOpenDocument(item.Path);

            item.IsShown = mine
                && (startLine is null
                    ? item.Kind == CollectionItemKind.All
                    : item.Kind == CollectionItemKind.Request && item.Line == startLine);
        }
    }

    private void OnShowWholeFileClicked(object sender, RoutedEventArgs e)
    {
        ShowWholeFile();
        RequestPane.Focus();
    }

    /// <summary>Ends the narrowing when the caret leaves what it shows.</summary>
    private void OnCaretMovedInNarrowedPane(object? sender, EventArgs e)
    {
        if (_focusUpdating || _closed || _loadingDocument || !IsNarrowed)
        {
            return;
        }

        var caret = RequestPane.TextArea.Caret.Line;

        if (HidesAnyLineIn(caret, caret))
        {
            ShowWholeFile();
        }
    }

    /// <summary>Ends the narrowing when a selection reaches past what it shows.</summary>
    /// <remarks>
    /// <para>
    /// The caret rule does not cover this on its own: a drag from inside the request
    /// downwards leaves the caret inside it while the selection reaches over hidden
    /// requests. Either way, what is about to be typed over has to be on screen first.
    /// </para>
    /// <para>
    /// The whole span rather than its two ends. A view has two runs, so a selection running
    /// from the file's <c>@variables</c> down into the request has both of its ends visible
    /// and every request between them hidden inside it - which is exactly the selection that
    /// loses work.
    /// </para>
    /// </remarks>
    private void OnSelectionChangedInNarrowedPane(object? sender, EventArgs e)
    {
        if (_focusUpdating || _closed || _loadingDocument || !IsNarrowed)
        {
            return;
        }

        var selection = RequestPane.TextArea.Selection;

        if (selection.IsEmpty)
        {
            return;
        }

        // The surrounding segment rather than the two positions: a drag upwards, and a
        // rectangular selection, both report a start after their end.
        var segment = selection.SurroundingSegment;

        if (segment is null)
        {
            return;
        }

        var first = RequestPane.Document.GetLineByOffset(segment.Offset).LineNumber;
        var last = RequestPane.Document.GetLineByOffset(segment.EndOffset).LineNumber;

        if (HidesAnyLineIn(first, last))
        {
            ShowWholeFile();
        }
    }

    /// <summary>
    /// Whether any line from <paramref name="first"/> to <paramref name="last"/> is one the
    /// pane is currently hiding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read off the editor's own collapsed sections, not off the line numbers the
    /// narrowing was computed from.</b> Those sections hold <c>DocumentLine</c>s, so they
    /// move with every edit; a set of remembered numbers is right until the first keystroke
    /// and then describes a document that no longer exists. The two rules above are the ones
    /// standing between the user and text they cannot see, so being right only between idle
    /// ticks is not good enough for them.
    /// </para>
    /// <para>
    /// It also collapses the two questions into one. Asking whether each end of a span is
    /// visible is the natural way to write the selection rule and is wrong in precisely the
    /// case that matters; asking whether a hidden run <em>intersects</em> the span cannot be.
    /// </para>
    /// </remarks>
    private bool HidesAnyLineIn(int first, int last)
    {
        foreach (var section in _collapsed)
        {
            if (!section.IsCollapsed
                || section.Start is not { IsDeleted: false } start
                || section.End is not { IsDeleted: false } end)
            {
                continue;
            }

            if (start.LineNumber <= last && first <= end.LineNumber)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The request the caret means, which while narrowed is the one on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RequestDocument.BlockAtLine"/> resolves a caret between requests
    /// <em>forward</em>, which for a caret in the file's <c>@variables</c> means the first
    /// request in the document. That is right read against the whole file and wrong read
    /// against what is on screen, where a narrowed pane shows the preamble and exactly one
    /// request, and the next request below the preamble is that one.
    /// </para>
    /// <para>
    /// It is more than a surprise. The auth panel rewrites the block it resolves, so
    /// applying it with the caret up in the <c>@variables</c> edited lines nobody could see -
    /// the hazard the caret and selection rules exist to close, arriving through a door
    /// neither of them watches.
    /// </para>
    /// </remarks>
    private RequestBlock? BlockUnderCaret(RequestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var block = document.BlockAtLine(RequestPane.TextArea.Caret.Line);

        // Only when the answer is a request the pane is hiding. A caret inside the shown
        // request already resolves to it, and an un-narrowed pane hides nothing.
        return block is not null && IsNarrowed && HidesAnyLineIn(block.StartLine, block.StartLine)
            ? document.BlockAtLine(_focusAnchor!.Line) ?? block
            : block;
    }

    private static int IndexOf(RequestDocument document, RequestBlock block)
    {
        for (var i = 0; i < document.Requests.Count; i++)
        {
            if (ReferenceEquals(document.Requests[i], block))
            {
                return i + 1;
            }
        }

        return 0;
    }
}
