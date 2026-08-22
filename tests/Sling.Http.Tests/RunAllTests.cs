using System.Net;
using Sling.Core.Parsing;
using Sling.Core.Variables;

namespace Sling.Http.Tests;

/// <summary>
/// Running the whole document: one run, in order, and a failure does not stop it.
/// </summary>
public sealed class RunAllTests
{
    private static readonly ResolutionContext Context = new();

    [Fact]
    public async Task Every_request_is_sent_in_source_order()
    {
        var handler = new StubHandler((_, _) => StubHandler.Ok("{}"));

        var result = await RunAllAsync(handler, """
            GET https://api.example.com/one

            ###
            GET https://api.example.com/two

            ###
            GET https://api.example.com/three
            """);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Exchanges.Count);

        Assert.Equal(
            ["https://api.example.com/one", "https://api.example.com/two", "https://api.example.com/three"],
            result.Exchanges.Select(x => x.Request.Url.ToString()));
    }

    [Fact]
    public async Task A_failure_does_not_stop_the_rest_of_the_run()
    {
        // Half a document sent and half not is the worst outcome to be left with, and the
        // reason to press run-all is usually to find out which requests are broken.
        var handler = new StubHandler((_, index) => index == 1
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }
            : StubHandler.Ok("{}"));

        var result = await RunAllAsync(handler, """
            GET https://api.example.com/one

            ###
            GET https://api.example.com/two

            ###
            GET https://api.example.com/three
            """);

        // A 500 is a response, not a transport failure — all three exchanges happened.
        Assert.Equal(3, result.Exchanges.Count);
        Assert.Equal(500, result.Exchanges[1].Response.StatusCode);
    }

    [Fact]
    public async Task A_transport_failure_is_reported_and_the_run_continues()
    {
        var handler = new StubHandler((_, index) => index == 0
            ? throw new HttpRequestException("no such host")
            : StubHandler.Ok("{}"));

        var result = await RunAllAsync(handler, """
            GET https://nowhere.invalid/one

            ###
            GET https://api.example.com/two
            """);

        Assert.Single(result.Exchanges);
        Assert.Single(result.Errors);
        Assert.Equal("https://api.example.com/two", result.Exchanges[0].Request.Url.ToString());
    }

    [Fact]
    public async Task A_chain_dependency_is_not_sent_twice()
    {
        // One run, so the stored responses are shared: reaching 'login' as a dependency of
        // the second request and again as a request in its own right is one send.
        var handler = new StubHandler((_, _) => StubHandler.Ok("""{"access_token":"s3cret"}"""));

        var result = await RunAllAsync(handler, """
            # @name login
            POST https://api.example.com/auth

            ###
            GET https://api.example.com/me
            Authorization: Bearer {{login.response.body.$.access_token}}
            """);

        Assert.Equal(2, result.Exchanges.Count);
        Assert.Equal("Bearer s3cret", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task A_dependency_declared_below_its_dependent_is_still_sent_only_once()
    {
        // The ordering the forward test does not reach, and the one that costs something:
        // 'login' is auto-sent for '/me' and then reached again on its own turn, which
        // without a guard is a duplicated POST against a live API — a second login, and on
        // an identity provider that rotates on issue, a token invalidated the moment after
        // the request that used it.
        var handler = new StubHandler((_, _) => StubHandler.Ok("""{"access_token":"s3cret"}"""));

        var result = await RunAllAsync(handler, """
            GET https://api.example.com/me
            Authorization: Bearer {{login.response.body.$.access_token}}

            ###
            # @name login
            POST https://api.example.com/auth
            """);

        Assert.Equal(2, result.Exchanges.Count);
        Assert.Equal(
            ["https://api.example.com/auth", "https://api.example.com/me"],
            handler.Requests.Select(r => r.Url.ToString()));
    }

    [Fact]
    public async Task A_second_run_sends_everything_again()
    {
        // The skip is scoped to one run. The response store outlives it, and pressing
        // run-all a second time is a fresh instruction rather than a request to do nothing.
        var handler = new StubHandler((_, _) => StubHandler.Ok("""{"access_token":"s3cret"}"""));

        var document = RequestDocumentParser.Parse("""
            # @name login
            POST https://api.example.com/auth
            """);

        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAllAsync(document, document.Requests, Context, TestContext.Current.CancellationToken);
        await runner.RunAllAsync(document, document.Requests, Context, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Cancellation_stops_the_run()
    {
        using var cancellation = new CancellationTokenSource();

        var handler = new StubHandler((_, index) =>
        {
            if (index == 0)
            {
                cancellation.Cancel();
            }

            return StubHandler.Ok("{}");
        });

        var document = RequestDocumentParser.Parse("""
            GET https://api.example.com/one

            ###
            GET https://api.example.com/two
            """);

        using var runner = new RequestRunner(new RequestSender(handler));

        // Cancellation is an instruction rather than a failure, so it propagates out
        // untouched rather than becoming a diagnostic against a line.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAllAsync(document, document.Requests, Context, cancellation.Token));

        Assert.Single(handler.Requests);
    }

    private static async Task<RunResult> RunAllAsync(StubHandler handler, string text)
    {
        var document = RequestDocumentParser.Parse(text);
        using var runner = new RequestRunner(new RequestSender(handler));

        return await runner.RunAllAsync(
            document,
            document.Requests,
            Context,
            TestContext.Current.CancellationToken);
    }
}
