using Sling.Core.Documents;
using Sling.Core.Parsing;

namespace Sling.Core.Tests;

/// <summary>
/// The <c>.http</c> parser against the shapes real files take. Each case is written as
/// the text a user would type, because a parser tested only against tidy input is a
/// parser tested against input it will rarely see.
/// </summary>
public sealed class RequestDocumentParserTests
{
    /// <summary>
    /// A request's literal body text, with CRLF folded to LF.
    /// </summary>
    /// <remarks>
    /// The parser preserves the document's own line terminators, and these cases are
    /// written as raw string literals - so their endings are whatever git checked the
    /// test file out with, which differs between this machine and CI. Normalising here
    /// keeps that out of every assertion that is not about line endings; the ones that
    /// <em>are</em> build their input from explicit escapes and do not call this.
    /// </remarks>
    private static string? Body(RequestBlock request) => request.LiteralText?.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void A_bare_url_is_a_GET()
    {
        var document = RequestDocumentParser.Parse("https://api.example.com/things");

        var request = Assert.Single(document.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://api.example.com/things", request.Target);
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void A_lowercase_method_is_normalised()
    {
        var document = RequestDocumentParser.Parse("post https://api.example.com/things");

        Assert.Equal("POST", Assert.Single(document.Requests).Method);
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void An_unknown_method_is_sent_as_written_but_warned_about()
    {
        var document = RequestDocumentParser.Parse("PROPFIND https://files.example.com/");

        Assert.Equal("PROPFIND", Assert.Single(document.Requests).Method);
        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(document.Diagnostics).Severity);
    }

    [Fact]
    public void Headers_end_at_the_blank_line_and_the_body_is_everything_after_it()
    {
        var document = RequestDocumentParser.Parse(
            """
            POST https://api.example.com/things HTTP/1.1
            Content-Type: application/json
            Accept: application/json

            {
              "name": "ada"
            }
            """);

        var request = Assert.Single(document.Requests);
        Assert.Equal("HTTP/1.1", request.Version);
        Assert.Equal(2, request.Headers.Count);
        Assert.Equal("application/json", request.Headers[0].Value);
        Assert.Equal("{\n  \"name\": \"ada\"\n}", Body(request));
    }

    [Fact]
    public void A_hash_inside_a_body_is_body_text_not_a_comment()
    {
        var document = RequestDocumentParser.Parse(
            """
            POST https://api.example.com/things

            # this is a shell comment in a body
            echo hello
            """);

        Assert.Equal("# this is a shell comment in a body\necho hello", Body(Assert.Single(document.Requests)));
    }

    [Fact]
    public void Separators_split_requests_and_carry_a_title()
    {
        var document = RequestDocumentParser.Parse(
            """
            ### list them
            GET https://api.example.com/things

            ### make one
            POST https://api.example.com/things

            {}
            """);

        Assert.Equal(2, document.Requests.Count);
        Assert.Equal("list them", document.Requests[0].Title);
        Assert.Equal("make one", document.Requests[1].Title);
        Assert.Null(document.Requests[0].Body);
        Assert.Equal("{}", Body(document.Requests[1]));
    }

    [Fact]
    public void File_variables_are_collected_without_being_resolved()
    {
        var document = RequestDocumentParser.Parse(
            """
            @base = https://api.example.com
            @token = {{login.response.body.$.access_token}}

            GET {{base}}/me
            """);

        Assert.Equal(2, document.Variables.Count);
        Assert.Equal("base", document.Variables[0].Name);
        Assert.Equal("{{login.response.body.$.access_token}}", document.Variables[1].Value);
        Assert.Equal("{{base}}/me", Assert.Single(document.Requests).Target);
    }

    [Fact]
    public void A_name_directive_names_the_request_that_follows_it()
    {
        var document = RequestDocumentParser.Parse(
            """
            # @name login
            POST https://api.example.com/auth

            ###
            GET https://api.example.com/me
            """);

        Assert.Equal("login", document.Requests[0].Name);

        // The second request must not inherit it. A chain reference that resolves to the
        // wrong request is worse than one that fails to resolve.
        Assert.Null(document.Requests[1].Name);
    }

    [Fact]
    public void Two_requests_claiming_the_same_name_is_an_error()
    {
        // Silently accepted, this is the worst defect the format can produce: BlockNamed
        // returns the first, the response store is keyed by name so the last one to run
        // wins, and a chain resolves against a different request than its dependency
        // graph points at - with nothing shown to say so.
        var document = RequestDocumentParser.Parse(
            """
            # @name login
            POST https://api.example.com/auth

            ###
            # @name login
            POST https://api.example.com/auth2
            """);

        var diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("already used by the request on line 2", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_name_on_one_request_is_an_error()
    {
        var document = RequestDocumentParser.Parse(
            """
            # @name first
            # @name second
            GET https://api.example.com/a
            """);

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(document.Diagnostics).Severity);
        Assert.Equal("first", Assert.Single(document.Requests).Name);
    }

    [Fact]
    public void A_request_line_that_is_only_a_method_is_an_error_where_it_happens()
    {
        // It used to take the bare-URL branch, producing Target="GET" with no diagnostic
        // and surfacing far away as "'GET' is not an absolute URL".
        var document = RequestDocumentParser.Parse("GET");

        var diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("no request target", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_requests_first_line_covers_the_metadata_above_its_request_line()
    {
        // FirstLine is what the UI filters diagnostics by. Anchored at StartLine instead,
        // every '# @name' diagnostic fell outside its own request's window and was
        // discarded - so a nameless '@name' sent an unnamed request and every chain
        // against it failed for an unrelated reason.
        var document = RequestDocumentParser.Parse(
            """
            ### a title
            # @name login
            POST https://api.example.com/auth
            """);

        var request = Assert.Single(document.Requests);
        Assert.Equal(3, request.StartLine);
        Assert.Equal(2, request.FirstLine);
    }

    [Fact]
    public void An_unsupported_directive_is_reported_rather_than_ignored()
    {
        var document = RequestDocumentParser.Parse(
            """
            # @no-redirect
            GET https://api.example.com/thing
            """);

        var diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("no-redirect", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_indented_query_continuation_joins_the_request_line()
    {
        var document = RequestDocumentParser.Parse(
            """
            GET https://api.example.com/search
                ?q=ada
                &page=2
            Accept: application/json
            """);

        var request = Assert.Single(document.Requests);
        Assert.Equal("https://api.example.com/search?q=ada&page=2", request.Target);
        Assert.Single(request.Headers);
    }

    [Fact]
    public void A_line_that_is_neither_header_nor_body_is_an_error()
    {
        var document = RequestDocumentParser.Parse(
            """
            GET https://api.example.com/thing
            this is not a header
            """);

        var diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(2, diagnostic.Line);
    }

    [Fact]
    public void A_header_name_that_is_not_a_token_is_rejected()
    {
        var document = RequestDocumentParser.Parse(
            """
            GET https://api.example.com/thing
            Bad(Name): value
            """);

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(document.Diagnostics).Severity);
    }

    [Fact]
    public void Line_numbers_survive_every_line_ending()
    {
        var document = RequestDocumentParser.Parse("GET https://api.example.com/a\r\nAccept: */*\rX-Bad\n");

        Assert.Equal(3, Assert.Single(document.Diagnostics).Line);
    }

    [Fact]
    public void An_empty_document_parses_to_nothing_rather_than_throwing()
    {
        var document = RequestDocumentParser.Parse(string.Empty);

        Assert.Empty(document.Requests);
        Assert.Empty(document.Diagnostics);
        Assert.Null(document.BlockAtLine(1));
    }

    [Theory]
    [InlineData(1, "GET")]
    [InlineData(2, "GET")]
    [InlineData(4, "POST")]
    [InlineData(5, "POST")]
    [InlineData(99, "POST")]
    public void A_caret_resolves_to_the_request_it_is_in_or_the_next_one(int line, string expected)
    {
        var document = RequestDocumentParser.Parse(
            """
            GET https://api.example.com/a

            ###
            POST https://api.example.com/b

            {}
            """);

        Assert.Equal(expected, document.BlockAtLine(line)?.Method);
    }

    [Theory]
    [InlineData("< ./payload.json", "./payload.json", false, null)]
    [InlineData("<  ../fixtures/big.bin", "../fixtures/big.bin", false, null)]
    [InlineData("<@ ./template.json", "./template.json", true, null)]
    [InlineData("<@utf16 ./legacy.xml", "./legacy.xml", true, "utf16")]
    [InlineData("<@windows-1252 ./old.txt", "./old.txt", true, "windows-1252")]
    public void A_body_import_is_recognised_in_all_three_forms(
        string line,
        string expectedPath,
        bool expectedInterpolate,
        string? expectedEncoding)
    {
        var document = RequestDocumentParser.Parse(
            "POST https://api.example.com/upload\nContent-Type: application/json\n\n" + line);

        var body = Assert.Single(document.Requests).Body;
        var import = Assert.IsType<BodyFile>(Assert.Single(body!));

        Assert.Equal(expectedPath, import.Path);
        Assert.Equal(expectedInterpolate, import.Interpolate);
        Assert.Equal(expectedEncoding, import.Encoding);
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?>")]
    [InlineData("<html>")]
    [InlineData("<root><child /></root>")]
    [InlineData("<")]
    public void A_body_that_merely_starts_with_an_angle_bracket_is_not_an_import(string first)
    {
        // The whitespace after the marker is the whole disambiguation. Without it an XML
        // or HTML body - two things people send constantly - would be read as an import of
        // a file that does not exist, and the request would refuse to send.
        var document = RequestDocumentParser.Parse(
            "POST https://api.example.com/things\nContent-Type: application/xml\n\n" + first + "\nmore");

        var body = Assert.Single(document.Requests).Body;

        Assert.All(body!, segment => Assert.IsType<BodyText>(segment));
    }

    [Fact]
    public void An_import_between_body_lines_keeps_the_text_on_both_sides_of_it()
    {
        // The multipart shape, which is the reason imports are a sequence rather than a
        // whole-body replacement.
        var document = RequestDocumentParser.Parse(
            "POST https://api.example.com/upload\r\n"
                + "Content-Type: multipart/form-data; boundary=b\r\n"
                + "\r\n"
                + "--b\r\n"
                + "Content-Disposition: form-data; name=\"file\"; filename=\"a.png\"\r\n"
                + "\r\n"
                + "< ./a.png\r\n"
                + "--b--");

        var body = Assert.Single(document.Requests).Body!;

        Assert.Equal(3, body.Count);
        Assert.EndsWith("\r\n\r\n", Assert.IsType<BodyText>(body[0]).Value, StringComparison.Ordinal);
        Assert.Equal("./a.png", Assert.IsType<BodyFile>(body[1]).Path);

        // The import's own terminator belongs AFTER it. Folding it into the text before
        // would put the CRLF on the wrong side of the boundary that follows.
        Assert.Equal("\r\n--b--", Assert.IsType<BodyText>(body[2]).Value);
    }

    [Fact]
    public void A_body_keeps_the_line_endings_it_was_written_with()
    {
        // RFC 2046 requires CRLF between multipart parts. The parser used to normalise
        // every body to LF, which most servers tolerate and strict ones reject - with
        // nothing in the document to point at.
        var crlf = RequestDocumentParser.Parse(
            "POST https://api.example.com/x\r\n\r\nline one\r\nline two");

        var lf = RequestDocumentParser.Parse(
            "POST https://api.example.com/x\n\nline one\nline two");

        Assert.Equal("line one\r\nline two", Assert.Single(crlf.Requests).LiteralText);
        Assert.Equal("line one\nline two", Assert.Single(lf.Requests).LiteralText);
    }

    [Fact]
    public void An_import_line_is_reported_by_its_own_line_number()
    {
        var document = RequestDocumentParser.Parse(
            "@name = x\nPOST https://api.example.com/x\nContent-Type: text/plain\n\n< ./one.txt");

        var import = Assert.IsType<BodyFile>(Assert.Single(Assert.Single(document.Requests).Body!));

        Assert.Equal(5, import.Line);
    }

    [Fact]
    public void A_multipart_body_written_with_bare_newlines_is_warned_about()
    {
        // RFC 2046 separates parts with CRLF, and the body is sent exactly as written - so
        // a repo carrying '*.http text eol=lf' produces a body strict servers reject, which
        // is the failure preserving line endings set out to remove, arriving from the other
        // direction. A warning rather than a rewrite: normalising every terminator would
        // also rewrite the content of a text part, which is not ours to change.
        var document = RequestDocumentParser.Parse(
            "POST https://api.example.com/upload\n"
                + "Content-Type: multipart/form-data; boundary=b\n"
                + "\n"
                + "--b\n"
                + "\n"
                + "value\n"
                + "--b--");

        var warning = Assert.Single(document.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("CRLF", warning.Message, StringComparison.Ordinal);
        Assert.False(document.HasErrors);
    }

    [Fact]
    public void A_multipart_body_written_with_crlf_is_left_alone()
    {
        var document = RequestDocumentParser.Parse(
            "POST https://api.example.com/upload\r\n"
                + "Content-Type: multipart/form-data; boundary=b\r\n"
                + "\r\n"
                + "--b\r\n"
                + "\r\n"
                + "value\r\n"
                + "--b--");

        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void A_non_multipart_body_with_bare_newlines_is_not_warned_about()
    {
        // JSON does not care, and warning about every LF body would train people to ignore
        // the warning that matters.
        var document = RequestDocumentParser.Parse(
            "POST https://api.example.com/things\nContent-Type: application/json\n\n{\n  \"a\": 1\n}");

        Assert.Empty(document.Diagnostics);
    }
}
