using Sling.Core.Auth;
using Sling.Core.Documents;
using Sling.Core.Parsing;
using Sling.Core.Variables;

namespace Sling.Core.Tests;

/// <summary>
/// The <c># @auth oauth2</c> block: what the parser makes of it, and what resolution does
/// with the result.
/// </summary>
/// <remarks>
/// A divergence from the reference dialect, which has no syntax for this at all. Every
/// rule here is recorded in <c>docs/http-dialect.md</c> as well, because a dialect
/// difference nobody wrote down is a bug report waiting to be filed.
/// </remarks>
public sealed class AuthDirectiveTests
{
    [Fact]
    public void A_complete_block_becomes_a_grant()
    {
        var block = ParseOne("""
            # @auth oauth2
            # @token-url https://auth.example.com/token
            # @client-id my-client
            # @client-secret {{client_secret}}
            # @scope orders.read orders.write
            # @audience https://api.example.com
            GET https://api.example.com/orders
            """);

        var grant = Assert.IsType<OAuth2Grant>(block.Auth);

        Assert.Equal("https://auth.example.com/token", grant.TokenUrl);
        Assert.Equal("my-client", grant.ClientId);

        // Still braced. Keeping the unresolved form is what lets a diagnostic quote the
        // grant without printing the secret.
        Assert.Equal("{{client_secret}}", grant.ClientSecret);
        Assert.Equal("orders.read orders.write", grant.Scope);
        Assert.Equal("https://api.example.com", grant.Audience);
        Assert.Equal(ClientAuthPlacement.BasicHeader, grant.Placement);
    }

    [Fact]
    public void The_longer_spelling_of_the_grant_is_accepted()
    {
        var block = ParseOne("""
            # @auth oauth2 client_credentials
            # @token-url https://auth.example.com/token
            # @client-id c
            # @client-secret s
            GET https://api.example.com/orders
            """);

        Assert.NotNull(block.Auth);
    }

    [Fact]
    public void Client_auth_selects_where_the_credentials_go()
    {
        var block = ParseOne("""
            # @auth oauth2
            # @token-url https://auth.example.com/token
            # @client-id c
            # @client-secret s
            # @client-auth body
            GET https://api.example.com/orders
            """);

        Assert.Equal(ClientAuthPlacement.FormBody, block.Auth!.Placement);
    }

    [Fact]
    public void The_authorization_code_flow_is_recognised_by_either_spelling()
    {
        // It used to be refused with a sentence about needing a browser, which was true and
        // was not a reason: Sling has the sender, the cache, the exchange and the https
        // enforcement, and what was missing was a loopback listener and PKCE.
        foreach (var spelling in new[] { "oauth2-code", "oauth2 authorization_code" })
        {
            var document = RequestDocumentParser.Parse($"""
                # @auth {spelling}
                # @authorize-url https://auth.example.com/authorize
                # @token-url https://auth.example.com/token
                # @client-id my-client
                # @redirect-uri http://127.0.0.1:7890/callback
                GET https://api.example.com/orders
                """);

            Assert.Empty(Errors(document));
            Assert.Equal(OAuth2Flow.AuthorizationCode, document.Requests.Single().Auth?.Flow);
        }
    }

    [Fact]
    public void A_scheme_sling_does_not_perform_says_what_it_does()
    {
        var document = RequestDocumentParser.Parse("""
            # @auth device_code
            GET https://api.example.com/orders
            """);

        var error = Assert.Single(Errors(document));

        Assert.Contains("oauth2", error.Message, StringComparison.Ordinal);
        Assert.Contains("oauth2-code", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configuration_directive_without_an_auth_block_is_an_error()
    {
        // Not a comment worth ignoring. A document that quietly does not authenticate
        // fails at the API, several layers from the line that caused it.
        var document = RequestDocumentParser.Parse("""
            # @client-secret {{secret}}
            GET https://api.example.com/orders
            """);

        var error = Assert.Single(Errors(document));
        Assert.Contains("only means something under", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_missing_a_required_directive_names_what_is_missing()
    {
        var document = RequestDocumentParser.Parse("""
            # @auth oauth2
            # @token-url https://auth.example.com/token
            GET https://api.example.com/orders
            """);

        var error = Assert.Single(Errors(document));

        Assert.Contains("@client-id", error.Message, StringComparison.Ordinal);
        Assert.Contains("@client-secret", error.Message, StringComparison.Ordinal);

        // Reported against the '@auth' line: that is where the block was opened and where
        // a reader looks to see what it declares.
        Assert.Equal(1, error.Line);
    }

    [Fact]
    public void A_second_auth_block_on_one_request_is_an_error()
    {
        var document = RequestDocumentParser.Parse("""
            # @auth oauth2
            # @auth oauth2
            GET https://api.example.com/orders
            """);

        Assert.Contains(Errors(document), e => e.Message.Contains("one way", StringComparison.Ordinal));
    }

    [Fact]
    public void A_grant_does_not_leak_onto_the_next_request()
    {
        // The sharper version of the '@name' rule: a leaked grant would send the next
        // request a bearer token it never asked for.
        var document = RequestDocumentParser.Parse("""
            # @auth oauth2
            # @token-url https://auth.example.com/token
            # @client-id c
            # @client-secret s
            GET https://api.example.com/orders

            ###
            GET https://api.example.com/public
            """);

        Assert.NotNull(document.Requests[0].Auth);
        Assert.Null(document.Requests[1].Auth);
    }

    [Fact]
    public void A_bad_client_auth_value_says_what_is_allowed()
    {
        var document = RequestDocumentParser.Parse("""
            # @auth oauth2
            # @token-url https://auth.example.com/token
            # @client-id c
            # @client-secret s
            # @client-auth query
            GET https://api.example.com/orders
            """);

        var error = Assert.Single(Errors(document));
        Assert.Contains("'basic' or 'body'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolution_substitutes_the_grants_variables()
    {
        var document = RequestDocumentParser.Parse("""
            @auth_base = https://auth.example.com
            @secret = s3cret

            # @auth oauth2
            # @token-url {{auth_base}}/token
            # @client-id my-client
            # @client-secret {{secret}}
            GET https://api.example.com/orders
            """);

        var result = RequestResolver.Resolve(document, document.Requests[0], new ResolutionContext());

        Assert.Empty(result.Errors);

        var grant = result.Request!.Auth!;

        Assert.Equal("https://auth.example.com/token", grant.TokenUrl.ToString());
        Assert.Equal("s3cret", grant.ClientSecret);
    }

    [Fact]
    public void A_token_url_over_plain_http_is_refused()
    {
        // A client secret and the token it buys are the two most valuable strings in the
        // process; plain HTTP puts both on the wire in clear.
        var result = ResolveWithTokenUrl("http://auth.example.com/token");

        var error = Assert.Single(result.Errors);
        Assert.Contains("must use https", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_url_on_localhost_may_use_plain_http()
    {
        // The same rule browsers use for a secure context. Refusing it would make a mock
        // authorization server impossible to use.
        Assert.Empty(ResolveWithTokenUrl("http://localhost:8080/token").Errors);
        Assert.Empty(ResolveWithTokenUrl("http://127.0.0.1:8080/token").Errors);
    }

    [Fact]
    public void A_token_url_carrying_userinfo_is_refused()
    {
        var result = ResolveWithTokenUrl("https://user:pass@auth.example.com/token");

        var error = Assert.Single(result.Errors);
        Assert.Contains("before the host", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_scope_is_absent_rather_than_empty()
    {
        // Sending 'scope=' is not the same request as sending no scope: some servers
        // reject it, others issue a token with no scopes at all.
        var document = RequestDocumentParser.Parse("""
            @scope =

            # @auth oauth2
            # @token-url https://auth.example.com/token
            # @client-id c
            # @client-secret s
            # @scope {{scope}}
            GET https://api.example.com/orders
            """);

        var result = RequestResolver.Resolve(document, document.Requests[0], new ResolutionContext());

        Assert.Empty(result.Errors);
        Assert.Null(result.Request!.Auth!.Scope);
    }

    private static ResolutionResult ResolveWithTokenUrl(string tokenUrl)
    {
        var document = RequestDocumentParser.Parse($"""
            # @auth oauth2
            # @token-url {tokenUrl}
            # @client-id c
            # @client-secret s
            GET https://api.example.com/orders
            """);

        Assert.Empty(Errors(document));

        return RequestResolver.Resolve(document, document.Requests[0], new ResolutionContext());
    }

    private static RequestBlock ParseOne(string text)
    {
        var document = RequestDocumentParser.Parse(text);

        Assert.Empty(Errors(document));
        return Assert.Single(document.Requests);
    }

    private static List<ParseDiagnostic> Errors(RequestDocument document) =>
        document.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
}
