using Sling.Core.Parsing;
using Sling.Core.Variables;

namespace Sling.Http.Tests;

/// <summary>
/// Chaining end to end: asking for a request that needs an earlier response sends the
/// earlier request first, once, and shows both.
/// </summary>
public sealed class RequestRunnerTests
{
    /// <summary>
    /// No environment and no importable files: these documents are self-contained, and
    /// the runner replaces the response store on this with its own regardless.
    /// </summary>
    private static readonly ResolutionContext Context = new();

    [Fact]
    public async Task A_dependency_is_sent_first_and_its_value_flows_into_the_request_asked_for()
    {
        var handler = new StubHandler((_, index) => index == 0
            ? StubHandler.Ok("""{"access_token":"s3cret"}""")
            : StubHandler.Ok("""{"login":"ada"}"""));

        var result = await RunLastAsync(
            handler,
            """
            @base = https://api.example.com

            # @name login
            POST {{base}}/auth

            {"user":"ada"}

            ###
            GET {{base}}/me
            Authorization: Bearer {{login.response.body.$.access_token}}
            """);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Exchanges.Count);

        // Both are visible. A tool that makes network calls the user did not explicitly
        // ask for has to show them.
        Assert.Equal("https://api.example.com/auth", result.Exchanges[0].Request.Url.ToString());
        Assert.Equal("https://api.example.com/me", result.Exchanges[1].Request.Url.ToString());
        Assert.Equal("Bearer s3cret", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task A_dependency_already_sent_is_not_sent_again()
    {
        var handler = new StubHandler((_, _) => StubHandler.Ok("""{"access_token":"s3cret"}"""));

        const string Text = """
            # @name login
            POST https://api.example.com/auth

            ###
            GET https://api.example.com/me
            Authorization: Bearer {{login.response.body.$.access_token}}
            """;

        var document = RequestDocumentParser.Parse(Text);
        using var runner = new RequestRunner(new RequestSender(handler));
        await runner.RunAsync(document, document.Requests[1], Context, TestContext.Current.CancellationToken);
        var second = await runner.RunAsync(document, document.Requests[1], Context, TestContext.Current.CancellationToken);

        // The login response is remembered for the session, so the second send is one
        // request, not two.
        Assert.Single(second.Exchanges);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Forgetting_the_stored_responses_re_runs_the_chain()
    {
        var handler = new StubHandler((_, _) => StubHandler.Ok("""{"access_token":"s3cret"}"""));

        const string Text = """
            # @name login
            POST https://api.example.com/auth

            ###
            GET https://api.example.com/me
            Authorization: Bearer {{login.response.body.$.access_token}}
            """;

        var document = RequestDocumentParser.Parse(Text);
        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAsync(document, document.Requests[1], Context, TestContext.Current.CancellationToken);
        runner.ForgetSession();
        var again = await runner.RunAsync(document, document.Requests[1], Context, TestContext.Current.CancellationToken);

        Assert.Equal(2, again.Exchanges.Count);
    }

    [Fact]
    public async Task A_reference_to_a_request_that_does_not_exist_says_how_to_fix_it()
    {
        var handler = new StubHandler((_, _) => StubHandler.Ok("{}"));

        var result = await RunLastAsync(
            handler,
            """
            GET https://api.example.com/me
            Authorization: Bearer {{login.response.body.$.access_token}}
            """);

        Assert.Empty(handler.Requests);

        var error = Assert.Single(result.Errors);
        Assert.Contains("@name login", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chain_that_depends_on_itself_is_reported_rather_than_looping()
    {
        var handler = new StubHandler((_, _) => StubHandler.Ok("{}"));

        var result = await RunLastAsync(
            handler,
            """
            # @name first
            GET https://api.example.com/a
            X-From: {{second.response.body.$.v}}

            ###
            # @name second
            GET https://api.example.com/b
            X-From: {{first.response.body.$.v}}
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains("depends on itself", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_partway_through_a_chain_keeps_the_exchanges_that_did_happen()
    {
        // The successful login is the evidence needed to understand why the next request
        // failed. Throwing it away would hide it.
        var handler = new StubHandler((_, _) => StubHandler.Ok("""{"wrong_field":"s3cret"}"""));

        var result = await RunLastAsync(
            handler,
            """
            # @name login
            POST https://api.example.com/auth

            ###
            GET https://api.example.com/me
            Authorization: Bearer {{login.response.body.$.access_token}}
            """);

        Assert.False(result.Succeeded);
        Assert.Single(result.Exchanges);
        Assert.Contains(result.Errors, e => e.Message.Contains("access_token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_transport_failure_is_reported_against_the_request_line()
    {
        var handler = new StubHandler((_, _) =>
            throw new HttpRequestException("no such host", new InvalidOperationException("DNS said no")));

        var result = await RunLastAsync(handler, "GET https://api.example.com/thing");

        var error = Assert.Single(result.Errors);
        Assert.Equal(1, error.Line);

        // The innermost message is the one that says what actually went wrong; the outer
        // one is always a generic wrapper.
        Assert.Contains("DNS said no", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_url_the_transport_rejects_is_reported_rather_than_lost()
    {
        // The real trigger is a host holding a character that is illegal under IDN — for
        // example U+FF0F FULLWIDTH SOLIDUS, which Uri.TryCreate accepts and the transport
        // then rejects while resolving IdnHost. A stub handler never resolves a host, so
        // the throw is staged here instead: what is under test is that the catch exists,
        // not that HttpClient still raises it. Uncaught, it escaped through
        // fire-and-forget and left the status bar reading "Sending …" for ever.
        var handler = new StubHandler((_, _) =>
            throw new UriFormatException("An invalid Unicode character by IDN standards was specified in the host."));

        var result = await RunLastAsync(handler, "GET https://api.example.com/x");

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task A_connection_reset_while_reading_the_body_is_reported_rather_than_lost()
    {
        // With ResponseHeadersRead this arrives from the body read as an IOException, not
        // from the send as an HttpRequestException — so it missed every catch and
        // disappeared the same way.
        var handler = new StubHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingStream()),
        });

        var result = await RunLastAsync(handler, "GET https://api.example.com/thing");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains("connection", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A body that starts arriving and then does not.</summary>
    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            throw new IOException("connection reset by peer");

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("connection reset by peer");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<RunResult> RunLastAsync(StubHandler handler, string text)
    {
        var document = RequestDocumentParser.Parse(text);

        using var runner = new RequestRunner(new RequestSender(handler));
        return await runner.RunAsync(
            document,
            document.Requests[^1],
            Context,
            TestContext.Current.CancellationToken);
    }
}
