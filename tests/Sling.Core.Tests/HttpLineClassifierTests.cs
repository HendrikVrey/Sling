using Sling.Core.Parsing;

namespace Sling.Core.Tests;

/// <summary>
/// The classifier says what every line of a document is so the editor can colour it, and
/// the thing worth testing is that it agrees with the parser about which line is which.
/// </summary>
/// <remarks>
/// A highlighter that disagrees with the parser is worse than no highlighter: it draws a
/// picture of a request that is not the one that will be sent, in the pane where somebody
/// is looking for the reason their request is wrong. Most of what is below is that
/// agreement rather than the colours.
/// </remarks>
public sealed class HttpLineClassifierTests
{
    private static IReadOnlyList<IReadOnlyList<HttpToken>> Classify(string text) =>
        HttpLineClassifier.Classify(text.Split('\n').Select(line => line.TrimEnd('\r')).ToList());

    /// <summary>The kinds on one line, in order.</summary>
    private static HttpTokenKind[] Kinds(IReadOnlyList<IReadOnlyList<HttpToken>> lines, int line) =>
        [.. lines[line].Select(token => token.Kind)];

    /// <summary>The text one token covers, which is what catches an off-by-one offset.</summary>
    private static string Text(string source, IReadOnlyList<HttpToken> tokens, int index)
    {
        var line = source.Split('\n')[0];
        var token = tokens[index];

        return line.Substring(token.Start, token.Length);
    }

    [Fact]
    public void A_separator_is_a_comment_and_the_title_after_it()
    {
        var lines = Classify("### Log in");

        Assert.Equal([HttpTokenKind.Comment, HttpTokenKind.Title], Kinds(lines, 0));
        Assert.Equal("###", Text("### Log in", lines[0], 0));
        Assert.Equal("Log in", Text("### Log in", lines[0], 1));
    }

    [Fact]
    public void A_separator_with_no_title_is_only_the_hashes()
    {
        var lines = Classify("###");

        Assert.Equal([HttpTokenKind.Comment], Kinds(lines, 0));
    }

    /// <summary>
    /// <c>IsSeparator</c> does not trim, so an indented one is a comment. The classifier has
    /// to make the same choice or a body carrying markdown headings changes meaning between
    /// the picture and the send.
    /// </summary>
    [Fact]
    public void An_indented_separator_is_a_comment_like_the_parser_says()
    {
        var lines = Classify("  ### not a separator");

        Assert.Equal([HttpTokenKind.Comment], Kinds(lines, 0));
    }

    [Fact]
    public void A_plain_comment_is_one_run()
    {
        Assert.Equal([HttpTokenKind.Comment], Kinds(Classify("# just a note"), 0));
        Assert.Equal([HttpTokenKind.Comment], Kinds(Classify("// also a note"), 0));
        Assert.Equal([HttpTokenKind.Comment], Kinds(Classify("    # indented"), 0));
    }

    [Fact]
    public void A_metadata_line_separates_the_hash_the_directive_and_the_argument()
    {
        const string Source = "# @name login";

        var lines = Classify(Source);

        Assert.Equal(
            [HttpTokenKind.Comment, HttpTokenKind.Directive, HttpTokenKind.DirectiveValue],
            Kinds(lines, 0));

        Assert.Equal("@name", Text(Source, lines[0], 1));
        Assert.Equal("login", Text(Source, lines[0], 2));
    }

    [Fact]
    public void A_metadata_line_with_no_argument_is_still_a_directive()
    {
        var lines = Classify("# @auth oauth2");

        Assert.Equal(
            [HttpTokenKind.Comment, HttpTokenKind.Directive, HttpTokenKind.DirectiveValue],
            Kinds(lines, 0));

        Assert.Equal([HttpTokenKind.Comment, HttpTokenKind.Directive], Kinds(Classify("# @auth"), 0));
    }

    [Fact]
    public void A_variable_definition_is_a_name_an_operator_and_a_value()
    {
        const string Source = "@base = https://api.example.com";

        var lines = Classify(Source);

        Assert.Equal(
            [HttpTokenKind.VariableName, HttpTokenKind.Operator, HttpTokenKind.HeaderValue],
            Kinds(lines, 0));

        Assert.Equal("@base", Text(Source, lines[0], 0));
        Assert.Equal("=", Text(Source, lines[0], 1));
    }

    [Fact]
    public void A_request_line_is_a_method_a_target_and_a_version()
    {
        const string Source = "GET https://api.example.com/users HTTP/1.1";

        var lines = Classify(Source);

        Assert.Equal(
            [HttpTokenKind.Method, HttpTokenKind.Target, HttpTokenKind.Version],
            Kinds(lines, 0));

        Assert.Equal("GET", Text(Source, lines[0], 0));
        Assert.Equal("https://api.example.com/users", Text(Source, lines[0], 1));
        Assert.Equal("HTTP/1.1", Text(Source, lines[0], 2));
    }

    /// <summary>
    /// A bare target means GET, and there is no verb on the line to colour. Painting the
    /// first word of a URL as a method is what a rule that keyed off the space would do.
    /// </summary>
    [Fact]
    public void A_bare_target_has_no_method_run()
    {
        const string Source = "https://api.example.com/users";

        var lines = Classify(Source);

        Assert.Equal([HttpTokenKind.Target], Kinds(lines, 0));
        Assert.Equal(Source, Text(Source, lines[0], 0));
    }

    /// <summary>
    /// The verb test is "does the first token look like a verb", not "is there a space" -
    /// a target may hold one.
    /// </summary>
    [Fact]
    public void A_target_holding_a_space_is_not_split_into_a_method()
    {
        const string Source = "https://api.example.com/search?q=two words";

        var lines = Classify(Source);

        Assert.Equal([HttpTokenKind.Target], Kinds(lines, 0));
        Assert.Equal(Source, Text(Source, lines[0], 0));
    }

    [Fact]
    public void An_extension_verb_is_still_a_method()
    {
        const string Source = "PURGE https://cdn.example.com/asset";

        var lines = Classify(Source);

        Assert.Equal([HttpTokenKind.Method, HttpTokenKind.Target], Kinds(lines, 0));
        Assert.Equal("PURGE", Text(Source, lines[0], 0));
    }

    /// <summary>
    /// The parser reaches the target through a Trim(), so a tab between the verb and the URL
    /// belongs to neither. A tab has no glyph, which is why a classifier that stopped at the
    /// space would look right in every screenshot and still disagree with what gets sent.
    /// </summary>
    [Fact]
    public void A_tab_between_the_verb_and_the_target_belongs_to_neither()
    {
        const string Source = "GET \thttps://api.example.com/users";

        var lines = Classify(Source);

        Assert.Equal([HttpTokenKind.Method, HttpTokenKind.Target], Kinds(lines, 0));
        Assert.Equal("GET", Text(Source, lines[0], 0));
        Assert.Equal("https://api.example.com/users", Text(Source, lines[0], 1));

        // The classification agrees with the parse, which is the property that matters.
        Assert.Equal(
            "https://api.example.com/users",
            RequestDocumentParser.Parse(Source).Requests[0].Target);
    }

    [Fact]
    public void An_indented_query_continuation_is_more_target()
    {
        var lines = Classify("GET https://api.example.com/users\n  ?page=2\n  &size=50");

        Assert.Equal([HttpTokenKind.Target], Kinds(lines, 1));
        Assert.Equal([HttpTokenKind.Target], Kinds(lines, 2));
    }

    [Fact]
    public void A_header_is_a_name_a_colon_and_a_value()
    {
        const string Source = "Accept: application/json";

        var lines = Classify("GET https://api.example.com/users\n" + Source);

        Assert.Equal(
            [HttpTokenKind.HeaderName, HttpTokenKind.Operator, HttpTokenKind.HeaderValue],
            Kinds(lines, 1));

        var line = lines[1];

        Assert.Equal("Accept", Source.Substring(line[0].Start, line[0].Length));
        Assert.Equal(":", Source.Substring(line[1].Start, line[1].Length));
        Assert.Equal("application/json", Source.Substring(line[2].Start, line[2].Length));
    }

    /// <summary>
    /// The reason this is not a <c>.xshd</c> grammar. Whether a line is a header or body
    /// text is decided by a blank line arbitrarily far above it, and a JSON body is full of
    /// lines a header pattern matches.
    /// </summary>
    [Fact]
    public void A_json_body_line_is_not_a_header()
    {
        var lines = Classify(
            "POST https://api.example.com/users\n"
            + "Content-Type: application/json\n"
            + "\n"
            + "{\n"
            + "  \"name\": \"Ada\"\n"
            + "}");

        Assert.Equal(
            [HttpTokenKind.HeaderName, HttpTokenKind.Operator, HttpTokenKind.HeaderValue],
            Kinds(lines, 1));

        Assert.Empty(lines[3]);
        Assert.Empty(lines[4]);
        Assert.Empty(lines[5]);
    }

    /// <summary>
    /// A body full of <c>#</c> comments keeps them, because the parser does. Only a line
    /// starting at column one with <c>###</c> ends the request.
    /// </summary>
    [Fact]
    public void A_hash_inside_a_body_is_body_text()
    {
        var lines = Classify(
            "POST https://api.example.com/things\n"
            + "\n"
            + "# this is body text, not a comment\n"
            + "value=1");

        Assert.Empty(lines[2]);
        Assert.Empty(lines[3]);
    }

    [Fact]
    public void A_separator_ends_the_body_and_starts_the_next_request()
    {
        var lines = Classify(
            "POST https://api.example.com/things\n"
            + "\n"
            + "payload\n"
            + "### Second\n"
            + "GET https://api.example.com/things");

        Assert.Empty(lines[2]);
        Assert.Equal([HttpTokenKind.Comment, HttpTokenKind.Title], Kinds(lines, 3));
        Assert.Equal([HttpTokenKind.Method, HttpTokenKind.Target], Kinds(lines, 4));
    }

    [Fact]
    public void A_body_import_is_a_marker_and_a_path()
    {
        const string Source = "<@utf16 ./body.json";

        var lines = Classify("POST https://api.example.com/things\n\n" + Source);

        Assert.Equal([HttpTokenKind.ImportMarker, HttpTokenKind.ImportPath], Kinds(lines, 2));

        var line = lines[2];

        Assert.Equal("<@utf16", Source.Substring(line[0].Start, line[0].Length));
        Assert.Equal("./body.json", Source.Substring(line[1].Start, line[1].Length));
    }

    /// <summary>
    /// The whitespace in the import pattern is what keeps XML and HTML bodies from being
    /// read as imports of files that do not exist. The classifier inherits that.
    /// </summary>
    [Fact]
    public void An_xml_body_is_not_an_import()
    {
        var lines = Classify("POST https://api.example.com/things\n\n<?xml version=\"1.0\"?>");

        Assert.Empty(lines[2]);
    }

    [Fact]
    public void A_reference_is_picked_out_of_a_target()
    {
        const string Source = "GET {{base}}/users/{{id}}";

        var lines = Classify(Source);

        Assert.Equal(
            [
                HttpTokenKind.Method,
                HttpTokenKind.Reference,
                HttpTokenKind.Target,
                HttpTokenKind.Reference,
            ],
            Kinds(lines, 0));

        Assert.Equal("{{base}}", Text(Source, lines[0], 1));
        Assert.Equal("/users/", Text(Source, lines[0], 2));
        Assert.Equal("{{id}}", Text(Source, lines[0], 3));
    }

    [Fact]
    public void A_reference_is_picked_out_of_a_header_value_and_a_body()
    {
        var lines = Classify(
            "GET https://api.example.com/me\n"
            + "Authorization: Bearer {{login.response.body.$.token}}\n"
            + "\n"
            + "{ \"id\": \"{{id}}\" }");

        Assert.Equal(
            [
                HttpTokenKind.HeaderName,
                HttpTokenKind.Operator,
                HttpTokenKind.HeaderValue,
                HttpTokenKind.Reference,
            ],
            Kinds(lines, 1));

        Assert.Equal([HttpTokenKind.Reference], Kinds(lines, 3));
    }

    /// <summary>
    /// An unclosed <c>{{</c> is a typo the parser reports. Colouring the rest of the line as
    /// a reference would make the mistake look like the thing it is not.
    /// </summary>
    [Fact]
    public void An_unclosed_reference_leaves_the_rest_of_the_span_alone()
    {
        const string Source = "GET https://api.example.com/{{oops";

        var lines = Classify(Source);

        Assert.Equal([HttpTokenKind.Method, HttpTokenKind.Target], Kinds(lines, 0));
        Assert.Equal("https://api.example.com/{{oops", Text(Source, lines[0], 1));
    }

    /// <summary>
    /// The contract the editor relies on. Overlapping runs would need a rule about which
    /// one wins, and that rule is where a highlighter gets a run right at one zoom level
    /// and wrong at another.
    /// </summary>
    [Fact]
    public void Tokens_are_ordered_non_overlapping_and_inside_their_line()
    {
        const string Document = """
            @base = https://api.example.com
            @token = {{$processEnv TOKEN}}

            ### Log in
            # @name login
            POST {{base}}/oauth/token HTTP/1.1
            Content-Type: application/json
            # a comment among the headers

            { "client_id": "{{id}}" }

            ### Use it
            GET {{base}}/me
              ?verbose=true
            Authorization: Bearer {{login.response.body.$.token}}

            <@ ./payload.json
            """;

        var source = Document.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var lines = HttpLineClassifier.Classify(source);

        Assert.Equal(source.Count, lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            var previousEnd = 0;

            foreach (var token in lines[i])
            {
                Assert.True(token.Length > 0, $"line {i + 1}: a zero-length token");
                Assert.True(token.Start >= previousEnd, $"line {i + 1}: tokens overlap or are out of order");
                Assert.True(
                    token.Start + token.Length <= source[i].Length,
                    $"line {i + 1}: a token runs past the end of the line");

                previousEnd = token.Start + token.Length;
            }
        }
    }

    /// <summary>
    /// The classification has to hold for the same documents the parser reads, so the two
    /// are checked against each other rather than each against its own idea of the format.
    /// </summary>
    [Fact]
    public void Every_request_line_the_parser_found_is_a_request_line_here()
    {
        const string Document = """
            @base = https://api.example.com

            ### Log in
            # @name login
            POST {{base}}/oauth/token
            Content-Type: application/json

            { "grant_type": "client_credentials" }

            ### Read
            GET {{base}}/me
            Authorization: Bearer {{login.response.body.$.token}}
            """;

        var source = Document.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var lines = HttpLineClassifier.Classify(source);
        var document = RequestDocumentParser.Parse(Document);

        Assert.Equal(2, document.Requests.Count);

        foreach (var request in document.Requests)
        {
            var tokens = lines[request.StartLine - 1];

            Assert.Contains(tokens, token => token.Kind == HttpTokenKind.Method);
            Assert.Contains(tokens, token => token.Kind is HttpTokenKind.Target or HttpTokenKind.Reference);
        }
    }

    [Fact]
    public void An_empty_document_classifies_to_nothing()
    {
        Assert.Empty(HttpLineClassifier.Classify([]));
        Assert.Empty(HttpLineClassifier.Classify([string.Empty])[0]);
    }
}
