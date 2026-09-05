using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Sling.Core.Auth;
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
public static class RequestDocumentParser
{
    /// <summary>
    /// The directives that configure a <c># @auth oauth2</c> block. Recognised only after
    /// <c># @auth</c>, so that one of them written on its own is an error rather than a
    /// comment that quietly does nothing.
    /// </summary>
    private static readonly string[] AuthDirectives =
    [
        "token-url",
        "authorize-url",
        "redirect-uri",
        "client-id",
        "client-secret",
        "scope",
        "audience",
        "client-auth",
    ];

    public static RequestDocument Parse(string? text) => new Walker(SplitLines(text ?? string.Empty)).Run();

    /// <summary>
    /// The directive name on a metadata line - <c>name</c> for <c># @name login</c> - or
    /// null when the line is not one.
    /// </summary>
    /// <remarks>
    /// Exposed so that editing a document does not need a second copy of the grammar. The
    /// auth panel has to find the <c># @auth</c> block's lines in order to replace them, and
    /// the parse does not carry them: the directives under <c># @auth</c> become fields on a
    /// grant, and only the opening line's number survives. A private regex in the editor
    /// would be the two-homes failure <see cref="HttpSyntax"/> exists to avoid.
    /// </remarks>
    /// <param name="line">One line, without its terminator.</param>
    public static string? MetadataDirective(string line) =>
        HttpGrammar.Metadata.Match(line ?? string.Empty) is { Success: true } match
            ? match.Groups[1].Value
            : null;

    /// <summary>
    /// One physical line and the terminator that ended it.
    /// </summary>
    /// <remarks>
    /// The terminator is kept because a body is bytes, not lines. Normalising it to
    /// <c>\n</c> - which this parser used to do - is wrong for the one body format that
    /// specifies its own framing: RFC 2046 multipart requires CRLF between parts, and a
    /// multipart body typed on Windows and sent with LF is rejected by strict servers for
    /// a reason nothing in the document could explain.
    /// </remarks>
    private readonly record struct SourceLine(string Text, string Ending);

    /// <summary>
    /// Splits on CRLF, LF or a lone CR, keeping the count aligned with what an editor
    /// shows so a diagnostic's line number is the line the user can see, and keeping each
    /// terminator so a body can be reassembled exactly as written.
    /// </summary>
    private static List<SourceLine> SplitLines(string text)
    {
        var lines = new List<SourceLine>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\n' or '\r'))
            {
                continue;
            }

            var endingStart = i;

            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            lines.Add(new SourceLine(text[start..endingStart], text[endingStart..(i + 1)]));
            start = i + 1;
        }

        if (start < text.Length || lines.Count == 0)
        {
            lines.Add(new SourceLine(text[start..], string.Empty));
        }

        return lines;
    }

    /// <summary>
    /// A cursor over the document's lines plus the results accumulated so far. A class
    /// rather than a pile of <c>ref</c> parameters threaded through six methods.
    /// </summary>
    private sealed class Walker(List<SourceLine> lines)
    {
        private readonly List<VariableDefinition> _variables = [];
        private readonly List<RequestBlock> _requests = [];
        private readonly List<ParseDiagnostic> _diagnostics = [];

        private int _index;
        private string? _pendingName;
        private int _pendingNameLine;
        private string? _pendingTitle;
        private int _pendingTitleLine;
        private PendingAuth? _pendingAuth;

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
                var line = lines[_index].Text;

                if (HttpGrammar.IsSeparator(line))
                {
                    // A separator both ends the previous request and titles the next one.
                    _pendingTitle = TitleOf(line);
                    _pendingTitleLine = LineNumber;
                    _pendingName = null;
                    _pendingAuth = null;
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
                else if (HttpGrammar.IsComment(line))
                {
                    _index++;
                }
                else if (HttpGrammar.VariableDefinition.Match(line) is { Success: true } variable)
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
            var (method, target, version) = ReadRequestLine(lines[_index].Text, startLine);
            _index++;

            target += ReadTargetContinuations();

            var headers = ReadHeaders();
            var body = ReadBody();

            WarnIfMultipartUsesBareNewlines(headers, body, startLine);

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
                BuildAuth(),
                Math.Min(_segmentStart, startLine),
                startLine,
                Math.Max(startLine, _index))
            {
                TitleLine = _pendingTitleLine,
            });

            // All five belong to the request just built. Leaving any of them set would
            // silently attach this request's name to the next one, and a chain reference
            // that resolves to the wrong request is worse than one that fails to resolve.
            // The auth block is the sharper case of the same thing: it would send the next
            // request a bearer token it never asked for.
            _pendingName = null;
            _pendingTitle = null;
            _pendingTitleLine = 0;
            _pendingAuth = null;
            _segmentStart = Math.Max(startLine, _index) + 1;
        }

        /// <summary>
        /// Warns when a multipart body's own line endings are LF.
        /// </summary>
        /// <remarks>
        /// <para>
        /// RFC 2046 separates multipart parts with CRLF, and Sling sends a body exactly as
        /// the document holds it - so a repository carrying <c>*.http text eol=lf</c> in
        /// its <c>.gitattributes</c>, or a file written on Linux, produces a body that
        /// lenient servers accept and strict ones reject. That is the same failure
        /// preserving line endings was meant to remove, arriving from the other direction.
        /// </para>
        /// <para>
        /// A warning rather than a rewrite, deliberately. Normalising every terminator
        /// would also rewrite the <em>content</em> of a text part, which is not this
        /// code's to change: a part whose author wanted LF is entitled to it. The request
        /// still sends, and the document says what is odd about it.
        /// </para>
        /// <para>
        /// A <c>Content-Type</c> holding a <c>{{variable}}</c> is left alone rather than
        /// guessed at. Nothing here is resolved yet, and warning on a value that might not
        /// be multipart would be worse than staying quiet.
        /// </para>
        /// </remarks>
        private void WarnIfMultipartUsesBareNewlines(
            List<HeaderField> headers,
            List<BodySegment>? body,
            int line)
        {
            if (body is null)
            {
                return;
            }

            var isMultipart = headers.Any(h =>
                h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                && h.Value.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase));

            if (!isMultipart)
            {
                return;
            }

            var literal = string.Concat(body.OfType<BodyText>().Select(s => s.Value));

            if (literal.Contains('\n', StringComparison.Ordinal)
                && !literal.Contains("\r\n", StringComparison.Ordinal))
            {
                _diagnostics.Add(ParseDiagnostic.Warning(
                    "This multipart body's lines end in LF. RFC 2046 separates parts with CRLF "
                        + "and Sling sends the body exactly as written, so some servers will "
                        + "reject it. Save the file with CRLF endings, or check your .gitattributes.",
                    line));
            }
        }

        /// <summary>
        /// Reports a second request claiming a name an earlier one already has.
        /// </summary>
        /// <remarks>
        /// Left unreported this is the worst kind of defect the format can produce.
        /// <c>BlockNamed</c> returns the first match while the response store is keyed by
        /// name, so a chain's dependency graph points at one request and its substituted
        /// value comes from another - with nothing sent, shown or logged to say so.
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
            // whether the first token looks like a verb, not whether a space exists - a
            // URL can carry a space in a query value.
            var firstToken = space < 0 ? trimmed : trimmed[..space];
            var method = firstToken.ToUpperInvariant();
            var looksLikeMethod = space > 0
                && firstToken.All(char.IsAsciiLetter)
                && (HttpGrammar.IsKnownMethod(method)
                    || firstToken.Equals(method, StringComparison.Ordinal));

            if (!looksLikeMethod)
            {
                // A line holding nothing but a verb reaches here, because the verb test
                // needs a space. Without this it became the request target - surfacing
                // much later, and much less usefully, as "'GET' is not an absolute URL".
                if (space < 0 && HttpGrammar.IsKnownMethod(method))
                {
                    _diagnostics.Add(ParseDiagnostic.Error(
                        $"'{method}' has no request target. Write the method, a space, then the URL.",
                        lineNumber));
                }

                return ("GET", StripVersion(trimmed, out var bareVersion), bareVersion);
            }

            if (!HttpGrammar.IsKnownMethod(method))
            {
                _diagnostics.Add(ParseDiagnostic.Warning(
                    $"'{method}' is not a standard HTTP method. It will be sent as written.",
                    lineNumber));
            }

            // rest is non-empty: trimmed was already trimmed, so a trailing space cannot
            // survive to make it empty. A version-only tail can, though - "GET HTTP/1.1".
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
            if (!tail.StartsWith("HTTP/", StringComparison.Ordinal) || !HttpGrammar.HttpVersion.IsMatch(tail))
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
                var line = lines[_index].Text;

                if (!HttpGrammar.IsTargetContinuation(line))
                {
                    break;
                }

                appended += line.Trim();
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
                var line = lines[_index].Text;

                if (string.IsNullOrWhiteSpace(line) || HttpGrammar.IsSeparator(line))
                {
                    break;
                }

                if (TryReadMetadata(line) || HttpGrammar.IsComment(line))
                {
                    _index++;
                    continue;
                }

                var match = HttpGrammar.Header.Match(line);
                if (!match.Success)
                {
                    _diagnostics.Add(ParseDiagnostic.Error(
                        "Expected a header in the form 'Name: value'. A blank line separates "
                            + "the headers from the body - add one if this line is meant to be body text.",
                        LineNumber));
                    _index++;
                    continue;
                }

                var name = match.Groups[1].Value;

                // A name holding a {{reference}} cannot be checked yet - braces are not
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
        /// <para>
        /// Everything in that span is body text, including lines that begin with <c>#</c>
        /// - a JSON body full of comments would otherwise lose them, and a shell script in
        /// a body would lose most of itself. The one casualty is a body line that begins
        /// with <c>###</c>, which no dialect can represent; that is in the divergence table.
        /// </para>
        /// <para>
        /// Line terminators are preserved exactly as written rather than normalised, which
        /// is what lets a multipart body work: its parts are separated by CRLF by
        /// specification, and rewriting them to LF produces a body that most servers
        /// accept and strict ones reject, with nothing in the document to point at.
        /// </para>
        /// </remarks>
        private List<BodySegment>? ReadBody()
        {
            if (AtEnd || HttpGrammar.IsSeparator(lines[_index].Text))
            {
                return null;
            }

            // Consume the single blank line that ended the headers. Any further blank
            // lines are the body's own leading whitespace and are trimmed below.
            _index++;

            var first = _index;
            while (!AtEnd && !HttpGrammar.IsSeparator(lines[_index].Text))
            {
                _index++;
            }

            var last = _index - 1;

            while (first <= last && string.IsNullOrWhiteSpace(lines[first].Text))
            {
                first++;
            }

            while (last >= first && string.IsNullOrWhiteSpace(lines[last].Text))
            {
                last--;
            }

            return first > last ? null : BuildBody(first, last);
        }

        /// <summary>
        /// Turns the body's line range into literal text and file imports.
        /// </summary>
        /// <remarks>
        /// The terminator of an import's own line is appended <em>after</em> the import,
        /// where it belongs: the newline that follows <c>&lt; ./part.bin</c> separates the
        /// file's bytes from whatever comes next, and folding it into the text before the
        /// import would move a CRLF to the wrong side of a multipart boundary.
        /// </remarks>
        private List<BodySegment> BuildBody(int first, int last)
        {
            var segments = new List<BodySegment>();
            var text = new StringBuilder();

            void FlushText()
            {
                if (text.Length > 0)
                {
                    segments.Add(new BodyText(text.ToString()));
                    text.Clear();
                }
            }

            for (var i = first; i <= last; i++)
            {
                // The last line's own terminator ends the body rather than belonging to
                // it - it is the blank line before the next separator, or end of file.
                var ending = i == last ? string.Empty : lines[i].Ending;

                if (TryReadBodyImport(lines[i].Text, i + 1, out var import))
                {
                    FlushText();
                    segments.Add(import);
                    text.Append(ending);
                    continue;
                }

                text.Append(lines[i].Text).Append(ending);
            }

            FlushText();
            return segments;
        }

        /// <summary>
        /// Recognises <c>&lt; ./file</c>, <c>&lt;@ ./file</c> and <c>&lt;@utf16 ./file</c>.
        /// </summary>
        /// <remarks>
        /// An encoding can only ever accompany the <c>&lt;@</c> form, because the pattern
        /// reaches it through the <c>@</c>. That is the invariant the resolver relies on
        /// when it ignores <see cref="BodyFile.Encoding"/> for a raw import: there is no
        /// input that sets one. The pattern itself is <see cref="HttpGrammar.BodyImport"/>.
        /// </remarks>
        private static bool TryReadBodyImport(string line, int lineNumber, [NotNullWhen(true)] out BodyFile? import)
        {
            import = null;

            var match = HttpGrammar.BodyImport.Match(line);
            if (!match.Success)
            {
                return false;
            }

            import = new BodyFile(
                match.Groups[2].Value.Trim(),
                line.StartsWith("<@", StringComparison.Ordinal),
                match.Groups[1].Success ? match.Groups[1].Value : null,
                lineNumber);

            return true;
        }

        /// <summary>
        /// Recognises a <c># @directive</c> comment. <c>@name</c> is stored; anything else
        /// is reported rather than ignored, so an unsupported directive is visible instead
        /// of silently doing nothing.
        /// </summary>
        private bool TryReadMetadata(string line)
        {
            var match = HttpGrammar.Metadata.Match(line);
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

            if (TryReadAuthDirective(directive, argument))
            {
                return true;
            }

            _diagnostics.Add(ParseDiagnostic.Warning(
                $"'@{directive}' is not supported yet and is being ignored.",
                LineNumber));

            return true;
        }

        /// <summary>
        /// Recognises the <c># @auth oauth2</c> block and the directives that configure it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A divergence from the reference dialect, which has no syntax for this at all,
        /// recorded in <c>docs/http-dialect.md</c>. One directive per parameter rather than
        /// a positional line, because the positional form puts a client id and a client
        /// secret next to each other with nothing but order distinguishing them, and
        /// getting that wrong sends the secret as the id.
        /// </para>
        /// <para>
        /// The configuration directives are recognised only after <c># @auth</c> has been
        /// seen. A <c># @client-secret</c> on its own is a mistake worth reporting rather
        /// than a comment worth ignoring: a document that quietly does not authenticate
        /// fails at the API, several layers from the line that caused it.
        /// </para>
        /// </remarks>
        private bool TryReadAuthDirective(string directive, string argument)
        {
            if (string.Equals(directive, "auth", StringComparison.OrdinalIgnoreCase))
            {
                ReadAuthStart(argument);
                return true;
            }

            if (!AuthDirectives.Contains(directive, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            if (_pendingAuth is null)
            {
                _diagnostics.Add(ParseDiagnostic.Error(
                    $"'@{directive}' only means something under '# @auth oauth2'. Add that line above it.",
                    LineNumber));

                return true;
            }

            if (argument.Length == 0)
            {
                _diagnostics.Add(ParseDiagnostic.Error($"'@{directive}' needs a value.", LineNumber));
                return true;
            }

            ApplyAuthDirective(directive, argument);
            return true;
        }

        private void ReadAuthStart(string argument)
        {
            if (_pendingAuth is not null)
            {
                _diagnostics.Add(ParseDiagnostic.Error(
                    "This request already has an '@auth' block. A request authenticates one way.",
                    LineNumber));

                return;
            }

            if (ReadFlow(argument) is not { } flow)
            {
                _diagnostics.Add(ParseDiagnostic.Error(
                    "'@auth' takes 'oauth2' for the client-credentials grant or 'oauth2-code' for "
                        + "the authorization-code flow. Any other scheme is a header you write "
                        + "yourself.",
                    LineNumber));

                return;
            }

            _pendingAuth = new PendingAuth { Line = LineNumber, Flow = flow };
        }

        /// <summary>
        /// Which flow an <c>@auth</c> argument names, or null when it names none.
        /// </summary>
        /// <remarks>
        /// Several spellings for each, because the RFC's own name for a flow and the word
        /// people reach for are not the same. <c>oauth2</c> alone stays the
        /// client-credentials grant, which is what it has always meant and what every
        /// document already written with it says.
        /// </remarks>
        private static OAuth2Flow? ReadFlow(string argument)
        {
            var words = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == 0)
            {
                return null;
            }

            var first = words[0];

            if (words.Length == 1)
            {
                if (first.Equals("oauth2", StringComparison.OrdinalIgnoreCase))
                {
                    return OAuth2Flow.ClientCredentials;
                }

                return first.Equals("oauth2-code", StringComparison.OrdinalIgnoreCase)
                    ? OAuth2Flow.AuthorizationCode
                    : null;
            }

            if (words.Length != 2 || !first.Equals("oauth2", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (words[1].Equals("client_credentials", StringComparison.OrdinalIgnoreCase))
            {
                return OAuth2Flow.ClientCredentials;
            }

            return words[1].Equals("authorization_code", StringComparison.OrdinalIgnoreCase)
                ? OAuth2Flow.AuthorizationCode
                : null;
        }

        private void ApplyAuthDirective(string directive, string argument)
        {
            var auth = _pendingAuth!;

            switch (directive.ToLowerInvariant())
            {
                case "token-url":
                    auth.TokenUrl = argument;
                    break;

                case "authorize-url":
                    auth.AuthorizeUrl = argument;
                    break;

                case "redirect-uri":
                    auth.RedirectUri = argument;
                    break;

                case "client-id":
                    auth.ClientId = argument;
                    break;

                case "client-secret":
                    auth.ClientSecret = argument;
                    break;

                case "scope":
                    auth.Scope = argument;
                    break;

                case "audience":
                    auth.Audience = argument;
                    break;

                case "client-auth":
                    if (string.Equals(argument, "basic", StringComparison.OrdinalIgnoreCase))
                    {
                        auth.Placement = ClientAuthPlacement.BasicHeader;
                    }
                    else if (string.Equals(argument, "body", StringComparison.OrdinalIgnoreCase))
                    {
                        auth.Placement = ClientAuthPlacement.FormBody;
                    }
                    else
                    {
                        _diagnostics.Add(ParseDiagnostic.Error(
                            $"'@client-auth' takes 'basic' or 'body', not '{argument}'.",
                            LineNumber));
                    }

                    break;

                default:
                    // Unreachable: the caller has already matched against AuthDirectives.
                    // Kept so adding a name to that list without a case here is a build
                    // error rather than a directive that silently does nothing.
                    throw new InvalidOperationException($"'@{directive}' is listed as an auth directive but not handled.");
            }
        }

        /// <summary>
        /// Turns the accumulated <c># @auth</c> directives into a grant, reporting anything
        /// missing.
        /// </summary>
        /// <remarks>
        /// The three required fields are checked here rather than as each line is read,
        /// because a directive that has not been reached yet is not missing. Reporting is
        /// against the <c># @auth</c> line: that is where the block was opened and where a
        /// reader looks to see what it declares.
        /// </remarks>
        private OAuth2Grant? BuildAuth()
        {
            if (_pendingAuth is not { } auth)
            {
                return null;
            }

            var code = auth.Flow == OAuth2Flow.AuthorizationCode;
            var missing = new List<string>();

            if (string.IsNullOrEmpty(auth.TokenUrl))
            {
                missing.Add("@token-url");
            }

            if (string.IsNullOrEmpty(auth.ClientId))
            {
                missing.Add("@client-id");
            }

            // A client secret is required of a confidential client and meaningless for a
            // public one. The code flow with PKCE is what a public client uses, and RFC 7636
            // is what replaces the secret there, so demanding one would refuse the commonest
            // correct configuration - a desktop or single-page client with no secret to keep.
            if (!code && string.IsNullOrEmpty(auth.ClientSecret))
            {
                missing.Add("@client-secret");
            }

            if (code && string.IsNullOrEmpty(auth.AuthorizeUrl))
            {
                missing.Add("@authorize-url");
            }

            // Required rather than defaulted. It has to match what is registered with the
            // identity provider, and a default Sling chose would be a default that works on
            // nobody's account - a silent 'redirect_uri_mismatch' rather than a sentence.
            if (code && string.IsNullOrEmpty(auth.RedirectUri))
            {
                missing.Add("@redirect-uri");
            }

            if (!code && (auth.AuthorizeUrl is not null || auth.RedirectUri is not null))
            {
                _diagnostics.Add(ParseDiagnostic.Error(
                    "'@authorize-url' and '@redirect-uri' belong to '# @auth oauth2-code'. A "
                        + "client-credentials grant has no browser step and needs neither.",
                    auth.Line));

                return null;
            }

            if (missing.Count > 0)
            {
                _diagnostics.Add(ParseDiagnostic.Error(
                    $"This '@auth {(code ? "oauth2-code" : "oauth2")}' block is missing "
                        + string.Join(", ", missing) + ".",
                    auth.Line));

                return null;
            }

            return new OAuth2Grant(
                auth.TokenUrl!,
                auth.ClientId!,
                auth.ClientSecret ?? string.Empty,
                auth.Scope,
                auth.Audience,
                auth.Placement,
                auth.Line)
            {
                Flow = auth.Flow,
                AuthorizeUrl = auth.AuthorizeUrl,
                RedirectUri = auth.RedirectUri,
            };
        }

        /// <summary>
        /// The <c># @auth</c> block being accumulated, before it is complete enough to be
        /// an <see cref="OAuth2Grant"/>.
        /// </summary>
        private sealed class PendingAuth
        {
            public int Line { get; init; }

            public OAuth2Flow Flow { get; init; }

            public string? TokenUrl { get; set; }

            public string? AuthorizeUrl { get; set; }

            public string? RedirectUri { get; set; }

            public string? ClientId { get; set; }

            public string? ClientSecret { get; set; }

            public string? Scope { get; set; }

            public string? Audience { get; set; }

            public ClientAuthPlacement Placement { get; set; } = ClientAuthPlacement.BasicHeader;
        }

        private static string? TitleOf(string separator)
        {
            var title = separator.TrimStart('#').Trim();
            return title.Length == 0 ? null : title;
        }
    }
}
