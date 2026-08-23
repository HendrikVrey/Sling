using System.Text;
using Sling.Core.Parsing;

namespace Sling.Import.Postman;

/// <summary>
/// Builds one <c>.http</c> document, and is the only place a value out of a collection
/// becomes a line of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every method here sanitises what it is given, and that is the design rather than a
/// precaution.</b> M2's review found two blockers in the curl importer that were the same
/// mistake twice: three call sites stripped control characters out of a value and two did
/// not, so a crafted command injected a whole extra request carrying a chained bearer
/// token. The fix that made it unrepeatable was not vigilance at the call sites — it was
/// putting the rule where the value enters the type. This class is that shape applied
/// before the mistake rather than after it: a converter cannot write a raw line, because
/// there is no method that takes one.
/// </para>
/// <para>
/// Comments get a <c>#</c> on <em>every</em> line, not only the first, as a second line of
/// defence behind <see cref="TextSafety.StripControl"/>. One marker per note was exactly
/// the assumption that made the curl injection possible.
/// </para>
/// </remarks>
internal sealed class HttpWriter
{
    /// <summary>How much of a quoted value a note may carry.</summary>
    private const int NoteLimit = 160;

    /// <summary>How long a single comment line may be before it is cut.</summary>
    private const int CommentLimit = 400;

    /// <summary>The column a note is wrapped at, so it reads in an editor that does not wrap.</summary>
    private const int WrapColumn = 88;

    /// <summary>
    /// How much of a dropped script is reproduced as comments.
    /// </summary>
    /// <remarks>
    /// Reproduced at all because a pre-request script is very often the token-fetching
    /// logic, which is the part of a collection its owner most needs to see in order to
    /// rebuild it — and "a script was dropped" without the script is a note that cannot be
    /// acted on. Capped because some of them are hundreds of lines, and a request whose
    /// comment header is longer than the request is not a readable import.
    /// </remarks>
    private const int ScriptLineLimit = 40;

    private readonly StringBuilder _text = new();

    /// <summary>How many things this document could not do.</summary>
    public int NoteCount { get; private set; }

    /// <summary>How much of the document is boilerplate this importer added by itself.</summary>
    private int _boilerplate;

    /// <summary>
    /// True once anything worth keeping has been written — a request, a description, or a
    /// note — but not merely the header naming where the import came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not the same question as "does this hold a request", and the difference was a
    /// silent loss.</b> The normal shape of a real collection is that every request lives
    /// inside a folder, so the root document holds only the collection's description and its
    /// collection-level scripts — the token-fetching one included — and a file emitted only
    /// when it held a request discarded all of that, along with the note count that would
    /// have said so. That broke the promise this importer is built on, in the case that
    /// happens most.
    /// </para>
    /// <para>
    /// The provenance header does not count, which is what <see cref="MarkBoilerplate"/> is
    /// for. Otherwise every collection whose requests all live in folders would get a file
    /// holding two comment lines and nothing else.
    /// </para>
    /// </remarks>
    public bool HasContent => _text.Length > _boilerplate;

    /// <summary>Records that everything written so far is this importer's own preamble.</summary>
    public void MarkBoilerplate() => _boilerplate = _text.Length;

    /// <summary>
    /// Opens a request with its <c>###</c> separator, carrying the Postman name as the
    /// title.
    /// </summary>
    /// <remarks>
    /// A title and not a <c># @name</c>. <c>@name</c> is the handle chain references use,
    /// it must be unique across the file, and nothing generated here chains — Postman
    /// expresses that dependency in a script, which this importer deliberately does not
    /// translate. Emitting one per request would put a wall of names in the document and
    /// make two requests Postman is happy to let share a name an error in the result.
    /// </remarks>
    public void StartRequest(string? postmanName)
    {
        if (_text.Length > 0)
        {
            _text.Append('\n');
        }

        var title = TextSafety.StripControl(postmanName ?? string.Empty).Trim();

        _text.Append("###");

        if (title.Length > 0)
        {
            _text.Append(' ').Append(TextSafety.Cap(title, NoteLimit));
        }

        _text.Append('\n');
    }

    /// <summary>
    /// Writes documentation from the collection — a folder or request description.
    /// </summary>
    public void Comment(string? text)
    {
        var clean = TextSafety.StripControl(text ?? string.Empty, keepLineBreaks: true).TrimEnd();

        if (clean.Length == 0)
        {
            return;
        }

        foreach (var line in clean.Split('\n'))
        {
            var trimmed = line.TrimEnd();

            if (trimmed.Length == 0)
            {
                _text.Append("#\n");
                continue;
            }

            _text.Append(WouldReadAsADirective(trimmed) ? "# > " : "# ")
                .Append(TextSafety.Cap(trimmed, CommentLimit))
                .Append('\n');
        }
    }

    /// <summary>
    /// Whether a comment line would be read back as a <c># @directive</c> rather than as
    /// prose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the Postman-shaped version of the curl note injection, and it is worse.</b>
    /// A description is markdown a collection's author wrote, and a line of it beginning
    /// <c>@</c> comes back through <see cref="Comment"/> as <c># @something</c> — which the
    /// parser reads as a directive, not as text. <c>@name</c> is the one that matters:
    /// a crafted description could name the request it sits above, and a second request's
    /// <c>Authorization: Bearer {{that-name.response.body.$.token}}</c> would then send a
    /// token fetched from the real API to whatever host the second request points at. The
    /// leading whitespace does not help — the pattern the parser matches allows any amount
    /// of it between the <c>#</c> and the <c>@</c>.
    /// </para>
    /// <para>
    /// Quoted with <c>&gt;</c> rather than dropped or rewritten. The text stays readable and
    /// stays the author's, and a non-whitespace character between the marker and the
    /// <c>@</c> is all it takes to make the line prose again.
    /// </para>
    /// </remarks>
    private static bool WouldReadAsADirective(string line) => line.TrimStart().StartsWith('@');

    /// <summary>
    /// Writes something the conversion could not do, and counts it.
    /// </summary>
    /// <remarks>
    /// Written into the document rather than only reported, because that is where the
    /// person who has to act on it will be. A silent drop turns an import into a request
    /// that looks right and behaves differently, which is worse than an import that
    /// visibly did not finish.
    /// </remarks>
    public void Note(string text)
    {
        Comment(Wrap(TextSafety.StripControl(text)));
        NoteCount++;
    }

    /// <summary>
    /// Breaks a note into lines that fit in an editor.
    /// </summary>
    /// <remarks>
    /// Applied to notes only, never to a description. A note is this project's own prose and
    /// re-flowing it costs nothing; a description is the collection author's, and rewrapping
    /// somebody's markdown silently reformats their documentation. A word longer than the
    /// column simply overruns — breaking a URL in half to fit would be worse than a long
    /// line.
    /// </remarks>
    private static string Wrap(string text)
    {
        var wrapped = new StringBuilder(text.Length + 16);
        var column = 0;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (column > 0 && column + 1 + word.Length > WrapColumn)
            {
                wrapped.Append('\n');
                column = 0;
            }
            else if (column > 0)
            {
                wrapped.Append(' ');
                column++;
            }

            wrapped.Append(word);
            column += word.Length;
        }

        return wrapped.ToString();
    }

    /// <summary>
    /// Writes a <c># @directive</c> line — deliberately, unlike <see cref="Comment"/>, which
    /// quotes one out of the way.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole point of having two methods. A directive changes what
    /// the request <em>does</em>, so only text this project composed may become one; text
    /// out of the collection goes through <see cref="Comment"/> and is prevented from
    /// becoming one. The values interpolated into a directive here are still stripped, for
    /// the ordinary reason: a newline inside <c>@scope</c> would end the directive and start
    /// a line of live document text.
    /// </remarks>
    public void Directive(string text) =>
        _text.Append("# ").Append(TextSafety.Cap(TextSafety.StripControl(text), CommentLimit)).Append('\n');

    /// <summary>Quotes a value inside a note, capped and stripped.</summary>
    public static string Describe(string? value) =>
        TextSafety.Cap(TextSafety.StripControl(value ?? string.Empty), NoteLimit);

    /// <summary>
    /// Reproduces a dropped script as comments.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here is ever executed</b> (<c>Sling.md</c> §5.8). It is copied into the
    /// document as text so its author can see what they have to rebuild, and every line of
    /// it goes through <see cref="Comment"/> — a script is the most obviously hostile thing
    /// in a collection, and a line of it escaping the comment would be a request the
    /// document did not appear to contain.
    /// </remarks>
    public void Script(string kind, string source) =>
        Excerpt(
            $"Postman ran a {kind} script here. Sling does not run scripts, so it was not "
                + "imported. It is reproduced below so you can see what it did.",
            source);

    /// <summary>
    /// Notes something, then reproduces the text it is about as indented comments.
    /// </summary>
    /// <remarks>
    /// The shape both "a script was dropped" and "this body could not be written" need: the
    /// user has to see the content in order to act on it, and the content must not become
    /// part of the document. Every line goes through <see cref="Comment"/>, so a
    /// <c>###</c> or a <c>@name</c> inside it is neutralised the same way it is everywhere
    /// else.
    /// </remarks>
    public void Excerpt(string note, string text)
    {
        var lines = TextSafety.StripControl(text, keepLineBreaks: true)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(l => l.TrimEnd())
            .SkipWhile(string.IsNullOrEmpty)
            .ToList();

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            return;
        }

        Note(note);

        foreach (var line in lines.Take(ScriptLineLimit))
        {
            Comment("    " + line);
        }

        if (lines.Count > ScriptLineLimit)
        {
            Comment($"    … and {lines.Count - ScriptLineLimit} more lines.");
        }
    }

    /// <summary>Writes the request line.</summary>
    /// <remarks>
    /// The target keeps only what a request target may hold, which is stricter than
    /// dropping control characters: whitespace <em>ends</em> a target, so a space smuggled
    /// into one turns the remainder into an HTTP version token and changes which URL is
    /// contacted. That is the rule <see cref="HttpSyntax.IsLegalRequestTargetChar"/> states
    /// for a substituted value, asked here of an imported one. The caller checks and notes
    /// first; this is the layer that cannot be forgotten.
    /// </remarks>
    public void RequestLine(string method, string target)
    {
        var cleanMethod = new string([.. method.Where(HttpSyntax.IsTokenChar)]);
        var cleanTarget = new string([.. target.Where(HttpSyntax.IsLegalRequestTargetChar)]);

        _text.Append(cleanMethod.Length == 0 ? "GET" : cleanMethod.ToUpperInvariant())
            .Append(' ')
            .Append(cleanTarget)
            .Append('\n');
    }

    /// <summary>
    /// Writes a header, or reports why it could not be written.
    /// </summary>
    /// <returns>False when the name was not a legal header name, so the caller can note it.</returns>
    public bool Header(string name, string? value)
    {
        // Checked as written, NOT after stripping — that ordering was the bug. Stripping
        // first turned "Y\nZ" into the token "YZ", which passed the check and wrote a header
        // the collection never described. IsToken already rejects every control character,
        // so checking first is both stricter and simpler.
        var cleanName = (name ?? string.Empty).Trim();

        if (!HttpSyntax.IsToken(cleanName))
        {
            return false;
        }

        var given = TextSafety.StripControl(value ?? string.Empty);
        var cleanValue = new string([.. given.Where(HttpSyntax.IsLegalHeaderValueChar)]);

        // Said out loud, for the reason TargetBuilder says it about a URL: a header value
        // that quietly loses a character is a different header, and "the API rejects it" is
        // a long way from the export that caused it.
        if (cleanValue.Length != (value ?? string.Empty).Length)
        {
            Note(
                $"The value of '{cleanName}' held characters a header cannot carry, and they "
                    + "were removed. Check the line below before sending it.");
        }

        _text.Append(cleanName).Append(": ").Append(cleanValue.Trim()).Append('\n');

        return true;
    }

    /// <summary>
    /// Writes the body, after the blank line that separates it from the headers.
    /// </summary>
    /// <param name="text">
    /// The body exactly as it should be sent. Line breaks survive — a body is terminated by
    /// end-of-request rather than by a delimiter it could contain, so a newline in one is
    /// content. A caller that needs CRLF framing writes the carriage returns itself and
    /// they survive too: the parser keeps each line's own terminator, which is what makes a
    /// multipart body sendable.
    /// </param>
    public void Body(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        _text.Append('\n').Append(text);

        if (!text.EndsWith('\n'))
        {
            _text.Append('\n');
        }
    }

    /// <summary>
    /// Whether <paramref name="body"/> holds a line that would split the document.
    /// </summary>
    /// <remarks>
    /// <c>###</c> at the start of a line separates requests in this format — in the
    /// reference dialect too, so it is a limitation of <c>.http</c> rather than of Sling —
    /// and nothing can escape it. A body carrying one is named rather than quietly
    /// corrupted into two requests, the second of them nonsense.
    /// <para>
    /// Split on <c>\r</c> as well as <c>\n</c>, because the parser treats a lone carriage
    /// return as a line terminator too. Splitting on <c>\n</c> alone left
    /// <c>"payload\r### injected"</c> looking like one line that merely contains a hash
    /// run — and it is the exact body a crafted collection would use.
    /// </para>
    /// </remarks>
    public static bool WouldSplitTheDocument(string body) =>
        body.Split('\n', '\r').Any(line => line.StartsWith("###", StringComparison.Ordinal));

    public override string ToString() => _text.ToString();
}
