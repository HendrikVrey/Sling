using Sling.Core.Json;

namespace Sling.Core.Tests;

/// <summary>
/// Turning a position in a response body into the path that reads it.
/// </summary>
/// <remarks>
/// The paths this produces are fed straight back to <see cref="JsonPathReader"/> at send
/// time, so every test here asserts a round trip rather than a string: locate the path from
/// a position, then read the value back with it. A path that looks right and reads the wrong
/// field is the one failure worth designing the tests around.
/// </remarks>
public sealed class JsonPathLocatorTests
{
    private const string Body = """
        {
          "access_token": "abc123",
          "expires_in": 3600,
          "user": { "id": 42, "roles": ["admin", "auditor"] },
          "items": [ { "sku": "A-1" }, { "sku": "B-2" } ]
        }
        """;

    [Theory]
    [InlineData("abc123", "$.access_token")]
    [InlineData("3600", "$.expires_in")]
    [InlineData("42", "$.user.id")]
    [InlineData("auditor", "$.user.roles[1]")]
    [InlineData("B-2", "$.items[1].sku")]
    public void A_value_resolves_to_the_path_that_reads_it(string needle, string expected)
    {
        Assert.True(JsonPathLocator.TryLocate(Body, Body.IndexOf(needle, StringComparison.Ordinal), out var path));
        Assert.Equal(expected, path);
    }

    /// <summary>
    /// Pointing at <c>"access_token"</c> means the token, not the object it sits in. Anything
    /// else would make the commonest click in the pane produce the least useful answer.
    /// </summary>
    [Fact]
    public void Clicking_a_property_name_answers_with_that_property()
    {
        var offset = Body.IndexOf("\"access_token\"", StringComparison.Ordinal) + 3;

        Assert.True(JsonPathLocator.TryLocate(Body, offset, out var path));
        Assert.Equal("$.access_token", path);
    }

    [Fact]
    public void A_container_is_a_legal_thing_to_point_at()
    {
        var offset = Body.IndexOf("{ \"id\": 42", StringComparison.Ordinal);

        Assert.True(JsonPathLocator.TryLocate(Body, offset, out var path));
        Assert.Equal("$.user", path);
    }

    [Fact]
    public void A_body_that_is_not_json_answers_no()
    {
        Assert.False(JsonPathLocator.TryLocate("<html><body>nope</body></html>", 8, out _));
    }

    [Fact]
    public void An_offset_past_the_end_answers_no()
    {
        Assert.False(JsonPathLocator.TryLocate(Body, Body.Length + 10, out _));
    }

    /// <summary>
    /// The reader counts UTF-8 bytes and the pane counts characters. A body with an emoji
    /// above the click is where an unconverted offset lands on the wrong field.
    /// </summary>
    [Fact]
    public void A_click_below_non_ascii_text_lands_on_the_right_field()
    {
        var body = """{ "greeting": "grüß dich 🔑", "token": "abc123" }""";

        Assert.True(
            JsonPathLocator.TryLocate(body, body.IndexOf("abc123", StringComparison.Ordinal), out var path));

        Assert.Equal("$.token", path);
    }

    /// <summary>
    /// A name that is not an identifier still has a spelling, and it has to be one the reader
    /// accepts - a path this produces and that cannot be read is worse than no path at all.
    /// </summary>
    [Fact]
    public void An_awkward_name_uses_the_bracket_form_and_still_reads_back()
    {
        var body = """{ "content type": "application/json" }""";

        Assert.True(
            JsonPathLocator.TryLocate(body, body.IndexOf("application", StringComparison.Ordinal), out var path));

        Assert.Equal("$['content type']", path);
    }

    [Fact]
    public void Every_located_path_reads_the_value_it_was_located_from()
    {
        foreach (var (needle, expected) in new[]
        {
            ("abc123", "abc123"),
            ("3600", "3600"),
            ("42", "42"),
            ("auditor", "auditor"),
            ("B-2", "B-2"),
        })
        {
            Assert.True(
                JsonPathLocator.TryLocate(Body, Body.IndexOf(needle, StringComparison.Ordinal), out var path));

            Assert.True(JsonPathReader.TryRead(Body, path, out var value, out var error), error);
            Assert.Equal(expected, value);
        }
    }
}
