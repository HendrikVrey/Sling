using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Sling.Core.Documents;
using Sling.Core.Variables;

namespace Sling.Http.Tests;

/// <summary>
/// What a send reports back, and the switches the handler is built with.
/// </summary>
public sealed class RequestSenderTests
{
    [Fact]
    public void The_handler_leaves_tls_validation_alone()
    {
        using var handler = RequestSender.CreateHandler();

        // Sling.md §5.3 allows a TLS bypass only per request and only with loud
        // indication, so there is deliberately no code that could weaken validation
        // globally. Asserting the absence is the only way to notice if some future
        // convenience adds one.
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(handler.SslOptions.ClientCertificates);
    }

    [Fact]
    public void The_handler_follows_no_redirects_and_keeps_no_cookies()
    {
        using var handler = RequestSender.CreateHandler();

        // Redirects are followed by hand so credentials can be stripped on a cross-origin
        // hop; the handler doing it would remove that opportunity entirely.
        Assert.False(handler.AllowAutoRedirect);

        // An implicit process-wide jar is the mechanism that would carry a staging cookie
        // to production. The real jar arrives in M3, scoped per environment.
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task Status_headers_body_and_size_all_come_back()
    {
        var handler = new StubHandler((_, _) => StubHandler.Ok("""{"ok":true}"""));

        var response = await SendAsync(handler, Get("https://api.example.com/thing"));

        Assert.Equal(200, response.StatusCode);
        Assert.True(response.IsSuccess);
        Assert.Equal("""{"ok":true}""", response.Body);
        Assert.Equal(11, response.BodyByteCount);
        Assert.False(response.BodyTruncated);
        Assert.Contains(response.Headers, h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        Assert.True(response.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task A_body_is_sent_as_utf8_with_the_headers_the_document_asked_for()
    {
        var handler = new StubHandler((_, _) => StubHandler.Ok("{}"));

        var request = new ResolvedRequest(
            null,
            "POST",
            new Uri("https://api.example.com/things"),
            [new HeaderField("Content-Type", "application/json", 2), new HeaderField("X-Trace", "abc", 3)],
            """{"name":"ada"}""",
            null);

        await SendAsync(handler, request);

        var sent = handler.Requests[0];
        Assert.Equal("""{"name":"ada"}""", sent.Body);
        Assert.Equal("abc", sent.Header("X-Trace"));
        Assert.Equal("application/json", sent.Header("Content-Type"));
    }

    [Fact]
    public async Task A_content_header_on_a_bodyless_request_is_still_sent()
    {
        // The null-conditional on message.Content swallowed these, so a GET carrying
        // Content-Type sent none — silently, in a method whose comment says the document
        // decides what goes on the wire.
        var handler = new StubHandler((_, _) => StubHandler.Ok("{}"));

        var request = new ResolvedRequest(
            null,
            "GET",
            new Uri("https://api.example.com/thing"),
            [new HeaderField("Content-Type", "application/json", 2), new HeaderField("X-Ok", "1", 3)],
            null,
            null);

        await SendAsync(handler, request);

        Assert.Equal("1", handler.Requests[0].Header("X-Ok"));
        Assert.Equal("application/json", handler.Requests[0].Header("Content-Type"));
    }

    [Fact]
    public async Task A_body_larger_than_the_cap_is_kept_as_a_prefix_and_flagged()
    {
        var handler = new StubHandler((_, _) => StubHandler.Ok(new string('x', 4096), "text/plain"));

        var response = await SendAsync(
            handler,
            Get("https://api.example.com/big"),
            new SendOptions { MaxBodyBytes = 64 });

        Assert.True(response.BodyTruncated);
        Assert.Equal(64, response.BodyByteCount);
        Assert.Equal(64, response.Body.Length);
    }

    [Fact]
    public async Task A_body_that_exactly_fills_the_cap_is_not_called_truncated()
    {
        var handler = new StubHandler((_, _) => StubHandler.Ok(new string('x', 64), "text/plain"));

        var response = await SendAsync(
            handler,
            Get("https://api.example.com/exact"),
            new SendOptions { MaxBodyBytes = 64 });

        Assert.False(response.BodyTruncated);
        Assert.Equal(64, response.BodyByteCount);
    }

    [Fact]
    public async Task A_declared_charset_is_honoured()
    {
        var payload = Encoding.Latin1.GetBytes("café");
        var handler = new StubHandler((_, _) =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "iso-8859-1" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var response = await SendAsync(handler, Get("https://api.example.com/text"));

        Assert.Equal("café", response.Body);
    }

    [Fact]
    public async Task An_unknown_charset_falls_back_to_utf8_rather_than_throwing()
    {
        // A body decoded imperfectly is far more useful than an exception where a
        // response should be.
        var handler = new StubHandler((_, _) =>
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello"));
            content.Headers.TryAddWithoutValidation("Content-Type", "text/plain; charset=not-a-charset");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var response = await SendAsync(handler, Get("https://api.example.com/text"));

        Assert.Equal("hello", response.Body);
    }

    [Fact]
    public async Task A_utf8_byte_order_mark_is_not_left_at_the_start_of_the_body()
    {
        // Left in place it is an invisible first character that breaks JSON parsing and
        // every string comparison a chain reference makes.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("""{"a":1}""")).ToArray();

        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });

        var response = await SendAsync(handler, Get("https://api.example.com/bom"));

        Assert.Equal("""{"a":1}""", response.Body);
    }

    private static ResolvedRequest Get(string url) =>
        new(null, "GET", new Uri(url), [], null, null);

    private static async Task<ResponseSnapshot> SendAsync(
        StubHandler handler,
        ResolvedRequest request,
        SendOptions? options = null)
    {
        using var sender = new RequestSender(handler, options);
        return await sender.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
