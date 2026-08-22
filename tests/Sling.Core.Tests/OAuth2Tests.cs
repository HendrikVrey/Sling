using System.Text;
using Sling.Core.Auth;

namespace Sling.Core.Tests;

/// <summary>
/// The client-credentials grant: reading a token response, refusing one that cannot
/// safely be used, and building the request that asks for it.
/// </summary>
public sealed class OAuth2Tests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_token_response_yields_a_bearer_token_with_an_expiry()
    {
        Assert.True(OAuth2Token.TryParseResponse(
            """{"access_token":"abc123","token_type":"bearer","expires_in":3600}""",
            Now,
            out var token,
            out _));

        Assert.Equal("abc123", token.AccessToken);

        // Normalised, because some gateways compare the scheme case-sensitively.
        Assert.Equal("Bearer", token.TokenType);
        Assert.Equal("Bearer abc123", token.HeaderValue);
        Assert.Equal(Now.AddSeconds(3600), token.ExpiresUtc);
    }

    [Fact]
    public void A_token_response_with_no_token_type_is_assumed_to_be_a_bearer()
    {
        Assert.True(OAuth2Token.TryParseResponse("""{"access_token":"abc123"}""", Now, out var token, out _));
        Assert.Equal("Bearer", token.TokenType);
    }

    [Fact]
    public void An_expires_in_sent_as_a_string_is_read()
    {
        // Not in RFC 6749, and sent by real servers. Refusing it would mean the feature
        // does not work against them for a reason the user cannot fix.
        Assert.True(OAuth2Token.TryParseResponse(
            """{"access_token":"abc123","expires_in":"600"}""",
            Now,
            out var token,
            out _));

        Assert.Equal(Now.AddSeconds(600), token.ExpiresUtc);
    }

    [Fact]
    public void A_token_with_no_stated_lifetime_has_no_expiry_and_is_not_usable_later()
    {
        Assert.True(OAuth2Token.TryParseResponse("""{"access_token":"abc123"}""", Now, out var token, out _));

        Assert.Null(token.ExpiresUtc);

        // Which is what stops it being cached: an unknown lifetime must not become a
        // guessed one.
        Assert.False(token.IsUsableAt(Now));
    }

    [Fact]
    public void A_token_is_spent_before_its_stated_expiry()
    {
        Assert.True(OAuth2Token.TryParseResponse(
            """{"access_token":"abc123","expires_in":60}""",
            Now,
            out var token,
            out _));

        Assert.True(token.IsUsableAt(Now));

        // The margin covers the flight time of the request it is about to be used on and
        // any clock skew against the authorization server.
        Assert.False(token.IsUsableAt(Now.AddSeconds(60) - OAuth2Token.ExpiryMargin));
    }

    [Fact]
    public void An_rfc_6749_error_response_is_reported_as_the_refusal_it_is()
    {
        Assert.False(OAuth2Token.TryParseResponse(
            """{"error":"invalid_client","error_description":"Client authentication failed"}""",
            Now,
            out _,
            out var error));

        // The server's own words, which are the answer the user needs — "no access_token
        // field" describes the same response and helps nobody.
        Assert.Contains("invalid_client", error, StringComparison.Ordinal);
        Assert.Contains("Client authentication failed", error, StringComparison.Ordinal);
    }

    [Theory]
    // Sling.md §5.7 applied to the token: it is a value taken out of a response body, and
    // appending one carrying CR, LF or NUL to 'Authorization: Bearer ' would add headers
    // of its own to every request the grant covers.
    [InlineData("abc\\u000Adef")]
    [InlineData("abc\\u000Ddef")]
    [InlineData("abc\\u0000def")]
    public void A_token_that_could_inject_a_header_is_refused(string encoded)
    {
        var json = "{\"access_token\":\"" + encoded + "\"}";

        Assert.False(OAuth2Token.TryParseResponse(json, Now, out _, out var error));
        Assert.Contains("cannot go in a header", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_refusal_message_does_not_contain_the_token()
    {
        const string Secret = "zzq-distinctive-secret";

        var json = "{\"access_token\":\"" + Secret + "\\u000Ainjected\"}";

        Assert.False(OAuth2Token.TryParseResponse(json, Now, out _, out var error));

        // A needle only the excluded value could supply. Asserting on a fragment the
        // message legitimately contains would be an assertion that can never fail.
        Assert.DoesNotContain(Secret, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_type_that_is_not_a_header_token_is_refused()
    {
        Assert.False(OAuth2Token.TryParseResponse(
            """{"access_token":"abc","token_type":"Bearer, X-Admin: 1"}""",
            Now,
            out _,
            out var error));

        Assert.Contains("not a usable token type", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"access_token":42}""")]
    [InlineData("{}")]
    public void A_response_that_is_not_a_token_response_is_reported_rather_than_thrown(string json) =>
        Assert.False(OAuth2Token.TryParseResponse(json, Now, out _, out _));

    [Fact]
    public void A_records_to_string_does_not_print_the_client_secret()
    {
        // A record's generated ToString prints every property, which is the quietest way a
        // credential reaches a screen: it needs nobody to have written the secret into a
        // message, only for someone to have interpolated the object into one.
        var grant = new ResolvedOAuth2Grant(
            new Uri("https://auth.example.com/token"),
            "client",
            "zzq-distinctive-secret",
            null,
            null,
            ClientAuthPlacement.BasicHeader,
            1);

        Assert.DoesNotContain("zzq-distinctive-secret", grant.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("zzq-distinctive-secret", grant.CacheKey.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_token_request_is_a_form_encoded_post_with_basic_credentials()
    {
        var request = OAuth2TokenRequest.Build(Grant(ClientAuthPlacement.BasicHeader, scope: "orders.read"));

        Assert.Equal("POST", request.Method);
        Assert.Equal("https://auth.example.com/token", request.Url.ToString());
        Assert.Equal("application/x-www-form-urlencoded", HeaderOf(request, "Content-Type"));

        // RFC 6749 §2.3.1: form-urlencoded, then joined with a colon, then base64. Skipping
        // the encoding step is invisible for an alphanumeric secret and wrong for one
        // holding a colon, which the server would then split in the wrong place.
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("cli%3Aent:s%2Fcret"));
        Assert.Equal(expected, HeaderOf(request, "Authorization"));

        var body = Encoding.UTF8.GetString(request.Body!);
        Assert.Equal("grant_type=client_credentials&scope=orders.read", body);
    }

    [Fact]
    public void The_body_placement_puts_the_credentials_in_the_form()
    {
        var request = OAuth2TokenRequest.Build(Grant(ClientAuthPlacement.FormBody));

        Assert.Null(HeaderOf(request, "Authorization"));

        var body = Encoding.UTF8.GetString(request.Body!);

        // Percent-encoded, which is what makes injection structural rather than checked: a
        // secret containing '&' cannot add a form field.
        Assert.Equal("grant_type=client_credentials&client_id=cli%3Aent&client_secret=s%2Fcret", body);
    }

    [Fact]
    public void An_audience_is_sent_when_one_is_given()
    {
        var request = OAuth2TokenRequest.Build(
            Grant(ClientAuthPlacement.BasicHeader) with { Audience = "https://api.example.com" });

        Assert.Contains(
            "audience=https%3A%2F%2Fapi.example.com",
            Encoding.UTF8.GetString(request.Body!),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_token_request_never_carries_a_grant_of_its_own()
    {
        // A token request that itself needed a token would be a loop with no base case.
        Assert.Null(OAuth2TokenRequest.Build(Grant(ClientAuthPlacement.BasicHeader)).Auth);
    }

    [Fact]
    public void The_token_response_is_never_stored_under_a_chainable_name()
    {
        // A null name is what keeps the raw token out of the response store, where any
        // later request could interpolate it by hand.
        Assert.Null(OAuth2TokenRequest.Build(Grant(ClientAuthPlacement.BasicHeader)).Name);
    }

    [Fact]
    public void The_cache_key_separates_grants_that_would_yield_different_tokens()
    {
        var read = Grant(ClientAuthPlacement.BasicHeader, scope: "orders.read");

        // Leaving the scope out would hand a request asking for orders.write a token that
        // only carries orders.read, and the API's refusal would say nothing about a cache.
        Assert.NotEqual(read.CacheKey, (read with { Scope = "orders.write" }).CacheKey);

        // A rotated secret takes effect at once rather than at the old token's expiry.
        Assert.NotEqual(read.CacheKey, (read with { ClientSecret = "rotated" }).CacheKey);

        // The source line is not part of what the token depends on.
        Assert.Equal(read.CacheKey, (read with { Line = 99 }).CacheKey);
    }

    private static ResolvedOAuth2Grant Grant(ClientAuthPlacement placement, string? scope = null) =>
        new(
            new Uri("https://auth.example.com/token"),
            // Characters that have to survive form-encoding: a colon would otherwise split
            // the Basic credential in the wrong place.
            "cli:ent",
            "s/cret",
            scope,
            null,
            placement,
            1);

    private static string? HeaderOf(Core.Variables.ResolvedRequest request, string name) =>
        request.Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}
