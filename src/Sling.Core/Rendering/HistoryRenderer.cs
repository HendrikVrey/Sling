using System.Globalization;
using System.Text;
using Sling.Core.Cookies;
using Sling.Core.History;

namespace Sling.Core.Rendering;

/// <summary>
/// Renders the things Sling keeps <em>about</em> requests — history and cookies — as
/// plain text.
/// </summary>
/// <remarks>
/// Text, because the response pane is already an editor: it searches with <c>Ctrl+F</c>,
/// folds, scrolls and copies. Building a history window and a cookie window would mean
/// rebuilding all of that worse, and it would put two more panels in a product whose
/// pitch is that there are no panels.
/// </remarks>
public static class HistoryRenderer
{
    /// <summary>
    /// Renders <paramref name="entries"/> newest first.
    /// </summary>
    /// <remarks>
    /// Local time, with the offset shown. History is read by a person sitting at the
    /// machine asking "was that before or after I changed the config", and UTC makes them
    /// do arithmetic to answer it. The offset is printed so a file compared across
    /// machines is still unambiguous.
    /// </remarks>
    public static string Render(IReadOnlyList<HistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return "No history yet.\n\nSling records one line per request: what was sent, when, and "
                + "what came back. Credentials are removed before anything is written, and no "
                + "request or response body is stored at all.";
        }

        var builder = new StringBuilder();

        builder
            .Append(entries.Count.ToString(CultureInfo.InvariantCulture))
            .Append(entries.Count == 1 ? " request" : " requests")
            .Append(", newest first. Credentials are redacted; bodies are not stored.\n\n");

        foreach (var entry in entries.OrderByDescending(e => e.SentUtc))
        {
            builder
                .Append(entry.SentUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))
                .Append("  ")
                .Append(entry.StatusCode.ToString(CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(entry.ReasonPhrase)
                .Append("  ")
                .Append(Humanize.Duration(entry.Elapsed))
                .Append("  ")
                .Append(Humanize.Size(entry.ResponseBodyBytes));

            if (entry.EnvironmentName is { } environment)
            {
                builder.Append("  [").Append(environment).Append(']');
            }

            builder
                .Append('\n')
                .Append("  ")
                .Append(entry.Method)
                .Append(' ')
                .Append(entry.Url)
                .Append('\n');

            AppendHeaders(builder, "->", entry.RequestHeaders);
            AppendHeaders(builder, "<-", entry.ResponseHeaders);

            builder.Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>Renders a cookie jar's contents, so "why is this session not working" is answerable.</summary>
    /// <remarks>
    /// <strong>Values are not shown.</strong> A session cookie is a credential in exactly
    /// the way a bearer token is, and a jar listing is the sort of thing that ends up in a
    /// screenshot on an issue tracker. What debugging actually needs is which cookies
    /// exist, what they are scoped to and when they expire — the value answers no question
    /// a person is asking.
    /// </remarks>
    public static string RenderCookies(IReadOnlyList<Cookie> cookies, string? environmentName)
    {
        ArgumentNullException.ThrowIfNull(cookies);

        var scope = environmentName is null
            ? "no environment selected"
            : $"environment '{environmentName}'";

        if (cookies.Count == 0)
        {
            return $"No cookies held for {scope}.\n\nSling keeps a separate jar per environment, in "
                + "memory only — nothing is written to disk, and switching environment or closing "
                + "the window discards them.";
        }

        var builder = new StringBuilder();

        builder
            .Append(cookies.Count.ToString(CultureInfo.InvariantCulture))
            .Append(cookies.Count == 1 ? " cookie" : " cookies")
            .Append(" held for ")
            .Append(scope)
            .Append(". Values are not shown.\n\n");

        foreach (var cookie in cookies)
        {
            builder
                .Append(cookie.Name)
                .Append("\n  ")
                .Append(cookie.HostOnly ? "host " : "domain ")
                .Append(cookie.Domain)
                .Append("  path ")
                .Append(cookie.Path);

            if (cookie.Secure)
            {
                builder.Append("  secure");
            }

            if (cookie.HttpOnly)
            {
                builder.Append("  httponly");
            }

            builder
                .Append("\n  expires ")
                .Append(cookie.Expires is { } expires
                    ? expires.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
                    : "when this window closes")
                .Append("\n\n");
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static void AppendHeaders(StringBuilder builder, string marker, IReadOnlyList<HistoryHeader> headers)
    {
        foreach (var header in headers)
        {
            builder
                .Append("  ")
                .Append(marker)
                .Append(' ')
                .Append(header.Name)
                .Append(": ")
                .Append(header.Value)
                .Append('\n');
        }
    }
}
