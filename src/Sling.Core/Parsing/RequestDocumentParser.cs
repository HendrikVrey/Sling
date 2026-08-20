using System.Globalization;
using System.Text.RegularExpressions;
using Sling.Core.Documents;

namespace Sling.Core.Parsing;

/// <summary>
/// Parses the <c>.http</c> format into a <see cref="RequestDocument"/>.
/// </summary>
/// <remarks>
/// <para>
/// The reference dialect is the VS Code REST Client's, chosen in <c>Sling.md</c> §2
/// because it is the one the other implementations were written against. Every place
/// Sling differs is recorded in <c>docs/http-dialect.md</c> rather than left to be
/// discovered from a bug report.
/// </para>
/// <para>
/// The parser never fails: anything it cannot make sense of becomes a
/// <see cref="ParseDiagnostic"/> and parsing continues. A request document is edited
/// live, so it is malformed most of the time it is looked at, and an exception would
/// mean the editor could not describe what is wrong.
/// </para>
/// </remarks>
public static partial class RequestDocumentParser
{
    /// <summary>
    /// Verbs that identify a request line on sight. An unrecognised all-caps token in
    /// the same position is still treated as a method — extension verbs exist (WebDAV,
    /// and every API that invented one) — but it earns a warning.
    /// </summary>
    private static readonly string[] KnownMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"];

    public static RequestDocument Parse(string? text) => new Walker(SplitLines(text ?? string.Empty)).Run();

    [GeneratedRegex(@"^\s*@([A-Za-z_][A-Za-z0-9_.\-]*)\s*=\s*(.*)$")]
    private static partial Regex VariableDefinitionPattern { get; }

    [GeneratedRegex(@"^\s*(?:#+|//)\s*@([A-Za-z][A-Za-z0-9\-]*)(?:\s*[=\s]\s*(.*))?$")]
    private static partial Regex MetadataPattern { get; }

    [GeneratedRegex(@"^([^:\s]+)\s*:\s*(.*)$")]
    private static partial Regex HeaderPattern { get; }

    [GeneratedRegex(@"^\s*(?:HTTP/)?(\d)(?:\.(\d))?\s*$")]
    private static partial Regex HttpVersionPattern { get; }

    /// <summary>
    /// Splits on CRLF, LF or a lone CR, keeping the count aligned with what an editor
    /// shows so a diagnostic's line number is the line the user can see.
    /// </summary>
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\n' or '\r'))
            {
                continue;
            }

            lines.Add(text[start..i]);

            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        if (start < text.Length || lines.Count == 0)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>
    /// A cursor over the document's lines plus the results accumulated so far. A class
    /// rather than a pile of <c>ref</c> parameters threaded through six methods.
    /// </summary>
    private sealed class Walker(List<string> lines)
    {
        private readonly List<VariableDefinition> _variables = [];
        private readonly List<RequestBlock> _requests = [];
        private readonly List<ParseDiagnostic> _diagnostics = [];

        private int _index;
        private string? _pendingName;
        private int _pendingNameLine;
        private string? _pendingTitle;

        /// <summary>
        /// The line the current request's text began on, counting the <c># @name</c> and
        /// comment lines above its request line. Reset whenever a separator or a
        /// completed request starts a new one.
        /// </summary>
        private int _segmentStart = 1;

        /// <summary>The 1-based number of the line the cursor is on.</summary>
        private int LineNumber => _index + 1;

        private bool AtEnd => _index >= lines.Count;

        public RequestDocument Run()
        {
            while (!AtEnd)
            {
                var line = lines[_index];

                if (IsSeparator(line))
                {
                    // A separator both ends the previous request and titles the next one.
                    _pendingTitle = TitleOf(line);
                    _pendingName = null;
                    _index++;
                    _segmentStart = LineNumber;
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    _index++;
                }
                else if (TryReadMetadata(line))
                {
                    _index++;
                }
                else if (IsComment(line))
                {
                    _index++;
                }
                else if (VariableDefinitionPattern.Match(line) is { Success: true } variable)
                {
                    _variables.Add(new VariableDefinition(
                        variable.Groups[1].Value,
                        variable.Groups[2].Value.Trim(),
                        LineNumber));
                    _index++;
                }
                else
                {
                    ReadRequest();
                }
            }

            return new RequestDocument(_variables, _requests, _diagnostics);
        }

        /// <summary>
        /// Consumes a request line, its headers and its body, leaving the cursor on the
        /// separator (or end of file) that terminated it.
        /// </summary>
        private void ReadRequest()
        {
            var startLine = LineNumber;
            var (method, target, version) = ReadRequestLine(lines[_index], startLine);
            _index++;

            target += ReadTargetContinuations();

            var headers = ReadHeaders();
            var body = ReadBody();

            if (_pendingName is not null)
            {
                RejectDuplicateName(_pendingName, _pendingNameLine);
            }

            _requests.Add(new RequestBlock(
                _pendingName,
                _pendingTitle,
                method,
                target,
                version,
                headers,
                body,
                Math.Min(_segmentStart, startLine),
                startLine,
                Math.Max(startLine, _index)));

            // All three belong to the request just built. Leaving any of them set would
            // silently attach this request's name to the next one, and a chain reference
            // that resolves to the wrong request is worse than one that fails to resolve.
            _pendingName = null;
            _pendingTitle = null;
            _segmentStart = Math.Max(startLine, _index) + 1;
        }

        /// <summary>
        /// Reports a second request claiming a name an earlier one already has.
        /// </summary>
        /// <remarks>
        /// Left unreported this is the worst kind of defect the format can produce.
        /// <c>BlockNamed</c> returns the first match while the response store is keyed by
        /// name, so a chain's dependency graph points at one request and its substituted
        /// value comes from another — with nothing sent, shown or logged to say so.
        /// </remarks>
        private void RejectDuplicateName(string name, int line)
        {
            var existing = _requests.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
            if (existing is null)
            {
                return;
            }

            _diagnostics.Add(ParseDiagnostic.Error(
                $"'@name {name}' is already used by the request on line "
                    + $"{existing.StartLine.ToString(CultureInfo.InvariantCulture)}. A chain reference "
                    + "must name exactly one request.",
                line));
        }

        private (string Method, string Target, string? Version) ReadRequestLine(string line, int lineNumber)
        {
            var trimmed = line.Trim();
            var space = trimmed.IndexOf(' ', StringComparison.Ordinal);

            // "GET https://..." versus a bare "https://...". The distinguishing test is
            // whether the first token looks like a verb, not whether a space exists — a
            // URL can carry a space in a query value.
            var firstToken = space < 0 ? trimmed : trimmed[..space];
            var method = firstToken.ToUpperInvariant();
            var looksLikeMethod = space > 0
                && firstToken.All(char.IsAsciiLetter)
                && (KnownMethods.Contains(method, StringComparer.Ordinal)
                    || firstToken.Equals(method, StringComparison.Ordinal));

            if (!looksLikeMethod)
            {
                // A line holding nothing but a verb reaches here, because the verb test
                // needs a space. Without this it became the request target — surfacing
                // much later, and much less usefully, as "'GET' is not an absolute URL".
                if (space < 0 && KnownMethods.Contains(method, StringComparer.Ordinal))
                {
                    _diagnostics.Add(ParseDiagnostic.Error(
                        $"'{method}' has no request target. Write the method, a space, then the URL.",
                        lineNumber));
                }

                return ("GET", StripVersion(trimmed, out var bareVersion), bareVersion);
            }

            if (!KnownMethods.Contains(method, StringComparer.Ordinal))
            {
                _diagnostics.Add(ParseDiagnostic.Warning(
                    $"'{method}' is not a standard HTTP method. It will be sent as written.",
                    lineNumber));
            }

            // rest is non-empty: trimmed was already trimmed, so a trailing space cannot
            // survive to make it empty. A version-only tail can, though — "GET HTTP/1.1".
            var rest = trimmed[(space + 1)..].Trim();
            var target = StripVersion(rest, out var version);

            if (target.Length == 0)
            {
                _diagnostics.Add(ParseDiagnostic.Error(
                    $"'{method}' has no request target. Write the method, a space, then the URL.",
                    lineNumber));
            }

            return (method, target, version);
        }

        /// <summary>
        /// Removes a trailing <c>HTTP/1.1</c> from a request line and hands it back
        /// separately.
        /// </summary>
        private static string StripVersion(string target, out string? version)
        {
            version = null;

            var lastSpace = target.LastIndexOf(' ');
            if (lastSpace < 0)
            {
                return target;
            }

            var tail = target[(lastSpace + 1)..];
            if (!tail.StartsWith("HTTP/", StringComparison.Ordinal) || !HttpVersionPattern.IsMatch(tail))
            {
                return target;
            }

            version = tail;
            return target[..lastSpace].TrimEnd();
        }

        /// <summary>
        /// Query strings split across lines: an indented continuation beginning with
        /// <c>?</c> or <c>&amp;</c> appends to the target. Part of the reference dialect
        /// and the one place the format allows a logical line to span physical ones.
        /// </summary>
        private string ReadTargetContinuations()
        {
            var appended = string.Empty;

            while (!AtEnd)
            {
                var line = lines[_index];
                var trimmed = line.TrimStart();

                var isContinuation = line.Length > trimmed.Length
                    && (trimmed.StartsWith('?') || trimmed.StartsWith('&'));

                if (!isContinuation)
                {
                    break;
                }

                appended += trimmed.TrimEnd();
                _index++;
            }

            return appended;
        }

        /// <summary>
        /// Reads header lines up to the blank line that ends them, the next separator, or
        /// end of file.
        /// </summary>
        private List<HeaderField> ReadHeaders()
        {
            var headers = new List<HeaderField>();

            while (!AtEnd)
            {
                var line = lines[_index];

                if (string.IsNullOrWhiteSpace(line) || IsSeparator(line))
                {
                    break;
                }

                if (TryReadMetadata(line) || IsComment(line))
                {
                    _index++;
                    continue;
                }

                var match = HeaderPattern.Match(line);
                if (!match.Success)
                {
                    _diagnostics.Add(ParseDiagnostic.Error(
                        "Expected a header in the form 'Name: value'. A blank line separates "
                            + "the headers from the body — add one if this line is meant to be body text.",
                        LineNumber));
                    _index++;
                    continue;
                }

                var name = match.Groups[1].Value;

                // A name holding a {{reference}} cannot be checked yet — braces are not
                // token characters, and what the reference becomes is not known until
                // send time. RequestResolver settles it after substitution.
                if (!HttpSyntax.IsToken(name) && !name.Contains("{{", StringComparison.Ordinal))
                {
                    _diagnostics.Add(ParseDiagnostic.Error(
                        $"'{name}' is not a valid header name.",
                        LineNumber));
                    _index++;
                    continue;
                }

                headers.Add(new HeaderField(name, match.Groups[2].Value.Trim(), LineNumber));
                _index++;
            }

            return headers;
        }

        /// <summary>
        /// Reads the body, which runs from the blank line after the headers to the next
        /// separator or end of file.
        /// </summary>
        /// <remarks>
        /// Everything in that span is body text, including lines that begin with <c>#</c>
        /// — a JSON body full of comments would otherwise lose them, and a shell script in
        /// a body would lose most of itself. The one casualty is a body line that begins
        /// with <c>###</c>, which no dialect can represent; that is in the divergence table.
        /// </remarks>
        private string? ReadBody()
        {
            if (AtEnd || IsSeparator(lines[_index]))
            {
                return null;
            }

            // Consume the single blank line that ended the headers. Any further blank
            // lines are the body's own leading whitespace and are trimmed below.
            _index++;

            var first = _index;
            while (!AtEnd && !IsSeparator(lines[_index]))
            {
                _index++;
            }

            var last = _index - 1;

            while (first <= last && string.IsNullOrWhiteSpace(lines[first]))
            {
                first++;
            }

            while (last >= first && string.IsNullOrWhiteSpace(lines[last]))
            {
                last--;
            }

            return first > last ? null : string.Join('\n', lines.GetRange(first, last - first + 1));
        }

        /// <summary>
        /// Recognises a <c># @directive</c> comment. <c>@name</c> is stored; anything else
        /// is reported rather than ignored, so an unsupported directive is visible instead
        /// of silently doing nothing.
        /// </summary>
        private bool TryReadMetadata(string line)
        {
            var match = MetadataPattern.Match(line);
            if (!match.Success)
            {
                return false;
            }

            var directive = match.Groups[1].Value;
            var argument = match.Groups[2].Value.Trim();

            if (string.Equals(directive, "name", StringComparison.OrdinalIgnoreCase))
            {
                if (argument.Length == 0)
                {
                    _diagnostics.Add(ParseDiagnostic.Error(
                        "'@name' needs a name: '# @name login'.",
                        LineNumber));
                }
                else if (_pendingName is not null)
                {
                    _diagnostics.Add(ParseDiagnostic.Error(
                        $"This request is already named '{_pendingName}'. A request has one name.",
                        LineNumber));
                }
                else
                {
                    _pendingName = argument;
                    _pendingNameLine = LineNumber;
                }

                return true;
            }

            _diagnostics.Add(ParseDiagnostic.Warning(
                $"'@{directive}' is not supported yet and is being ignored.",
                LineNumber));

            return true;
        }

        private static bool IsSeparator(string line) => line.StartsWith("###", StringComparison.Ordinal);

        private static bool IsComment(string line)
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal);
        }

        private static string? TitleOf(string separator)
        {
            var title = separator.TrimStart('#').Trim();
            return title.Length == 0 ? null : title;
        }
    }
}
