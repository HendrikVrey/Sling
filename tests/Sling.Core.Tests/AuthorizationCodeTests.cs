using System.Security.Cryptography;
using System.Text;
using Sling.Core.Auth;
using Sling.Core.Parsing;
using Sling.Core.Variables;

namespace Sling.Core.Tests;

/// <summary>
/// The authorization-code flow's document half: what the block declares, what is refused,
/// and what goes in the URL the browser is sent to.
/// </summary>
/// <remarks>
/// The browser round trip is exercised in <c>Sling.Http.Tests</c> against a real loopback
/// listener. Everything here is pure, which is where the parameter names, the PKCE method and
/// the loopback rule can be checked without one.
/// </remarks>
public sealed class AuthorizationCodeTests
{
    private const string Block = """
        # @auth oauth2-code
        # @authorize-url https://auth.example.com/authorize
        # @token-url https://auth.example.com/token
        # @client-id my-client
        # @redirect-uri http://127.0.0.1:7890/callback
        # @scope orders.read
        GET https://api.example.com/orders
        """;

    [Fact]
    public void The_block_parses_as_an_authorization_code_grant()
    {
        var grant = Parse(Block).Auth;

        Assert.NotNull(grant);
        Assert.Equal(OAuth2Flow.AuthorizationCode, grant.Flow);
        Assert.Equal("https://auth.example.com/authorize", grant.AuthorizeUrl);
        Assert.Equal("http://127.0.0.1:7890/callback", grant.RedirectUri);
    }

    /// <summary>
    /// A public client has no secret to keep, and PKCE is what replaces it. Demanding one
    /// would refuse the commonest correct configuration for a desktop application.
    /// </summary>
    [Fact]
    public void A_client_secret_is_not_required_for_the_code_flow()
    {
        Assert.Empty(RequestDocumentParser.Parse(Block).Diagnostics);
    }

    [Fact]
    public void A_client_secret_is_still_required_for_client_credentials()
    {
        var document = RequestDocumentParser.Parse("""
            # @auth oauth2
            # @token-url https://auth.example.com/token
            # @client-id my-client
            GET https://api.example.com/orders
            """);

        Assert.Contains(
            document.Diagnostics,
            d => d.Message.Contains("@client-secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("@authorize-url")]
    [InlineData("@redirect-uri")]
    public void The_code_flow_names_what_it_is_missing(string directive)
    {
        var without = string.Join(
            '\n',
            Block.Split('\n').Where(l => !l.Contains(directive.TrimStart('@'), StringComparison.Ordinal)));

        var document = RequestDocumentParser.Parse(without);

        Assert.Contains(document.Diagnostics, d => d.Message.Contains(directive, StringComparison.Ordinal));
    }

    /// <summary>
    /// Two directives that mean nothing without a browser step. Accepting them silently on a
    /// client-credentials block would be accepting a document that says something it does not
    /// do.
    /// </summary>
    [Fact]
    public void The_browser_directives_are_refused_on_a_client_credentials_block()
    {
        var document = RequestDocumentParser.Parse("""
            # @auth oauth2
            # @token-url https://auth.example.com/token
            # @authorize-url https://auth.example.com/authorize
            # @client-id my-client
            # @client-secret s3cret
            GET https://api.example.com/orders
            """);

        Assert.Contains(
            document.Diagnostics,
            d => d.Message.Contains("'@authorize-url' and '@redirect-uri' belong", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://example.com/callback", "must be an http address on this machine")]
    [InlineData("http://example.com:7890/callback", "must be an http address on this machine")]
    [InlineData("http://127.0.0.1/callback", "needs an explicit port")]
    public void A_redirect_that_is_not_loopback_with_a_port_is_refused(string redirect, string expected)
    {
        var text = Block.Replace("http://127.0.0.1:7890/callback", redirect, StringComparison.Ordinal);
        var document = RequestDocumentParser.Parse(text);

        var resolution = RequestResolver.Resolve(document, document.Requests[0], new ResolutionContext());

        Assert.Contains(resolution.Errors, e => e.Message.Contains(expected, StringComparison.Ordinal));
    }

    /// <summary>
    /// The request that produces a code is as worth protecting as the one that redeems it: a
    /// code intercepted in flight is an account.
    /// </summary>
    [Fact]
    public void A_plain_http_authorization_endpoint_is_refused()
    {
        var text = Block.Replace(
            "https://auth.example.com/authorize",
            "http://auth.example.com/authorize",
            StringComparison.Ordinal);

        var document = RequestDocumentParser.Parse(text);
        var resolution = RequestResolver.Resolve(document, document.Requests[0], new ResolutionContext());

        Assert.Contains(
            resolution.Errors,
            e => e.Message.Contains("'@authorize-url' must use https", StringComparison.Ordinal));
    }

    [Fact]
    public void The_authorization_url_carries_everything_the_specification_asks_for()
    {
        var grant = Resolve(Block);
        var pkce = Pkce.Create();

        var url = OAuth2AuthorizeRequest.Build(grant, pkce.Challenge, "the-state");
        var query = url.Query;

        Assert.StartsWith("https://auth.example.com/authorize?", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("response_type=code", query, StringComparison.Ordinal);
        Assert.Contains("client_id=my-client", query, StringComparison.Ordinal);
        Assert.Contains("state=the-state", query, StringComparison.Ordinal);
        Assert.Contains("code_challenge=" + pkce.Challenge, query, StringComparison.Ordinal);
        Assert.Contains("scope=orders.read", query, StringComparison.Ordinal);
        Assert.Contains(
            "redirect_uri=" + Uri.EscapeDataString("http://127.0.0.1:7890/callback"),
            query,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A client that offers <c>plain</c> can be asked for <c>plain</c>, which removes the one
    /// thing protecting the code.
    /// </summary>
    [Fact]
    public void The_challenge_method_is_always_s256()
    {
        var url = OAuth2AuthorizeRequest.Build(Resolve(Block), Pkce.Create().Challenge, "s");

        Assert.Contains("code_challenge_method=S256", url.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("plain", url.Query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Several providers put a tenant or a connection in the authorization URL's own query,
    /// and dropping it sends the browser somewhere that looks right and answers differently.
    /// </summary>
    [Fact]
    public void A_query_written_on_the_authorization_url_survives()
    {
        var text = Block.Replace(
            "https://auth.example.com/authorize",
            "https://auth.example.com/authorize?tenant=acme",
            StringComparison.Ordinal);

        var url = OAuth2AuthorizeRequest.Build(Resolve(text), Pkce.Create().Challenge, "s");

        Assert.Contains("tenant=acme", url.Query, StringComparison.Ordinal);
        Assert.Contains("response_type=code", url.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void The_challenge_is_the_sha256_of_the_verifier_in_base64url()
    {
        var pkce = Pkce.Create();

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(pkce.Verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        Assert.Equal(expected, pkce.Challenge);
    }

    [Fact]
    public void A_verifier_is_long_enough_and_uses_only_what_the_specification_allows()
    {
        var verifier = Pkce.Create().Verifier;

        // RFC 7636 §4.1: at least 43 characters, at most 128, from the unreserved set.
        Assert.InRange(verifier.Length, 43, 128);
        Assert.All(verifier, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~'));
    }

    [Fact]
    public void Two_attempts_never_share_a_verifier()
    {
        Assert.NotEqual(Pkce.Create().Verifier, Pkce.Create().Verifier);
        Assert.NotEqual(Pkce.State(), Pkce.State());
    }

    /// <summary>
    /// A record's generated <c>ToString</c> prints every property, and the verifier is the
    /// half that has to stay in the process.
    /// </summary>
    [Fact]
    public void Printing_a_pkce_pair_does_not_print_the_verifier()
    {
        var pkce = Pkce.Create();

        Assert.DoesNotContain(pkce.Verifier, pkce.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_code_exchange_sends_the_verifier_and_the_redirect()
    {
        var request = OAuth2TokenRequest.BuildCodeExchange(Resolve(Block), "the-code", "the-verifier");
        var body = Encoding.UTF8.GetString(request.Body!);

        Assert.Equal("POST", request.Method);
        Assert.Contains("grant_type=authorization_code", body, StringComparison.Ordinal);
        Assert.Contains("code=the-code", body, StringComparison.Ordinal);
        Assert.Contains("code_verifier=the-verifier", body, StringComparison.Ordinal);
        Assert.Contains(
            "redirect_uri=" + Uri.EscapeDataString("http://127.0.0.1:7890/callback"),
            body,
            StringComparison.Ordinal);

        // A public client has no Basic header to send, so its identity travels in the body.
        Assert.Contains("client_id=my-client", body, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Headers, h => h.Name == "Authorization");
    }

    [Fact]
    public void A_confidential_client_still_authenticates_with_its_secret()
    {
        var text = Block.Replace(
            "# @client-id my-client",
            "# @client-id my-client\n# @client-secret s3cret",
            StringComparison.Ordinal);

        var request = OAuth2TokenRequest.BuildCodeExchange(Resolve(text), "the-code", "the-verifier");

        Assert.Contains(
            request.Headers,
            h => h.Name == "Authorization" && h.Value.StartsWith("Basic ", StringComparison.Ordinal));
    }

    /// <summary>
    /// One flow is the application acting as itself and the other is it acting for a person.
    /// A cache that could not tell them apart would hand one a token meant for the other.
    /// </summary>
    [Fact]
    public void The_two_flows_do_not_share_a_cache_entry()
    {
        var machine = new TokenCacheKey(
            "https://auth.example.com/token",
            "my-client",
            string.Empty,
            null,
            null,
            ClientAuthPlacement.BasicHeader);

        var person = machine with { Flow = OAuth2Flow.AuthorizationCode };

        Assert.NotEqual(machine.Fingerprint, person.Fingerprint);
    }

    private static Documents.RequestBlock Parse(string text) =>
        RequestDocumentParser.Parse(text).Requests.Single();

    private static ResolvedOAuth2Grant Resolve(string text)
    {
        var document = RequestDocumentParser.Parse(text);
        var resolution = RequestResolver.Resolve(document, document.Requests[0], new ResolutionContext());

        Assert.Empty(resolution.Errors);

        return resolution.Request!.Auth!;
    }
}
