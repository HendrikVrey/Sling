using System.Diagnostics.CodeAnalysis;
using System.Text;
using Sling.Core.Auth;
using Sling.Core.Documents;
using Sling.Core.Parsing;

namespace Sling.Core.Variables;

/// <summary>
/// Turns a parsed <see cref="RequestBlock"/> into a <see cref="ResolvedRequest"/>: every
/// variable substituted, every chained value checked, and the target validated as an
/// absolute HTTP URL.
/// </summary>
/// <remarks>
/// The only route from a document to something sendable. Nothing in <c>Sling.Http</c>
/// accepts a request that has not been through here, which is what makes the injection
/// rules in <see cref="VariableExpander"/> impossible to bypass at a call site.
/// </remarks>
public static class RequestResolver
{
    /// <summary>
    /// The cap on an assembled body.
    /// </summary>
    /// <remarks>
    /// Per-file limits do not bound this on their own: a multipart body may import many
    /// files, and a document is free to import the same one repeatedly. The limit is
    /// generous because uploading a large file is a thing people legitimately do with an
    /// HTTP client, and it exists because "the process died" is a worse answer than a
    /// sentence saying which line went too far.
    /// </remarks>
    private const long MaxBodyBytes = 128L * 1024 * 1024;

    /// <summary>
    /// How body text becomes bytes. Explicitly BOM-free: the encoder's singleton emits
    /// one, and a byte order mark at the head of a JSON body makes servers reject it as
    /// malformed.
    /// </summary>
    private static readonly UTF8Encoding BodyEncoding = new(encoderShouldEmitUTF8Identifier: false);

    public static ResolutionResult Resolve(
        RequestDocument document,
        RequestBlock request,
        ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var expander = new VariableExpander(document.Variables, context);
        var errors = new List<ParseDiagnostic>();

        var target = expander.Expand(request.Target, request.StartLine, FieldKind.Target);
        var headers = ResolveHeaders(request, expander, errors);

        // Expanded here rather than after the checks below, so a grant field holding a
        // chain reference joins the same missing-response pass every other field uses.
        var grant = SubstituteAuth(request.Auth, expander);

        // Substitution first, with no file opened. A body import whose path still holds an
        // unresolved {{reference}} would otherwise be reported as a missing file, burying
        // the real cause under a symptom - the same reason the URL is checked last.
        var body = SubstituteBody(request, expander);

        // The disk is touched only once substitution has come out clean, so a request whose
        // variables do not resolve never opens a file. Not a blanket promise: the URL is
        // validated afterwards, so a request with an unusable URL does read its imports
        // first - worth the ordering, because reporting a missing file for a path that is
        // still {{braced}} would bury the cause under a symptom.
        byte[]? bytes = null;
        var canRead = errors.Count == 0
            && expander.Errors.Count == 0
            && expander.MissingResponses.Count == 0;

        if (canRead)
        {
            TryAssembleBody(body, context.Files, expander, request.StartLine, errors, out bytes);
        }

        errors.AddRange(expander.Errors);

        // Missing responses can arrive from inside an imported file as easily as from the
        // request line, and they are still not errors: the runner sends the dependency and
        // resolves again, which re-reads the file. Re-reading is the honest behaviour
        // anyway - the file may have changed in between.
        if (errors.Count > 0 || expander.MissingResponses.Count > 0)
        {
            return new ResolutionResult(null, expander.MissingResponses, errors);
        }

        if (!TryBuildUrl(target, request.Target, request.StartLine, errors, out var url))
        {
            return new ResolutionResult(null, [], errors);
        }

        ResolvedOAuth2Grant? auth = null;
        if (grant is not null && !TryBuildGrant(request.Auth!, grant, errors, out auth))
        {
            return new ResolutionResult(null, [], errors);
        }

        return new ResolutionResult(
            new ResolvedRequest(request.Name, request.Method, url, headers, bytes, request.Version, auth),
            [],
            []);
    }

    /// <summary>
    /// Substitutes the grant's fields, leaving validation for
    /// <see cref="TryBuildGrant"/>.
    /// </summary>
    /// <remarks>
    /// The token URL is expanded as a target - same character rules, and the same
    /// percent-encoding of any value that came from a response, which is what stops a
    /// chained value retargeting the token request at a host of its choosing.
    /// <para>
    /// Everything else is expanded as a body field, meaning no character restrictions. That
    /// is safe because a client id and secret only ever reach the wire percent-encoded, in
    /// a form field or inside a base64 Basic credential - there is no syntax left for them
    /// to break. Restricting characters instead would refuse perfectly ordinary secrets.
    /// </para>
    /// </remarks>
    private static OAuth2Grant? SubstituteAuth(OAuth2Grant? grant, VariableExpander expander)
    {
        if (grant is null)
        {
            return null;
        }

        return grant with
        {
            TokenUrl = expander.Expand(grant.TokenUrl, grant.Line, FieldKind.Target),
            ClientId = expander.Expand(grant.ClientId, grant.Line, FieldKind.Body),
            ClientSecret = expander.Expand(grant.ClientSecret, grant.Line, FieldKind.Body),
            Scope = grant.Scope is null ? null : expander.Expand(grant.Scope, grant.Line, FieldKind.Body),
            Audience = grant.Audience is null ? null : expander.Expand(grant.Audience, grant.Line, FieldKind.Body),
        };
    }

    /// <summary>
    /// Validates a substituted grant and turns its token endpoint into a
    /// <see cref="Uri"/>.
    /// </summary>
    /// <param name="asWritten">
    /// The grant with its values still braced, which is what any diagnostic quotes. The
    /// substituted grant holds a client secret by definition.
    /// </param>
    private static bool TryBuildGrant(
        OAuth2Grant asWritten,
        OAuth2Grant resolved,
        List<ParseDiagnostic> errors,
        out ResolvedOAuth2Grant? grant)
    {
        grant = null;

        if (!Uri.TryCreate(resolved.TokenUrl, UriKind.Absolute, out var tokenUrl))
        {
            errors.Add(ParseDiagnostic.Error(
                $"'@token-url {asWritten.TokenUrl}' is not an absolute URL.",
                asWritten.Line));
            return false;
        }

        if (tokenUrl.UserInfo.Length > 0)
        {
            errors.Add(ParseDiagnostic.Error(
                "A token URL may not carry a username or password before the host. Put the "
                    + "client credentials in '@client-id' and '@client-secret'.",
                asWritten.Line));
            return false;
        }

        // HTTPS, or a loopback address. A client secret and the token it buys are the two
        // most valuable strings in the process, and sending them over plain HTTP puts both
        // on the wire in clear.
        //
        // This check covers one hop, which is why the token request refuses to be
        // redirected - see ResolvedRequest.FollowRedirects. Checking here and following a
        // 307 would make the rule about where the secret was first addressed rather than
        // where it actually goes.
        if (!SecureContext.Is(tokenUrl))
        {
            errors.Add(ParseDiagnostic.Error(
                $"'@token-url' must use https - '{tokenUrl.Scheme}' would send the client secret "
                    + "in clear. Plain http is allowed only for localhost.",
                asWritten.Line));
            return false;
        }

        grant = new ResolvedOAuth2Grant(
            tokenUrl,
            resolved.ClientId,
            resolved.ClientSecret,
            NullIfEmpty(resolved.Scope),
            NullIfEmpty(resolved.Audience),
            resolved.Placement,
            asWritten.Line);

        return true;
    }

    /// <summary>
    /// A scope or audience that resolved to nothing is absent, not empty.
    /// </summary>
    /// <remarks>
    /// An environment where <c>scope</c> is not set expands <c>{{scope}}</c> to an empty
    /// string, and sending <c>scope=</c> is not the same request as sending no scope at
    /// all - some authorization servers reject it and others issue a token with no scopes.
    /// </remarks>
    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Substitutes variables through the body, leaving the import segments in place with
    /// their paths resolved.
    /// </summary>
    private static List<BodySegment>? SubstituteBody(RequestBlock request, VariableExpander expander)
    {
        if (request.Body is null)
        {
            return null;
        }

        var segments = new List<BodySegment>(request.Body.Count);

        foreach (var segment in request.Body)
        {
            segments.Add(segment switch
            {
                BodyText text => new BodyText(expander.Expand(text.Value, request.StartLine, FieldKind.Body)),

                // A path is expanded as a body field - no character restrictions - because
                // restricting characters is not what keeps a document from reading an
                // arbitrary file. Containment is, and it lives behind IRequestFileSource
                // where it applies to a literal path and a substituted one alike.
                BodyFile file => file with { Path = expander.Expand(file.Path, file.Line, FieldKind.Body) },

                _ => segment,
            });
        }

        return segments;
    }

    /// <summary>
    /// Reads whatever the body imports and concatenates everything into the bytes that
    /// will go on the wire.
    /// </summary>
    /// <remarks>
    /// Text is UTF-8 encoded, which is what it would have been on the wire anyway. An
    /// import contributes its bytes unaltered unless it was written as <c>&lt;@</c>, the
    /// form that asks for the file to be read as text and substituted into - and that one
    /// cannot carry a PNG, which is the whole reason the two forms are different.
    /// </remarks>
    private static bool TryAssembleBody(
        IReadOnlyList<BodySegment>? body,
        IRequestFileSource files,
        VariableExpander expander,
        int bodyLine,
        List<ParseDiagnostic> errors,
        out byte[]? assembled)
    {
        assembled = null;

        if (body is null)
        {
            return true;
        }

        // The overwhelmingly common shape - a body typed into the document, no imports.
        // Worth its own path so the ordinary case does not copy through a MemoryStream.
        if (body is [BodyText only])
        {
            assembled = BodyEncoding.GetBytes(only.Value);
            return true;
        }

        using var buffer = new MemoryStream();

        foreach (var segment in body)
        {
            // An if rather than a `when` guard: the guard did the read and the buffer write
            // inside a pattern match and then fell through to `default`, which is the
            // hidden side effect Dev.md names, for one saved line.
            if (segment is BodyFile file)
            {
                if (!ReadImport(file, files, expander, errors, buffer))
                {
                    return false;
                }
            }
            else if (segment is BodyText text)
            {
                buffer.Write(BodyEncoding.GetBytes(text.Value));
            }

            if (buffer.Length > MaxBodyBytes)
            {
                errors.Add(ParseDiagnostic.Error(
                    $"This body reaches more than {MaxBodyBytes / (1024 * 1024)} MB once its "
                        + "imported files are included.",
                    (segment as BodyFile)?.Line ?? bodyLine));
                return false;
            }
        }

        assembled = buffer.ToArray();
        return true;
    }

    private static bool ReadImport(
        BodyFile file,
        IRequestFileSource files,
        VariableExpander expander,
        List<ParseDiagnostic> errors,
        MemoryStream buffer)
    {
        if (!files.TryRead(file.Path, out var bytes, out var error))
        {
            // Quoted as written, still braced. The resolved path routinely holds a secret
            // - '< ./{{token}}.json' substitutes before it can fail - and ParseDiagnostic
            // promises its messages never carry one.
            errors.Add(ParseDiagnostic.Error($"'< {file.AsWritten}' could not be read: {error}.", file.Line));
            return false;
        }

        if (!file.Interpolate)
        {
            buffer.Write(bytes);
            return true;
        }

        Encoding encoding = BodyEncoding;
        if (file.Encoding is not null && !TryResolveEncoding(file.Encoding, file.Line, errors, out encoding))
        {
            return false;
        }

        // Decoded with the named encoding and written back as UTF-8: the encoding says how
        // to read the file, not how to send it. Re-encoding to a legacy code page on the
        // way out would be a second decision about bytes nobody asked for.
        //
        // A byte order mark is consumed rather than sent. '<@' says "read this as text",
        // and a leading U+FEFF is not text content - a JSON body starting with one is
        // rejected by most servers. GetString would keep it; a StreamReader given the
        // encoding does not. BOM'd files are the Windows norm and a UTF-16 file essentially
        // always has one. The raw '<' form is untouched by this: verbatim means verbatim.
        using var reader = new StreamReader(
            new MemoryStream(bytes),
            encoding,
            detectEncodingFromByteOrderMarks: true);

        var text = expander.Expand(reader.ReadToEnd(), file.Line, FieldKind.Body);

        buffer.Write(BodyEncoding.GetBytes(text));
        return true;
    }

    private static bool TryResolveEncoding(
        string name,
        int line,
        List<ParseDiagnostic> errors,
        out Encoding encoding)
    {
        encoding = BodyEncoding;

        try
        {
            encoding = Encoding.GetEncoding(name);
            return true;
        }
        catch (ArgumentException)
        {
            // Deliberately does not offer code page names. CodePagesEncodingProvider is
            // never registered - and registering it is a process-wide side effect that
            // would belong in Sling.App, making this pure method behave differently under
            // test than in the application. So 'windows-1252' genuinely does not resolve,
            // and the message says what actually works rather than what sounds complete.
            errors.Add(ParseDiagnostic.Error(
                $"'{name}' is not an encoding Sling reads. Use utf-8, utf-16, utf-32 or latin1.",
                line));
            return false;
        }
    }

    private static List<HeaderField> ResolveHeaders(
        RequestBlock request,
        VariableExpander expander,
        List<ParseDiagnostic> errors)
    {
        var headers = new List<HeaderField>(request.Headers.Count);

        foreach (var header in request.Headers)
        {
            var name = expander.Expand(header.Name, header.Line, FieldKind.HeaderName);
            var value = expander.Expand(header.Value, header.Line, FieldKind.HeaderValue);

            // The parser lets a header name through if it holds a reference, because it
            // cannot know what the reference will become. This is where that is settled.
            if (!HttpSyntax.IsToken(name))
            {
                // Reports the name as written, still braced. Reporting the substituted
                // name would put a resolved secret in a message that goes on screen,
                // '{{token}}' resolves before this check runs, and ParseDiagnostic
                // promises its messages never carry one.
                errors.Add(ParseDiagnostic.Error(
                    $"'{header.Name}' is not a valid header name once its variables are substituted.",
                    header.Line));
                continue;
            }

            headers.Add(new HeaderField(name, value, header.Line));
        }

        return headers;
    }

    /// <summary>
    /// Validates the resolved target and turns it into a <see cref="Uri"/>.
    /// </summary>
    /// <param name="target">The resolved text, which may contain secrets.</param>
    /// <param name="asWritten">
    /// The same target still braced, which is what any diagnostic quotes. A resolved
    /// target routinely holds a token - the very reason to quote the unresolved form.
    /// </param>
    private static bool TryBuildUrl(
        string target,
        string asWritten,
        int line,
        List<ParseDiagnostic> errors,
        [NotNullWhen(true)] out Uri? url)
    {
        url = null;

        if (!Uri.TryCreate(target, UriKind.Absolute, out var parsed))
        {
            errors.Add(ParseDiagnostic.Error(
                $"'{asWritten}' is not an absolute URL. Write the scheme and host in full - "
                    + "reusable base URLs arrive with environments.",
                line));
            return false;
        }

        // Schemes are an allow-list, not a deny-list. Sling is a tool for talking to HTTP
        // APIs; letting a document name file:// would turn a request that looks like a
        // network call into a local read.
        if (!parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.Ordinal)
            && !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            errors.Add(ParseDiagnostic.Error(
                $"'{parsed.Scheme}' is not a scheme Sling sends. Use http or https.",
                line));
            return false;
        }

        // Userinfo is refused outright. It is the syntax that makes
        // https://api.example.com@evil.example.com/x send to evil.example.com while
        // reading as the opposite, which is why a substituted response value is
        // percent-encoded before it ever gets here. This is the second line of that
        // defence and it also catches the user's own literal text, where the same URL is
        // very likely a mistake and is deprecated regardless.
        if (parsed.UserInfo.Length > 0)
        {
            errors.Add(ParseDiagnostic.Error(
                "A URL may not carry a username or password before the host - the part before "
                    + "the '@'. It hides which host the request actually goes to. Send credentials "
                    + "in an Authorization header instead.",
                line));
            return false;
        }

        url = parsed;
        return true;
    }
}
