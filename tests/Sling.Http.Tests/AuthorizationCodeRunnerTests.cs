using System.Net;
using System.Net.Sockets;
using System.Text;
using Sling.Core.Auth;
using Sling.Core.Documents;
using Sling.Core.Parsing;
using Sling.Core.Variables;

namespace Sling.Http.Tests;

/// <summary>
/// The authorization-code flow end to end, with a real loopback listener and a stand-in for
/// the browser.
/// </summary>
/// <remarks>
/// <para>
/// The browser is the only part that cannot be automated, and it is the only part replaced:
/// <see cref="RequestRunner.OpenBrowser"/> is a seam for exactly this, and what stands in
/// makes a real HTTP request to the real redirect address the flow published. So the listener,
/// the path matching, the <c>state</c> check and the code exchange are all the production code.
/// </para>
/// <para>
/// Every test picks its own free port. A fixed one would make two tests running at once fight
/// over it, which is a failure that appears and disappears with the scheduling.
/// </para>
/// </remarks>
public sealed class AuthorizationCodeRunnerTests
{
    private static readonly ResolutionContext Context = new();

    [Fact]
    public async Task A_code_becomes_a_token_and_the_request_goes_out_with_it()
    {
        var port = FreePort();
        using var client = new HttpClient();

        var handler = new StubHandler((request, _) =>
            request.RequestUri!.AbsolutePath == "/token"
                ? StubHandler.Ok("""{"access_token":"abc123","token_type":"Bearer","expires_in":3600}""")
                : StubHandler.Ok("""{"orders":[]}"""));

        var document = RequestDocumentParser.Parse(Document(port));
        using var runner = new RequestRunner(new RequestSender(handler));

        Uri? opened = null;

        runner.OpenBrowser = url =>
        {
            opened = url;
            _ = CallBackAsync(client, url, "the-code");
        };

        var result = await runner.RunAsync(
            document,
            document.Requests[0],
            Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));

        // The browser went to the authorization endpoint with the parameters the flow needs.
        Assert.NotNull(opened);
        Assert.Equal("/authorize", opened.AbsolutePath);
        Assert.Contains("code_challenge_method=S256", opened.Query, StringComparison.Ordinal);

        // Two exchanges: the token request Sling made, labelled as its own, and the API call.
        Assert.Equal(2, result.Exchanges.Count);
        Assert.Equal(ExchangeRole.TokenRequest, result.Exchanges[0].Role);
        Assert.Equal("Bearer abc123", handler.Requests[1].Header("Authorization"));

        // And the exchange carried the verifier, which is the whole of PKCE.
        Assert.Contains("code_verifier=", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("code=the-code", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without this check any code delivered to the loopback address would be accepted,
    /// including one an attacker arranged to have sent there.
    /// </summary>
    [Fact]
    public async Task A_callback_carrying_the_wrong_state_is_refused()
    {
        var port = FreePort();
        using var client = new HttpClient();

        var handler = new StubHandler((_, _) => StubHandler.Ok("{}"));
        var document = RequestDocumentParser.Parse(Document(port));

        using var runner = new RequestRunner(new RequestSender(handler));

        runner.OpenBrowser = url =>
        {
            var forged = new UriBuilder(RedirectFor(port)) { Query = "code=stolen&state=not-the-one" }.Uri;
            _ = client.GetAsync(forged, TestContext.Current.CancellationToken);
        };

        var result = await runner.RunAsync(
            document,
            document.Requests[0],
            Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Errors,
            e => e.Message.Contains("did not belong to this sign-in", StringComparison.Ordinal));

        // Nothing was sent. A code Sling did not ask for must not be redeemed.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_provider_that_refuses_says_so_against_the_auth_line()
    {
        var port = FreePort();
        using var client = new HttpClient();

        var handler = new StubHandler((_, _) => StubHandler.Ok("{}"));
        var document = RequestDocumentParser.Parse(Document(port));

        using var runner = new RequestRunner(new RequestSender(handler));

        runner.OpenBrowser = url =>
        {
            var state = StateOf(url);
            var refused = new UriBuilder(RedirectFor(port))
            {
                Query = $"error=access_denied&state={state}",
            }.Uri;

            _ = client.GetAsync(refused, TestContext.Current.CancellationToken);
        };

        var result = await runner.RunAsync(
            document,
            document.Requests[0],
            Context,
            TestContext.Current.CancellationToken);

        var error = Assert.Single(result.Errors);

        Assert.Contains("access_denied", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, error.Line);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// A browser asking for a favicon in the middle of this is ordinary, and taking it as the
    /// answer would end the wait with nothing.
    /// </summary>
    [Fact]
    public async Task A_request_on_another_path_is_not_taken_for_the_callback()
    {
        var port = FreePort();
        using var client = new HttpClient();

        var handler = new StubHandler((request, _) =>
            request.RequestUri!.AbsolutePath == "/token"
                ? StubHandler.Ok("""{"access_token":"abc123","expires_in":3600}""")
                : StubHandler.Ok("{}"));

        var document = RequestDocumentParser.Parse(Document(port));
        using var runner = new RequestRunner(new RequestSender(handler));

        runner.OpenBrowser = url => _ = KnockThenCallBackAsync(client, url, port);

        var result = await runner.RunAsync(
            document,
            document.Requests[0],
            Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    /// <summary>
    /// The one part of a send that waits on a person, so it answers the same Escape as the
    /// rest of one.
    /// </summary>
    [Fact]
    public async Task Cancelling_stops_the_wait()
    {
        var port = FreePort();
        var handler = new StubHandler((_, _) => StubHandler.Ok("{}"));
        var document = RequestDocumentParser.Parse(Document(port));

        using var runner = new RequestRunner(new RequestSender(handler));
        using var cancellation = new CancellationTokenSource();

        runner.OpenBrowser = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(document, document.Requests[0], Context, cancellation.Token));
    }

    /// <summary>
    /// A tool that pops a consent screen up because a request came back 401 is a tool that
    /// does something startling in response to something ordinary.
    /// </summary>
    [Fact]
    public async Task A_401_does_not_reopen_the_browser_on_its_own()
    {
        var port = FreePort();
        using var client = new HttpClient();

        var handler = new StubHandler((request, _) =>
            request.RequestUri!.AbsolutePath == "/token"
                ? StubHandler.Ok("""{"access_token":"abc123","expires_in":3600}""")
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var document = RequestDocumentParser.Parse(Document(port));
        using var runner = new RequestRunner(new RequestSender(handler));

        var opened = 0;

        runner.OpenBrowser = url =>
        {
            opened++;
            _ = CallBackAsync(client, url, "the-code");
        };

        // First run fills the cache. Second run reuses it and is refused, which is the case
        // the client-credentials flow retries.
        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);

        var result = await runner.RunAsync(
            document,
            document.Requests[0],
            Context,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, opened);
        Assert.DoesNotContain(result.Exchanges, e => e.Role == ExchangeRole.Retry);
        Assert.Contains(result.Notes, n => n.Contains("Signing in again means a browser", StringComparison.Ordinal));
    }

    /// <summary>Stands in for the browser: follows the URL, then comes back with a code.</summary>
    private static async Task CallBackAsync(HttpClient client, Uri authorize, string code)
    {
        var redirect = new Uri(Query(authorize, "redirect_uri"));
        var back = new UriBuilder(redirect) { Query = $"code={code}&state={StateOf(authorize)}" }.Uri;

        await client.GetAsync(back, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The same, with a stray request on another path first.</summary>
    private static async Task KnockThenCallBackAsync(HttpClient client, Uri authorize, int port)
    {
        try
        {
            await client.GetAsync(new Uri($"http://127.0.0.1:{port}/favicon.ico"), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The listener answers it with a 404 and carries on, which is the behaviour under
            // test; a transport failure here is not.
        }

        await CallBackAsync(client, authorize, "the-code").ConfigureAwait(false);
    }

    private static string StateOf(Uri authorize) => Query(authorize, "state");

    private static string Query(Uri url, string name)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&'))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);

            if (equals > 0 && Uri.UnescapeDataString(pair[..equals]) == name)
            {
                return Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }

        return string.Empty;
    }

    private static Uri RedirectFor(int port) =>
        new($"http://127.0.0.1:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}/callback");

    private static string Document(int port) => $"""
        # @auth oauth2-code
        # @authorize-url https://auth.example.com/authorize
        # @token-url https://auth.example.com/token
        # @client-id my-client
        # @redirect-uri {RedirectFor(port).AbsoluteUri}
        GET https://api.example.com/orders
        """;

    /// <summary>
    /// A port nothing is listening on.
    /// </summary>
    /// <remarks>
    /// Bound and released rather than picked from a range, so two tests running at once cannot
    /// choose the same one. There is a window between the release and the listener taking it,
    /// which is the ordinary trade for not having a fixed port these tests would fight over.
    /// </remarks>
    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();

        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();

        return port;
    }
}
