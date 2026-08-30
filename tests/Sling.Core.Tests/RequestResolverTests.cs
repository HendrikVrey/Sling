using System.Diagnostics.CodeAnalysis;
using Sling.Core.Documents;
using Sling.Core.Parsing;
using Sling.Core.Variables;

namespace Sling.Core.Tests;

/// <summary>
/// Variable substitution, chain resolution, and the rules that stop a value taken from a
/// response changing the shape of the request it lands in.
/// </summary>
public sealed class RequestResolverTests
{
    private const string Secret = "sup3rs3cret";

    [Fact]
    public void File_variables_are_substituted()
    {
        var resolved = ResolveFirst(
            """
            @base = https://api.example.com
            @version = v2

            GET {{base}}/{{version}}/things
            """);

        Assert.Equal("https://api.example.com/v2/things", resolved.Url.ToString());
    }

    [Fact]
    public void A_variable_may_be_defined_in_terms_of_another()
    {
        var resolved = ResolveFirst(
            """
            @host = api.example.com
            @base = https://{{host}}

            GET {{base}}/things
            """);

        Assert.Equal("https://api.example.com/things", resolved.Url.ToString());
    }

    [Fact]
    public void A_variable_defined_in_terms_of_itself_is_reported_not_hung()
    {
        var result = Resolve(
            """
            @loop = {{loop}}

            GET https://api.example.com/{{loop}}
            """);

        Assert.Null(result.Request);
        Assert.Contains(result.Errors, e => e.Message.Contains("itself", StringComparison.Ordinal));
    }

    [Fact]
    public void An_undefined_variable_names_itself_in_the_error()
    {
        var result = Resolve("GET https://api.example.com/{{missing}}");

        Assert.Contains(result.Errors, e => e.Message.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_relative_target_is_refused_with_an_explanation()
    {
        var result = Resolve("GET /things");

        Assert.Null(result.Request);
        Assert.Contains(result.Errors, e => e.Message.Contains("absolute", StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_web_scheme_is_refused()
    {
        // A deny-list would be a list of the things someone forgot. Sling sends HTTP; a
        // document naming file:// is a local read wearing a request's clothes.
        var result = Resolve("GET file:///C:/Windows/win.ini");

        Assert.Null(result.Request);
        Assert.Contains(result.Errors, e => e.Message.Contains("http", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unsent_named_request_is_reported_as_missing_rather_than_as_an_error()
    {
        var result = Resolve(
            """
            GET https://api.example.com/me
            Authorization: Bearer {{login.response.body.$.token}}
            """);

        Assert.Null(result.Request);
        Assert.Empty(result.Errors);
        Assert.Equal("login", Assert.Single(result.MissingResponses));
    }

    [Fact]
    public void A_chained_body_value_is_substituted_once_the_response_exists()
    {
        var result = Resolve(
            """
            GET https://api.example.com/me
            Authorization: Bearer {{login.response.body.$.access_token}}
            """,
            Responded("login", """{ "access_token": "s3cret" }"""));

        var header = Assert.Single(result.Request!.Headers);
        Assert.Equal("Bearer s3cret", header.Value);
    }

    [Fact]
    public void A_chained_header_value_is_substituted()
    {
        var result = Resolve(
            """
            GET https://api.example.com/next
            X-Trace: {{first.response.headers.X-Request-Id}}
            """,
            Responded("first", "{}", new ResponseHeader("x-request-id", "abc123")));

        Assert.Equal("abc123", Assert.Single(result.Request!.Headers).Value);
    }

    [Fact]
    public void A_chained_value_from_a_truncated_response_is_refused()
    {
        var lookup = new StubLookup();
        lookup.Add("big", Snapshot("{}", truncated: true));

        var result = Resolve(
            """
            GET https://api.example.com/me
            Authorization: Bearer {{big.response.body.$.token}}
            """,
            lookup);

        Assert.Null(result.Request);
        Assert.Contains(result.Errors, e => e.Message.Contains("too large", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Secret + "\r\nX-Injected: yes")]
    [InlineData(Secret + "\nX-Injected: yes")]
    [InlineData(Secret + "\u0000")]
    public void A_response_value_cannot_inject_a_header(string hostile)
    {
        var result = Resolve(
            """
            GET https://api.example.com/me
            Authorization: Bearer {{login.response.body.$.token}}
            """,
            Responded("login", JsonWithToken(hostile)));

        Assert.Null(result.Request);

        var error = Assert.Single(result.Errors);
        Assert.Contains("never allowed to alter the shape", error.Message, StringComparison.Ordinal);

        // The message names the reference and the code point, never the value: a rejected
        // value is very often a credential, and a diagnostic is not a place to print one.
        Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Secret + " HTTP/1.1\r\nGET /elsewhere")]
    [InlineData(Secret + " and a space")]
    [InlineData(Secret + "\ttab")]
    [InlineData(Secret + "/../../admin")]
    public void A_response_value_cannot_inject_a_request_line(string hostile)
    {
        var result = Resolve(
            "GET https://api.example.com/things/{{login.response.body.$.token}}",
            Responded("login", JsonWithToken(hostile)));

        // Percent-encoded rather than refused: a URL, unlike a header, has an escape
        // mechanism, so the value can be carried safely instead of thrown away. What must
        // not survive is its ability to act as syntax.
        Assert.NotNull(result.Request);
        Assert.DoesNotContain(result.Request.Url.AbsoluteUri, char.IsControl);
        Assert.Equal("api.example.com", result.Request.Url.Host);
        Assert.StartsWith("/things/", result.Request.Url.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("@evil.example.com")]
    [InlineData(":8080@evil.example.com")]
    [InlineData("%40evil.example.com")]
    public void A_response_value_cannot_retarget_the_request_to_another_host(string hostile)
    {
        // The attack this exists for. '@' is a legal URL character, so a character check
        // passes it - and Uri parses the authority afterwards, making everything before
        // the '@' userinfo and everything after it the host. The Authorization header
        // then goes to a server the document never named, on the FIRST request, where no
        // redirect policy can intervene. Following a 'next' link out of a paginated
        // response is the ordinary shape of this template.
        var result = Resolve(
            """
            GET https://api.example.com{{page.response.body.$.next}}/x
            Authorization: Bearer hunter2
            """,
            Responded("page", """{ "next": "PLACEHOLDER" }""".Replace("PLACEHOLDER", hostile, StringComparison.Ordinal)));

        // Either outcome is acceptable and both are asserted together, because which one
        // happens is an artefact of Uri's parser rather than of the rule: encoding the
        // '@' usually leaves a host Uri rejects as malformed, so the request is refused
        // outright. What must never happen is a request that resolves and points
        // somewhere else.
        Assert.DoesNotContain("evil", result.Request?.Url.Host ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(result.Request?.Url.UserInfo ?? string.Empty);
    }

    [Fact]
    public void A_url_carrying_userinfo_is_refused_outright()
    {
        // The second line of the same defence, and it covers the user's own literal text
        // rather than only substituted values.
        var result = Resolve("GET https://api.example.com@evil.example.com/x");

        Assert.Null(result.Request);
        Assert.Contains("username or password", Assert.Single(result.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_variable_holding_a_response_value_is_still_encoded_and_encoded_once()
    {
        // Provenance has to be transitive: '@next' is the user's own text, but what it
        // resolves to is not. Encoding happens where the response value enters, so the
        // outer substitution must not encode it a second time.
        // The value sits in the path rather than against the authority, so the URL stays
        // well-formed and the encoding itself can be read back and counted.
        var result = Resolve(
            """
            @next = {{page.response.body.$.next}}

            GET https://api.example.com/x/{{next}}
            """,
            Responded("page", """{ "next": "@evil.example.com" }"""));

        Assert.NotNull(result.Request);
        Assert.Equal("api.example.com", result.Request.Url.Host);

        // %2540 would be '@' encoded twice; %40 is the single, correct encoding.
        Assert.Contains("%40evil.example.com", result.Request.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("%2540", result.Request.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_variable_may_still_supply_the_whole_base_url()
    {
        // The mirror case, and the reason encoding is scoped to response values: '@base'
        // holding a scheme and host is the format's central idiom. Encoding it would
        // break every document ever written.
        var resolved = ResolveFirst(
            """
            @base = https://api.example.com/v2

            GET {{base}}/things
            """);

        Assert.Equal("https://api.example.com/v2/things", resolved.Url.ToString());
    }

    [Fact]
    public void An_expansion_that_doubles_per_level_is_stopped_before_it_exhausts_memory()
    {
        // Nesting depth alone does not bound this: each level doubles, so thirty legal
        // levels reach gigabytes. It ran synchronously on the UI thread, so the symptom
        // was a frozen window and then an out-of-memory.
        var document = new System.Text.StringBuilder("@v0 = xxxxxxxx\n");
        for (var i = 1; i <= 31; i++)
        {
            document.Append(System.Globalization.CultureInfo.InvariantCulture, $"@v{i} = {{{{v{i - 1}}}}}{{{{v{i - 1}}}}}\n");
        }

        document.Append("\nGET https://api.example.com/{{v31}}\n");

        var result = Resolve(document.ToString());

        Assert.Null(result.Request);
        Assert.Contains(result.Errors, e => e.Message.Contains("1 MB", StringComparison.Ordinal));
    }

    [Fact]
    public void A_body_is_not_held_to_the_header_expansion_budget()
    {
        // The budget was sized for a request target and then, when bodies started going
        // through the same expander, silently governed them too - so a payload well inside
        // the 32 MB import cap was refused, with a message blaming a doubling variable the
        // document does not contain. Worse, it depended on whether the body happened to
        // hold a reference at all: the same bytes with no {{name}} in them went through.
        var payload = new string('x', (2 * 1024 * 1024) + 1);

        var resolved = ResolveFirst(
            """
            POST https://api.example.com/upload
            Content-Type: text/plain

            {{payload}}
            """,
            environment: Environment(("payload", payload)));

        Assert.Equal(payload.Length, resolved.Body!.Length);
    }

    [Fact]
    public void A_chain_reference_with_a_trailing_dot_does_not_return_the_whole_body()
    {
        // It walked zero path steps and returned the root, so this quietly sent the entire
        // login response - every secret in it - as a header value.
        var result = Resolve(
            """
            GET https://api.example.com/me
            Authorization: Bearer {{login.response.body.}}
            """,
            Responded("login", """{ "access_token": "s3cret" }"""));

        Assert.Null(result.Request);
        Assert.NotEmpty(result.Errors);
        Assert.DoesNotContain(result.Errors, e => e.Message.Contains("s3cret", StringComparison.Ordinal));
    }

    [Fact]
    public void A_diagnostic_about_a_bad_url_quotes_the_target_as_written()
    {
        // TryBuildUrl runs after substitution has succeeded, so quoting the resolved
        // target puts the token on screen. ParseDiagnostic documents that its messages
        // never carry one.
        var result = Resolve(
            "GET api.example.com/me?k={{login.response.body.$.token}}",
            Responded("login", JsonWithToken(Secret)));

        Assert.Null(result.Request);
        Assert.DoesNotContain(Secret, Assert.Single(result.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_diagnostic_about_a_bad_header_name_quotes_the_name_as_written()
    {
        var result = Resolve(
            """
            GET https://api.example.com/me
            X-Bad({{login.response.body.$.token}}): v
            """,
            Responded("login", JsonWithToken(Secret)));

        Assert.Null(result.Request);
        Assert.DoesNotContain(Secret, Assert.Single(result.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_percent_encoded_control_character_stays_encoded_through_url_parsing()
    {
        // The character check runs on the substituted text, but System.Uri canonicalises
        // afterwards - so the guard would be worthless if Uri decoded escapes. Probed
        // rather than assumed: Uri unescapes only unreserved characters (%41 becomes A)
        // and leaves %0d%0a alone, and a raw CR would be encoded rather than passed
        // through. This pins that, because a future change to how the target is built is
        // exactly what would silently reopen it.
        var resolved = ResolveFirst("GET https://api.example.com/a%0d%0aX-Injected:%20yes");

        Assert.DoesNotContain(resolved.Url.AbsoluteUri, char.IsControl);
        Assert.Contains("%0d%0a", resolved.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void A_response_value_may_contain_anything_when_it_lands_in_a_body()
    {
        // A body is terminated by a length, not by a delimiter the value could contain,
        // so there is nothing to break out of and nothing to refuse.
        var result = Resolve(
            """
            POST https://api.example.com/echo
            Content-Type: application/json

            {"was": "{{login.response.body.$.token}}"}
            """,
            Responded("login", JsonWithToken("line one\nline two")));

        Assert.NotNull(result.Request);
        Assert.Contains("line one\nline two", BodyText(result.Request), StringComparison.Ordinal);
    }

    [Fact]
    public void A_header_name_is_re_checked_after_substitution()
    {
        var result = Resolve(
            """
            @evil = Bad: Header

            GET https://api.example.com/things
            {{evil}}: value
            """);

        Assert.Null(result.Request);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void The_name_and_version_survive_resolution()
    {
        var resolved = ResolveFirst(
            """
            # @name login
            POST https://api.example.com/auth HTTP/1.1
            """);

        Assert.Equal("login", resolved.Name);
        Assert.Equal("HTTP/1.1", resolved.Version);
        Assert.Equal("POST", resolved.Method);
    }

    private static string JsonWithToken(string token) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["token"] = token });

    private static ResolutionResult Resolve(
        string text,
        IResponseLookup? responses = null,
        IVariableSource? environment = null,
        IRequestFileSource? files = null)
    {
        var document = RequestDocumentParser.Parse(text);

        return RequestResolver.Resolve(
            document,
            document.Requests[0],
            new ResolutionContext
            {
                Responses = responses ?? NoResponses.Instance,
                Environment = environment ?? NoVariables.Instance,
                Files = files ?? NoRequestFiles.Instance,
            });
    }

    /// <summary>The resolved body as text. Every body in these tests is UTF-8.</summary>
    private static string BodyText(ResolvedRequest request) =>
        request.Body is null ? string.Empty : System.Text.Encoding.UTF8.GetString(request.Body);

    private static ResolvedRequest ResolveFirst(
        string text,
        IVariableSource? environment = null,
        IRequestFileSource? files = null)
    {
        var result = Resolve(text, environment: environment, files: files);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Request);

        return result.Request;
    }

    private static StubLookup Responded(string name, string body, params ResponseHeader[] headers)
    {
        var lookup = new StubLookup();
        lookup.Add(name, Snapshot(body, truncated: false, headers));
        return lookup;
    }

    private static ResponseSnapshot Snapshot(string body, bool truncated, params ResponseHeader[] headers) =>
        new(
            200,
            "OK",
            "1.1",
            headers,
            body,
            body.Length,
            truncated,
            TimeSpan.FromMilliseconds(1),
            new Uri("https://api.example.com/auth"),
            []);

    private sealed class StubLookup : IResponseLookup
    {
        private readonly Dictionary<string, ResponseSnapshot> _byName = new(StringComparer.Ordinal);

        public void Add(string name, ResponseSnapshot response) => _byName[name] = response;

        public ResponseSnapshot? Find(string requestName) => _byName.GetValueOrDefault(requestName);
    }

    [Fact]
    public void An_environment_value_beats_the_same_name_defined_in_the_file()
    {
        // Sling.md §4c, and a deliberate divergence from the reference dialect, which
        // gives the file precedence. The other way round, a document holding
        // '@base = https://api.example.com' could not be pointed at staging without
        // editing the very line the environment exists to replace.
        var resolved = ResolveFirst(
            """
            @base = https://api.example.com

            GET {{base}}/things
            """,
            environment: Environment(("base", "https://staging.api.example.com")));

        Assert.Equal("https://staging.api.example.com/things", resolved.Url.ToString());
    }

    [Fact]
    public void A_file_variable_still_resolves_when_the_environment_does_not_define_it()
    {
        var resolved = ResolveFirst(
            """
            @path = things

            GET https://api.example.com/{{path}}
            """,
            environment: Environment(("base", "unused")));

        Assert.Equal("https://api.example.com/things", resolved.Url.ToString());
    }

    [Fact]
    public void An_environment_value_may_reference_another_variable()
    {
        var resolved = ResolveFirst(
            """
            @host = api.example.com

            GET {{base}}/things
            """,
            environment: Environment(("base", "https://{{host}}")));

        Assert.Equal("https://api.example.com/things", resolved.Url.ToString());
    }

    [Fact]
    public void An_environment_value_defined_in_terms_of_itself_is_reported_not_hung()
    {
        // The environment shadows the file, so '{{base}}' inside the environment's own
        // 'base' cannot fall through to the file's - it is a cycle, and saying so beats
        // hanging.
        var result = Resolve(
            """
            @base = https://api.example.com

            GET {{base}}/things
            """,
            environment: Environment(("base", "{{base}}/v2")));

        Assert.Null(result.Request);
        Assert.Contains(result.Errors, e => e.Message.Contains("itself", StringComparison.Ordinal));
    }

    [Fact]
    public void An_undefined_variable_mentions_the_environment_as_a_place_to_define_it()
    {
        var result = Resolve("GET https://api.example.com/{{missing}}");

        Assert.Contains(result.Errors, e => e.Message.Contains("environment", StringComparison.Ordinal));
    }

    [Fact]
    public void A_body_import_contributes_the_file_bytes()
    {
        var resolved = ResolveFirst(
            "POST https://api.example.com/upload\nContent-Type: application/json\n\n< ./payload.json",
            files: Files(("./payload.json", """{"from":"disk"}""")));

        Assert.Equal("""{"from":"disk"}""", BodyText(resolved));
    }

    [Fact]
    public void A_raw_import_does_not_substitute_variables_inside_the_file()
    {
        // The whole reason the two forms exist: a raw import may be a PNG, and running a
        // substitution pass over one would corrupt it while looking like it worked.
        var resolved = ResolveFirst(
            """
            @who = ada

            POST https://api.example.com/upload

            < ./template.json
            """,
            files: Files(("./template.json", """{"user":"{{who}}"}""")));

        Assert.Equal("""{"user":"{{who}}"}""", BodyText(resolved));
    }

    [Fact]
    public void An_interpolating_import_substitutes_variables_inside_the_file()
    {
        var resolved = ResolveFirst(
            """
            @who = ada

            POST https://api.example.com/upload

            <@ ./template.json
            """,
            files: Files(("./template.json", """{"user":"{{who}}"}""")));

        Assert.Equal("""{"user":"ada"}""", BodyText(resolved));
    }

    [Fact]
    public void A_raw_import_carries_bytes_that_are_not_text()
    {
        // A PNG header, which is not valid UTF-8. Decoding and re-encoding it would
        // replace the invalid sequences with U+FFFD and hand the server a corrupt file.
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF, 0xD8];

        var resolved = ResolveFirst(
            "POST https://api.example.com/upload\nContent-Type: image/png\n\n< ./logo.png",
            files: new StubFiles { ["./logo.png"] = png });

        Assert.Equal(png, resolved.Body);
    }

    [Fact]
    public void An_import_that_cannot_be_read_names_the_path_and_the_reason()
    {
        var result = Resolve(
            "POST https://api.example.com/upload\n\n< ./missing.json",
            files: new StubFiles());

        Assert.Null(result.Request);

        var error = Assert.Single(result.Errors);
        Assert.Contains("./missing.json", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, error.Line);
    }

    [Fact]
    public void An_import_path_may_itself_be_a_variable()
    {
        var resolved = ResolveFirst(
            """
            @fixture = ./payload.json

            POST https://api.example.com/upload

            < {{fixture}}
            """,
            files: Files(("./payload.json", "{}")));

        Assert.Equal("{}", BodyText(resolved));
    }

    [Fact]
    public void An_import_between_body_lines_is_spliced_in_where_it_was_written()
    {
        var resolved = ResolveFirst(
            "POST https://api.example.com/upload\r\n"
                + "Content-Type: multipart/form-data; boundary=b\r\n"
                + "\r\n"
                + "--b\r\n"
                + "\r\n"
                + "< ./part.txt\r\n"
                + "--b--",
            files: Files(("./part.txt", "PART")));

        Assert.Equal("--b\r\n\r\nPART\r\n--b--", BodyText(resolved));
    }

    [Fact]
    public void No_file_is_opened_for_a_request_that_cannot_be_sent()
    {
        // A body import whose path holds an unresolved reference would otherwise be
        // reported as a missing file, burying the real cause under a symptom.
        var files = new StubFiles();

        var result = Resolve(
            "POST https://api.example.com/{{nope}}\n\n< ./payload.json",
            files: files);

        Assert.Null(result.Request);
        Assert.Empty(files.Requested);
        Assert.Contains(result.Errors, e => e.Message.Contains("nope", StringComparison.Ordinal));
    }

    [Fact]
    public void An_import_is_read_after_the_chain_it_depends_on_has_run()
    {
        // Reported as missing rather than as an error, so the runner sends the dependency
        // and resolves again - the same loop a chained URL drives.
        var result = Resolve(
            "POST https://api.example.com/upload\n\n<@ ./template.json",
            files: Files(("./template.json", "{{login.response.body.$.token}}")));

        Assert.Null(result.Request);
        Assert.Empty(result.Errors);
        Assert.Equal("login", Assert.Single(result.MissingResponses));
    }

    [Fact]
    public void An_interpolating_import_consumes_a_byte_order_mark()
    {
        // '<@' says "read this as text", and a leading U+FEFF is not text content - a JSON
        // body starting with one is rejected by most servers. Encoding.GetString keeps it;
        // a StreamReader given the encoding does not. BOM'd files are the Windows norm.
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(System.Text.Encoding.UTF8.GetBytes("""{"a":1}"""))
            .ToArray();

        var resolved = ResolveFirst(
            "POST https://api.example.com/upload\n\n<@ ./bom.json",
            files: new StubFiles { ["./bom.json"] = withBom });

        Assert.Equal("""{"a":1}""", BodyText(resolved));
    }

    [Fact]
    public void A_raw_import_keeps_a_byte_order_mark_because_verbatim_means_verbatim()
    {
        // The mirror case, and the reason the BOM fix belongs only on the '<@' path: a raw
        // import may be a file whose first three bytes matter.
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF, 0x7B, 0x7D };

        var resolved = ResolveFirst(
            "POST https://api.example.com/upload\n\n< ./bom.json",
            files: new StubFiles { ["./bom.json"] = withBom });

        Assert.Equal(withBom, resolved.Body);
    }

    [Fact]
    public void An_import_failure_quotes_the_path_as_written_never_the_resolved_secret()
    {
        // ParseDiagnostic promises its messages never carry a resolved secret, and
        // '< ./{{token}}.json' substitutes before it can fail. ResolveHeaders and
        // TryBuildUrl both quote the unresolved form for this reason; this path did not.
        const string Token = "s3cr3t-bearer-value";

        var result = Resolve(
            """
            @token = SECRET_PLACEHOLDER

            POST https://api.example.com/upload

            < ./{{token}}.json
            """.Replace("SECRET_PLACEHOLDER", Token, StringComparison.Ordinal),
            files: new StubFiles());

        var error = Assert.Single(result.Errors);
        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
        Assert.Contains("{{token}}", error.Message, StringComparison.Ordinal);
    }


    private static StubEnvironment Environment(params (string Name, string Value)[] values)
    {
        var source = new StubEnvironment();

        foreach (var (name, value) in values)
        {
            source[name] = value;
        }

        return source;
    }

    private static StubFiles Files(params (string Path, string Text)[] files)
    {
        var source = new StubFiles();

        foreach (var (path, text) in files)
        {
            source[path] = System.Text.Encoding.UTF8.GetBytes(text);
        }

        return source;
    }

    private sealed class StubEnvironment : Dictionary<string, string>, IVariableSource
    {
        public StubEnvironment()
            : base(StringComparer.Ordinal)
        {
        }

        public bool TryGet(string name, [NotNullWhen(true)] out string? value) => TryGetValue(name, out value);
    }

    /// <summary>
    /// Files by the path the document wrote, recording what was asked for - which is how
    /// "nothing was opened" is asserted.
    /// </summary>
    private sealed class StubFiles : Dictionary<string, byte[]>, IRequestFileSource
    {
        public StubFiles()
            : base(StringComparer.Ordinal)
        {
        }

        public List<string> Requested { get; } = [];

        public bool TryRead(
            string path,
            [NotNullWhen(true)] out byte[]? bytes,
            [NotNullWhen(false)] out string? reason)
        {
            Requested.Add(path);

            if (TryGetValue(path, out bytes))
            {
                reason = null;
                return true;
            }

            reason = "there is no such file";
            return false;
        }
    }
}
