using System.Text;

namespace Sling.Import.Curl;

/// <summary>
/// The request being assembled from a curl command, and every rule about what a flag
/// means once it has been recognised.
/// </summary>
/// <remarks>
/// Separate from <see cref="CurlImport"/> so that the flag table there reads as a table.
/// The interesting decisions — what curl's defaults are, and what must not be allowed
/// through — live here.
/// </remarks>
internal sealed class CurlRequest
{
    /// <summary>How much of a quoted value a note may carry.</summary>
    private const int NoteLimit = 160;

    private readonly List<string> _headers = [];
    private readonly List<string> _notes = [];
    private readonly List<string> _data = [];

    private string? _method;
    private bool _sawContentType;

    /// <summary>Whether the URL in hand arrived carrying a scheme.</summary>
    private bool _urlHadScheme;

    /// <summary>Whether the URL in hand came from <c>--url</c> rather than from a bare argument.</summary>
    private bool _urlIsExplicit;

    internal string? Url { get; private set; }

    internal IReadOnlyList<string> Headers => _headers;

    internal IReadOnlyList<string> Notes => _notes;

    /// <summary>Set by <c>-G</c>: the accumulated data goes into the query string instead.</summary>
    internal bool DataBecomesQuery { get; set; }

    /// <summary>
    /// The method actually sent.
    /// </summary>
    /// <remarks>
    /// curl's rule, not an invention: an explicit <c>-X</c> always wins, a body without
    /// one implies POST, and everything else is GET. Reproducing it exactly matters more
    /// than reproducing it simply — a copied command that silently becomes a GET is a
    /// request that looks identical and does nothing.
    /// </remarks>
    internal string Method =>
        _method ?? (_data.Count > 0 && !DataBecomesQuery ? "POST" : "GET");

    /// <summary>The request body, or null when there is none.</summary>
    internal string? Body
    {
        get
        {
            if (_data.Count == 0 || DataBecomesQuery)
            {
                return null;
            }

            // curl joins repeated -d arguments with '&'. It is not a formatting choice —
            // it is how two -d flags become one form-encoded body.
            return string.Join('&', _data);
        }
    }

    /// <summary>
    /// Records something the conversion could not do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sanitising happens here, at the one place every note goes through, and that is
    /// the entire point.</b> Notes are written into the output as <c>#</c> comments, and
    /// several of them quote a value taken straight off the command line. A value carrying
    /// a newline turns one comment into a comment followed by *live document text* — a
    /// crafted command could add a whole extra request, complete with an
    /// <c>Authorization: Bearer {{…}}</c> header, and the caret would resolve to it. That
    /// was a real defect, found in review, and it existed because three call sites
    /// remembered to strip and two did not.
    /// </para>
    /// <para>
    /// An invariant that holds at three call sites out of five is not an invariant.
    /// Putting it in <see cref="Note"/> means a note added later cannot reintroduce it,
    /// and <c>CurlImport.Write</c> comments every line of a note as a second line of
    /// defence rather than trusting this one.
    /// </para>
    /// <para>
    /// Capped, too: the value being quoted is often a body, and a note containing a
    /// megabyte of JSON is not a note.
    /// </para>
    /// </remarks>
    internal void Note(string note)
    {
        var clean = StripControl(note);

        _notes.Add(clean.Length <= NoteLimit ? clean : string.Concat(clean.AsSpan(0, NoteLimit), "…"));
    }

    internal void SetMethod(string method)
    {
        var clean = StripControl(method).Trim();

        if (clean.Length == 0)
        {
            Note("-X was given an empty method and was ignored.");
            return;
        }

        _method = clean.ToUpperInvariant();
    }

    /// <summary>Sets the URL, from <c>--url</c>, which is always authoritative.</summary>
    internal void SetUrl(string url) => Accept(url, explicitFlag: true);

    /// <summary>
    /// Offers a bare argument as the URL, keeping whichever candidate is the better one.
    /// </summary>
    /// <returns>False when the offer was rejected, so the caller can name what it dropped.</returns>
    /// <remarks>
    /// <para>
    /// "Better" means carrying a scheme, and the rule exists because an unknown flag's
    /// value arrives here looking exactly like a bare argument. Sling does not step over
    /// the value of a flag it does not recognise — guessing wrong there eats the URL — so
    /// <c>curl --some-flag json https://api.example.com</c> offers <c>json</c> first and
    /// the real URL second. Preferring the one with a scheme gets that right.
    /// </para>
    /// <para>
    /// Between two candidates that both carry a scheme the first wins, which is curl's own
    /// order; and an explicit <c>--url</c> is never displaced by a bare argument, because
    /// the user said which one they meant.
    /// </para>
    /// </remarks>
    internal bool OfferUrl(string url)
    {
        if (Url is null)
        {
            Accept(url, explicitFlag: false);
            return Url is not null;
        }

        if (_urlIsExplicit || _urlHadScheme || !LooksLikeAUrl(url))
        {
            return false;
        }

        // The candidate in hand has a scheme and the one already held does not. Name the
        // displaced one rather than letting it vanish — it was almost certainly the value
        // of a flag Sling did not recognise, and the note above it says which.
        Note($"Treated as the URL instead: {url}. The earlier bare argument was dropped.");

        Accept(url, explicitFlag: false);

        return true;
    }

    /// <summary>
    /// Whether a candidate is an absolute URL, as opposed to merely containing the
    /// characters <c>://</c> somewhere inside it.
    /// </summary>
    /// <remarks>
    /// A substring test is not enough, and the difference is the whole of a bug found in
    /// review. An argument carrying injected line breaks was collapsed by
    /// <see cref="StripControl"/> into one long token that happened to contain the
    /// attacker's <c>https://</c> — so it counted as "has a scheme", and the *real* URL
    /// that followed it was rejected as a duplicate. Parsing it as a URI instead answers
    /// the question actually being asked.
    /// </remarks>
    /// <remarks>
    /// The <c>://</c> requirement is not redundant beside the parse. <c>Uri.TryCreate</c>
    /// accepts <c>C:\tools\thing</c> as an absolute <c>file:</c> URI and <c>mailto:x</c>
    /// as an absolute <c>mailto:</c> one — neither is a request target, and treating
    /// either as "already has a scheme" would skip the <c>https://</c> that makes a bare
    /// host sendable.
    /// </remarks>
    private static bool LooksLikeAUrl(string candidate) =>
        candidate.Contains("://", StringComparison.Ordinal)
        && !candidate.Any(char.IsWhiteSpace)
        && Uri.TryCreate(candidate, UriKind.Absolute, out _);

    private void Accept(string url, bool explicitFlag)
    {
        // Refused outright rather than stripped, and this is the one value where that is
        // the right trade. Stripping a newline out of a header value leaves a wrong
        // header; stripping one out of a URL silently decides *which host is contacted*,
        // by welding two lines into an address that resembles neither. An argument with a
        // line break in it is not a URL, so it is named and set aside — which also lets
        // the next candidate, usually the real one, be taken.
        if (url.Any(char.IsControl))
        {
            Note("An argument containing line breaks was not treated as a URL.");
            return;
        }

        var clean = url.Trim();

        if (clean.Length == 0)
        {
            return;
        }

        _urlHadScheme = LooksLikeAUrl(clean);

        // curl accepts a scheme-less URL and assumes http. Sling's parser requires one, so
        // the assumption is made here and made visible, rather than producing a request
        // that will not send for a reason the user did not cause. https rather than http
        // because defaulting a credential-carrying tool to cleartext would be indefensible
        // — and saying so is what makes it correctable.
        if (!_urlHadScheme)
        {
            Note("No scheme was given, so https:// was assumed. curl would have used http://.");
            clean = "https://" + clean;
        }

        Url = clean;
        _urlIsExplicit = explicitFlag;
    }

    internal void AddHeader(string header)
    {
        var clean = StripControl(header);
        var colon = clean.IndexOf(':', StringComparison.Ordinal);

        if (colon <= 0)
        {
            // curl uses `-H 'Name;'` to send an empty header and `-H 'Name:'` to remove
            // one it would otherwise add. Neither has a .http equivalent worth inventing.
            Note($"A header without a value was dropped: {clean}");
            return;
        }

        var name = clean[..colon].Trim();
        var value = clean[(colon + 1)..].Trim();

        if (name.Length == 0)
        {
            Note("A header with no name was dropped.");
            return;
        }

        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            _sawContentType = true;
        }

        _headers.Add($"{name}: {value}");
    }

    /// <summary>
    /// Accumulates a <c>-d</c> payload.
    /// </summary>
    /// <remarks>
    /// Unlike every other value here, a body keeps its line breaks. A body is terminated
    /// by a blank line and then by end-of-request rather than by a delimiter, so a newline
    /// inside one is content and not structure — stripping them would silently rewrite a
    /// pretty-printed JSON payload into one long line, which changes the bytes sent. The
    /// one structural sequence a body *can* still collide with is handled in
    /// <see cref="Finish"/>.
    /// </remarks>
    internal void AddData(string data) => _data.Add(StripControl(data, keepLineBreaks: true));

    /// <summary>
    /// <c>-d</c>, <c>--data</c>, <c>--data-ascii</c> and <c>--data-binary</c>, all of
    /// which read from a file when the value begins with <c>@</c>.
    /// </summary>
    /// <remarks>
    /// The importer does no I/O — it is a pure text-to-text function, which is what makes
    /// it testable against a corpus — so a file reference is named rather than followed.
    /// <c>-</c> after the <c>@</c> means standard input, which has even less meaning here.
    /// </remarks>
    internal void AddDataOrFile(string data)
    {
        var clean = StripControl(data, keepLineBreaks: true);

        if (!clean.StartsWith('@'))
        {
            _data.Add(clean);
            return;
        }

        Note(clean == "@-"
            ? "The body was read from standard input, which was dropped."
            // Naming the equivalent is worth the extra clause: '< ./file' is a real
            // construct Sling sends, so the note tells the reader what to type rather than
            // only what they lost.
            : $"The body was read from a file, which was dropped: {clean[1..]}. "
                + $"Write it as '< ./{clean[1..]}' if the file is inside the workspace.");
    }

    /// <summary>
    /// <c>--data-urlencode</c>, which has four forms and only one of them is plain content.
    /// </summary>
    /// <remarks>
    /// <c>name=value</c> encodes the value and keeps the name; <c>=value</c> encodes the
    /// whole thing; <c>name@file</c> and <c>@file</c> read from disk, which this importer
    /// does not do. Getting the encoding boundary wrong is invisible until a value happens
    /// to contain an ampersand, at which point the request means something else.
    /// </remarks>
    internal void AddUrlEncodedData(string data)
    {
        var clean = StripControl(data);

        if (clean.Contains('@', StringComparison.Ordinal)
            && clean.IndexOf('=', StringComparison.Ordinal) is var eq
            && (eq < 0 || clean.IndexOf('@', StringComparison.Ordinal) < eq))
        {
            Note($"--data-urlencode read from a file, which was dropped: {clean}");
            return;
        }

        var split = clean.IndexOf('=', StringComparison.Ordinal);

        _data.Add(split switch
        {
            < 0 => Uri.EscapeDataString(clean),
            0 => Uri.EscapeDataString(clean[1..]),
            _ => clean[..split] + "=" + Uri.EscapeDataString(clean[(split + 1)..]),
        });
    }

    internal void SetCookie(string cookie)
    {
        var clean = StripControl(cookie);

        // `-b file` loads a cookie jar; `-b 'a=1; b=2'` sends a header. The '=' is what
        // tells them apart, and it is curl's own test.
        if (!clean.Contains('=', StringComparison.Ordinal))
        {
            Note($"A cookie file was referenced and dropped: {clean}");
            return;
        }

        _headers.Add($"Cookie: {clean}");
    }

    /// <summary>
    /// <c>-u user:pass</c>, which becomes a Basic <c>Authorization</c> header.
    /// </summary>
    /// <remarks>
    /// Converted rather than dropped — a request that silently loses its credentials fails
    /// with a 401 the user then debugs — but converted loudly. The whole premise of the
    /// <c>.http</c> format is that the file gets committed, and this writes a working
    /// credential into it (<c>Sling.md</c> §5.1). Saying so at the point it happens is the
    /// only honest option until the secrets file exists in M3.
    /// </remarks>
    internal void SetBasicAuth(string credentials)
    {
        var clean = StripControl(credentials);

        if (clean.Length == 0)
        {
            return;
        }

        // curl prompts for a password when none is given. There is nothing to prompt with
        // here, and an empty password is a legal, meaningful value, so it is encoded as
        // given rather than guessed at.
        _headers.Add($"Authorization: Basic {CurlImport.Base64(clean)}");

        Note(
            "-u became an Authorization header below. THAT IS A CREDENTIAL IN A FILE that is "
                + "meant to be committed — move it out before you commit this.");
    }

    internal void AddFormPart(string part)
    {
        var clean = StripControl(part);

        Note(
            $"A multipart form field was dropped, because multipart bodies arrive in M3: {clean}");
    }

    /// <summary>
    /// Called once everything has been applied, to add what curl would have added.
    /// </summary>
    internal void Finish()
    {
        if (DataBecomesQuery && _data.Count > 0 && Url is { } url)
        {
            // Stripped again, and NOT redundantly. Accept() strips the URL as it arrives,
            // but the -G fold happens afterwards and builds the query out of _data — which
            // deliberately keeps its line breaks, because the same values are a body in
            // every other case. Trusting the earlier strip let a newline back into the
            // request line, where the parser reads what follows as real headers on a
            // request aimed at the legitimate host. Quieter than the note-injection defect
            // beside it, because it produced no notes at all.
            var query = StripControl(string.Join('&', _data));

            Url = url.Contains('?', StringComparison.Ordinal)
                ? $"{url}&{query}"
                : $"{url}?{query}";
        }

        // curl sends this by default with -d and does not mention it. Writing it out makes
        // the document say what will actually go on the wire, which is the entire reason
        // the request is a document.
        if (Body is { Length: > 0 } body)
        {
            if (!_sawContentType)
            {
                _headers.Add("Content-Type: application/x-www-form-urlencoded");
            }

            // The one sequence a body cannot contain. '###' at the start of a line
            // separates requests in this format — in the reference dialect too, so it is a
            // limitation of .http rather than of Sling — and a body carrying one would be
            // read back as two requests, the second of them nonsense. Nothing can be
            // escaped away, so it is named instead of being quietly corrupted.
            if (StartsARequestSeparator(body))
            {
                Note(
                    "The body contains a line starting with ### , which separates requests in "
                        + "a .http file. This document will not read back the way it was written; "
                        + "the body needs editing by hand.");
            }
        }
    }

    private static bool StartsARequestSeparator(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            if (line.TrimStart().StartsWith("###", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes CR, LF and other control characters from a value taken out of the command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a security boundary, not tidying.</b> The output is a newline-delimited
    /// document: a header value containing a line break would become an additional header
    /// line, and a URL containing one would end the request line early. A curl command is
    /// untrusted input — it comes from a chat message or a web page as often as from the
    /// user's own shell history — so a crafted one must not be able to write structure
    /// into the file the user is about to trust and send.
    /// </para>
    /// <para>
    /// Stripped rather than rejected, and rejected rather than escaped: there is no escape
    /// mechanism in a header field, and refusing the whole import over one stray character
    /// would be a worse outcome than importing the value without it. The removal is
    /// invisible in the output by design — the value is simply the value, minus characters
    /// that could never have been part of it.
    /// </para>
    /// </remarks>
    internal static string StripControl(string value, bool keepLineBreaks = false)
    {
        // Tab survives everywhere: it is legal inside a header value and appears in real
        // ones. A line feed survives only in a body, where it is content rather than
        // structure. A carriage return never survives — the document is written with \n
        // line endings, and a lone \r inside a value is invisible and confusing.
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
}
