using System.Net;
using Sling.Core.Documents;
using Sling.Core.Parsing;
using Sling.Core.Variables;

namespace Sling.Http.Tests;

/// <summary>
/// What the runner tells the window while it works, which is the whole of what the window
/// can say about a run in progress.
/// </summary>
/// <remarks>
/// Reported before the request goes out rather than after it comes back: a progress line
/// that appears once a response has arrived describes a wait that is already over.
/// </remarks>
public sealed class RunProgressTests
{
    private static readonly ResolutionContext Context = new();

    private const string Granted = """
        # @auth oauth2
        # @token-url https://auth.example.com/token
        # @client-id my-client
        # @client-secret s3cret
        GET https://api.example.com/orders
        """;

    [Fact]
    public async Task Run_all_reports_each_request_in_order_with_its_position()
    {
        var handler = new StubHandler((_, _) => StubHandler.Ok("{}"));
        var reports = new List<RunProgress>();

        var document = RequestDocumentParser.Parse("""
            GET https://api.example.com/one

            ###
            POST https://api.example.com/two

            ###
            DELETE https://api.example.com/three
            """);

        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAllAsync(
            document,
            document.Requests,
            Context,
            TestContext.Current.CancellationToken,
            new SynchronousProgress(reports.Add));

        Assert.Equal(["GET", "POST", "DELETE"], reports.Select(r => r.Request.Method));
        Assert.Equal([1, 2, 3], reports.Select(r => r.Number));
        Assert.All(reports, report => Assert.Equal(3, report.Total));
        Assert.All(reports, report => Assert.Equal(ExchangeRole.Requested, report.Role));
    }

    /// <summary>
    /// A chain sends a request nobody pressed send on, and the window says so. This is the
    /// case the role travels for: it is not the caller's request number two, it is the
    /// dependency of request number one.
    /// </summary>
    [Fact]
    public async Task A_dependency_is_reported_before_the_request_that_needs_it()
    {
        var handler = new StubHandler((_, index) => StubHandler.Ok(
            index == 0 ? """{ "token": "abc" }""" : "{}"));

        var reports = new List<RunProgress>();

        var document = RequestDocumentParser.Parse("""
            # @name login
            POST https://api.example.com/login

            ###
            GET https://api.example.com/me
            Authorization: Bearer {{login.response.body.$.token}}
            """);

        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAsync(
            document,
            document.Requests[1],
            Context,
            TestContext.Current.CancellationToken,
            new SynchronousProgress(reports.Add));

        Assert.Equal(2, reports.Count);

        Assert.Equal("login", reports[0].Request.Name);
        Assert.Equal(ExchangeRole.Dependency, reports[0].Role);
        Assert.Equal(1, reports[0].Number);

        Assert.Equal(ExchangeRole.Requested, reports[1].Role);
        Assert.Equal(2, reports[1].Number);

        // One request was asked for, and two went out. The window shows the role rather
        // than "2 of 1" precisely because this can happen.
        Assert.All(reports, report => Assert.Equal(1, report.Total));
    }

    /// <summary>
    /// The request as the document wrote it. A resolved target can carry a substituted
    /// token in its query string, and a progress line is drawn on screen.
    /// </summary>
    [Fact]
    public async Task The_reported_request_is_the_unresolved_one()
    {
        var handler = new StubHandler((_, _) => StubHandler.Ok("{}"));
        var reports = new List<RunProgress>();

        var document = RequestDocumentParser.Parse("""
            @base = https://api.example.com
            @secret = s3cr3t

            GET {{base}}/me?key={{secret}}
            """);

        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAsync(
            document,
            document.Requests[0],
            Context,
            TestContext.Current.CancellationToken,
            new SynchronousProgress(reports.Add));

        var reported = Assert.Single(reports);

        Assert.Equal("{{base}}/me?key={{secret}}", reported.Request.Target);
        Assert.DoesNotContain("s3cr3t", reported.Request.Target, StringComparison.Ordinal);
    }

    /// <summary>
    /// A token exchange is a network call Sling decided to make, and on a slow identity
    /// provider it is the whole of the wait. Reported, but it does not advance the counter.
    /// </summary>
    [Fact]
    public async Task A_token_exchange_is_reported_without_advancing_the_count()
    {
        var handler = new StubHandler((request, _) => StubHandler.Ok(
            request.RequestUri!.Host == "auth.example.com"
                ? """{"access_token":"abc123","expires_in":3600}"""
                : "{}"));

        var reports = new List<RunProgress>();

        var document = RequestDocumentParser.Parse(Granted);

        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAsync(
            document,
            document.Requests[0],
            Context,
            TestContext.Current.CancellationToken,
            new SynchronousProgress(reports.Add));

        Assert.Equal(
            [ExchangeRole.Requested, ExchangeRole.TokenRequest],
            reports.Select(r => r.Role));

        // Both name the request the user asked for. The token endpoint is another host, and
        // "sending POST https://auth.example.com/token" beside a document that says GET is a
        // line nobody can connect to what they did.
        Assert.All(reports, report => Assert.Equal("GET", report.Request.Method));

        // One request was asked for and one number was handed out.
        Assert.Equal([1, 1], reports.Select(r => r.Number));
    }

    /// <summary>
    /// A cached token is not a network call, so there is nothing to announce.
    /// </summary>
    [Fact]
    public async Task A_cached_token_reports_nothing_extra()
    {
        var handler = new StubHandler((request, _) => StubHandler.Ok(
            request.RequestUri!.Host == "auth.example.com"
                ? """{"access_token":"abc123","expires_in":3600}"""
                : "{}"));

        var document = RequestDocumentParser.Parse(Granted);

        using var runner = new RequestRunner(new RequestSender(handler));

        // The first run fills the cache.
        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);

        var reports = new List<RunProgress>();

        await runner.RunAsync(
            document,
            document.Requests[0],
            Context,
            TestContext.Current.CancellationToken,
            new SynchronousProgress(reports.Add));

        var only = Assert.Single(reports);

        Assert.Equal(ExchangeRole.Requested, only.Role);
    }

    /// <summary>
    /// A 401 on a cached token sends two more requests - a fresh token and the retry - and
    /// both are announced. Without them the window names one request and waits through three.
    /// </summary>
    [Fact]
    public async Task A_retry_after_a_401_reports_the_fresh_token_and_the_second_attempt()
    {
        var answers = new Queue<HttpResponseMessage>(
        [
            StubHandler.Ok("{}"),
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            StubHandler.Ok("""{"orders":[]}"""),
        ]);

        var handler = new StubHandler((request, _) => request.RequestUri!.Host == "auth.example.com"
            ? StubHandler.Ok("""{"access_token":"abc123","expires_in":3600}""")
            : answers.Dequeue());

        var document = RequestDocumentParser.Parse(Granted);

        using var runner = new RequestRunner(new RequestSender(handler));

        // Fills the cache, so the second run's token is one the server then refuses.
        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);

        var reports = new List<RunProgress>();

        await runner.RunAsync(
            document,
            document.Requests[0],
            Context,
            TestContext.Current.CancellationToken,
            new SynchronousProgress(reports.Add));

        Assert.Equal(
            [ExchangeRole.Requested, ExchangeRole.TokenRequest, ExchangeRole.Retry],
            reports.Select(r => r.Role));

        Assert.All(reports, report => Assert.Equal(1, report.Number));
    }

    [Fact]
    public async Task A_run_with_no_sink_still_runs()
    {
        var handler = new StubHandler((_, _) => StubHandler.Ok("{}"));

        var document = RequestDocumentParser.Parse("GET https://api.example.com/one");

        using var runner = new RequestRunner(new RequestSender(handler));

        var result = await runner.RunAsync(
            document,
            document.Requests[0],
            Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    /// <summary>
    /// Calls back on the thread that reported, unlike <see cref="Progress{T}"/> which posts
    /// to a captured context. A test has no dispatcher, and a posted report would arrive
    /// after the assertions.
    /// </summary>
    private sealed class SynchronousProgress(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }
}
