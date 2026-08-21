using Sling.Core.Documents;
using Sling.Core.Rendering;
using Sling.Core.Variables;

namespace Sling.Core.Tests;

/// <summary>
/// What the response pane and the status bar actually say. Asserted here rather than
/// eyeballed in a screenshot, which is the reason rendering is a pure function in
/// <c>Sling.Core</c> instead of code-behind.
/// </summary>
public sealed class ResponseRendererTests
{
    [Fact]
    public void The_summary_carries_status_time_and_size()
    {
        var summary = ResponseRenderer.Summarize(Snapshot());

        Assert.Contains("HTTP/1.1 200 OK", summary, StringComparison.Ordinal);
        Assert.Contains("124 ms", summary, StringComparison.Ordinal);
        Assert.Contains("2 KB", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_body_says_so_in_the_summary()
    {
        var summary = ResponseRenderer.Summarize(Snapshot() with { BodyTruncated = true });

        Assert.Contains("truncated", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_request_line_names_what_was_actually_sent()
    {
        var text = ResponseRenderer.RenderRequestLine(Request("GET", "https://api.example.com/me"), Snapshot());

        Assert.Equal("GET https://api.example.com/me", text);
    }

    [Fact]
    public void Redirect_hops_are_listed_so_a_moved_endpoint_is_visible()
    {
        var snapshot = Snapshot() with
        {
            RedirectTrail = [new Uri("https://api.example.com/v2/me")],
        };

        var text = ResponseRenderer.RenderRequestLine(Request("GET", "https://api.example.com/me"), snapshot);

        Assert.Contains("https://api.example.com/v2/me", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Headers_are_rendered_in_the_order_the_server_sent_them()
    {
        var snapshot = Snapshot() with
        {
            Headers =
            [
                new ResponseHeader("Content-Type", "application/json"),
                new ResponseHeader("X-Request-Id", "abc"),
            ],
        };

        Assert.Equal(
            "Content-Type: application/json\nX-Request-Id: abc",
            ResponseRenderer.RenderHeaders(snapshot));
    }

    /// <summary>
    /// The M2 split, asserted rather than assumed: the buffer holds the body and nothing
    /// else. A status line or a header leaking into it would break highlighting, folding
    /// and every transform at once, and would do it silently.
    /// </summary>
    [Fact]
    public void The_body_buffer_holds_the_body_and_nothing_around_it()
    {
        var body = ResponseRenderer.RenderBody(Snapshot());

        Assert.Equal("""{"ok":true}""", body);
    }

    [Fact]
    public void An_empty_body_says_so_rather_than_leaving_an_empty_buffer()
    {
        var snapshot = Snapshot() with { Body = string.Empty, BodyByteCount = 0 };

        Assert.Equal("(no body)", ResponseRenderer.RenderBody(snapshot));
        Assert.True(ResponseRenderer.IsPlaceholderBody(snapshot));
        Assert.False(ResponseRenderer.IsPlaceholderBody(Snapshot()));
    }

    /// <summary>
    /// A truncation notice must not be appended to the buffer. It was right when the pane
    /// held a transcript and is wrong now: a line of Sling's own prose inside a JSON body
    /// stops it being JSON, so the first thing the user would meet is a format error Sling
    /// caused.
    /// </summary>
    [Fact]
    public void A_truncated_body_carries_no_note_inside_the_buffer()
    {
        var snapshot = Snapshot() with { BodyTruncated = true };

        Assert.Equal("""{"ok":true}""", ResponseRenderer.RenderBody(snapshot));
        Assert.Contains("truncated", ResponseRenderer.Summarize(snapshot), StringComparison.Ordinal);
    }

    /// <summary>
    /// The picker's label. A named request is identified by its name, because that is the
    /// word the document uses and the word every chain reference is written against.
    /// </summary>
    [Fact]
    public void An_exchange_is_described_by_its_name_when_it_has_one()
    {
        Assert.Equal(
            "1.  login  ·  200",
            ResponseRenderer.DescribeExchange(1, Request("POST", "https://api.example.com/auth", "login"), Snapshot()));

        Assert.Equal(
            "2.  GET https://api.example.com/me  ·  200",
            ResponseRenderer.DescribeExchange(2, Request("GET", "https://api.example.com/me"), Snapshot()));
    }

    [Fact]
    public void Diagnostics_lead_with_errors_and_name_their_line()
    {
        var text = ResponseRenderer.RenderDiagnostics(
        [
            ParseDiagnostic.Warning("a warning", 9),
            ParseDiagnostic.Error("an error", 4),
        ]);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("error  line 4", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("warning  line 9", lines[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    public void Sizes_are_reported_at_a_precision_a_person_can_act_on(long bytes, string expected) =>
        Assert.Equal(expected, Humanize.Size(bytes));

    [Theory]
    [InlineData(0.4, "0 ms")]
    [InlineData(124, "124 ms")]
    [InlineData(1500, "1.50 s")]
    public void Durations_switch_to_seconds_above_a_second(double milliseconds, string expected) =>
        Assert.Equal(expected, Humanize.Duration(TimeSpan.FromMilliseconds(milliseconds)));

    private static ResolvedRequest Request(string method, string url, string? name = null) =>
        new(name, method, new Uri(url), [], null, null);

    private static ResponseSnapshot Snapshot() =>
        new(
            200,
            "OK",
            "1.1",
            [new ResponseHeader("Content-Type", "application/json")],
            """{"ok":true}""",
            2048,
            false,
            TimeSpan.FromMilliseconds(124),
            new Uri("https://api.example.com/me"),
            []);
}
