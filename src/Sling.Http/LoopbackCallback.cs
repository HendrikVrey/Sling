using System.Net;
using System.Text;

namespace Sling.Http;

/// <summary>
/// What came back to the loopback address after the browser round trip.
/// </summary>
/// <param name="Code">The authorization code, when the provider issued one.</param>
/// <param name="Error">
/// What went wrong instead, phrased for the user. Never carries anything the provider sent
/// verbatim beyond its own error code, which is a fixed vocabulary.
/// </param>
public sealed record CallbackResult(string? Code, string? Error);

/// <summary>
/// Listens on a loopback address for the redirect that carries an authorization code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Loopback is the security model, not a convenience.</b> RFC 8252 §7.3: a native
/// application receives its code on an interface no other machine can reach, which is why the
/// redirect is allowed to be plain <c>http</c> at all. The address is checked before it gets
/// here - this class assumes it and would be wrong to accept anything else.
/// </para>
/// <para>
/// The listener answers exactly one request on exactly one path and then stops. Anything
/// arriving on another path gets a 404 and is not treated as the callback: a browser asking
/// for <c>/favicon.ico</c> is an ordinary thing to happen in the middle of this, and taking it
/// as the answer would end the wait with nothing.
/// </para>
/// <para>
/// The page it replies with is a fixed string. Echoing anything from the query - the error the
/// provider sent, say - would be putting text from another system into a page rendered in the
/// user's browser, which is a cross-site scripting hole in a local server, which is still a
/// cross-site scripting hole.
/// </para>
/// </remarks>
internal static class LoopbackCallback
{
    /// <summary>The page the browser lands on when it worked.</summary>
    private const string DonePage = """
        <!doctype html><html><head><meta charset="utf-8"><title>Signed in</title></head>
        <body style="font:15px system-ui;margin:3rem;color:#111820">
        <h1 style="font-size:1.2rem">Signed in.</h1>
        <p>You can close this tab and go back to Sling.</p>
        </body></html>
        """;

    /// <summary>And when it did not.</summary>
    private const string FailedPage = """
        <!doctype html><html><head><meta charset="utf-8"><title>Not signed in</title></head>
        <body style="font:15px system-ui;margin:3rem;color:#111820">
        <h1 style="font-size:1.2rem">Not signed in.</h1>
        <p>Close this tab and go back to Sling, which has the reason.</p>
        </body></html>
        """;

    /// <summary>
    /// Waits for the browser to come back to <paramref name="redirect"/>.
    /// </summary>
    /// <param name="expectedState">
    /// The <c>state</c> the authorization request carried. A callback that does not echo it
    /// exactly is refused: without that check any code delivered to this address would be
    /// accepted, including one an attacker arranged to have sent here.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the wait. This is the only part of a send that waits on a person, so it has to
    /// be interruptible by the same Escape that cancels everything else.
    /// </param>
    /// <exception cref="HttpListenerException">The address could not be listened on.</exception>
    internal static async Task<CallbackResult> WaitAsync(
        Uri redirect,
        string expectedState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(redirect);
        ArgumentException.ThrowIfNullOrEmpty(expectedState);

        using var listener = new HttpListener();

        // The explicit address rather than a wildcard. A wildcard prefix needs an
        // administrator to reserve it on Windows, and it would also accept a connection from
        // another machine - which is the one thing loopback is chosen to prevent.
        listener.Prefixes.Add(PrefixFor(redirect));
        listener.Start();

        try
        {
            while (true)
            {
                var context = await Accept(listener, cancellationToken).ConfigureAwait(false);

                if (!IsCallback(context.Request, redirect))
                {
                    Reply(context, HttpStatusCode.NotFound, FailedPage);
                    continue;
                }

                var result = Read(context.Request, expectedState);

                Reply(
                    context,
                    result.Code is null ? HttpStatusCode.BadRequest : HttpStatusCode.OK,
                    result.Code is null ? FailedPage : DonePage);

                return result;
            }
        }
        finally
        {
            // Stop rather than Abort, so the reply above is actually flushed to the browser:
            // aborting drops the connection and leaves the user looking at a failed page load
            // in place of the one that says it worked.
            listener.Stop();
        }
    }

    /// <summary>
    /// The prefix that listens for exactly this address.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpListener"/> matches by prefix and requires a trailing slash, so the path
    /// is registered as a directory even though only one file under it is answered. The path
    /// itself is checked again on the way in, which is what keeps the match exact.
    /// </remarks>
    private static string PrefixFor(Uri redirect)
    {
        var path = redirect.AbsolutePath.EndsWith('/') ? redirect.AbsolutePath : redirect.AbsolutePath + "/";

        return $"{redirect.Scheme}://{redirect.Host}:{redirect.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}{path}";
    }

    private static bool IsCallback(HttpListenerRequest request, Uri redirect) =>
        request.Url is { } url
        && string.Equals(
            url.AbsolutePath.TrimEnd('/'),
            redirect.AbsolutePath.TrimEnd('/'),
            StringComparison.Ordinal);

    /// <summary>
    /// Reads the code out of the callback, or the reason there is not one.
    /// </summary>
    /// <remarks>
    /// The state is compared first and in full. RFC 6749 §10.12 puts it there for exactly this
    /// check, and a check that runs after the code has been read is a check whose result
    /// nothing depends on.
    /// </remarks>
    private static CallbackResult Read(HttpListenerRequest request, string expectedState)
    {
        var query = ParseQuery(request.Url?.Query);

        var state = query.GetValueOrDefault("state");

        if (!string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            return new CallbackResult(
                null,
                "the browser came back with a 'state' Sling did not send. The response was "
                    + "discarded: it did not belong to this sign-in.");
        }

        if (query.GetValueOrDefault("error") is { Length: > 0 } error)
        {
            // The provider's error code and nothing else it sent. The vocabulary is fixed by
            // RFC 6749 §4.1.2.1, and its description field is free text from another system.
            return new CallbackResult(null, $"the identity provider refused: {Safe(error)}.");
        }

        return query.GetValueOrDefault("code") is { Length: > 0 } code
            ? new CallbackResult(code, null)
            : new CallbackResult(null, "the browser came back without an authorization code.");
    }

    /// <summary>
    /// Splits a query string into its parameters.
    /// </summary>
    /// <remarks>
    /// Written here rather than taken from <c>System.Web</c>: three parameters are read from
    /// this and the alternative is a whole assembly whose behaviour on a repeated key is its
    /// own. The first occurrence of a name wins, so a callback carrying two <c>code</c>
    /// parameters cannot smuggle a second past a check made on the first.
    /// </remarks>
    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        if (query is not { Length: > 1 })
        {
            return values;
        }

        foreach (var pair in query.AsSpan(1).ToString().Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(pair[..equals]);

            // A '+' is a space in a query string and nowhere else, so it is put back before
            // the percent-decoding rather than after - the other order turns a literal '%2B'
            // into a space.
            var value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));

            values.TryAdd(name, value);
        }

        return values;
    }

    /// <summary>
    /// An error code with anything that is not a plain identifier taken out.
    /// </summary>
    /// <remarks>
    /// It reaches a status bar, and text from another system reaching a message is how a
    /// message stops being a sentence. RFC 6749's codes are all lowercase words with
    /// underscores, so nothing legitimate is lost.
    /// </remarks>
    private static string Safe(string error)
    {
        var kept = new string([.. error.Take(64).Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')]);

        return kept.Length > 0 ? kept : "no reason given";
    }

    /// <summary>
    /// Waits for one request, giving up when the token says to.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpListener.GetContextAsync"/> takes no cancellation token, so cancelling
    /// means stopping the listener underneath it - which surfaces as the listener being
    /// disposed. Both of those are turned back into the cancellation they actually were.
    /// </remarks>
    private static async Task<HttpListenerContext> Accept(
        HttpListener listener,
        CancellationToken cancellationToken)
    {
        var pending = listener.GetContextAsync();

        using (cancellationToken.Register(listener.Stop))
        {
            try
            {
                return await pending.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException
                && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    private static void Reply(HttpListenerContext context, HttpStatusCode status, string page)
    {
        var body = Encoding.UTF8.GetBytes(page);

        try
        {
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.Close();
        }
        catch (HttpListenerException)
        {
            // The browser closed the tab before the reply landed. The code is already in
            // hand and the exchange is what matters; the page was a courtesy.
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
