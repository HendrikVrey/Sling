namespace Sling.Core.Parsing;

/// <summary>What one run of characters in a <c>.http</c> document is.</summary>
/// <remarks>
/// Grammar elements, not colours. What each one is drawn in is the editor's business, and
/// naming these after the palette would put a UI decision in the one project that is not
/// allowed to have any.
/// </remarks>
public enum HttpTokenKind
{
    /// <summary>A <c>#</c> or <c>//</c> comment, and the <c>###</c> of a separator.</summary>
    Comment,

    /// <summary>The words after a <c>###</c>, which name the request below it.</summary>
    Title,

    /// <summary>The <c>@name</c> of a <c># @name login</c> metadata line.</summary>
    Directive,

    /// <summary>The argument of a metadata line: <c>login</c> in <c># @name login</c>.</summary>
    DirectiveValue,

    /// <summary>The <c>@base</c> of a <c>@base = https://…</c> document variable.</summary>
    VariableName,

    /// <summary>The <c>=</c> of a variable and the <c>:</c> of a header.</summary>
    Operator,

    /// <summary>The verb of a request line.</summary>
    Method,

    /// <summary>The request target, and the indented continuations that extend it.</summary>
    Target,

    /// <summary>The <c>HTTP/1.1</c> tail of a request line.</summary>
    Version,

    /// <summary>The name of a header field.</summary>
    HeaderName,

    /// <summary>The value of a header field.</summary>
    HeaderValue,

    /// <summary>The <c>&lt;</c>, <c>&lt;@</c> or <c>&lt;@utf16</c> of a body import.</summary>
    ImportMarker,

    /// <summary>The path a body import reads.</summary>
    ImportPath,

    /// <summary>A <c>{{name}}</c> reference, wherever it appears.</summary>
    Reference,
}

/// <summary>
/// One run of characters, by offset into the line it was found on.
/// </summary>
/// <param name="Start">Zero-based offset into the line, not into the document.</param>
public readonly record struct HttpToken(int Start, int Length, HttpTokenKind Kind);

/// <summary>
/// Says what every line of a <c>.http</c> document is, and where its parts are, so an
/// editor can colour it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not an AvalonEdit grammar.</b> A <c>.xshd</c> definition matches patterns
/// against a line with only a span stack for memory, and this format's meaning is not
/// decidable that way: whether <c>{"total": 3}</c> is a header or body text depends on
/// whether a blank line has been seen since the request line above it, arbitrarily far up.
/// A regex grammar paints that JSON as a header called <c>{"total"</c> - wrong, and wrong in
/// the pane where somebody is looking for the mistake in their request.
/// </para>
/// <para>
/// <b>Why it lives here rather than beside the editor.</b> It is grammar, and the grammar
/// has one home - <see cref="HttpGrammar"/>, which
/// <see cref="RequestDocumentParser"/> reads from too. The walk below deliberately mirrors
/// the parser's own, state for state, because the failure it is guarding against is the
/// editor drawing a line as one thing while the parser sends it as another.
/// </para>
/// <para>
/// The whole document at once, rather than a line at a time: the state a line is read in
/// comes from every line above it, so there is nothing cheaper available. It is linear, it
/// allocates a list per line that carries tokens, and the caller caches the result against a
/// document version rather than calling it per redraw.
/// </para>
/// </remarks>
public static class HttpLineClassifier
{
    private static readonly IReadOnlyList<HttpToken> NoTokens = [];

    /// <summary>
    /// Classifies every line of <paramref name="lines"/>.
    /// </summary>
    /// <param name="lines">
    /// The document's lines, without terminators, in order. Splitting them is the caller's
    /// job because the caller - an editor - already has them split, and re-splitting the
    /// text would risk numbering the lines differently from the control that shows them.
    /// </param>
    /// <returns>
    /// One entry per line, holding that line's tokens in order and without overlaps. A line
    /// with nothing worth colouring gets an empty list rather than a null.
    /// </returns>
    public static IReadOnlyList<IReadOnlyList<HttpToken>> Classify(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var result = new IReadOnlyList<HttpToken>[lines.Count];
        Array.Fill(result, NoTokens);

        var index = 0;

        while (index < lines.Count)
        {
            var line = lines[index];

            if (HttpGrammar.IsSeparator(line))
            {
                result[index] = Separator(line);
                index++;
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                index++;
            }
            else if (Metadata(line) is { } metadata)
            {
                result[index] = metadata;
                index++;
            }
            else if (HttpGrammar.IsComment(line))
            {
                result[index] = Comment(line);
                index++;
            }
            else if (Variable(line) is { } variable)
            {
                result[index] = variable;
                index++;
            }
            else
            {
                index = Request(lines, index, result);
            }
        }

        return result;
    }

    /// <summary>
    /// Classifies one request: its request line, the continuations, the headers and the
    /// body, and returns the index of the line that ended it.
    /// </summary>
    private static int Request(
        IReadOnlyList<string> lines,
        int index,
        IReadOnlyList<HttpToken>[] result)
    {
        result[index] = RequestLine(lines[index]);
        index++;

        while (index < lines.Count && HttpGrammar.IsTargetContinuation(lines[index]))
        {
            result[index] = Trimmed(lines[index], HttpTokenKind.Target);
            index++;
        }

        index = Headers(lines, index, result);

        // A separator - or the end of the file - ends the request with no body at all. The
        // blank line the header loop stopped on is the body's opener and belongs to neither.
        if (index >= lines.Count || HttpGrammar.IsSeparator(lines[index]))
        {
            return index;
        }

        index++;

        while (index < lines.Count && !HttpGrammar.IsSeparator(lines[index]))
        {
            result[index] = BodyLine(lines[index]) ?? NoTokens;
            index++;
        }

        return index;
    }

    private static int Headers(
        IReadOnlyList<string> lines,
        int index,
        IReadOnlyList<HttpToken>[] result)
    {
        while (index < lines.Count)
        {
            var line = lines[index];

            if (string.IsNullOrWhiteSpace(line) || HttpGrammar.IsSeparator(line))
            {
                return index;
            }

            if (Metadata(line) is { } metadata)
            {
                result[index] = metadata;
            }
            else if (HttpGrammar.IsComment(line))
            {
                result[index] = Comment(line);
            }
            else if (HttpGrammar.Header.Match(line) is { Success: true } header)
            {
                result[index] = Header(line, header.Groups[1].Length);
            }

            // Anything else is the line the parser reports as "expected a header". It is
            // left uncoloured deliberately: the diagnostic says what is wrong with it, and
            // painting it as one of the things it is not would argue with that.
            index++;
        }

        return index;
    }

    private static List<HttpToken> Separator(string line)
    {
        var hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
        {
            hashes++;
        }

        var titleStart = hashes;
        while (titleStart < line.Length && char.IsWhiteSpace(line[titleStart]))
        {
            titleStart++;
        }

        var titleEnd = line.Length;
        while (titleEnd > titleStart && char.IsWhiteSpace(line[titleEnd - 1]))
        {
            titleEnd--;
        }

        List<HttpToken> tokens = [new HttpToken(0, hashes, HttpTokenKind.Comment)];

        if (titleEnd > titleStart)
        {
            tokens.Add(new HttpToken(titleStart, titleEnd - titleStart, HttpTokenKind.Title));
        }

        return tokens;
    }

    /// <summary>
    /// A <c># @name login</c> line, or null when this is not one.
    /// </summary>
    /// <remarks>
    /// The leading <c>#</c> stays a comment: it is what makes the line invisible to every
    /// other tool that reads this format, and colouring it as part of the directive would
    /// suggest the directive is the syntax rather than a convention riding inside a comment.
    /// </remarks>
    private static List<HttpToken>? Metadata(string line)
    {
        if (HttpGrammar.Metadata.Match(line) is not { Success: true } match)
        {
            return null;
        }

        var name = match.Groups[1];

        // The '@' sits immediately before the directive name, and the pattern anchors the
        // name so the character before it is always that '@'.
        var directiveStart = name.Index - 1;

        List<HttpToken> tokens =
        [
            new HttpToken(0, directiveStart, HttpTokenKind.Comment),
            new HttpToken(directiveStart, name.Length + 1, HttpTokenKind.Directive),
        ];

        var argument = match.Groups[2];

        if (argument.Success && argument.Length > 0)
        {
            AddWithReferences(tokens, line, argument.Index, argument.Length, HttpTokenKind.DirectiveValue);
        }

        return tokens;
    }

    /// <summary>A <c>@base = https://…</c> line, or null when this is not one.</summary>
    private static List<HttpToken>? Variable(string line)
    {
        if (HttpGrammar.VariableDefinition.Match(line) is not { Success: true } match)
        {
            return null;
        }

        var name = match.Groups[1];
        var value = match.Groups[2];

        List<HttpToken> tokens =
        [
            new HttpToken(name.Index - 1, name.Length + 1, HttpTokenKind.VariableName),
            new HttpToken(line.IndexOf('=', name.Index + name.Length), 1, HttpTokenKind.Operator),
        ];

        if (value.Length > 0)
        {
            AddWithReferences(tokens, line, value.Index, value.Length, HttpTokenKind.HeaderValue);
        }

        return tokens;
    }

    private static List<HttpToken> RequestLine(string line)
    {
        var parts = HttpGrammar.SplitRequestLine(line);
        var tokens = new List<HttpToken>();

        if (parts.MethodLength > 0)
        {
            tokens.Add(new HttpToken(parts.MethodStart, parts.MethodLength, HttpTokenKind.Method));
        }

        if (parts.TargetLength > 0)
        {
            AddWithReferences(tokens, line, parts.TargetStart, parts.TargetLength, HttpTokenKind.Target);
        }

        if (parts.VersionLength > 0)
        {
            tokens.Add(new HttpToken(parts.VersionStart, parts.VersionLength, HttpTokenKind.Version));
        }

        return tokens;
    }

    private static List<HttpToken> Header(string line, int nameLength)
    {
        var colon = line.IndexOf(':', nameLength);

        List<HttpToken> tokens =
        [
            new HttpToken(0, nameLength, HttpTokenKind.HeaderName),
            new HttpToken(colon, 1, HttpTokenKind.Operator),
        ];

        var valueStart = colon + 1;
        while (valueStart < line.Length && char.IsWhiteSpace(line[valueStart]))
        {
            valueStart++;
        }

        if (valueStart < line.Length)
        {
            AddWithReferences(tokens, line, valueStart, line.Length - valueStart, HttpTokenKind.HeaderValue);
        }

        return tokens;
    }

    /// <summary>
    /// A body line: a file import, or plain text with its references picked out.
    /// </summary>
    /// <remarks>
    /// Body text itself is left in the editor's own foreground rather than given a colour.
    /// A body is JSON, XML, a form or a binary part, and Sling has no idea which until it is
    /// sent - inventing a colour for all of them would make one of them look wrong.
    /// </remarks>
    private static List<HttpToken>? BodyLine(string line)
    {
        if (HttpGrammar.BodyImport.Match(line) is { Success: true } import)
        {
            var path = import.Groups[2];

            List<HttpToken> tokens =
            [
                // Everything before the path is the marker: '<', '<@' or '<@utf16'.
                new HttpToken(0, MarkerLength(line), HttpTokenKind.ImportMarker),
            ];

            AddWithReferences(tokens, line, path.Index, path.Length, HttpTokenKind.ImportPath);

            return tokens;
        }

        var references = new List<HttpToken>();

        AddReferencesOnly(references, line, 0, line.Length);

        // Null rather than an empty list. Most lines of most bodies hold no reference
        // at all, and a list per one of them is an allocation per line per redraw.
        return references.Count == 0 ? null : references;
    }

    /// <summary>How many characters of an import line belong to its <c>&lt;</c> marker.</summary>
    private static int MarkerLength(string line)
    {
        var length = 1;

        while (length < line.Length && line[length] is not (' ' or '\t'))
        {
            length++;
        }

        return length;
    }

    /// <summary>A whole comment line, which by construction is never empty.</summary>
    private static List<HttpToken> Comment(string line) =>
        [new HttpToken(0, line.Length, HttpTokenKind.Comment)];

    private static List<HttpToken> Trimmed(string line, HttpTokenKind kind)
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

        var tokens = new List<HttpToken>();

        AddWithReferences(tokens, line, start, end - start, kind);

        return tokens;
    }

    /// <summary>
    /// Adds a span as <paramref name="kind"/>, split around any <c>{{references}}</c> in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split rather than layered. Tokens come out of this class ordered and non-overlapping,
    /// which means the editor can apply them in one pass without a rule about which one wins
    /// - and a rule about which one wins is exactly where a highlighter gets a run wrong at
    /// one zoom level and right at another.
    /// </para>
    /// <para>
    /// An unclosed <c>{{</c> leaves the rest of the span as it was. The parser reports it,
    /// and highlighting the tail as a reference would make a typo look like the thing it is
    /// not.
    /// </para>
    /// </remarks>
    private static void AddWithReferences(
        List<HttpToken> tokens,
        string line,
        int start,
        int length,
        HttpTokenKind kind)
    {
        if (length <= 0)
        {
            return;
        }

        var end = start + length;
        var position = start;

        while (position < end)
        {
            var opening = line.IndexOf("{{", position, end - position, StringComparison.Ordinal);

            if (opening < 0)
            {
                break;
            }

            var closing = line.IndexOf("}}", opening + 2, end - opening - 2, StringComparison.Ordinal);

            if (closing < 0)
            {
                break;
            }

            if (opening > position)
            {
                tokens.Add(new HttpToken(position, opening - position, kind));
            }

            tokens.Add(new HttpToken(opening, closing + 2 - opening, HttpTokenKind.Reference));
            position = closing + 2;
        }

        if (position < end)
        {
            tokens.Add(new HttpToken(position, end - position, kind));
        }
    }

    /// <summary>
    /// Adds only the <c>{{references}}</c> in a span, leaving everything else uncoloured.
    /// </summary>
    private static void AddReferencesOnly(List<HttpToken> tokens, string line, int start, int length)
    {
        var end = start + length;
        var position = start;

        while (position < end)
        {
            var opening = line.IndexOf("{{", position, end - position, StringComparison.Ordinal);

            if (opening < 0)
            {
                return;
            }

            var closing = line.IndexOf("}}", opening + 2, end - opening - 2, StringComparison.Ordinal);

            if (closing < 0)
            {
                return;
            }

            tokens.Add(new HttpToken(opening, closing + 2 - opening, HttpTokenKind.Reference));
            position = closing + 2;
        }
    }
}
