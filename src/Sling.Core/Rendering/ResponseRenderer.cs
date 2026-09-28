using System.Globalization;
using System.Text;
using Sling.Core.Documents;
using Sling.Core.Variables;

namespace Sling.Core.Rendering;

/// <summary>
/// Renders a completed exchange as plain text for the response pane.
/// </summary>
/// <remarks>
/// <para>
/// Text, always. <c>Sling.md</c> §5.5: a response body is untrusted input and must never
/// reach a control that can execute it, so there is no HTML path here and no browser
/// control anywhere in the application. An HTML response is shown as its source, which
/// is also what a developer debugging one actually wants to see.
/// </para>
/// <para>
/// Pure, and in <c>Sling.Core</c>, so what the pane says can be asserted in a unit test
/// rather than eyeballed in a screenshot.
/// </para>
/// </remarks>
public static class ResponseRenderer
{
    /// <summary>
    /// The line naming what was actually sent, followed by any redirect hops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split out from the body in M2, and the split is the point of that milestone. Until
    /// then the pane held one rendered transcript - request line, headers and body in a
    /// single string - which cannot be an editor buffer in any useful sense: highlighting
    /// it as JSON would colour the headers, folding would fold across them, and a
    /// transform would be handed a document that is mostly not the thing being
    /// transformed. Separating them is what lets the body be a real buffer.
    /// </para>
    /// <para>
    /// The redirect trail is here rather than beside the headers because a request that
    /// ended up somewhere other than where it was aimed is the first thing worth knowing,
    /// not a detail behind an expander.
    /// </para>
    /// </remarks>
    public static string RenderRequestLine(ResolvedRequest request, ResponseSnapshot response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        var text = new StringBuilder();

        text.Append(request.Method).Append(' ').Append(request.Url);

        foreach (var hop in response.RedirectTrail)
        {
            text.Append('\n').Append("  ↳ ").Append(hop);
        }

        return text.ToString();
    }

    /// <summary>
    /// The response headers, one per line, in the order the server sent them.
    /// </summary>
    /// <remarks>
    /// Not sorted. Order carries information - which <c>Set-Cookie</c> came first, whether
    /// a proxy appended its own <c>Via</c> - and a tool for debugging HTTP has no business
    /// tidying away what the wire said.
    /// </remarks>
    public static string RenderHeaders(ResponseSnapshot response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = new StringBuilder();

        foreach (var header in response.Headers)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(header.Name).Append(": ").Append(header.Value);
        }

        return text.ToString();
    }

    /// <summary>
    /// The order diagnostics are read in: errors first, then by line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Errors first, because when a request will not send, the reason it will not send is the
    /// whole message.
    /// </para>
    /// <para>
    /// <b>Public because the window has to be able to reproduce it.</b> Each rendered row is
    /// a line the reader can double-click to go to the line it names, and that mapping is
    /// "rendered line N is this list's Nth entry" - which only holds if the caller can ask
    /// for the same order the renderer used. A window re-sorting by eye and getting it
    /// subtly wrong would send somebody to a line that has nothing to do with the message
    /// they clicked.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ParseDiagnostic> InReadingOrder(IReadOnlyList<ParseDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return [.. diagnostics.OrderByDescending(d => d.Severity).ThenBy(d => d.Line)];
    }

    /// <summary>
    /// Renders diagnostics as the response pane's content, one per line.
    /// </summary>
    /// <remarks>
    /// <b>Exactly one rendered line per diagnostic, and that is a structural promise rather
    /// than a hope.</b> The window maps a double-clicked row back to a diagnostic by
    /// position, so a message carrying a line break would silently shift every row under it
    /// onto the wrong entry. A message is flattened here, where the promise is made, rather
    /// than trusted to hold everywhere one is constructed.
    /// </remarks>
    public static string RenderDiagnostics(IReadOnlyList<ParseDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var text = new StringBuilder();

        foreach (var diagnostic in InReadingOrder(diagnostics))
        {
            text
                .Append(diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning")
                .Append("  line ")
                .Append(diagnostic.Line.ToString(CultureInfo.InvariantCulture))
                .Append("  ")
                .Append(OneLine(diagnostic.Message))
                .Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Collapses any line break in a message to a single space.</summary>
    private static string OneLine(string message)
    {
        if (message.AsSpan().IndexOfAny('\r', '\n') < 0)
        {
            return message;
        }

        var flattened = new StringBuilder(message.Length);
        var space = false;

        foreach (var c in message)
        {
            if (c is '\r' or '\n')
            {
                space = true;
                continue;
            }

            if (space)
            {
                // Only where something follows, so a message ending in a newline does not
                // gain a trailing space that a test would have to know about.
                if (flattened.Length > 0)
                {
                    flattened.Append(' ');
                }

                space = false;
            }

            flattened.Append(c);
        }

        return flattened.ToString();
    }

    /// <summary>The one-line verdict shown in the status bar: status, time, size.</summary>
    public static string Summarize(ResponseSnapshot response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return string.Join(
            "  ·  ",
            StatusLine(response),
            Humanize.Duration(response.Elapsed),
            Humanize.Size(response.BodyByteCount) + (response.BodyTruncated ? " (truncated)" : string.Empty));
    }

    /// <summary>
    /// The one-line label for an exchange in the response pane's picker.
    /// </summary>
    /// <param name="position">One-based, as shown.</param>
    /// <param name="request">The request as it was sent.</param>
    /// <param name="response">What came back.</param>
    /// <param name="role">
    /// Why the exchange happened. Anything Sling did on the user's behalf says so in the
    /// label, because a tool that makes network calls nobody asked for has to show them as
    /// calls nobody asked for - a row that reads like every other row is not showing them.
    /// </param>
    /// <remarks>
    /// The request's <c>@name</c> when it has one, because that is the word the document
    /// itself uses and the word every chain reference is written against - seeing
    /// <c>login</c> in the picker and <c>{{login.response…}}</c> in the request is the
    /// whole point of naming one. Without a name there is nothing better than the position
    /// and the target.
    /// <para>
    /// In <c>Sling.Core</c> rather than in the window because it is a rule with a right
    /// answer, and a label built inline in a code-behind is a label nothing can check.
    /// </para>
    /// </remarks>
    public static string DescribeExchange(
        int position,
        ResolvedRequest request,
        ResponseSnapshot response,
        ExchangeRole role = ExchangeRole.Requested)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        var index = position.ToString(CultureInfo.InvariantCulture);
        var status = response.StatusCode.ToString(CultureInfo.InvariantCulture);

        var subject = request.Name is { Length: > 0 } name
            ? name
            : $"{request.Method} {request.Url}";

        return DescribeRole(role) is { } note
            ? $"{index}.  {subject}  ({note})  ·  {status}"
            : $"{index}.  {subject}  ·  {status}";
    }

    /// <summary>
    /// How a role is said in a picker row, or null for the one that needs no saying.
    /// </summary>
    /// <remarks>
    /// Phrased from the user's side rather than the implementation's. "sent for you" is the
    /// fact that matters about a chained dependency; "dependency" is what the code calls it.
    /// </remarks>
    public static string? DescribeRole(ExchangeRole role) => role switch
    {
        ExchangeRole.Dependency => "sent for you",
        ExchangeRole.TokenRequest => "token request",
        ExchangeRole.Retry => "retry after refresh",
        _ => null,
    };

    /// <summary>
    /// The body as the editor should hold it, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An empty body becomes a sentence rather than an empty buffer, because an empty
    /// editor after a send is indistinguishable from an editor that was never filled. A
    /// 204 is a perfectly good answer and should look like one.
    /// </para>
    /// <para>
    /// A truncation notice is deliberately <b>not</b> appended. That was right when the
    /// pane held a transcript and is wrong now: the buffer is the body, and a line of
    /// Sling's own prose inside it would be transformed, folded and searched along with
    /// the response - a JSON body plus a trailing English sentence does not parse, so the
    /// first thing the user would meet is a format error Sling caused. The notice belongs
    /// beside the buffer, and <see cref="Summarize"/> already carries it.
    /// </para>
    /// </remarks>
    public static string RenderBody(ResponseSnapshot response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Body.Length == 0 ? "(no body)" : response.Body;
    }

    /// <summary>
    /// Whether <see cref="RenderBody"/> returned Sling's own words rather than the
    /// server's.
    /// </summary>
    /// <remarks>
    /// The editor asks before it highlights or transforms: "(no body)" is not a document
    /// and should not be detected, coloured or offered a JSON transform.
    /// </remarks>
    public static bool IsPlaceholderBody(ResponseSnapshot response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Body.Length == 0;
    }

    private static string StatusLine(ResponseSnapshot response) =>
        string.IsNullOrEmpty(response.ReasonPhrase)
            ? $"HTTP/{response.HttpVersion} {response.StatusCode.ToString(CultureInfo.InvariantCulture)}"
            : $"HTTP/{response.HttpVersion} {response.StatusCode.ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase}";
}
