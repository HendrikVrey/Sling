using System.Globalization;
using System.Text;

namespace Sling.Core.Rendering;

/// <summary>
/// A line-by-line comparison of two versions of a document, rendered as text.
/// </summary>
/// <remarks>
/// <para>
/// It exists for one question: <c>.http</c> files are git artifacts, so the file in the
/// pane changes underneath it - a pull, a branch switch, the editor in the next window -
/// and being told so is only half an answer. <em>What</em> changed is the half that decides
/// whether to reload or to keep what is in the buffer, and it has to be answerable without
/// leaving Sling to run <c>git diff</c>.
/// </para>
/// <para>
/// Unified output, in the response buffer, because that buffer already searches with
/// <c>Ctrl+F</c>, folds, scrolls and copies. A side-by-side view would be all of that
/// rebuilt worse, in a pane that is 400 px wide.
/// </para>
/// <para>
/// Pure and in <c>Sling.Core</c>, so what it says can be asserted rather than looked at.
/// </para>
/// </remarks>
public static class TextDiff
{
    /// <summary>Lines of unchanged text kept either side of a change.</summary>
    private const int Context = 3;

    /// <summary>
    /// The largest comparison the exact algorithm is run over, in cells.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The matrix is the product of the two sides' lengths <em>after</em> the common
    /// beginning and end have been trimmed, and trimming is what makes the ordinary case
    /// free: a request added to a file of four hundred is a middle of one line against two.
    /// </para>
    /// <para>
    /// Past it the two middles are reported as one removed block and one added block, which
    /// is true, useful and bounded. Two documents with nothing in common is exactly the
    /// input where a line-by-line answer is worth least anyway.
    /// </para>
    /// <para>
    /// <b>The matrix is not what costs, which is worth saying because the number looks like
    /// it is about the matrix.</b> A budget of a quarter of a million cells is a megabyte of
    /// <see cref="int"/>s at worst; the edit script beside it is one entry per line whichever
    /// branch is taken, and that is what <see cref="MaxLines"/> bounds.
    /// </para>
    /// </remarks>
    private const long MaxCells = 250_000;

    /// <summary>
    /// The most lines either side may have before the answer becomes a summary.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxCells"/> bounds the matrix and not the edit script, which holds an entry
    /// per line however the comparison is reached - so two sixteen-megabyte documents, both
    /// of which this is allowed to be handed, would build a million-entry list and render it
    /// into an editor buffer on the dispatcher. A request file is text a person wrote;
    /// fifty thousand lines is far past any of them and is where a line-by-line answer has
    /// stopped being one anybody reads.
    /// </remarks>
    private const int MaxLines = 50_000;

    /// <summary>
    /// The most lines of output written before the rest is counted rather than printed.
    /// </summary>
    /// <remarks>
    /// The bound that matters most, because the result is assigned into an editor buffer.
    /// Two documents with nothing in common produce an edit per line and every one of them
    /// is inside a hunk, so without this the output is the two documents concatenated.
    /// </remarks>
    private const int MaxOutputLines = 2_000;

    /// <summary>
    /// Compares two versions of a document and describes the difference.
    /// </summary>
    /// <param name="mine">The version in hand. Rendered with <c>-</c>.</param>
    /// <param name="theirs">The version being compared against. Rendered with <c>+</c>.</param>
    /// <param name="mineLabel">What <paramref name="mine"/> is, in the user's words.</param>
    /// <param name="theirsLabel">What <paramref name="theirs"/> is.</param>
    public static string Unified(string mine, string theirs, string mineLabel, string theirsLabel)
    {
        ArgumentNullException.ThrowIfNull(mine);
        ArgumentNullException.ThrowIfNull(theirs);

        if (string.Equals(mine, theirs, StringComparison.Ordinal))
        {
            return "These are identical.";
        }

        var left = SplitLines(mine);
        var right = SplitLines(theirs);

        var text = new StringBuilder();

        text.Append("--- ").Append(mineLabel).Append('\n');
        text.Append("+++ ").Append(theirsLabel).Append('\n');

        // Before anything else, because it is the one difference a line-by-line answer
        // renders as every line having changed. A checkout under a .gitattributes that
        // normalises terminators produces exactly this, and "all 412 lines differ" is a
        // useless thing to say about it.
        if (left.SequenceEqual(right, StringComparer.Ordinal))
        {
            text.Append('\n').Append(OnlyTerminatorsDiffer(mine, theirs, mineLabel, theirsLabel));

            return text.ToString();
        }

        // The edit script holds an entry per line whichever branch Compare takes, and this
        // is allowed to be handed two sixteen-megabyte documents.
        if (left.Count > MaxLines || right.Count > MaxLines)
        {
            text
                .Append('\n')
                .Append("These are too long to compare line by line: ")
                .Append(Count(left.Count, "line"))
                .Append(" against ")
                .Append(Count(right.Count, "line"))
                .Append(".\n");

            return text.ToString();
        }

        var edits = Compare(left, right);

        text.Append('\n');
        text.Append(Summarize(edits)).Append('\n');

        WriteHunks(text, edits);

        return text.ToString();
    }

    /// <summary>
    /// What to say when the lines match and the bytes do not.
    /// </summary>
    /// <remarks>
    /// <b>Two cases, and reporting the second as the first says nothing at all.</b> When the
    /// terminator styles differ, naming them is the whole answer. When they do not, what
    /// changed is the <em>final</em> terminator - one side ends its last line and the other
    /// does not - and an editor that adds a trailing newline on save produces exactly that,
    /// on every file it touches. The first version of this printed "only the line endings
    /// differ" followed by the same style twice.
    /// </remarks>
    private static string OnlyTerminatorsDiffer(string mine, string theirs, string mineLabel, string theirsLabel)
    {
        var here = Terminators(mine);
        var there = Terminators(theirs);

        if (!string.Equals(here, there, StringComparison.Ordinal))
        {
            return "The lines are identical; only the line endings differ.\n"
                + "  " + mineLabel + ": " + here + "\n"
                + "  " + theirsLabel + ": " + there + "\n";
        }

        var ends = mine.Length > 0 && mine[^1] is '\n' or '\r' ? mineLabel : theirsLabel;

        return "The lines are identical. The only difference is the last one: "
            + ends + " ends with a line break and the other does not.\n";
    }

    /// <summary>What one line became.</summary>
    private enum EditKind
    {
        /// <summary>Present in both, unchanged.</summary>
        Same,

        /// <summary>In <c>mine</c> and not in <c>theirs</c>.</summary>
        Removed,

        /// <summary>In <c>theirs</c> and not in <c>mine</c>.</summary>
        Added,
    }

    /// <param name="LeftLine">1-based line in <c>mine</c>, or 0 for an added line.</param>
    /// <param name="RightLine">1-based line in <c>theirs</c>, or 0 for a removed line.</param>
    private readonly record struct Edit(EditKind Kind, string Text, int LeftLine, int RightLine);

    /// <summary>
    /// Splits into lines without their terminators, treating CRLF and LF alike.
    /// </summary>
    /// <remarks>
    /// A document's final terminator does not make an extra empty line: a file ending
    /// <c>}\n</c> has as many lines as one ending <c>}</c>, and reporting a phantom blank
    /// line as removed is a difference nobody made.
    /// </remarks>
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;

            lines.Add(text[start..end]);
            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>How a version terminates its lines, said in words.</summary>
    private static string Terminators(string text)
    {
        var crlf = 0;
        var lf = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            if (i > 0 && text[i - 1] == '\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        return (crlf, lf) switch
        {
            (0, 0) => "one line, with no terminator",
            (_, 0) => "CRLF (Windows)",
            (0, _) => "LF (Unix)",
            _ => $"mixed - {Count(crlf, "CRLF line")} and {Count(lf, "LF line")}",
        };
    }

    /// <summary>Walks both sides into an edit script.</summary>
    private static List<Edit> Compare(List<string> left, List<string> right)
    {
        var edits = new List<Edit>(left.Count + right.Count);

        // The common beginning and end come off first. It is what makes the exact algorithm
        // affordable on a real document: an edit to one request in a file of four hundred
        // leaves a middle of a few lines, whatever the file's size.
        var prefix = 0;
        while (prefix < left.Count
            && prefix < right.Count
            && string.Equals(left[prefix], right[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < left.Count - prefix
            && suffix < right.Count - prefix
            && string.Equals(
                left[left.Count - 1 - suffix],
                right[right.Count - 1 - suffix],
                StringComparison.Ordinal))
        {
            suffix++;
        }

        for (var i = 0; i < prefix; i++)
        {
            edits.Add(new Edit(EditKind.Same, left[i], i + 1, i + 1));
        }

        var leftMiddle = left.Count - prefix - suffix;
        var rightMiddle = right.Count - prefix - suffix;

        if ((long)leftMiddle * rightMiddle > MaxCells)
        {
            for (var i = 0; i < leftMiddle; i++)
            {
                edits.Add(new Edit(EditKind.Removed, left[prefix + i], prefix + i + 1, 0));
            }

            for (var i = 0; i < rightMiddle; i++)
            {
                edits.Add(new Edit(EditKind.Added, right[prefix + i], 0, prefix + i + 1));
            }
        }
        else
        {
            WalkMiddle(left, right, prefix, leftMiddle, rightMiddle, edits);
        }

        for (var i = 0; i < suffix; i++)
        {
            var l = left.Count - suffix + i;
            var r = right.Count - suffix + i;

            edits.Add(new Edit(EditKind.Same, left[l], l + 1, r + 1));
        }

        return edits;
    }

    /// <summary>
    /// The longest common subsequence of the two middles, walked back into an edit script.
    /// </summary>
    /// <remarks>
    /// The plain quadratic table rather than Hirschberg or Myers. The trimming above means
    /// the middle is small on every input this is actually run over, and a table that can be
    /// read straight through is worth more here than an algorithm that has to be trusted.
    /// </remarks>
    private static void WalkMiddle(
        List<string> left,
        List<string> right,
        int offset,
        int leftCount,
        int rightCount,
        List<Edit> edits)
    {
        var lengths = new int[leftCount + 1, rightCount + 1];

        for (var i = leftCount - 1; i >= 0; i--)
        {
            for (var j = rightCount - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(left[offset + i], right[offset + j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var x = 0;
        var y = 0;

        while (x < leftCount && y < rightCount)
        {
            if (string.Equals(left[offset + x], right[offset + y], StringComparison.Ordinal))
            {
                edits.Add(new Edit(EditKind.Same, left[offset + x], offset + x + 1, offset + y + 1));
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                edits.Add(new Edit(EditKind.Removed, left[offset + x], offset + x + 1, 0));
                x++;
            }
            else
            {
                edits.Add(new Edit(EditKind.Added, right[offset + y], 0, offset + y + 1));
                y++;
            }
        }

        for (; x < leftCount; x++)
        {
            edits.Add(new Edit(EditKind.Removed, left[offset + x], offset + x + 1, 0));
        }

        for (; y < rightCount; y++)
        {
            edits.Add(new Edit(EditKind.Added, right[offset + y], 0, offset + y + 1));
        }
    }

    /// <summary>The count, first, because it is what decides whether to read the rest.</summary>
    private static string Summarize(List<Edit> edits)
    {
        var removed = edits.Count(e => e.Kind == EditKind.Removed);
        var added = edits.Count(e => e.Kind == EditKind.Added);

        return $"{Count(removed, "line")} removed, {Count(added, "line")} added.";
    }

    /// <summary>Writes each run of changes with a few unchanged lines either side.</summary>
    /// <remarks>
    /// The unchanged lines are the point: a removed <c>Authorization</c> header means nothing
    /// until you can see which request it was removed from. Anything further away than that
    /// is noise, so a document with two changes at either end does not print itself twice.
    /// </remarks>
    private static void WriteHunks(StringBuilder text, List<Edit> edits)
    {
        var keep = new bool[edits.Count];

        for (var i = 0; i < edits.Count; i++)
        {
            if (edits[i].Kind == EditKind.Same)
            {
                continue;
            }

            for (var j = Math.Max(0, i - Context); j <= Math.Min(edits.Count - 1, i + Context); j++)
            {
                keep[j] = true;
            }
        }

        var inHunk = false;
        var written = 0;

        // Where the next line of each document would be, walked alongside the script. A
        // hunk's start is a POSITION in each version rather than a line that exists in both -
        // one beginning with a removed line has no right-hand line of its own, and naming
        // only the side that has one is what made the header say "@@ -1 @@" and leave the
        // reader to work out where that is in the other version.
        var left = 1;
        var right = 1;

        for (var i = 0; i < edits.Count; i++)
        {
            var at = (Left: left, Right: right);

            if (edits[i].Kind != EditKind.Added)
            {
                left++;
            }

            if (edits[i].Kind != EditKind.Removed)
            {
                right++;
            }

            if (!keep[i])
            {
                inHunk = false;
                continue;
            }

            if (written >= MaxOutputLines)
            {
                // Counted rather than dropped, so a comparison that stops does not read as
                // one that finished. The count is of changes, not of printed lines, because
                // that is the number somebody deciding whether to reload actually wants.
                var rest = edits.Skip(i).Count(e => e.Kind != EditKind.Same);

                text.Append('\n').Append("… and ").Append(Count(rest, "more change")).Append(".\n");
                return;
            }

            if (!inHunk)
            {
                inHunk = true;

                text
                    .Append('\n')
                    .Append("@@ -")
                    .Append(at.Left.ToString(CultureInfo.CurrentCulture))
                    .Append(" +")
                    .Append(at.Right.ToString(CultureInfo.CurrentCulture))
                    .Append(" @@\n");

                written++;
            }

            var edit = edits[i];

            text
                .Append(edit.Kind switch
                {
                    EditKind.Removed => '-',
                    EditKind.Added => '+',
                    _ => ' ',
                })
                .Append(' ')
                .Append(edit.Text)
                .Append('\n');

            written++;
        }
    }

    private static string Count(int n, string noun) =>
        $"{n.ToString(CultureInfo.CurrentCulture)} {noun}{(n == 1 ? string.Empty : "s")}";
}
