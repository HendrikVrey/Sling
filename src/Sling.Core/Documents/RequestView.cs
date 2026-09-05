namespace Sling.Core.Documents;

/// <summary>
/// Which lines of a document a request pane shows when it is looking at one request.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a view, never an edit.</b> The buffer keeps the whole file, so saving writes
/// the whole file, <c>Run all</c> runs the whole file, and a chain still finds the request
/// it depends on. All that changes is which lines are on screen. Anything that produced a
/// smaller <em>document</em> would be a second copy of the text with a second set of
/// offsets to keep in step, which is the shape of bug this codebase has paid for before.
/// </para>
/// <para>
/// <b>The preamble comes with it.</b> A <c>.http</c> file opens with the
/// <c>@base = https://api.example.com</c> definitions every request below resolves
/// against, and hiding those leaves somebody looking at <c>GET {{base}}/users</c> with no
/// way to see what <c>{{base}}</c> is. So the visible part is at most two runs: everything
/// above the first request, and the request itself.
/// </para>
/// </remarks>
/// <param name="Visible">
/// The runs the pane shows, in ascending order, neither overlapping nor touching.
/// </param>
/// <param name="LineCount">
/// How many lines the document has. Carried on the view rather than passed to
/// <see cref="Hidden"/>, so the number that decided where the last run ends is the same
/// number that decides what lies past it.
/// </param>
public sealed record RequestView(IReadOnlyList<LineRange> Visible, int LineCount)
{
    /// <summary>The view of <paramref name="request"/> within <paramref name="document"/>.</summary>
    /// <remarks>
    /// <paramref name="request"/> is expected to have come from <paramref name="document"/>'s
    /// own parse; only the first and last requests are read off the document, to find where
    /// the preamble ends and whether this is the one that runs to the bottom.
    /// </remarks>
    /// <param name="lineCount">
    /// The document's line count, from whatever is holding the text. Taken rather than
    /// derived because the editor's count is the one the caller will collapse against, and
    /// a second opinion about it is a second thing to be wrong.
    /// </param>
    public static RequestView Of(RequestDocument document, RequestBlock request, int lineCount)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);

        var first = DisplayStart(request);

        // The last request runs to the bottom of the file. Its own EndLine stops at the last
        // line that carries anything, so without this a document ending in a newline hides
        // its final empty line and announces "1 line hidden" about a line terminator.
        var isLast = document.Requests.Count > 0 && ReferenceEquals(document.Requests[^1], request);
        var last = Math.Max(isLast ? Math.Max(request.EndLine, lineCount) : request.EndLine, first);

        // Where the preamble stops is where the FIRST request starts being shown, not where
        // the selected one does: with the selected request's own start the "preamble" would
        // swallow every request above it.
        //
        // Never below 1, and that is a rendering requirement rather than a preference. A
        // hidden run reaching line 1 has no visible line above it, and an editor asked to
        // show a collapsed line walks back through PreviousLine until it finds one - off the
        // top of the document, into a null reference, on an ordinary press of Up or
        // Backspace. It is also the only place a "N requests hidden" notice can be drawn.
        // The cost is one line of somebody else's request on screen in a file that opens
        // straight into a '###'.
        var preambleLast = Math.Max(
            DisplayStart(document.Requests.Count > 0 ? document.Requests[0] : request) - 1,
            1);

        // Adjacent runs are merged rather than reported as two, so a caller that draws a
        // divider between them does not draw one against nothing. This is the ordinary case
        // for the first request in a file.
        return preambleLast >= first - 1
            ? new RequestView([new LineRange(1, Math.Max(preambleLast, last))], lineCount)
            : new RequestView([new LineRange(1, preambleLast), new LineRange(first, last)], lineCount);
    }

    /// <summary>The runs this view hides.</summary>
    /// <remarks>
    /// The complement of <see cref="Visible"/>, because an editor is told what to collapse
    /// rather than what to draw. Deriving it here keeps the two answers from disagreeing:
    /// a hidden run computed separately would eventually overlap a visible one, and the
    /// symptom would be a request that cannot be seen at all.
    /// </remarks>
    public IReadOnlyList<LineRange> Hidden()
    {
        var hidden = new List<LineRange>(Visible.Count + 1);
        var next = 1;

        foreach (var range in Visible)
        {
            if (range.First > next)
            {
                hidden.Add(new LineRange(next, Math.Min(range.First - 1, LineCount)));
            }

            next = Math.Max(next, range.Last + 1);
        }

        if (next <= LineCount)
        {
            hidden.Add(new LineRange(next, LineCount));
        }

        return hidden.Where(r => !r.IsEmpty).ToList();
    }

    /// <summary>
    /// The first line of a request as a reader sees it: its <c>###</c> line, if it has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RequestBlock.FirstLine"/> starts <em>below</em> the separator, because the
    /// separator both ends the request above and titles the one below and the parser hands
    /// it to neither. For a view it belongs to the request below: it carries the title the
    /// rail row is named after, and a request shown without it looks like a fragment.
    /// </para>
    /// <para>
    /// <b>The fallback is <see cref="RequestBlock.StartLine"/> and not
    /// <see cref="RequestBlock.FirstLine"/>, which is a bug rather than a taste.</b> Only one
    /// request in a document can have no separator above it - the first, and only when the
    /// file opens straight into a request - and for that one <c>FirstLine</c> is always 1,
    /// because the parser's segment starts there. Reading it as the display start therefore
    /// said "the preamble is empty" about every such file, and hid the <c>@variables</c> the
    /// requests below resolve against. That is the one thing this whole view exists to keep
    /// on screen.
    /// </para>
    /// </remarks>
    private static int DisplayStart(RequestBlock request) =>
        request.TitleLine > 0 ? request.TitleLine : request.StartLine;
}
