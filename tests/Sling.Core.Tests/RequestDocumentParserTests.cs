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
        Assert.Equal("{\n  \"name\": \"ada\"\n}", request.Body);
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

        Assert.Equal("# this is a shell comment in a body\necho hello", Assert.Single(document.Requests).Body);
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
        Assert.Equal("{}", document.Requests[1].Body);
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
        // graph points at — with nothing shown to say so.
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
        // discarded — so a nameless '@name' sent an unnamed request and every chain
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
}
