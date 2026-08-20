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
    public void A_rendered_exchange_shows_the_request_the_status_the_headers_and_the_body()
    {
        var text = ResponseRenderer.Render(Request("GET", "https://api.example.com/me"), Snapshot());

        Assert.Contains("GET https://api.example.com/me", text, StringComparison.Ordinal);
        Assert.Contains("HTTP/1.1 200 OK", text, StringComparison.Ordinal);
        Assert.Contains("Content-Type: application/json", text, StringComparison.Ordinal);
        Assert.Contains("""{"ok":true}""", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Redirect_hops_are_listed_so_a_moved_endpoint_is_visible()
    {
        var snapshot = Snapshot() with
        {
            RedirectTrail = [new Uri("https://api.example.com/v2/me")],
        };

        Assert.Contains("https://api.example.com/v2/me", ResponseRenderer.Render(Request("GET", "https://api.example.com/me"), snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void A_chain_shows_every_request_that_was_sent()
    {
        var text = ResponseRenderer.RenderChain(
        [
            (Request("POST", "https://api.example.com/auth", "login"), Snapshot()),
            (Request("GET", "https://api.example.com/me"), Snapshot()),
        ]);

        Assert.Contains("# @name login", text, StringComparison.Ordinal);
        Assert.Contains("POST https://api.example.com/auth", text, StringComparison.Ordinal);
        Assert.Contains("GET https://api.example.com/me", text, StringComparison.Ordinal);
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
