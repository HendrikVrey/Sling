using System.Globalization;
using Sling.Core.Rendering;

namespace Sling.Core.Tests;

/// <summary>
/// Comparing the buffer against the file on disk.
/// </summary>
/// <remarks>
/// The answer to "changed on disk - reload, keep mine or compare?", and the third option is
/// what makes the first two answerable. These assert what the text actually says, because a
/// comparison that is read under time pressure in front of an irreversible choice has to be
/// right about which side is which.
/// </remarks>
public sealed class TextDiffTests
{
    private const string Mine = "the pane";
    private const string Theirs = "the file";

    [Fact]
    public void Two_identical_versions_say_so_and_nothing_else()
    {
        var text = TextDiff.Unified("GET https://api.example.com/things\n", "GET https://api.example.com/things\n", Mine, Theirs);

        Assert.Equal("These are identical.", text);
    }

    [Fact]
    public void A_changed_line_is_shown_removed_then_added()
    {
        var mine = "### orders\nGET https://api.example.com/orders\nAccept: application/json\n";
        var theirs = "### orders\nGET https://staging.example.com/orders\nAccept: application/json\n";

        var text = TextDiff.Unified(mine, theirs, Mine, Theirs);

        Assert.Contains("- GET https://api.example.com/orders", text, StringComparison.Ordinal);
        Assert.Contains("+ GET https://staging.example.com/orders", text, StringComparison.Ordinal);

        // The unchanged neighbours are the point of a hunk: a changed target means nothing
        // until you can see which request it belongs to.
        Assert.Contains("  ### orders", text, StringComparison.Ordinal);
        Assert.Contains("  Accept: application/json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_labels_say_which_side_is_which()
    {
        var text = TextDiff.Unified("a\n", "b\n", Mine, Theirs);

        Assert.Contains("--- the pane", text, StringComparison.Ordinal);
        Assert.Contains("+++ the file", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_count_of_what_moved_comes_before_the_detail()
    {
        var mine = "one\ntwo\nthree\n";
        var theirs = "one\ntwo and a half\nthree\n";

        var text = TextDiff.Unified(mine, theirs, Mine, Theirs);

        Assert.Contains("1 line removed, 1 line added.", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("1 line removed", StringComparison.Ordinal)
                < text.IndexOf("- two", StringComparison.Ordinal),
            "The summary decides whether the detail is worth reading, so it comes first.");
    }

    [Fact]
    public void Only_the_line_endings_differing_is_said_in_words_rather_than_as_every_line()
    {
        // The case a .gitattributes normalising terminators produces on checkout. A
        // line-by-line answer would report all four hundred lines as changed, which is true
        // and useless.
        var mine = "### one\nGET https://api.example.com/a\n";
        var theirs = "### one\r\nGET https://api.example.com/a\r\n";

        var text = TextDiff.Unified(mine, theirs, Mine, Theirs);

        Assert.Contains("only the line endings differ", text, StringComparison.Ordinal);
        Assert.Contains("LF (Unix)", text, StringComparison.Ordinal);
        Assert.Contains("CRLF (Windows)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- ### one", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_added_request_at_the_end_reports_only_the_addition()
    {
        var mine = "### one\nGET https://api.example.com/a\n";
        var theirs = "### one\nGET https://api.example.com/a\n\n### two\nGET https://api.example.com/b\n";

        var text = TextDiff.Unified(mine, theirs, Mine, Theirs);

        Assert.Contains("0 lines removed, 3 lines added.", text, StringComparison.Ordinal);
        Assert.Contains("+ ### two", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n- ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_final_terminator_is_not_a_line_of_its_own()
    {
        // A file ending '}\n' has as many lines as one ending '}'. Reporting a phantom blank
        // line as added is a difference nobody made, and an editor that adds a trailing
        // newline on save would produce it every time. The two versions do differ - the
        // bytes are not the same - so it is described rather than diffed.
        var text = TextDiff.Unified("### one\nGET https://api.example.com/a", "### one\nGET https://api.example.com/a\n", Mine, Theirs);

        Assert.Contains("The only difference is the last one", text, StringComparison.Ordinal);
        Assert.Contains("the file ends with a line break", text, StringComparison.Ordinal);
        Assert.DoesNotContain("+ GET", text, StringComparison.Ordinal);

        // And NOT the sentence about line endings, which here would name the same style
        // twice and tell the reader nothing at all.
        Assert.DoesNotContain("only the line endings differ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_whitespace_only_answers_are_told_apart()
    {
        // The one the reviewer found: both versions reach the same branch, and reporting the
        // second as the first prints "only the line endings differ" followed by "LF (Unix)"
        // twice - which is a sentence that answers nothing, in the one place somebody is
        // deciding whether to throw work away.
        var terminators = TextDiff.Unified("a\nb\n", "a\r\nb\r\n", Mine, Theirs);
        var lastLine = TextDiff.Unified("a\nb", "a\nb\n", Mine, Theirs);

        Assert.Contains("only the line endings differ", terminators, StringComparison.Ordinal);
        Assert.DoesNotContain("The only difference is the last one", terminators, StringComparison.Ordinal);

        Assert.Contains("The only difference is the last one", lastLine, StringComparison.Ordinal);
        Assert.DoesNotContain("only the line endings differ", lastLine, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hunk_says_where_it_is_in_both_documents()
    {
        // After an insertion the two coordinate systems diverge, and a single unlabelled
        // number sends the reader to the wrong line in whichever version they have open.
        // Here the second change is at line 22 of one and line 25 of the other.
        var middle = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"# filler {i}"));

        var mine = $"first\n{middle}\nlast\n";
        var theirs = $"added\nadded\nadded\nfirst\n{middle}\nLAST\n";

        var text = TextDiff.Unified(mine, theirs, Mine, Theirs);

        // The property rather than the arithmetic: three lines were inserted above, so every
        // hunk after them sits three lines further down on the right. Asserting a computed
        // constant would pass just as well against a header that printed one number twice.
        //
        // The last hunk, not the first: at the first the two versions still agree, which is
        // exactly the case a header printing one number twice would also satisfy.
        var headers = text
            .Split('\n')
            .Where(l => l.StartsWith("@@ -", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, headers.Count);

        var parts = headers[^1].Split(' ');

        Assert.Equal(4, parts.Length);
        Assert.StartsWith("-", parts[1], StringComparison.Ordinal);
        Assert.StartsWith("+", parts[2], StringComparison.Ordinal);

        var left = int.Parse(parts[1][1..], CultureInfo.InvariantCulture);
        var right = int.Parse(parts[2][1..], CultureInfo.InvariantCulture);

        Assert.Equal(3, right - left);
    }

    [Fact]
    public void Output_is_capped_and_says_how_much_it_left_out()
    {
        // The result is assigned into an editor buffer on the dispatcher, and two documents
        // with nothing in common put every line inside a hunk. A comparison that stops
        // without saying so reads as one that found nothing more.
        var mine = string.Join('\n', Enumerable.Range(0, 4000).Select(i => $"a{i}"));
        var theirs = string.Join('\n', Enumerable.Range(0, 4000).Select(i => $"b{i}"));

        var text = TextDiff.Unified(mine, theirs, Mine, Theirs);

        Assert.Contains("more change", text, StringComparison.Ordinal);
        Assert.True(
            text.Split('\n').Length < 2_200,
            "the output is bounded, whatever the two documents are");
    }

    [Fact]
    public void Two_very_long_documents_are_summarized_rather_than_compared()
    {
        // The edit script holds an entry per line however the comparison is reached, and
        // this is allowed to be handed two sixteen-megabyte documents.
        var mine = string.Join('\n', Enumerable.Range(0, 60_000).Select(i => $"a{i}"));
        var theirs = string.Join('\n', Enumerable.Range(0, 60_000).Select(i => $"b{i}"));

        var text = TextDiff.Unified(mine, theirs, Mine, Theirs);

        Assert.Contains("too long to compare line by line", text, StringComparison.Ordinal);
        Assert.Contains("60000 lines", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Distant_changes_do_not_print_the_whole_document_between_them()
    {
        var middle = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"# filler {i}"));

        var text = TextDiff.Unified($"first\n{middle}\nlast\n", $"FIRST\n{middle}\nLAST\n", Mine, Theirs);

        Assert.Contains("- first", text, StringComparison.Ordinal);
        Assert.Contains("+ LAST", text, StringComparison.Ordinal);

        // Three lines of context either side of two changes, plus the head and the summary.
        Assert.DoesNotContain("# filler 100", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_documents_with_nothing_in_common_still_answer_within_the_bound()
    {
        // Past the cell budget the two middles are reported whole rather than aligned. It is
        // the input where a line-by-line answer is worth least, and the one where an exact
        // one would cost the most - so what matters is that it terminates and tells the
        // truth about the counts.
        var mine = string.Join('\n', Enumerable.Range(0, 1500).Select(i => $"a{i}"));
        var theirs = string.Join('\n', Enumerable.Range(0, 1500).Select(i => $"b{i}"));

        var text = TextDiff.Unified(mine, theirs, Mine, Theirs);

        Assert.Contains("1500 lines removed, 1500 lines added.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hunk_that_begins_with_an_added_line_is_announced_at_a_real_line_number()
    {
        // An added line has no line number on the left, and 'line 0' is the sort of small
        // wrongness that makes a reader stop believing the rest of the output.
        var text = TextDiff.Unified("one\ntwo\n", "inserted\none\ntwo\n", Mine, Theirs);

        // Both sides always, because a hunk's start is a POSITION in each document rather
        // than a line that exists in both: an added first line has no left-hand line of its
        // own, and it is still somewhere in the left-hand document.
        Assert.Contains("@@ -1 +1 @@", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" 0 ", text, StringComparison.Ordinal);
    }
}
