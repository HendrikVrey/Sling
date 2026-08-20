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
    private const string ExchangeSeparator = "########################################";

    /// <summary>
    /// Renders one request and its response: the request line that was actually sent,
    /// the status, timing and size, the response headers, then the body.
    /// </summary>
    public static string Render(ResolvedRequest request, ResponseSnapshot response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        var text = new StringBuilder();
        AppendExchange(text, request, response);
        return text.ToString();
    }

    /// <summary>
    /// Renders a whole chain in execution order, so the requests Sling sent on the user's
    /// behalf to satisfy a <c>{{name.response…}}</c> reference are visible rather than
    /// implied. A tool that makes network calls a user did not ask for has to show them.
    /// </summary>
    public static string RenderChain(IReadOnlyList<(ResolvedRequest Request, ResponseSnapshot Response)> exchanges)
    {
        ArgumentNullException.ThrowIfNull(exchanges);

        var text = new StringBuilder();

        for (var i = 0; i < exchanges.Count; i++)
        {
            if (i > 0)
            {
                text.Append('\n').Append(ExchangeSeparator).Append("\n\n");
            }

            AppendExchange(text, exchanges[i].Request, exchanges[i].Response);
        }

        return text.ToString();
    }

    /// <summary>
    /// Renders diagnostics as the response pane's content. Errors first, because when a
    /// request will not send, the reason it will not send is the whole message.
    /// </summary>
    public static string RenderDiagnostics(IReadOnlyList<ParseDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var text = new StringBuilder();

        foreach (var diagnostic in diagnostics
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Line))
        {
            text
                .Append(diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning")
                .Append("  line ")
                .Append(diagnostic.Line.ToString(CultureInfo.InvariantCulture))
                .Append("  ")
                .Append(diagnostic.Message)
                .Append('\n');
        }

        return text.ToString();
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

    private static void AppendExchange(StringBuilder text, ResolvedRequest request, ResponseSnapshot response)
    {
        if (request.Name is not null)
        {
            text.Append("# @name ").Append(request.Name).Append('\n');
        }

        text.Append(request.Method).Append(' ').Append(request.Url).Append('\n');

        foreach (var hop in response.RedirectTrail)
        {
            text.Append("  ↳ ").Append(hop).Append('\n');
        }

        text.Append('\n').Append(Summarize(response)).Append("\n\n");

        foreach (var header in response.Headers)
        {
            text.Append(header.Name).Append(": ").Append(header.Value).Append('\n');
        }

        if (response.Body.Length == 0)
        {
            text.Append("\n(no body)\n");
            return;
        }

        text.Append('\n').Append(response.Body).Append('\n');

        if (response.BodyTruncated)
        {
            text.Append("\n… body truncated at ").Append(Humanize.Size(response.BodyByteCount)).Append('\n');
        }
    }

    private static string StatusLine(ResponseSnapshot response) =>
        string.IsNullOrEmpty(response.ReasonPhrase)
            ? $"HTTP/{response.HttpVersion} {response.StatusCode.ToString(CultureInfo.InvariantCulture)}"
            : $"HTTP/{response.HttpVersion} {response.StatusCode.ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase}";
}
