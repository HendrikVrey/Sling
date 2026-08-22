using System.Net;
using Sling.Core.Documents;
using Sling.Core.Variables;

namespace Sling.Http.Tests;

/// <summary>
/// <c>Sling.md</c> §5.2 — credential headers must not survive a hop to a different
/// origin — and the method rewriting each redirect status implies.
/// </summary>
/// <remarks>
/// Tested explicitly because this is a real, shipped bug in more than one HTTP client,
/// and because it is invisible: everything appears to work, the request simply arrives
/// somewhere it should not have, carrying the user's token.
/// </remarks>
public sealed class RedirectPolicyTests
{
    private const string Token = "Bearer sup3rs3cret";

    [Theory]
    [InlineData("https://evil.example.com/take-it")]   // different host
    [InlineData("http://api.example.com/take-it")]     // different scheme
    [InlineData("https://api.example.com:8443/take-it")] // different port
    public async Task Credentials_are_dropped_on_a_cross_origin_redirect(string location)
    {
        var handler = new StubHandler((_, index) => index == 0
            ? StubHandler.Redirect(HttpStatusCode.Found, location)
            : StubHandler.Ok("{}"));

        await SendAsync(handler, Request("GET", "https://api.example.com/start", ("Authorization", Token), ("Cookie", "sid=1")));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(Token, handler.Requests[0].Header("Authorization"));
        Assert.Null(handler.Requests[1].Header("Authorization"));
        Assert.Null(handler.Requests[1].Header("Cookie"));
    }

    [Fact]
    public async Task Credentials_survive_a_same_origin_redirect()
    {
        // The mirror case matters as much: stripping on every hop would break every API
        // that redirects a trailing slash, and the user would blame their token.
        var handler = new StubHandler((_, index) => index == 0
            ? StubHandler.Redirect(HttpStatusCode.Found, "https://api.example.com/moved")
            : StubHandler.Ok("{}"));

        await SendAsync(handler, Request("GET", "https://api.example.com/start", ("Authorization", Token)));

        Assert.Equal(Token, handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task A_relative_location_resolves_against_the_current_url_and_keeps_credentials()
    {
        var handler = new StubHandler((_, index) => index == 0
            ? StubHandler.Redirect(HttpStatusCode.Found, "/v2/thing")
            : StubHandler.Ok("{}"));

        var response = await SendAsync(handler, Request("GET", "https://api.example.com/v1/thing", ("Authorization", Token)));

        Assert.Equal("https://api.example.com/v2/thing", handler.Requests[1].Url.ToString());
        Assert.Equal(Token, handler.Requests[1].Header("Authorization"));
        Assert.Equal(new Uri("https://api.example.com/v2/thing"), response.FinalUrl);
        Assert.Single(response.RedirectTrail);
    }

    [Fact]
    public async Task A_redirect_to_a_non_web_scheme_is_not_followed()
    {
        // A redirect is the server choosing Sling's next request. That choice must not be
        // able to turn a network call into something else.
        var handler = new StubHandler((_, _) => StubHandler.Redirect(HttpStatusCode.Found, "file:///C:/Windows/win.ini"));

        var response = await SendAsync(handler, Request("GET", "https://api.example.com/start"));

        Assert.Single(handler.Requests);
        Assert.Equal(302, response.StatusCode);
    }

    [Theory]
    [InlineData(301, "POST", "GET", null)]
    [InlineData(302, "POST", "GET", null)]
    [InlineData(303, "POST", "GET", null)]
    [InlineData(307, "POST", "POST", "{\"a\":1}")]
    [InlineData(308, "POST", "POST", "{\"a\":1}")]
    [InlineData(302, "GET", "GET", null)]
    public async Task The_method_is_rewritten_as_the_status_requires(
        int status,
        string sent,
        string expected,
        string? expectedBody)
    {
        var handler = new StubHandler((_, index) => index == 0
            ? StubHandler.Redirect((HttpStatusCode)status, "https://api.example.com/moved")
            : StubHandler.Ok("{}"));

        // Content-Type only where there is a body to describe. A content header with no
        // body now produces an empty body rather than being dropped, which would make the
        // GET row send "" instead of nothing and has nothing to do with what this asserts.
        var hasBody = sent == "POST";

        var request = new ResolvedRequest(
            null,
            sent,
            new Uri("https://api.example.com/start"),
            hasBody ? [new HeaderField("Content-Type", "application/json", 1)] : [],
            hasBody ? System.Text.Encoding.UTF8.GetBytes("{\"a\":1}") : null,
            null);

        await SendAsync(handler, request);

        Assert.Equal(expected, handler.Requests[1].Method);
        Assert.Equal(expectedBody, handler.Requests[1].Body);
    }

    [Fact]
    public void A_dropped_body_takes_its_content_headers_with_it()
    {
        // Asserted against FollowRedirect directly. Reading it off the second captured
        // request instead was the wrong instrument: it passed on the 302/GET row because
        // a bodyless request was silently dropping its Content-Type anyway, so two
        // separate defects cancelled and the test looked green either way.
        var headers = new List<HeaderField>
        {
            new("Content-Type", "application/json", 1),
            new("X-Trace", "abc", 2),
        };

        var (method, body, rewritten) = RequestSender.FollowRedirect(
            HttpStatusCode.Found,
            "POST",
            [1, 2, 3],
            headers);

        Assert.Equal("GET", method);
        Assert.Null(body);
        Assert.Equal("X-Trace", Assert.Single(rewritten).Name);
    }

    [Fact]
    public void A_safe_method_keeps_its_headers_across_a_redirect()
    {
        var headers = new List<HeaderField> { new("Content-Type", "application/json", 1) };

        var (method, _, rewritten) = RequestSender.FollowRedirect(HttpStatusCode.Found, "GET", null, headers);

        Assert.Equal("GET", method);
        Assert.Single(rewritten);
    }

    [Fact]
    public async Task A_redirect_loop_stops_at_the_budget_and_hands_back_the_3xx()
    {
        var handler = new StubHandler((_, index) =>
            StubHandler.Redirect(HttpStatusCode.Found, "https://api.example.com/hop" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var response = await SendAsync(handler, Request("GET", "https://api.example.com/start"), new SendOptions { MaxRedirects = 3 });

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(302, response.StatusCode);
        Assert.Equal(3, response.RedirectTrail.Count);
    }

    private static ResolvedRequest Request(string method, string url, params (string Name, string Value)[] headers) =>
        new(
            null,
            method,
            new Uri(url),
            headers.Select(h => new HeaderField(h.Name, h.Value, 1)).ToList(),
            null,
            null);

    private static async Task<ResponseSnapshot> SendAsync(
        StubHandler handler,
        ResolvedRequest request,
        SendOptions? options = null)
    {
        using var sender = new RequestSender(handler, options);
        return await sender.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
