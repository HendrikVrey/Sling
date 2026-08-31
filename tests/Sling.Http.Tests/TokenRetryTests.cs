using System.Net;
using Sling.Core.Documents;
using Sling.Core.Parsing;
using Sling.Core.Variables;

namespace Sling.Http.Tests;

/// <summary>
/// The 401 retry, and the three boundaries that make it acceptable.
/// </summary>
/// <remarks>
/// <para>
/// A token refreshes on expiry and not otherwise, so a token the server stopped honouring
/// early used to cost a restart: notice the 401, guess that the token is the reason, then
/// find something to poke to make Sling fetch another.
/// </para>
/// <para>
/// The objection to retrying is that it hides the signal, and the answer is to show the
/// retry rather than to refuse it. Every test here asserts both halves: that the second
/// attempt happened, and that both attempts are in the picker with the second labelled.
/// </para>
/// </remarks>
public sealed class TokenRetryTests
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
    public async Task A_401_on_a_cached_token_refreshes_it_and_sends_again()
    {
        // First run: token, then a 200 that fills the cache. Second run: the cached token is
        // refused, so the sequence is 401, a fresh token, and the retry.
        var handler = new StubHandler(Refusing(ApiAnswers()));

        var document = RequestDocumentParser.Parse(Granted);
        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);

        var result = await runner.RunAsync(
            document,
            document.Requests[0],
            Context,
            TestContext.Current.CancellationToken);

        // The 401, the second token exchange, and the retry that succeeded.
        Assert.Equal(3, result.Exchanges.Count);
        Assert.Equal(401, result.Exchanges[0].Response.StatusCode);
        Assert.Equal(ExchangeRole.TokenRequest, result.Exchanges[1].Role);
        Assert.Equal(ExchangeRole.Retry, result.Exchanges[2].Role);
        Assert.Equal(200, result.Exchanges[2].Response.StatusCode);

        // Shown rather than hidden: without the note the user sees a success they cannot
        // account for.
        Assert.Contains(result.Notes, n => n.Contains("fetched a new one", StringComparison.Ordinal));
    }

    /// <summary>
    /// The whole point of the retry: the chain reads the answer that worked, not the 401
    /// that came first.
    /// </summary>
    [Fact]
    public async Task The_stored_response_is_the_retry_rather_than_the_401()
    {
        var handler = new StubHandler(Refusing(ApiAnswers()));

        var named = Granted.Replace(
            "GET https://api.example.com/orders",
            "# @name orders\nGET https://api.example.com/orders",
            StringComparison.Ordinal);

        var document = RequestDocumentParser.Parse(named);
        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);

        var result = await runner.RunAsync(
            document,
            document.Requests[0],
            Context,
            TestContext.Current.CancellationToken);

        Assert.Equal(200, result.Exchanges[^1].Response.StatusCode);
    }

    /// <summary>
    /// A token minted seconds ago and refused is one the server is refusing for a reason a
    /// refresh will not fix - a wrong scope, a client that is not entitled. Fetching another
    /// would be one wasted round trip per send, for ever.
    /// </summary>
    [Fact]
    public async Task A_401_on_a_token_that_was_just_fetched_is_not_retried()
    {
        var handler = new StubHandler((request, _) =>
            request.RequestUri!.Host == "auth.example.com"
                ? StubHandler.Ok("""{"access_token":"abc123","expires_in":3600}""")
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await RunOnceAsync(handler, Granted);

        // The token exchange and the one 401. Nothing more.
        Assert.Equal(2, result.Exchanges.Count);
        Assert.DoesNotContain(result.Exchanges, e => e.Role == ExchangeRole.Retry);
    }

    /// <summary>
    /// A bearer token the user typed is theirs, and a 401 on it is news rather than
    /// something to paper over. Sling has nothing to refresh and no business trying.
    /// </summary>
    [Fact]
    public async Task A_401_on_a_token_sling_does_not_own_is_left_alone()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await RunOnceAsync(
            handler,
            """
            GET https://api.example.com/orders
            Authorization: Bearer mine
            """);

        Assert.Single(handler.Requests);
        Assert.Single(result.Exchanges);
        Assert.Equal(401, result.Exchanges[0].Response.StatusCode);
    }

    [Fact]
    public async Task A_token_exchange_and_a_chained_dependency_both_say_they_were_not_asked_for()
    {
        var handler = new StubHandler((request, _) =>
            request.RequestUri!.AbsolutePath.Contains("login", StringComparison.Ordinal)
                ? StubHandler.Ok("""{"id":"42"}""")
                : StubHandler.Ok("{}"));

        var result = await RunOnceAsync(
            handler,
            """
            ### log in
            # @name login
            POST https://api.example.com/login

            ### the one that was asked for
            GET https://api.example.com/orders/{{login.response.body.$.id}}
            """,
            index: 1);

        Assert.Equal(ExchangeRole.Dependency, result.Exchanges[0].Role);
        Assert.Equal(ExchangeRole.Requested, result.Exchanges[1].Role);
    }

    /// <summary>
    /// One retry, never a second. A server answering 401 to everything must not turn one
    /// send into an unbounded run of token fetches.
    /// </summary>
    [Fact]
    public async Task A_retry_that_is_also_refused_is_not_retried_again()
    {
        var handler = new StubHandler((request, _) =>
            request.RequestUri!.Host == "auth.example.com"
                ? StubHandler.Ok("""{"access_token":"abc123","expires_in":3600}""")
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var document = RequestDocumentParser.Parse(Granted);
        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);

        var result = await runner.RunAsync(
            document,
            document.Requests[0],
            Context,
            TestContext.Current.CancellationToken);

        Assert.Single(result.Exchanges, e => e.Role == ExchangeRole.Retry);
    }

    /// <summary>
    /// The answers the API side gives across the two runs: a 200 that fills the cache, then
    /// a 401 that the retry turns into a 200.
    /// </summary>
    /// <remarks>
    /// Built per test rather than shared. A queue on the class is state two tests running at
    /// once would consume from each other, which is a failure that appears and disappears
    /// with the scheduling.
    /// </remarks>
    private static Queue<HttpResponseMessage> ApiAnswers() => new(
    [
        StubHandler.Ok("{}"),
        new HttpResponseMessage(HttpStatusCode.Unauthorized),
        StubHandler.Ok("""{"orders":[]}"""),
    ]);

    /// <summary>An authorization server that always issues, and an API reading from a script.</summary>
    private static Func<HttpRequestMessage, int, HttpResponseMessage> Refusing(
        Queue<HttpResponseMessage> answers) =>
        (request, _) => request.RequestUri!.Host == "auth.example.com"
            ? StubHandler.Ok("""{"access_token":"abc123","expires_in":3600}""")
            : answers.Dequeue();

    private static async Task<RunResult> RunOnceAsync(StubHandler handler, string text, int index = 0)
    {
        var document = RequestDocumentParser.Parse(text);
        using var runner = new RequestRunner(new RequestSender(handler));

        return await runner.RunAsync(
            document,
            document.Requests[index],
            Context,
            TestContext.Current.CancellationToken);
    }
}
