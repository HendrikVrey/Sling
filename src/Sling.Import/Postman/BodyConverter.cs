using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sling.Import.Postman;

/// <summary>The body a request should carry, and the type Postman would have sent with it.</summary>
/// <param name="ImpliedContentType">
/// Null when the mode says nothing about the type. Written out only when the request has no
/// <c>Content-Type</c> header of its own — the document should say what goes on the wire,
/// which is the entire reason a request is a document, but not by overriding what the
/// collection stated.
/// </param>
internal sealed record BodyPlan(string Text, string? ImpliedContentType);

/// <summary>
/// Converts Postman's five body modes.
/// </summary>
internal static class BodyConverter
{
    /// <summary>
    /// The stem of the multipart boundary.
    /// </summary>
    /// <remarks>
    /// Fixed rather than random, and that is deliberate: an imported file goes into a
    /// repository, and a boundary that changed on every import would make every re-import a
    /// diff. <see cref="FreeBoundary"/> lengthens it if any part's content happens to
    /// contain it, which is the only property a boundary actually has to have.
    /// </remarks>
    private const string BoundaryStem = "SlingFormBoundary";

    /// <summary>How many times a colliding boundary may be lengthened before giving up.</summary>
    private const int MaxBoundaryAttempts = 8;

    public static BodyPlan? Convert(PostmanBody? body, HttpWriter writer)
    {
        if (body is null || body.Mode.Length == 0)
        {
            return null;
        }

        return body.Mode.ToLowerInvariant() switch
        {
            "raw" => Raw(body),
            "urlencoded" => UrlEncoded(body),
            "formdata" => FormData(body, writer),
            "file" => FromFile(body, writer),
            "graphql" => GraphQl(body),
            _ => Unsupported(body, writer),
        };
    }

    private static BodyPlan? Raw(PostmanBody body)
    {
        if (string.IsNullOrEmpty(body.Raw))
        {
            return null;
        }

        return new BodyPlan(body.Raw, TypeFor(body.RawLanguage));
    }

    /// <summary>
    /// The <c>Content-Type</c> Postman derives from the raw body's language setting.
    /// </summary>
    /// <remarks>
    /// This is not a guess about the content — it is what Postman puts on the wire for each
    /// setting of that dropdown, and a request imported without it is a request the server
    /// sees differently. An unrecognised language says nothing rather than defaulting to
    /// text, because a wrong <c>Content-Type</c> is worse than none.
    /// </remarks>
    private static string? TypeFor(string? language) => language?.ToLowerInvariant() switch
    {
        "json" => "application/json",
        "xml" => "application/xml",
        "html" => "text/html",
        "javascript" => "application/javascript",
        "text" => "text/plain",
        _ => null,
    };

    private static BodyPlan? UrlEncoded(PostmanBody body)
    {
        var fields = body.UrlEncoded.Where(p => p.Key.Length > 0).ToList();

        if (fields.Count == 0)
        {
            return null;
        }

        // The name is encoded as well as the value. A field called 'a b' is legal in
        // Postman's grid and illegal on the wire, and encoding only the value is the kind of
        // asymmetry that goes unnoticed until one collection has a space in a field name.
        var text = string.Join(
            '&',
            fields.Select(f => Uri.EscapeDataString(f.Key) + "=" + Uri.EscapeDataString(f.Value ?? string.Empty)));

        return new BodyPlan(text, "application/x-www-form-urlencoded");
    }

    /// <summary>
    /// Writes a multipart body out in full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>.http</c> format has no multipart syntax, so a multipart body <em>is</em> the
    /// body written out with a <c>&lt; ./file</c> standing in for each file part — which is
    /// exactly what M3 built the import line for. Nothing else here is possible: Postman's
    /// form grid is a description of a body, and the format only carries the body.
    /// </para>
    /// <para>
    /// <b>CRLF, not LF, and it is a specification rather than a preference.</b> RFC 2046
    /// separates parts with CRLF, and a multipart body written with LF is accepted by most
    /// servers and refused by strict ones with nothing in the document to point at. The
    /// parser keeps each line's own terminator, which is what lets a CRLF body live inside a
    /// document whose other lines end in LF.
    /// </para>
    /// <para>
    /// A file part's path is reduced to its bare file name. The collection carries the
    /// exporter's own absolute path — <c>/Users/someone/Desktop/avatar.png</c> — which does
    /// not exist here, would not be inside the workspace, and would be refused by the
    /// containment rule if it were written out. Taking the last segment removes the
    /// traversal question entirely rather than answering it.
    /// </para>
    /// </remarks>
    private static BodyPlan? FormData(PostmanBody body, HttpWriter writer)
    {
        var parts = body.FormData.Where(p => p.Key.Length > 0).ToList();

        if (parts.Count == 0)
        {
            return null;
        }

        if (FreeBoundary(parts) is not { } boundary)
        {
            writer.Note(
                "The form fields all contain every separator this could use, so there is no "
                    + "boundary that would keep the parts apart. The multipart body was left out "
                    + "— half a multipart body is worse than none.");

            return null;
        }

        var text = new StringBuilder();
        var files = 0;

        foreach (var part in parts)
        {
            var name = Quotable(part.Key);

            text.Append("--").Append(boundary).Append("\r\n");
            text.Append("Content-Disposition: form-data; name=\"").Append(name).Append('"');

            if (part.IsFile)
            {
                var file = FileReference(part.Source);

                if (file is null)
                {
                    writer.Note(
                        $"The form field '{HttpWriter.Describe(part.Key)}' attaches a file whose "
                            + "name could not be used. The whole multipart body was left out — "
                            + "half a multipart body is worse than none.");

                    return null;
                }

                files++;

                text.Append("; filename=\"").Append(file).Append('"').Append("\r\n");

                if (!string.IsNullOrEmpty(part.ContentType))
                {
                    text.Append("Content-Type: ").Append(Quotable(part.ContentType)).Append("\r\n");
                }

                text.Append("\r\n");

                // The import's own terminator is emitted after the file's bytes, so this CRLF
                // ends up on the correct side of the next boundary.
                text.Append("< ./").Append(file).Append("\r\n");

                if (part.HadMoreSources)
                {
                    writer.Note(
                        $"The form field '{HttpWriter.Describe(part.Key)}' attached several files. "
                            + $"Only '{file}' was imported — each file needs a part of its own, and "
                            + "writing the rest would send a body the collection never described.");
                }

                continue;
            }

            if (!string.IsNullOrEmpty(part.ContentType))
            {
                text.Append("\r\nContent-Type: ").Append(Quotable(part.ContentType));
            }

            text.Append("\r\n\r\n");
            text.Append(Crlf(part.Value ?? string.Empty)).Append("\r\n");
        }

        text.Append("--").Append(boundary).Append("--\r\n");

        if (files > 0)
        {
            writer.Note(
                $"This request attaches {files.ToString(CultureInfo.InvariantCulture)} file"
                    + (files == 1 ? string.Empty : "s")
                    + ". Put "
                    + (files == 1 ? "it" : "them")
                    + " beside this .http file — a body import may only read files inside the "
                    + "workspace, and the collection's own paths point at the machine it was "
                    + "exported from.");
        }

        return new BodyPlan(text.ToString(), "multipart/form-data; boundary=" + boundary);
    }

    private static BodyPlan? FromFile(PostmanBody body, HttpWriter writer)
    {
        var file = FileReference(body.FileSource);

        if (file is null)
        {
            writer.Note("The body was a file whose name the collection did not carry, so it was dropped.");
            return null;
        }

        writer.Note(
            $"The body is imported from '{file}'. Put that file beside this .http file — a body "
                + "import may only read files inside the workspace, and the collection's path "
                + "points at the machine it was exported from.");

        return new BodyPlan("< ./" + file, null);
    }

    /// <summary>
    /// A GraphQL body, which goes on the wire as JSON.
    /// </summary>
    /// <remarks>
    /// Built with <see cref="Utf8JsonWriter"/> rather than by concatenation, because the
    /// query is arbitrary text with quotes and newlines in it and hand-escaping that is how
    /// a body stops being JSON. The variables come out of the collection as a JSON
    /// <em>string</em>; it is embedded as real JSON when it parses and as a string when it
    /// does not, which keeps a half-written variables block visible instead of failing the
    /// whole import.
    /// </remarks>
    private static BodyPlan? GraphQl(PostmanBody body)
    {
        if (string.IsNullOrEmpty(body.GraphQlQuery) && string.IsNullOrEmpty(body.GraphQlVariables))
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();

        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("query", body.GraphQlQuery ?? string.Empty);

            if (!string.IsNullOrEmpty(body.GraphQlVariables))
            {
                json.WritePropertyName("variables");

                if (IsJson(body.GraphQlVariables))
                {
                    json.WriteRawValue(body.GraphQlVariables);
                }
                else
                {
                    json.WriteStringValue(body.GraphQlVariables);
                }
            }

            json.WriteEndObject();
        }

        return new BodyPlan(Encoding.UTF8.GetString(buffer.WrittenSpan), "application/json");
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // JsonDocument.Parse transcodes to UTF-8 first, so a lone surrogate raises this
            // rather than a JsonException. Untrusted text reaches here, so both are real.
            return false;
        }
    }

    private static BodyPlan? Unsupported(PostmanBody body, HttpWriter writer)
    {
        writer.Note(
            $"The body used Postman's '{HttpWriter.Describe(body.Mode)}' mode, which this "
                + "importer does not know, so the request below has no body.");

        return null;
    }

    /// <summary>
    /// A boundary no part's content contains.
    /// </summary>
    /// <remarks>
    /// A boundary appearing inside a part splits the body at the wrong place, and the server
    /// reads the remainder as another part.
    /// <para>
    /// <b>Bounded, because the collection chooses the content and the search was quadratic
    /// in it.</b> Lengthening by one character per collision, rescanning every part each
    /// time, meant a part whose value was the stem followed by a million <c>x</c>s — under a
    /// megabyte of input, well inside the export size cap — took over three minutes, with
    /// the ceiling at days. So: try the plain stem, then a content-derived fingerprint, then
    /// a small fixed number of lengthenings, then give up and say so. A body that cannot be
    /// given a boundary is a body that cannot be sent, and refusing it beats hanging.
    /// </para>
    /// <para>
    /// Only the text parts can be checked. A file part's content is not in the document and
    /// is not read here — this project does no I/O — so a file that happens to contain the
    /// boundary would still split the body. Stated rather than pretended away; the same
    /// limitation applies to any hand-written multipart body in this format.
    /// </para>
    /// </remarks>
    private static string? FreeBoundary(IReadOnlyList<PostmanFormPart> parts)
    {
        if (!Collides(parts, BoundaryStem))
        {
            return BoundaryStem;
        }

        // Content-derived rather than random, so importing the same collection twice produces
        // the same file and a re-import is not a diff.
        var boundary = BoundaryStem + "-" + Fingerprint(parts);

        for (var attempt = 0; attempt < MaxBoundaryAttempts; attempt++)
        {
            if (!Collides(parts, boundary))
            {
                return boundary;
            }

            boundary += "x";
        }

        return null;
    }

    private static bool Collides(IReadOnlyList<PostmanFormPart> parts, string boundary) =>
        parts.Any(p => (p.Value ?? string.Empty).Contains(boundary, StringComparison.Ordinal));

    /// <summary>
    /// A short hash of everything the parts contain, so the boundary depends on the content
    /// rather than on how many times a loop has run.
    /// </summary>
    /// <remarks>
    /// FNV-1a, and it does not need to be a cryptographic hash: it is not standing between
    /// anybody and anything, it only has to be a value the author of the content did not
    /// think to include. The bounded retry above is what makes even that unnecessary to
    /// argue about.
    /// </remarks>
    private static string Fingerprint(IReadOnlyList<PostmanFormPart> parts)
    {
        var hash = 14695981039346656037UL;

        foreach (var part in parts)
        {
            foreach (var c in part.Key + " " + (part.Value ?? string.Empty) + " ")
            {
                hash = (hash ^ c) * 1099511628211UL;
            }
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Makes a value safe to sit inside a quoted multipart parameter.
    /// </summary>
    /// <remarks>
    /// A quote or a line break inside <c>name="…"</c> ends the parameter and lets the rest
    /// be read as more of the part's headers — the same injection this importer refuses
    /// everywhere else, one format further in. Removed rather than backslash-escaped: RFC
    /// 7578 §4.2 points at a quoting rule that servers implement inconsistently, so a name
    /// that needs escaping is a name worth losing a character from.
    /// </remarks>
    private static string Quotable(string value) =>
        new string([.. TextSafety.StripControl(value).Where(c => c is not ('"' or '\\'))]);

    /// <summary>Rewrites a part's own line endings to CRLF.</summary>
    private static string Crlf(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>
    /// The bare file name a <c>&lt; ./file</c> import may point at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last path segment and nothing else, so traversal is not a question that has to be
    /// answered: <c>../../../etc/passwd</c> becomes <c>passwd</c>. Split on both separators
    /// because a collection exported on macOS carries <c>/</c> and one exported on Windows
    /// carries <c>\</c>, and the file may be imported on either.
    /// </para>
    /// <para>
    /// Braces are removed with everything else outside the whitelist, and that one matters
    /// on its own: an import path is variable-expanded, so a file called
    /// <c>{{access_token}}.json</c> would make which file is read depend on a secret.
    /// A leading dot is dropped so the name cannot be <c>..</c> or a dotfile.
    /// </para>
    /// </remarks>
    private static string? FileReference(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        var slash = source.LastIndexOfAny(['/', '\\']);
        var name = slash < 0 ? source : source[(slash + 1)..];

        // Runes, not chars: char.IsLetterOrDigit is false for both halves of every surrogate
        // pair, so a file named in an astral script would filter away to nothing and the part
        // would be dropped. That is the same predicate that once deleted the ideograph out of
        // Etch's word splitter.
        var kept = new StringBuilder(name.Length);

        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune) || rune.Value is '.' or '-' or '_')
            {
                kept.Append(rune.ToString());
            }
        }

        var safe = kept.ToString().Trim('.');

        while (safe.Contains("..", StringComparison.Ordinal))
        {
            safe = safe.Replace("..", ".", StringComparison.Ordinal);
        }

        return safe.Length == 0 ? null : safe;
    }
}
