using System.Text;

namespace Sling.Import;

/// <summary>
/// The one rule about what a value taken out of a foreign format may carry into a
/// generated <c>.http</c> document.
/// </summary>
/// <remarks>
/// <para>
/// <b>A security boundary, shared by both importers on purpose.</b> The output is a
/// newline-delimited document: a header value holding a line break becomes an extra header
/// line, a URL holding one ends the request line early, and a comment holding one becomes
/// a comment followed by <em>live document text</em>. A crafted curl command did exactly
/// that during M2's review, and a Postman collection is a strictly easier file to craft,
/// it arrives as a download rather than being typed by the person pasting it.
/// </para>
/// <para>
/// It lives here rather than in either importer because the same rule written twice will
/// eventually disagree, and the copy that ships is not necessarily the tested one. That is
/// the lesson that has now recurred three times in this repository; a second private copy
/// of it would have been the fourth.
/// </para>
/// </remarks>
internal static class TextSafety
{
    /// <summary>
    /// Removes CR, LF and the other control characters from a value taken out of a foreign
    /// document.
    /// </summary>
    /// <param name="value">The value as it arrived.</param>
    /// <param name="keepLineBreaks">
    /// True only for a request body, where a newline is content rather than structure - a
    /// body is terminated by a blank line and then end-of-request, not by a delimiter the
    /// value could contain. Stripping them there would silently rewrite a pretty-printed
    /// JSON payload into one long line, which changes the bytes sent.
    /// </param>
    /// <remarks>
    /// Stripped rather than rejected, and rejected rather than escaped: a header field has
    /// no escape mechanism, and refusing a whole import over one stray character would be a
    /// worse outcome than importing the value without it. Tab survives everywhere - it is
    /// legal inside a header value and appears in real ones. A carriage return never
    /// survives, even in a body: the caller that needs CRLF framing (a multipart body)
    /// writes the terminators itself rather than trusting them to arrive intact.
    /// </remarks>
    public static string StripControl(string value, bool keepLineBreaks = false)
    {
        static bool Keep(char c, bool keepLineBreaks) =>
            !char.IsControl(c) || c == '\t' || (keepLineBreaks && c == '\n');

        var needsWork = false;

        foreach (var c in value)
        {
            if (!Keep(c, keepLineBreaks))
            {
                needsWork = true;
                break;
            }
        }

        if (!needsWork)
        {
            return value;
        }

        var clean = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            if (Keep(c, keepLineBreaks))
            {
                clean.Append(c);
            }
        }

        return clean.ToString();
    }

    /// <summary>
    /// Shortens a value that is about to be quoted in a note.
    /// </summary>
    /// <remarks>
    /// The value being described is frequently a body or a script, and a note containing a
    /// megabyte of JSON is not a note.
    /// </remarks>
    public static string Cap(string value, int limit) =>
        value.Length <= limit ? value : string.Concat(value.AsSpan(0, limit), "…");
}
