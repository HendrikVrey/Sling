using System.Text;

namespace Sling.Import.Curl;

/// <summary>
/// The result of converting one curl command line.
/// </summary>
/// <param name="Http">
/// The <c>.http</c> text, with every note already written into it as a comment.
/// </param>
/// <param name="Notes">
/// The same notes, separately, so the UI can say how many there were without parsing the
/// text it just produced.
/// </param>
/// <param name="Recognized">
/// False when the input did not look like a curl command at all, in which case
/// <paramref name="Http"/> is empty and the caller should leave what the user pasted
/// alone.
/// </param>
public sealed record CurlImportResult(string Http, IReadOnlyList<string> Notes, bool Recognized)
{
    /// <summary>Nothing recognisable.</summary>
    public static CurlImportResult NotCurl { get; } = new(string.Empty, [], Recognized: false);
}

/// <summary>
/// Turns a curl command line into a <c>.http</c> request.
/// </summary>
/// <remarks>
/// <para>
/// The escape hatch for every request not yet migrated (<c>Sling.md</c> §4b). Postman
/// copies as curl, browsers copy as curl, documentation is written in curl - so this is
/// the shortest path from anywhere into Sling, and it is worth more than its size
/// suggests.
/// </para>
/// <para>
/// <b>Nothing is dropped silently.</b> Anything this cannot express becomes a comment in
/// the output naming what was lost, the same rule the Postman importer will follow. A
/// silent drop turns an import into a request that looks right and behaves differently,
/// which is worse than an import that visibly did not finish.
/// </para>
/// <para>
/// <b>Security.</b> A pasted command is untrusted input - it comes from a chat message or
/// a web page as often as from the user's own history. Two rules follow. Nothing is ever
/// executed: this is a quoting parser and a table of flags, with no shell and no file
/// access. And no value taken from the command may carry a CR or LF into the generated
/// document, because the <c>.http</c> format is newline-delimited and a header value
/// containing a newline would silently become an extra header - an injection into the
/// artifact, one layer above the request-splitting the resolver already refuses.
/// </para>
/// </remarks>
public static class CurlImport
{
    /// <summary>
    /// Flags that change nothing about the request Sling would send.
    /// </summary>
    /// <remarks>
    /// Accepted and ignored without a note, deliberately. They are overwhelmingly the
    /// most common flags in a copied command, and a note for each would bury the ones
    /// that matter under noise about <c>-s</c>. Each is here because Sling's behaviour
    /// already matches it: output goes to a pane rather than a file, headers are always
    /// shown, redirects are always followed, and the handler always negotiates and decodes
    /// compression.
    /// </remarks>
    private static readonly HashSet<string> Ignorable = new(StringComparer.Ordinal)
    {
        "-s", "--silent", "-S", "--show-error", "-v", "--verbose", "-i", "--include",
        "-L", "--location", "--compressed", "-f", "--fail", "--fail-with-body",
        "-#", "--progress-bar", "--no-progress-meter", "-N", "--no-buffer",
        "-g", "--globoff", "--no-keepalive", "-Z", "--parallel",
    };

    /// <summary>Flags that take a value Sling has no use for, so both are stepped over.</summary>
    private static readonly HashSet<string> IgnorableWithValue = new(StringComparer.Ordinal)
    {
        "-o", "--output", "-w", "--write-out", "--max-time", "-m",
        "--connect-timeout", "--retry", "--limit-rate",
    };

    /// <summary>
    /// Converts a curl command line, or reports that it was not one.
    /// </summary>
    /// <param name="commandLine">The pasted text.</param>
    public static CurlImportResult Convert(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return CurlImportResult.NotCurl;
        }

        var tokens = CurlTokenizer.Tokenize(commandLine);

        if (tokens.Count == 0 || !IsCurl(tokens[0]))
        {
            return CurlImportResult.NotCurl;
        }

        var request = new CurlRequest();

        for (var i = 1; i < tokens.Count; i++)
        {
            i = Apply(request, tokens, i);
        }

        // Everything curl does implicitly - the -G query fold, the default Content-Type,
        // the body/separator collision - is applied here, after every flag has been seen.
        // Doing it as the flags arrive would make the answer depend on their order.
        request.Finish();

        if (request.Url is not null)
        {
            return new CurlImportResult(Write(request), request.Notes, Recognized: true);
        }

        // Kept, not replaced. An earlier version returned only "no URL", which threw away
        // the note explaining *why* there was none - and the commonest reason is that an
        // argument was refused as a URL for carrying line breaks. Answering "no URL" while
        // discarding the sentence that says what happened to it is the least useful thing
        // this could do.
        request.Note("The command has no URL.");

        return new CurlImportResult(string.Empty, request.Notes, Recognized: true);
    }

    /// <summary>
    /// Whether the first token names curl.
    /// </summary>
    /// <remarks>
    /// Matched against the file name rather than the whole token, because a pasted command
    /// is as likely to say <c>/usr/bin/curl</c> or <c>curl.exe</c> as <c>curl</c>. Ordinal
    /// and case-insensitive: the casing varies with the platform it was copied from.
    /// </remarks>
    private static bool IsCurl(string token)
    {
        var slash = token.LastIndexOfAny(['/', '\\']);
        var name = slash < 0 ? token : token[(slash + 1)..];

        return name.Equals("curl", StringComparison.OrdinalIgnoreCase)
            || name.Equals("curl.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Applies the token at <paramref name="i"/>, returning the index it consumed to.</summary>
    private static int Apply(CurlRequest request, List<string> tokens, int i)
    {
        var token = tokens[i];

        // A bare argument is the URL. curl accepts several and fetches each; Sling writes
        // one request, so the rest are named rather than dropped in silence.
        if (!token.StartsWith('-') || token == "-")
        {
            if (!request.OfferUrl(token))
            {
                request.Note($"A second URL was ignored: {Describe(token)}");
            }

            return i;
        }

        // --flag=value is as common as --flag value in copied commands, and curl accepts
        // both. Split here so the table below only has to know one shape.
        var equals = token.StartsWith("--", StringComparison.Ordinal)
            ? token.IndexOf('=', StringComparison.Ordinal)
            : -1;

        string? inlineValue = null;

        if (equals > 0)
        {
            inlineValue = token[(equals + 1)..];
            token = token[..equals];
        }

        if (Ignorable.Contains(token))
        {
            return i;
        }

        if (IgnorableWithValue.Contains(token))
        {
            return inlineValue is null ? i + 1 : i;
        }

        switch (token)
        {
            case "-X" or "--request":
                return Take(tokens, i, inlineValue, request, static (r, v) => r.SetMethod(v));

            case "-H" or "--header":
                return Take(tokens, i, inlineValue, request, static (r, v) => r.AddHeader(v));

            // --data-raw is the one form that does NOT read from a file when the value
            // starts with '@'. Every other spelling does, and treating them alike turned
            // `-d @payload.json` - one of the commonest shapes in API documentation - into
            // a POST whose body was the literal thirteen characters of the file name, with
            // no note saying so.
            case "--data-raw":
                return Take(tokens, i, inlineValue, request, static (r, v) => r.AddData(v));

            case "-d" or "--data" or "--data-ascii" or "--data-binary":
                return Take(tokens, i, inlineValue, request, static (r, v) => r.AddDataOrFile(v));

            case "--data-urlencode":
                return Take(tokens, i, inlineValue, request, static (r, v) => r.AddUrlEncodedData(v));

            case "-G" or "--get":
                request.DataBecomesQuery = true;
                return i;

            case "-I" or "--head":
                request.SetMethod("HEAD");
                return i;

            case "-u" or "--user":
                return Take(tokens, i, inlineValue, request, static (r, v) => r.SetBasicAuth(v));

            case "-A" or "--user-agent":
                return Take(tokens, i, inlineValue, request, static (r, v) => r.AddHeader($"User-Agent: {v}"));

            case "-e" or "--referer":
                return Take(tokens, i, inlineValue, request, static (r, v) => r.AddHeader($"Referer: {v}"));

            case "-b" or "--cookie":
                return Take(tokens, i, inlineValue, request, static (r, v) => r.SetCookie(v));

            case "--url":
                return Take(tokens, i, inlineValue, request, static (r, v) => r.SetUrl(v));

            case "-F" or "--form" or "--form-string":
                return Take(tokens, i, inlineValue, request, static (r, v) => r.AddFormPart(v));

            case "-k" or "--insecure":
                request.Note(
                    "--insecure was NOT applied. Sling verifies TLS certificates and has no "
                        + "global way to turn that off; if this endpoint genuinely needs it, the "
                        + "request will fail and say so.");
                return i;

            case "-x" or "--proxy":
                request.Note("A proxy was specified. Sling does not support proxies yet, and it was dropped.");
                return inlineValue is null ? i + 1 : i;

            default:
                request.Note($"{token} is not supported and was dropped.");

                // Deliberately does NOT step over the next token, even though an unknown
                // flag may well take one. Guessing that it does eats the URL, which is the
                // one thing the import cannot do without; guessing that it does not leaves
                // a stray value to be considered as a URL, and OfferUrl prefers a
                // candidate that actually carries a scheme. The failure mode of not
                // consuming is recoverable and visible; the other one is neither.
                return i;
        }
    }

    /// <summary>
    /// Feeds a flag's value to <paramref name="apply"/>, whether it arrived inline or as
    /// the next token.
    /// </summary>
    private static int Take(
        List<string> tokens,
        int i,
        string? inlineValue,
        CurlRequest request,
        Action<CurlRequest, string> apply)
    {
        if (inlineValue is not null)
        {
            apply(request, inlineValue);
            return i;
        }

        if (i + 1 >= tokens.Count)
        {
            request.Note($"{tokens[i]} was given no value and was dropped.");
            return i;
        }

        apply(request, tokens[i + 1]);
        return i + 1;
    }

    private static string Write(CurlRequest request)
    {
        var text = new StringBuilder();

        // Every line of a note gets its own '#', not just the first. CurlRequest.Note
        // already strips the control characters that could produce a second line, so this
        // is the second line of defence rather than the first - but a note is written into
        // a live document, and one comment marker per note was exactly the assumption that
        // made a crafted command able to inject a whole extra request.
        foreach (var note in request.Notes)
        {
            foreach (var line in note.Split('\n'))
            {
                text.Append("# ").Append(line).Append('\n');
            }
        }

        if (request.Notes.Count > 0)
        {
            text.Append('\n');
        }

        text.Append(request.Method).Append(' ').Append(request.Url).Append('\n');

        foreach (var header in request.Headers)
        {
            text.Append(header).Append('\n');
        }

        if (request.Body is { Length: > 0 } body)
        {
            text.Append('\n').Append(body).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Quotes a value for a diagnostic, with a length cap.
    /// </summary>
    /// <remarks>
    /// Capped because the value being described is frequently a body, and a note that
    /// contains a megabyte of JSON is not a note. Control characters are stripped for the
    /// same reason they are stripped everywhere else here: this string is about to be
    /// written into a line-oriented document as a comment.
    /// </remarks>
    private static string Describe(string value)
    {
        var clean = CurlRequest.StripControl(value);

        return clean.Length <= 60
            ? clean
            : string.Concat(clean.AsSpan(0, 60), "…");
    }

    internal static string Base64(string value) =>
        System.Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
