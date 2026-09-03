using System.Text.RegularExpressions;

namespace Sling.Core.Parsing;

/// <summary>
/// The line-level rules of the <c>.http</c> format: what a separator looks like, what a
/// comment looks like, and the patterns that recognise every other kind of line.
/// </summary>
/// <remarks>
/// <para>
/// One home, for the same reason <see cref="HttpSyntax"/> has one. Two things read this
/// format line by line - <see cref="RequestDocumentParser"/>, which turns it into requests,
/// and <see cref="HttpLineClassifier"/>, which says what each line is so an editor can
/// colour it - and a grammar written twice is a grammar that drifts. When it drifts the
/// symptom is the worst kind: the editor draws a line as a header while the parser sends it
/// as body text, and the picture disagrees with the request without either being obviously
/// wrong.
/// </para>
/// <para>
/// Internal rather than public. The format is Sling's to read, not its callers'; what
/// leaves this assembly is a parsed document.
/// </para>
/// </remarks>
internal static partial class HttpGrammar
{
    /// <summary>
    /// Verbs that identify a request line on sight. An unrecognised all-caps token in
    /// the same position is still treated as a method - extension verbs exist (WebDAV,
    /// and every API that invented one) - but it earns a warning.
    /// </summary>
    private static readonly string[] KnownMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"];

    /// <summary>A <c>@name = value</c> document variable.</summary>
    [GeneratedRegex(@"^\s*@([A-Za-z_][A-Za-z0-9_.\-]*)\s*=\s*(.*)$")]
    internal static partial Regex VariableDefinition { get; }

    /// <summary>A <c># @directive argument</c> metadata comment.</summary>
    [GeneratedRegex(@"^\s*(?:#+|//)\s*@([A-Za-z][A-Za-z0-9\-]*)(?:\s*[=\s]\s*(.*))?$")]
    internal static partial Regex Metadata { get; }

    /// <summary>A <c>Name: value</c> header field.</summary>
    [GeneratedRegex(@"^([^:\s]+)\s*:\s*(.*)$")]
    internal static partial Regex Header { get; }

    /// <summary>The <c>HTTP/1.1</c> tail of a request line.</summary>
    [GeneratedRegex(@"^\s*(?:HTTP/)?(\d)(?:\.(\d))?\s*$")]
    internal static partial Regex HttpVersion { get; }

    /// <summary>
    /// A <c>&lt; ./body.json</c> body import, in all three forms the reference dialect
    /// spells it: raw bytes, <c>&lt;@</c> to substitute variables inside the file, and
    /// <c>&lt;@utf16</c> to say which encoding to read it as first.
    /// </summary>
    /// <remarks>
    /// The whitespace after the marker is what makes this safe to apply to every body
    /// line. Without it the pattern would claim <c>&lt;?xml version="1.0"?&gt;</c> and
    /// <c>&lt;html&gt;</c> - the opening line of two body formats people actually send,
    /// and turn them into imports of files that do not exist.
    /// </remarks>
    [GeneratedRegex(@"^<(?:@([A-Za-z0-9._\-]+)?)?[ \t]+(\S.*)$")]
    internal static partial Regex BodyImport { get; }

    /// <summary>
    /// A <c>###</c> separator, which both ends the request above it and titles the one
    /// below.
    /// </summary>
    /// <remarks>
    /// Deliberately not trimmed. An indented <c>###</c> is a comment rather than a
    /// separator, which is what lets a body carrying markdown headings survive - and the
    /// unindented case is in the divergence table because no dialect can represent it.
    /// </remarks>
    internal static bool IsSeparator(string line) =>
        line.StartsWith("###", StringComparison.Ordinal);

    /// <summary>A <c>#</c> or <c>//</c> comment line, indented or not.</summary>
    internal static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>
    /// An indented continuation of the request target: a query string split across lines.
    /// </summary>
    /// <remarks>
    /// The indent is load-bearing. Without it a body line beginning with <c>&amp;</c>, or a
    /// URL-encoded form starting at column one, would be swallowed into the target of the
    /// request above it.
    /// </remarks>
    internal static bool IsTargetContinuation(string line)
    {
        var trimmed = line.TrimStart();

        return line.Length > trimmed.Length
            && (trimmed.StartsWith('?') || trimmed.StartsWith('&'));
    }

    /// <summary>Whether <paramref name="method"/> is one of the nine standard verbs.</summary>
    /// <param name="method">An already upper-cased verb.</param>
    internal static bool IsKnownMethod(string method) =>
        KnownMethods.Contains(method, StringComparer.Ordinal);

    /// <summary>
    /// Splits a request line into its verb, its target and its version, by offset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one piece of the request line's grammar that both readers need to agree on, and
    /// the one most easily got subtly different: the test for a verb is whether the first
    /// token <em>looks like</em> one, not whether a space exists, because a URL can carry a
    /// space in a query value. A classifier that used the simpler rule would paint the first
    /// word of <c>https://host/a b</c> as a method.
    /// </para>
    /// <para>
    /// Offsets rather than substrings, because the caller that colours the line needs to
    /// know where each part <em>is</em>, and the caller that parses it can slice. Lengths of
    /// zero mean the part is absent.
    /// </para>
    /// </remarks>
    /// <param name="line">One line, without its terminator.</param>
    internal static RequestLineParts SplitRequestLine(string line)
    {
        var start = 0;
        while (start < line.Length && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        var end = line.Length;
        while (end > start && char.IsWhiteSpace(line[end - 1]))
        {
            end--;
        }

        var trimmed = line[start..end];
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        var firstToken = space < 0 ? trimmed : trimmed[..space];
        var method = firstToken.ToUpperInvariant();

        var looksLikeMethod = space > 0
            && firstToken.All(char.IsAsciiLetter)
            && (IsKnownMethod(method) || firstToken.Equals(method, StringComparison.Ordinal));

        // A bare target - "https://host/thing" with no verb - is legal and means GET. The
        // whole of it is the target, and there is no method span to colour.
        var targetStart = looksLikeMethod ? start + space + 1 : start;

        // Whitespace, not spaces: the parser reaches the target through a Trim(), so a tab
        // between the verb and the URL is not part of either. A tab has no glyph, which is
        // exactly why a classifier that stopped at the space would look right and be wrong.
        while (targetStart < end && char.IsWhiteSpace(line[targetStart]))
        {
            targetStart++;
        }

        var versionStart = VersionStart(line, targetStart, end);
        var targetEnd = versionStart < 0 ? end : versionStart;

        // The space that separated the target from the version belongs to neither. Trimmed
        // here so the two spans are adjacent rather than overlapping by a blank.
        while (targetEnd > targetStart && line[targetEnd - 1] == ' ')
        {
            targetEnd--;
        }

        return new RequestLineParts(
            MethodStart: start,
            MethodLength: looksLikeMethod ? firstToken.Length : 0,
            TargetStart: targetStart,
            TargetLength: targetEnd - targetStart,
            VersionStart: versionStart,
            VersionLength: versionStart < 0 ? 0 : end - versionStart);
    }

    /// <summary>
    /// Where the trailing <c>HTTP/1.1</c> begins, or -1 when the line has none.
    /// </summary>
    /// <remarks>
    /// The last space, matching <c>StripVersion</c>: a target may hold spaces, and only the
    /// final token can be a version.
    /// </remarks>
    private static int VersionStart(string line, int targetStart, int end)
    {
        if (targetStart >= end)
        {
            return -1;
        }

        var lastSpace = line.LastIndexOf(' ', end - 1, end - targetStart);

        if (lastSpace < targetStart)
        {
            return -1;
        }

        var tail = line[(lastSpace + 1)..end];

        return tail.StartsWith("HTTP/", StringComparison.Ordinal) && HttpVersion.IsMatch(tail)
            ? lastSpace + 1
            : -1;
    }
}

/// <summary>
/// Where each part of a request line sits, by offset into the line. A length of zero means
/// the part is not there.
/// </summary>
internal readonly record struct RequestLineParts(
    int MethodStart,
    int MethodLength,
    int TargetStart,
    int TargetLength,
    int VersionStart,
    int VersionLength);
