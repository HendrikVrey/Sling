using System.Text;
using System.Text.Json;
using Sling.Core.Auth;

namespace Sling.Core.Tests;

/// <summary>
/// Reading a JWT, and the one thing this deliberately never does.
/// </summary>
/// <remarks>
/// Nothing here verifies a signature, so nothing here may say a token is valid. The tests
/// assert the vocabulary as well as the arithmetic, because the wording is the safety
/// property: somebody told a token is "valid" by a tool that checked no signature will act
/// on it.
/// </remarks>
public sealed class JwtTests
{
    [Fact]
    public void A_token_is_recognised_by_its_shape_and_its_header()
    {
        Assert.True(Jwt.LooksLike(Token(expires: null)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc123")]
    [InlineData("not.a.jwt")]
    [InlineData("one.two")]
    [InlineData("a.b.c.d")]
    public void Anything_that_is_not_one_is_refused(string? value) => Assert.False(Jwt.LooksLike(value));

    /// <summary>
    /// Plenty of opaque tokens carry dots. Offering to decode one produces a failure the
    /// user cannot act on, from a row that promised something.
    /// </summary>
    [Fact]
    public void A_dotted_string_whose_header_is_not_a_jose_header_is_refused()
    {
        var pretend = Base64Url("{\"hello\":1}") + "." + Base64Url("{}") + ".signature";

        Assert.False(Jwt.LooksLike(pretend));
    }

    [Fact]
    public void The_expiry_claim_is_read_as_a_unix_timestamp()
    {
        var expires = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

        Assert.True(Jwt.TryReadExpiry(Token(expires), out var read));
        Assert.Equal(expires, read);
    }

    [Fact]
    public void A_token_with_no_expiry_claim_answers_no_rather_than_never()
    {
        Assert.False(Jwt.TryReadExpiry(Token(expires: null), out _));
    }

    [Fact]
    public void An_expired_token_is_described_by_its_clock_and_nothing_else()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var token = Token(now.AddMinutes(-41));

        var message = Jwt.DescribeIfExpired(token, now);

        Assert.NotNull(message);
        Assert.Contains("41 minutes ago", message, StringComparison.Ordinal);

        // The safety property, asserted rather than assumed: nothing here checked a
        // signature, so nothing here may imply the token is trustworthy.
        Assert.DoesNotContain("valid", message, StringComparison.OrdinalIgnoreCase);

        // And it names the token nowhere. A credential is not a thing to print in order to
        // say something about it.
        Assert.DoesNotContain(token, message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_that_has_not_expired_produces_no_sentence()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(Jwt.DescribeIfExpired(Token(now.AddHours(2)), now));
    }

    [Fact]
    public void A_token_is_found_from_a_caret_anywhere_inside_it()
    {
        var token = Token(expires: null);
        var body = $$"""{ "access_token": "{{token}}", "type": "Bearer" }""";

        var inside = body.IndexOf(token, StringComparison.Ordinal) + 10;

        Assert.True(Jwt.TryFindAt(body, inside, out var start, out var length));
        Assert.Equal(token, body.Substring(start, length));
    }

    [Fact]
    public void A_caret_on_something_that_is_not_a_token_finds_nothing()
    {
        var body = """{ "access_token": "opaque-value-here" }""";

        Assert.False(Jwt.TryFindAt(body, body.IndexOf("opaque", StringComparison.Ordinal), out _, out _));
    }

    /// <summary>Builds a real token: a JOSE header, a payload, and a signature it never checks.</summary>
    private static string Token(DateTimeOffset? expires)
    {
        var claims = expires is { } when
            ? $$"""{"sub":"someone","exp":{{when.ToUnixTimeSeconds()}}}"""
            : """{"sub":"someone"}""";

        return Base64Url("""{"alg":"HS256","typ":"JWT"}""")
            + "."
            + Base64Url(claims)
            + ".c2lnbmF0dXJl";
    }

    private static string Base64Url(string json)
    {
        // Round-tripped through the parser first, so a malformed fixture fails here rather
        // than as a puzzling assertion further down.
        using (JsonDocument.Parse(json))
        {
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
