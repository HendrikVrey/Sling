using Sling.Core.Auth;
using Sling.Core.Documents;
using Sling.Core.Parsing;

namespace Sling.Core.Tests;

/// <summary>
/// Reading a request's auth out of the document, and writing it back.
/// </summary>
/// <remarks>
/// <para>
/// The property the whole auth panel rests on is asserted here rather than in the window:
/// after an edit, the document still parses, and it parses into the auth that was asked
/// for. A panel whose changes have to be believed is a panel that owns the state - which is
/// the thing Sling refuses to be.
/// </para>
/// <para>
/// So every rewrite test reads the result back through the parser instead of comparing
/// text. Comparing text asserts the formatting; parsing asserts the meaning.
/// </para>
/// </remarks>
public sealed class RequestAuthTests
{
    [Fact]
    public void A_request_with_no_credential_says_so()
    {
        var view = Describe("GET https://api.example.com/orders");

        Assert.Equal(AuthOrigin.None, view.Origin);
        Assert.Equal(AuthScheme.None, view.Scheme);
        Assert.Equal(0, view.Line);
    }

    [Fact]
    public void A_bearer_header_is_read_with_its_line_and_its_variable()
    {
        var view = Describe(
            """
            GET https://api.example.com/orders
            Authorization: Bearer {{token}}
            """);

        Assert.Equal(AuthOrigin.Header, view.Origin);
        Assert.Equal(AuthScheme.Bearer, view.Scheme);
        Assert.Equal(2, view.Line);
        Assert.Equal("{{token}}", view.Written);
        Assert.Equal("token", view.Variable);
    }

    [Fact]
    public void A_literal_credential_names_no_variable()
    {
        var view = Describe(
            """
            GET https://api.example.com/orders
            Authorization: Bearer abc123
            """);

        Assert.Equal("abc123", view.Written);
        Assert.Null(view.Variable);
    }

    /// <summary>
    /// A value that is a variable plus something else has no single source, and saying it
    /// has one would be a sentence that is wrong rather than incomplete.
    /// </summary>
    [Fact]
    public void A_value_that_is_more_than_one_reference_names_no_variable()
    {
        Assert.Null(RequestAuth.SoleVariable("{{a}}{{b}}"));
        Assert.Null(RequestAuth.SoleVariable("prefix-{{a}}"));
        Assert.Equal("a", RequestAuth.SoleVariable("  {{ a }}  "));
    }

    [Fact]
    public void An_api_key_header_is_recognised()
    {
        var view = Describe(
            """
            GET https://api.example.com/orders
            X-API-Key: {{api_key}}
            """);

        Assert.Equal(AuthScheme.ApiKeyHeader, view.Scheme);
        Assert.Equal("X-API-Key", view.HeaderName);
        Assert.Equal("api_key", view.Variable);
    }

    /// <summary>
    /// There is no rule that makes a header a credential, so anything cleverer than a closed
    /// list would report an unrelated header as auth - and then offer to rewrite it.
    /// </summary>
    [Fact]
    public void A_header_that_is_not_on_the_list_is_not_auth()
    {
        var view = Describe(
            """
            GET https://api.example.com/orders
            X-Correlation-Key: 42
            """);

        Assert.Equal(AuthOrigin.None, view.Origin);
    }

    [Fact]
    public void A_scheme_sling_does_not_write_is_reported_rather_than_guessed()
    {
        var view = Describe(
            """
            GET https://api.example.com/orders
            Authorization: Negotiate {{ticket}}
            """);

        Assert.Equal(AuthScheme.Unrecognized, view.Scheme);
        Assert.Equal("Negotiate {{ticket}}", view.Written);
    }

    [Fact]
    public void An_auth_block_is_read_as_the_grant_it_declares()
    {
        var view = Describe(
            """
            # @auth oauth2
            # @token-url {{auth_base}}/oauth2/token
            # @client-id {{client_id}}
            # @client-secret {{client_secret}}
            # @scope orders.read
            GET https://api.example.com/orders
            """);

        Assert.Equal(AuthOrigin.Grant, view.Origin);
        Assert.Equal(AuthScheme.ClientCredentials, view.Scheme);
        Assert.Equal(1, view.Line);
        Assert.Equal("orders.read", view.Grant?.Scope);
    }

    /// <summary>
    /// The sender puts the fetched token in the <c>Authorization</c> header, over whatever
    /// was written there. A panel reporting the header would be naming the value that loses.
    /// </summary>
    [Fact]
    public void A_grant_wins_over_a_header_because_that_is_what_the_sender_does()
    {
        var view = Describe(
            """
            # @auth oauth2
            # @token-url https://auth.example.com/token
            # @client-id id
            # @client-secret {{secret}}
            GET https://api.example.com/orders
            Authorization: Bearer {{stale}}
            """);

        Assert.Equal(AuthOrigin.Grant, view.Origin);
    }

    [Fact]
    public void Adding_a_bearer_header_to_a_request_that_had_none()
    {
        var rewritten = Rewrite(
            "GET https://api.example.com/orders",
            new AuthSetting(AuthScheme.Bearer, Credential: "{{token}}"));

        var view = Describe(rewritten);

        Assert.Equal(AuthScheme.Bearer, view.Scheme);
        Assert.Equal("{{token}}", view.Written);
    }

    [Fact]
    public void An_existing_bearer_header_is_replaced_in_place()
    {
        var rewritten = Rewrite(
            """
            ### orders
            # @name orders
            GET https://api.example.com/orders
            Accept: application/json
            Authorization: Bearer {{old}}
            """,
            new AuthSetting(AuthScheme.Bearer, Credential: "{{new}}"));

        var document = RequestDocumentParser.Parse(rewritten);
        var block = document.Requests.Single();

        Assert.Equal("{{new}}", RequestAuth.Describe(block).Written);

        // Everything else about the request survives, which is the point of editing lines
        // rather than re-emitting the request.
        Assert.Equal("orders", block.Name);
        Assert.Contains(block.Headers, h => h.Name == "Accept");
    }

    [Fact]
    public void Removing_auth_takes_the_header_out()
    {
        var rewritten = Rewrite(
            """
            GET https://api.example.com/orders
            Accept: application/json
            Authorization: Bearer {{token}}
            """,
            new AuthSetting(AuthScheme.None));

        var document = RequestDocumentParser.Parse(rewritten);

        Assert.Equal(AuthOrigin.None, RequestAuth.Describe(document.Requests.Single()).Origin);
        Assert.Contains(document.Requests.Single().Headers, h => h.Name == "Accept");
    }

    [Fact]
    public void Switching_from_a_header_to_a_grant_removes_the_header()
    {
        var rewritten = Rewrite(
            """
            GET https://api.example.com/orders
            Authorization: Bearer {{token}}
            """,
            new AuthSetting(
                AuthScheme.ClientCredentials,
                Grant: new GrantFields(
                    "{{auth_base}}/oauth2/token",
                    "{{client_id}}",
                    "{{client_secret}}",
                    "orders.read",
                    null,
                    ClientAuthPlacement.BasicHeader)));

        var document = RequestDocumentParser.Parse(rewritten);
        var block = document.Requests.Single();

        Assert.Empty(document.Diagnostics);
        Assert.Equal(AuthOrigin.Grant, RequestAuth.Describe(block).Origin);
        Assert.DoesNotContain(block.Headers, h => h.Name == "Authorization");
        Assert.Equal("orders.read", block.Auth?.Scope);
    }

    [Fact]
    public void Switching_from_a_grant_to_a_header_removes_every_directive()
    {
        var rewritten = Rewrite(
            """
            # @auth oauth2
            # @token-url https://auth.example.com/token
            # @client-id id
            # @client-secret {{secret}}
            # @scope orders.read
            # @client-auth body
            GET https://api.example.com/orders
            """,
            new AuthSetting(AuthScheme.Bearer, Credential: "{{token}}"));

        var document = RequestDocumentParser.Parse(rewritten);

        // Not one of them left behind: an orphaned '@token-url' is an error rather than a
        // comment, so half a removal is a document that will not send.
        Assert.Empty(document.Diagnostics);
        Assert.Null(document.Requests.Single().Auth);
        Assert.Equal(AuthScheme.Bearer, RequestAuth.Describe(document.Requests.Single()).Scheme);
    }

    [Fact]
    public void Changing_the_header_a_key_travels_in_moves_it()
    {
        var rewritten = Rewrite(
            """
            GET https://api.example.com/orders
            X-API-Key: {{api_key}}
            """,
            new AuthSetting(AuthScheme.Bearer, Credential: "{{token}}"));

        var block = RequestDocumentParser.Parse(rewritten).Requests.Single();

        Assert.DoesNotContain(block.Headers, h => h.Name == "X-API-Key");
        Assert.Equal(AuthScheme.Bearer, RequestAuth.Describe(block).Scheme);
    }

    /// <summary>
    /// An editor does not add a trailing newline, so the last line of most documents has
    /// none - and an insertion at the end of the text would otherwise be welded on to it.
    /// </summary>
    [Fact]
    public void A_header_added_to_a_document_with_no_trailing_newline_still_parses()
    {
        var rewritten = Rewrite(
            "GET https://api.example.com/orders",
            new AuthSetting(AuthScheme.Bearer, Credential: "{{token}}"));

        var block = RequestDocumentParser.Parse(rewritten).Requests.Single();

        Assert.Equal("https://api.example.com/orders", block.Target);
        Assert.Single(block.Headers);
    }

    /// <summary>
    /// A file from a CRLF checkout must not gain one LF line in the middle of it: invisible
    /// in the editor, and a whole-file diff for whoever reviews it next.
    /// </summary>
    [Fact]
    public void An_edit_uses_the_document_own_line_ending()
    {
        var rewritten = Rewrite(
            "GET https://api.example.com/orders\r\nAccept: application/json\r\n",
            new AuthSetting(AuthScheme.Bearer, Credential: "{{token}}"));

        var withoutCrlf = rewritten.Replace("\r\n", string.Empty, StringComparison.Ordinal);

        Assert.DoesNotContain('\n', withoutCrlf);
        Assert.DoesNotContain('\r', withoutCrlf);
    }

    [Fact]
    public void Only_the_request_under_the_caret_is_touched()
    {
        var text =
            """
            ### one
            GET https://api.example.com/one
            Authorization: Bearer {{one}}

            ### two
            GET https://api.example.com/two
            Authorization: Bearer {{two}}
            """;

        var document = RequestDocumentParser.Parse(text);
        var edits = AuthDocumentEditor.Rewrite(
            text,
            document.Requests[1],
            new AuthSetting(AuthScheme.Bearer, Credential: "{{changed}}"));

        var rewritten = RequestDocumentParser.Parse(TextEdit.Apply(text, edits));

        Assert.Equal("{{one}}", RequestAuth.Describe(rewritten.Requests[0]).Written);
        Assert.Equal("{{changed}}", RequestAuth.Describe(rewritten.Requests[1]).Written);
    }

    [Fact]
    public void The_default_credential_placement_is_left_unwritten()
    {
        var rewritten = Rewrite(
            "GET https://api.example.com/orders",
            new AuthSetting(
                AuthScheme.ClientCredentials,
                Grant: new GrantFields("https://auth.example.com/token", "id", "{{secret}}", null, null, ClientAuthPlacement.BasicHeader)));

        // A directive restating what would happen anyway is a line the next reader has to
        // look up before deciding it says nothing.
        Assert.DoesNotContain("@client-auth", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("@scope", rewritten, StringComparison.Ordinal);
        Assert.Equal(ClientAuthPlacement.BasicHeader, Parse(rewritten).Auth?.Placement);
    }

    [Fact]
    public void A_form_body_placement_is_written_because_it_is_not_the_default()
    {
        var rewritten = Rewrite(
            "GET https://api.example.com/orders",
            new AuthSetting(
                AuthScheme.ClientCredentials,
                Grant: new GrantFields("https://auth.example.com/token", "id", "{{secret}}", null, null, ClientAuthPlacement.FormBody)));

        Assert.Equal(ClientAuthPlacement.FormBody, Parse(rewritten).Auth?.Placement);
    }

    private static RequestAuthView Describe(string text) => RequestAuth.Describe(Parse(text));

    private static RequestBlock Parse(string text) => RequestDocumentParser.Parse(text).Requests.Single();

    private static string Rewrite(string text, AuthSetting setting) =>
        TextEdit.Apply(text, AuthDocumentEditor.Rewrite(text, Parse(text), setting));
}
