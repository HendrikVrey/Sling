using System.Net;
using Sling.Core.Cookies;
using Sling.Core.Documents;
using Sling.Core.Variables;

namespace Sling.Http.Tests;

/// <summary>
/// The cookie jar as the sender uses it: stored per hop, replayed only where the rules
/// allow, and never on top of a <c>Cookie</c> header the document wrote.
/// </summary>
public sealed class CookieSendTests
{
    [Fact]
    public async Task A_cookie_set_by_a_response_is_sent_on_the_next_request()
    {
        var handler = new StubHandler((_, index) => index == 0
            ? WithCookie(StubHandler.Ok("{}"), "sid=abc; Path=/")
            : StubHandler.Ok("{}"));

        var jar = new CookieJar();
        using var sender = new RequestSender(handler);

        await sender.SendAsync(Get("https://api.example.com/login"), jar, TestContext.Current.CancellationToken);
        await sender.SendAsync(Get("https://api.example.com/me"), jar, TestContext.Current.CancellationToken);

        Assert.Equal("sid=abc", handler.Requests[1].Header("Cookie"));
    }

    [Fact]
    public async Task A_cookie_is_not_sent_to_another_host()
    {
        var handler = new StubHandler((_, index) => index == 0
            ? WithCookie(StubHandler.Ok("{}"), "sid=abc; Path=/")
            : StubHandler.Ok("{}"));

        var jar = new CookieJar();
        using var sender = new RequestSender(handler);

        await sender.SendAsync(Get("https://api.example.com/login"), jar, TestContext.Current.CancellationToken);
        await sender.SendAsync(Get("https://other.example.org/me"), jar, TestContext.Current.CancellationToken);

        Assert.Null(handler.Requests[1].Header("Cookie"));
    }

    [Fact]
    public async Task A_cookie_header_written_in_the_document_wins_outright()
    {
        // Someone who writes a Cookie header is saying what this request should carry.
        // Appending stored cookies to it would send the session they were overriding.
        var handler = new StubHandler((_, index) => index == 0
            ? WithCookie(StubHandler.Ok("{}"), "sid=stored; Path=/")
            : StubHandler.Ok("{}"));

        var jar = new CookieJar();
        using var sender = new RequestSender(handler);

        await sender.SendAsync(Get("https://api.example.com/login"), jar, TestContext.Current.CancellationToken);

        var explicitCookie = new ResolvedRequest(
            null,
            "GET",
            new Uri("https://api.example.com/me"),
            [new HeaderField("Cookie", "sid=typed", 1)],
            null,
            null);

        await sender.SendAsync(explicitCookie, jar, TestContext.Current.CancellationToken);

        Assert.Equal("sid=typed", handler.Requests[1].Header("Cookie"));
    }

    [Fact]
    public async Task A_cookie_set_on_a_redirect_hop_is_carried_to_the_next_hop()
    {
        // The ordinary shape of a login: the 302 sets the session and the request that
        // follows it needs to carry one. Storing only at the end of the chain misses it.
        var handler = new StubHandler((_, index) => index == 0
            ? WithCookie(
                StubHandler.Redirect(HttpStatusCode.Found, "https://api.example.com/home"),
                "sid=abc; Path=/")
            : StubHandler.Ok("{}"));

        var jar = new CookieJar();
        using var sender = new RequestSender(handler);

        await sender.SendAsync(Get("https://api.example.com/login"), jar, TestContext.Current.CancellationToken);

        Assert.Equal("sid=abc", handler.Requests[1].Header("Cookie"));
    }

    [Fact]
    public async Task A_cookie_header_the_document_wrote_is_stripped_on_a_cross_origin_redirect()
    {
        // The document writes the Cookie header itself, which is what makes this a test of
        // the credential strip. With a *stored* cookie instead, the jar's own domain rule
        // already keeps it off the second request — so that version passes with the strip
        // deleted, and proves nothing about it.
        var handler = new StubHandler((_, index) => index == 0
            ? StubHandler.Redirect(HttpStatusCode.Found, "https://evil.example.org/collect")
            : StubHandler.Ok("{}"));

        using var sender = new RequestSender(handler);

        var request = new ResolvedRequest(
            null,
            "GET",
            new Uri("https://api.example.com/login"),
            [new HeaderField("Cookie", "sid=typed", 1)],
            null,
            null);

        await sender.SendAsync(request, cookies: null, TestContext.Current.CancellationToken);

        Assert.Equal("sid=typed", handler.Requests[0].Header("Cookie"));
        Assert.Null(handler.Requests[1].Header("Cookie"));
    }

    [Fact]
    public async Task A_stored_cookie_is_not_offered_to_a_redirect_target_of_another_origin()
    {
        var handler = new StubHandler((_, index) => index == 0
            ? WithCookie(
                StubHandler.Redirect(HttpStatusCode.Found, "https://evil.example.org/collect"),
                "sid=abc; Path=/")
            : StubHandler.Ok("{}"));

        var jar = new CookieJar();
        using var sender = new RequestSender(handler);

        await sender.SendAsync(Get("https://api.example.com/login"), jar, TestContext.Current.CancellationToken);

        Assert.Null(handler.Requests[1].Header("Cookie"));
    }

    [Fact]
    public async Task A_refused_cookie_comes_back_as_a_note_rather_than_a_failure()
    {
        var handler = new StubHandler((_, _) =>
            WithCookie(StubHandler.Ok("{}"), "sid=abc; Domain=example.org"));

        var jar = new CookieJar();
        using var sender = new RequestSender(handler);

        var outcome = await sender.SendAsync(
            Get("https://api.example.com/login"),
            jar,
            TestContext.Current.CancellationToken);

        // The request worked. A cookie the jar would not store does not change that, which
        // is why the notes are a separate list from the run's errors.
        Assert.Equal(200, outcome.Response.StatusCode);
        Assert.Single(outcome.CookieNotes);
    }

    [Fact]
    public async Task With_no_jar_nothing_is_stored_or_sent()
    {
        var handler = new StubHandler((_, _) => WithCookie(StubHandler.Ok("{}"), "sid=abc; Path=/"));

        using var sender = new RequestSender(handler);

        await sender.SendAsync(Get("https://api.example.com/login"), cookies: null, TestContext.Current.CancellationToken);
        await sender.SendAsync(Get("https://api.example.com/me"), cookies: null, TestContext.Current.CancellationToken);

        Assert.Null(handler.Requests[1].Header("Cookie"));
    }

    [Fact]
    public async Task Two_jars_do_not_share_a_session()
    {
        // Sling.md §5.6 made structural: a staging cookie cannot reach production because
        // the two environments do not share storage.
        var handler = new StubHandler((_, index) => index == 0
            ? WithCookie(StubHandler.Ok("{}"), "sid=staging; Path=/")
            : StubHandler.Ok("{}"));

        using var sender = new RequestSender(handler);

        await sender.SendAsync(Get("https://api.example.com/login"), new CookieJar(), TestContext.Current.CancellationToken);
        await sender.SendAsync(Get("https://api.example.com/me"), new CookieJar(), TestContext.Current.CancellationToken);

        Assert.Null(handler.Requests[1].Header("Cookie"));
    }

    private static HttpResponseMessage WithCookie(HttpResponseMessage response, string setCookie)
    {
        response.Headers.TryAddWithoutValidation("Set-Cookie", setCookie);
        return response;
    }

    private static ResolvedRequest Get(string url) =>
        new(null, "GET", new Uri(url), [], null, null);
}
