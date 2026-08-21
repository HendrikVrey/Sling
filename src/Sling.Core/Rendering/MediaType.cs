using System.Diagnostics.CodeAnalysis;

namespace Sling.Core.Rendering;

/// <summary>
/// What a response body is, as far as its <c>Content-Type</c> is willing to say.
/// </summary>
/// <remarks>
/// Deliberately a small closed set rather than a copy of the IANA registry. The only
/// question being asked is "how should this be shown", and the answers are: highlight it
/// as one of a handful of languages, show it as text, or say it is not text at all.
/// </remarks>
public enum MediaKind
{
    /// <summary>No <c>Content-Type</c>, or one that parsed to nothing usable.</summary>
    Unknown = 0,

    /// <summary>JSON, including the <c>+json</c> structured suffix and newline-delimited JSON.</summary>
    Json,

    /// <summary>XML, including the <c>+xml</c> structured suffix.</summary>
    Xml,

    /// <summary>HTML, and XHTML — which is XML underneath but reads better as HTML.</summary>
    Html,

    /// <summary>CSS.</summary>
    Css,

    /// <summary>JavaScript.</summary>
    JavaScript,

    /// <summary>Markdown.</summary>
    Markdown,

    /// <summary>Comma-separated values.</summary>
    Csv,

    /// <summary>Text with no more specific shape: <c>text/plain</c> and the rest of <c>text/*</c>.</summary>
    PlainText,

    /// <summary>Not text. An image, an archive, a protocol buffer.</summary>
    Binary,
}

/// <summary>
/// A parsed <c>Content-Type</c> header.
/// </summary>
/// <param name="Essence">
/// The lower-cased <c>type/subtype</c>, with parameters and whitespace removed. Empty when
/// the header was absent or unparseable.
/// </param>
/// <param name="Charset">The <c>charset</c> parameter, lower-cased, or null.</param>
/// <param name="Kind">How the body should be shown.</param>
/// <remarks>
/// <para>
/// <b>This parses untrusted input.</b> A <c>Content-Type</c> comes from a server the user
/// does not control, and Sling shows what it says. Everything here is a bounded scan of
/// the string with no backtracking and no allocation beyond the two substrings it returns:
/// a header is not a place to spend time, and a pathological one must not be able to make
/// it.
/// </para>
/// <para>
/// The kind is advisory. <see cref="MediaKind.Binary"/> does not stop the body being shown
/// — a server that mislabels JSON as <c>application/octet-stream</c> is common enough that
/// refusing to show it would be the wrong response. It decides what Sling *starts* from;
/// content sniffing gets the second word.
/// </para>
/// </remarks>
public readonly record struct MediaType(string Essence, string? Charset, MediaKind Kind)
{
    /// <summary>Nothing was said.</summary>
    public static MediaType None { get; } = new(string.Empty, null, MediaKind.Unknown);

    /// <summary>
    /// Parses a <c>Content-Type</c> header value.
    /// </summary>
    /// <param name="header">The raw header value, or null when the response carried none.</param>
    /// <remarks>
    /// Never throws and never rejects. A header this cannot make sense of yields
    /// <see cref="None"/>, because "the server said something odd" must degrade to "show it
    /// as text" rather than to an error about a header the user did not write.
    /// </remarks>
    public static MediaType Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return None;
        }

        var semicolon = IndexOfUnquoted(header, ';');
        var essence = (semicolon < 0 ? header : header[..semicolon]).Trim().ToLowerInvariant();

        if (essence.Length == 0 || essence.IndexOf('/', StringComparison.Ordinal) <= 0)
        {
            return None;
        }

        var charset = semicolon < 0 ? null : Parameter(header[(semicolon + 1)..], "charset");

        return new MediaType(essence, charset, Classify(essence));
    }

    /// <summary>True for anything worth putting in a text editor at all.</summary>
    public bool IsTextual => Kind is not (MediaKind.Unknown or MediaKind.Binary);

    private static MediaKind Classify(string essence)
    {
        // Ordered by specificity, and the order is load-bearing in one place:
        // application/xhtml+xml matches the +xml suffix as well, and HTML is the more
        // useful of the two answers for something a browser would render.
        if (essence is "text/html" or "application/xhtml+xml")
        {
            return MediaKind.Html;
        }

        if (essence is "application/json" or "text/json"
            or "application/ndjson" or "application/x-ndjson" or "application/jsonl"
            || HasSuffix(essence, "+json"))
        {
            return MediaKind.Json;
        }

        if (essence is "application/xml" or "text/xml" || HasSuffix(essence, "+xml"))
        {
            return MediaKind.Xml;
        }

        if (essence is "text/css")
        {
            return MediaKind.Css;
        }

        if (essence is "text/javascript" or "application/javascript" or "application/x-javascript"
            or "text/ecmascript" or "application/ecmascript")
        {
            return MediaKind.JavaScript;
        }

        if (essence is "text/markdown" or "text/x-markdown")
        {
            return MediaKind.Markdown;
        }

        if (essence is "text/csv")
        {
            return MediaKind.Csv;
        }

        // Two non-text types that are text in every way that matters here. Both are
        // routinely returned by APIs and both are unreadable as "binary".
        if (essence is "application/x-www-form-urlencoded" or "application/graphql")
        {
            return MediaKind.PlainText;
        }

        return essence.StartsWith("text/", StringComparison.Ordinal)
            ? MediaKind.PlainText
            : MediaKind.Binary;
    }

    /// <summary>
    /// Whether <paramref name="essence"/> ends in a structured syntax suffix, with a real
    /// subtype in front of it.
    /// </summary>
    /// <remarks>
    /// The length is measured against the <b>subtype</b>, not the whole essence, and that
    /// distinction is the bug this comment exists for: <c>application/+json</c> is longer
    /// than <c>+json</c> and ends with it, so an essence-length check passes it and claims
    /// JSON for a header that names no subtype at all. RFC 6838 requires something in
    /// front of the plus.
    /// </remarks>
    private static bool HasSuffix(string essence, string suffix)
    {
        var slash = essence.IndexOf('/', StringComparison.Ordinal);

        if (slash < 0)
        {
            return false;
        }

        var subtype = essence.AsSpan(slash + 1);

        return subtype.Length > suffix.Length && subtype.EndsWith(suffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads one parameter out of the parameter section, honouring quoted values.
    /// </summary>
    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "A charset name is compared against lower-cased constants and shown "
            + "as written in every specification that defines one. Upper-casing it here to "
            + "satisfy the rule would mean lower-casing it again at every use.")]
    private static string? Parameter(string parameters, string name)
    {
        foreach (var range in Split(parameters))
        {
            var part = parameters[range].Trim();
            var equals = part.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0)
            {
                continue;
            }

            if (!part.AsSpan(0, equals).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = part[(equals + 1)..].Trim();

            // A quoted-string value. Unquoted rather than parsed: the only escape RFC 9110
            // defines inside one is a backslash pair, and a charset that needs one is not a
            // charset.
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            return value.Length == 0 ? null : value.ToLowerInvariant();
        }

        return null;
    }

    /// <summary>Splits on semicolons that are not inside a quoted string.</summary>
    private static List<Range> Split(string parameters)
    {
        var ranges = new List<Range>();
        var start = 0;

        while (start <= parameters.Length)
        {
            var next = IndexOfUnquoted(parameters, ';', start);

            if (next < 0)
            {
                ranges.Add(start..parameters.Length);
                break;
            }

            ranges.Add(start..next);
            start = next + 1;
        }

        return ranges;
    }

    /// <summary>
    /// The first <paramref name="delimiter"/> at or after <paramref name="from"/> that is
    /// not inside a quoted string.
    /// </summary>
    /// <remarks>
    /// An unterminated quote swallows the rest of the string, which is the conservative
    /// answer: the alternative is treating a delimiter inside what the server intended as a
    /// value as a real one, and splitting a header in a place it does not split.
    /// </remarks>
    private static int IndexOfUnquoted(string text, char delimiter, int from = 0)
    {
        var quoted = false;

        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];

            if (quoted && c == '\\')
            {
                // Skip the escaped character so a \" does not close the string.
                i++;
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && c == delimiter)
            {
                return i;
            }
        }

        return -1;
    }
}
