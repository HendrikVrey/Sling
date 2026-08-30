using Sling.Core.Json;

namespace Sling.Core.Tests;

/// <summary>
/// The JSONPath subset used by chain references. The rejection cases matter as much as
/// the extraction cases: a path that silently returns the wrong element is how a token
/// ends up in the wrong request.
/// </summary>
public sealed class JsonPathReaderTests
{
    private const string Sample = """
        {
          "access_token": "abc.def",
          "expires_in": 3600,
          "renewable": true,
          "owner": { "login": "ada", "id": 7 },
          "scopes": ["read", "write"],
          "next": null
        }
        """;

    [Theory]
    [InlineData("$.access_token", "abc.def")]
    [InlineData("access_token", "abc.def")]
    [InlineData("$.expires_in", "3600")]
    [InlineData("$.renewable", "true")]
    [InlineData("$.owner.login", "ada")]
    [InlineData("$['owner']['login']", "ada")]
    [InlineData("$.scopes[0]", "read")]
    [InlineData("$.scopes[-1]", "write")]
    [InlineData("$.next", "")]
    public void Reads_the_value_at_the_path(string path, string expected)
    {
        Assert.True(JsonPathReader.TryRead(Sample, path, out var value, out var error), error);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void A_string_lands_unquoted_but_an_object_lands_as_json()
    {
        // The distinction is the whole reason this is not just GetRawText: a bearer token
        // substituted into a header must not arrive wrapped in quotation marks.
        Assert.True(JsonPathReader.TryRead(Sample, "$.access_token", out var token, out _));
        Assert.Equal("abc.def", token);

        Assert.True(JsonPathReader.TryRead(Sample, "$.owner", out var owner, out _));
        Assert.Contains("\"login\"", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_member_says_which_one()
    {
        Assert.False(JsonPathReader.TryRead(Sample, "$.refresh_token", out _, out var error));
        Assert.Contains("refresh_token", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Indexing_something_that_is_not_an_array_says_what_it_found()
    {
        Assert.False(JsonPathReader.TryRead(Sample, "$.owner[0]", out _, out var error));
        Assert.Contains("not an array", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_outside_the_array_is_reported_rather_than_clamped()
    {
        Assert.False(JsonPathReader.TryRead(Sample, "$.scopes[9]", out _, out var error));
        Assert.Contains("outside", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wildcard_is_refused_because_a_request_field_needs_one_value()
    {
        Assert.False(JsonPathReader.TryRead(Sample, "$.scopes.*", out _, out var error));
        Assert.Contains("wildcard", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_that_is_not_json_fails_with_a_reason_rather_than_throwing()
    {
        Assert.False(JsonPathReader.TryRead("<html><body>nope</body></html>", "$.token", out _, out var error));
        Assert.Contains("not JSON", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lone_surrogate_in_the_body_never_throws()
    {
        // JsonDocument.Parse transcodes to UTF-8 before parsing, so a malformed surrogate
        // surfaces as an ArgumentException rather than a JsonException. Catching only the
        // latter would let it escape and take the whole send down, so the invariant worth
        // pinning is that nothing propagates - not which branch it took.
        var body = "{\"a\":\"" + char.ConvertFromUtf32(0x1F600)[0] + "\"}";

        Assert.Null(Record.Exception(() => JsonPathReader.TryRead(body, "$.a", out _, out _)));
    }

    [Fact]
    public void An_unclosed_bracket_is_a_path_error()
    {
        Assert.False(JsonPathReader.TryRead(Sample, "$.scopes[0", out _, out var error));
        Assert.Contains("unclosed", error, StringComparison.Ordinal);
    }
}
