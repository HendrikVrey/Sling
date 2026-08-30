using Sling.Core.Parsing;
using Sling.Core.Variables;

namespace Sling.Http.Tests;

/// <summary>
/// The client-credentials grant end to end: a token is fetched, attached, cached, and
/// shown as an exchange of its own.
/// </summary>
public sealed class OAuth2RunnerTests
{
    private static readonly ResolutionContext Context = new();

    private const string Document = """
        # @auth oauth2
        # @token-url https://auth.example.com/token
        # @client-id my-client
        # @client-secret s3cret
        # @scope orders.read
        GET https://api.example.com/orders
        """;

    [Fact]
    public async Task A_token_is_fetched_and_attached_before_the_request_goes_out()
    {
        var handler = new StubHandler((_, index) => index == 0
            ? StubHandler.Ok("""{"access_token":"abc123","token_type":"Bearer","expires_in":3600}""")
            : StubHandler.Ok("""{"orders":[]}"""));

        var result = await RunAsync(handler, Document);

        Assert.True(result.Succeeded);

        // Both exchanges are visible. A tool that makes network calls the user did not ask
        // for has to show them, which is the same rule chained dependencies follow.
        Assert.Equal(2, result.Exchanges.Count);
        Assert.Equal("https://auth.example.com/token", result.Exchanges[0].Request.Url.ToString());
        Assert.Equal("Bearer abc123", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task The_token_request_is_the_form_post_rfc_6749_asks_for()
    {
        var handler = new StubHandler((_, index) => index == 0
            ? StubHandler.Ok("""{"access_token":"abc123","expires_in":3600}""")
            : StubHandler.Ok("{}"));

        await RunAsync(handler, Document);

        var token = handler.Requests[0];

        Assert.Equal("POST", token.Method);
        Assert.Equal("grant_type=client_credentials&scope=orders.read", token.Body);
        Assert.StartsWith("Basic ", token.Header("Authorization"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_request_under_the_same_grant_reuses_the_token()
    {
        var handler = new StubHandler((_, _) =>
            StubHandler.Ok("""{"access_token":"abc123","expires_in":3600}"""));

        var document = RequestDocumentParser.Parse(Document);
        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);
        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);

        // Three requests, not four: one token exchange and two API calls. Without the
        // cache every request doubles the traffic and an authorization server that
        // rate-limits issuance starts refusing.
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_token_with_no_stated_lifetime_is_fetched_again()
    {
        var handler = new StubHandler((_, _) => StubHandler.Ok("""{"access_token":"abc123"}"""));

        var document = RequestDocumentParser.Parse(Document);
        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);
        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);

        // Four: inventing a lifetime for a server that did not state one produces a run of
        // 401s partway through a session, from a cache the user cannot see.
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Forgetting_the_session_drops_the_cached_token()
    {
        var handler = new StubHandler((_, _) =>
            StubHandler.Ok("""{"access_token":"abc123","expires_in":3600}"""));

        var document = RequestDocumentParser.Parse(Document);
        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);
        runner.ForgetSession();
        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);

        // A token fetched against staging is a valid-looking bearer token, so switching
        // environment has to drop it along with the stored responses.
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task A_refused_grant_reports_the_status_and_does_not_send_the_request()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"invalid_client"}"""),
        });

        var result = await RunAsync(handler, Document);

        Assert.False(result.Succeeded);

        // One exchange - the token attempt. The request the grant was for must not go out
        // unauthenticated: it would fail at the API with a message about permissions and no
        // mention of the token.
        Assert.Single(result.Exchanges);
        Assert.Single(handler.Requests);

        var error = Assert.Single(result.Errors);
        Assert.Contains("401", error.Message, StringComparison.Ordinal);

        // Against the '@auth' line rather than the request line, because that is where the
        // mistake is.
        Assert.Equal(1, error.Line);
    }

    [Fact]
    public async Task A_token_response_that_is_not_usable_stops_the_request()
    {
        var handler = new StubHandler((_, _) => StubHandler.Ok("""{"nothing":"useful"}"""));

        var result = await RunAsync(handler, Document);

        Assert.False(result.Succeeded);
        Assert.Single(handler.Requests);
        Assert.Contains("no 'access_token'", Assert.Single(result.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_replaces_an_authorization_header_the_document_wrote()
    {
        var handler = new StubHandler((_, index) => index == 0
            ? StubHandler.Ok("""{"access_token":"abc123","expires_in":3600}""")
            : StubHandler.Ok("{}"));

        await RunAsync(handler, """
            # @auth oauth2
            # @token-url https://auth.example.com/token
            # @client-id my-client
            # @client-secret s3cret
            GET https://api.example.com/orders
            Authorization: Bearer stale
            """);

        // One header, not two. Two Authorization headers is a request no server has a
        // defined answer for, and the grant is the more current instruction.
        Assert.Equal("Bearer abc123", handler.Requests[1].Header("Authorization"));
    }

    [Theory]
    [InlineData("""{"access_token":"abc123","expires_in":3600}""")]
    // The un-cacheable one matters more, not less. It is the token Sling fetches most
    // often - once per request - and an earlier version returned from the cache before
    // recording it, so the only kind of token that was never cached was also the only kind
    // redaction had never heard of, and it reached the history file in clear.
    [InlineData("""{"access_token":"abc123"}""")]
    public async Task An_acquired_token_is_offered_for_redaction_whether_or_not_it_was_cached(string tokenResponse)
    {
        var handler = new StubHandler((_, index) => index % 2 == 0
            ? StubHandler.Ok(tokenResponse)
            : StubHandler.Ok("{}"));

        var document = RequestDocumentParser.Parse(Document);
        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);

        // The header-name rule already removes it from an Authorization header. This is
        // what catches it echoed back somewhere no name-based rule is looking.
        Assert.Contains("abc123", runner.AcquiredTokens());
    }

    [Fact]
    public async Task Forgetting_the_session_also_forgets_the_tokens_redaction_knew_about()
    {
        var handler = new StubHandler((_, _) =>
            StubHandler.Ok("""{"access_token":"abc123","expires_in":3600}"""));

        var document = RequestDocumentParser.Parse(Document);
        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);
        runner.ForgetSession();

        Assert.Empty(runner.AcquiredTokens());
    }

    [Fact]
    public async Task The_token_request_refuses_to_be_redirected()
    {
        // The whole reason it refuses: under 'client-auth body' the client secret is the
        // body, 307 and 308 carry a body across an origin change untouched, and the
        // https-only check on '@token-url' covers exactly one hop. Following would also
        // let whoever answered mint the bearer token attached to the real request.
        var handler = new StubHandler((_, _) =>
            StubHandler.Redirect(System.Net.HttpStatusCode.TemporaryRedirect, "https://evil.example.org/token"));

        var result = await RunAsync(handler, """
            # @auth oauth2
            # @token-url https://auth.example.com/token
            # @client-id my-client
            # @client-secret s3cret
            # @client-auth body
            GET https://api.example.com/orders
            """);

        Assert.False(result.Succeeded);

        // One request, to the host the document named, and nothing else went anywhere.
        Assert.Single(handler.Requests);
        Assert.Equal("https://auth.example.com/token", handler.Requests[0].Url.ToString());

        var error = Assert.Single(result.Errors);
        Assert.Contains("does not follow", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ordinary_request_still_follows_redirects()
    {
        // The refusal is a property of the token request, not a policy change.
        var handler = new StubHandler((_, index) => index == 0
            ? StubHandler.Redirect(System.Net.HttpStatusCode.Found, "https://api.example.com/moved")
            : StubHandler.Ok("{}"));

        var result = await RunAsync(handler, "GET https://api.example.com/orders");

        Assert.True(result.Succeeded);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static async Task<RunResult> RunAsync(StubHandler handler, string text)
    {
        var document = RequestDocumentParser.Parse(text);
        using var runner = new RequestRunner(new RequestSender(handler));

        return await runner.RunAsync(
            document,
            document.Requests[0],
            Context,
            TestContext.Current.CancellationToken);
    }
}
