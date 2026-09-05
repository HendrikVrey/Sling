using Sling.Core.Documents;
using Sling.Core.Parsing;

namespace Sling.Core.Tests;

/// <summary>
/// Which lines the request pane shows when it is looking at one request.
/// </summary>
/// <remarks>
/// Every case starts from real document text rather than from a hand-built
/// <see cref="RequestBlock"/>: the whole question is where a request begins and ends as a
/// reader sees it, and a block assembled by the test would be the test agreeing with itself
/// about the answer. The line counts are written out rather than derived from the parse, for
/// the same reason - the editor is the thing that knows how many lines it has, and deriving
/// the number from the requests would make the one case that is about the end of the file
/// true by construction.
/// </remarks>
public sealed class RequestViewTests
{
    /// <summary>
    /// A file with a preamble and three requests, one per shape that matters.
    /// </summary>
    /// <remarks>
    /// Line numbers, because every assertion below is about them:
    /// <code>
    /// 1  @base = https://api.example.com
    /// 2  @token = secret
    /// 3  (blank)
    /// 4  ### List users
    /// 5  GET {{base}}/users
    /// 6  (blank)
    /// 7  ### Create a user
    /// 8  # @name create
    /// 9  POST {{base}}/users
    /// 10 Content-Type: application/json
    /// 11 (blank)
    /// 12 {"name": "Ada"}
    /// 13 (blank)
    /// 14 ### Delete a user
    /// 15 DELETE {{base}}/users/1
    /// </code>
    /// </remarks>
    private const string Document =
        """
        @base = https://api.example.com
        @token = secret

        ### List users
        GET {{base}}/users

        ### Create a user
        # @name create
        POST {{base}}/users
        Content-Type: application/json

        {"name": "Ada"}

        ### Delete a user
        DELETE {{base}}/users/1
        """;

    private const int DocumentLines = 15;

    [Fact]
    public void A_request_in_the_middle_shows_the_preamble_and_itself()
    {
        var parsed = RequestDocumentParser.Parse(Document);

        var view = RequestView.Of(parsed, parsed.Requests[1], DocumentLines);

        Assert.Equal(
            [new LineRange(1, 3), new LineRange(7, 13)],
            view.Visible);
    }

    [Fact]
    public void The_separator_line_belongs_to_the_request_below_it()
    {
        var parsed = RequestDocumentParser.Parse(Document);

        // FirstLine starts below the ### because the separator belongs to neither request as
        // far as parsing goes. A reader sees it as the top of the one below, and it carries
        // the title the rail row is named after.
        Assert.Equal(8, parsed.Requests[1].FirstLine);
        Assert.Equal(7, parsed.Requests[1].TitleLine);
        Assert.Equal(7, RequestView.Of(parsed, parsed.Requests[1], DocumentLines).Visible[1].First);
    }

    [Fact]
    public void The_first_request_and_the_preamble_are_one_run()
    {
        var parsed = RequestDocumentParser.Parse(Document);

        var view = RequestView.Of(parsed, parsed.Requests[0], DocumentLines);

        // Adjacent, so reported as one range rather than two touching ones - anything drawing
        // a divider between the runs would otherwise draw one against nothing.
        Assert.Equal([new LineRange(1, 6)], view.Visible);
    }

    [Fact]
    public void The_last_request_runs_to_the_bottom_of_the_file()
    {
        var parsed = RequestDocumentParser.Parse(Document);

        // Three lines past the last request's own EndLine, which is what a file saved with a
        // trailing newline and an editor that counts the empty line after it looks like.
        // Hiding those would put "3 lines hidden" under the last request, about nothing.
        var view = RequestView.Of(parsed, parsed.Requests[2], DocumentLines + 3);

        Assert.Equal([new LineRange(1, 3), new LineRange(14, 18)], view.Visible);
        Assert.Equal([new LineRange(4, 13)], view.Hidden());
    }

    [Fact]
    public void A_request_that_is_not_the_last_one_stops_where_it_ends()
    {
        var parsed = RequestDocumentParser.Parse(Document);

        var view = RequestView.Of(parsed, parsed.Requests[1], DocumentLines + 3);

        Assert.Equal([new LineRange(1, 3), new LineRange(7, 13)], view.Visible);
    }

    [Fact]
    public void A_file_with_no_preamble_still_keeps_its_first_line()
    {
        var parsed = RequestDocumentParser.Parse(
            """
            ### One
            GET https://api.example.com/one

            ### Two
            GET https://api.example.com/two
            """);

        // Line 1 rather than the request alone, and it is a rendering requirement: a hidden
        // run reaching the top of the document has no visible line above it, and an editor
        // asked to show a collapsed line walks back through the line before it until it
        // finds one. Off the top it finds null, on an ordinary press of Up.
        var view = RequestView.Of(parsed, parsed.Requests[1], 5);

        Assert.Equal([new LineRange(1, 1), new LineRange(4, 5)], view.Visible);
        Assert.Equal([new LineRange(2, 3)], view.Hidden());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void No_hidden_run_ever_starts_at_line_one(int index)
    {
        // The invariant behind the case above, asserted for every request in a file that has
        // no preamble at all - which is the shape that reaches the top of the document.
        var parsed = RequestDocumentParser.Parse(
            """
            ### One
            GET https://api.example.com/one

            ### Two
            GET https://api.example.com/two

            ### Three
            GET https://api.example.com/three
            """);

        var view = RequestView.Of(parsed, parsed.Requests[index], 8);

        Assert.All(view.Hidden(), range => Assert.True(range.First > 1, $"hidden run {range} reaches line 1"));
    }

    [Fact]
    public void A_leading_request_with_no_separator_keeps_the_variables_above_it()
    {
        // The one request in any document that can have no ### above it is the first, and for
        // it the parser's FirstLine is always 1 - so reading FirstLine as the display start
        // said "there is no preamble" about every file of this shape and hid the @variables
        // the requests below resolve against.
        var parsed = RequestDocumentParser.Parse(
            """
            @base = https://api.example.com
            @token = secret

            GET {{base}}/health

            ### Create a user
            POST {{base}}/users
            """);

        Assert.Equal(0, parsed.Requests[0].TitleLine);
        Assert.Equal(1, parsed.Requests[0].FirstLine);

        var view = RequestView.Of(parsed, parsed.Requests[1], 7);

        Assert.Equal([new LineRange(1, 3), new LineRange(6, 7)], view.Visible);
        Assert.Equal([new LineRange(4, 5)], view.Hidden());
    }

    [Fact]
    public void A_document_with_no_separators_at_all_shows_the_whole_thing()
    {
        var parsed = RequestDocumentParser.Parse("GET https://api.example.com/one");

        var view = RequestView.Of(parsed, parsed.Requests[0], 1);

        Assert.Equal([new LineRange(1, 1)], view.Visible);
        Assert.Equal(0, parsed.Requests[0].TitleLine);
    }

    [Fact]
    public void Hidden_is_the_complement_of_visible()
    {
        var parsed = RequestDocumentParser.Parse(Document);

        var hidden = RequestView.Of(parsed, parsed.Requests[1], DocumentLines).Hidden();

        Assert.Equal([new LineRange(4, 6), new LineRange(14, 15)], hidden);
    }

    [Fact]
    public void Nothing_is_hidden_when_the_view_covers_the_file()
    {
        var parsed = RequestDocumentParser.Parse("GET https://api.example.com/one");

        Assert.Empty(RequestView.Of(parsed, parsed.Requests[0], 1).Hidden());
    }

    [Fact]
    public void Hidden_never_names_a_line_past_the_end()
    {
        var parsed = RequestDocumentParser.Parse(Document);

        // A stale count - the buffer shrank between the parse and the collapse - must not
        // produce a range the caller then turns into a document line that does not exist.
        // The LAST request, because that is the only one whose own run reaches past a short
        // count: asking this of a middle request clips nothing and passes with the clamp
        // deleted, which is a test that agrees rather than one that checks.
        Assert.Equal([new LineRange(4, 8)], RequestView.Of(parsed, parsed.Requests[2], 8).Hidden());
        Assert.Equal([new LineRange(4, 5)], RequestView.Of(parsed, parsed.Requests[2], 5).Hidden());
    }

    [Fact]
    public void Consecutive_separators_leave_the_last_one_titling_the_request()
    {
        var parsed = RequestDocumentParser.Parse(
            """
            ###
            ### The real title
            GET https://api.example.com/one
            """);

        var request = Assert.Single(parsed.Requests);

        Assert.Equal("The real title", request.Title);
        Assert.Equal(2, request.TitleLine);
        Assert.Equal([new LineRange(1, 3)], RequestView.Of(parsed, request, 3).Visible);
    }

    [Fact]
    public void Every_request_carries_its_own_separator_and_not_the_one_above_it()
    {
        var parsed = RequestDocumentParser.Parse(Document);

        // Each request takes the separator directly above it and no other. This does NOT
        // exercise the parser's clearing of the pending title: two requests cannot be built
        // without a separator between them, because the body reader scans to the next one, so
        // that reset is defensive and unobservable. What this pins is the mapping, which is
        // what every ordinal and every visible run is computed from.
        Assert.Equal([4, 7, 14], parsed.Requests.Select(r => r.TitleLine));
    }
}
