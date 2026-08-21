using Sling.Core.Rendering;

namespace Sling.Core.Tests;

/// <summary>
/// Parsing a <c>Content-Type</c>, which is untrusted input from a server the user does
/// not control.
/// </summary>
public sealed class MediaTypeTests
{
    [Theory]
    [InlineData("application/json", MediaKind.Json)]
    [InlineData("text/json", MediaKind.Json)]
    [InlineData("application/vnd.github.v3+json", MediaKind.Json)]
    [InlineData("application/x-ndjson", MediaKind.Json)]
    [InlineData("application/xml", MediaKind.Xml)]
    [InlineData("text/xml", MediaKind.Xml)]
    [InlineData("image/svg+xml", MediaKind.Xml)]
    [InlineData("text/html", MediaKind.Html)]
    [InlineData("text/css", MediaKind.Css)]
    [InlineData("application/javascript", MediaKind.JavaScript)]
    [InlineData("text/csv", MediaKind.Csv)]
    [InlineData("text/markdown", MediaKind.Markdown)]
    [InlineData("text/plain", MediaKind.PlainText)]
    [InlineData("text/something-nobody-has-registered", MediaKind.PlainText)]
    [InlineData("application/x-www-form-urlencoded", MediaKind.PlainText)]
    [InlineData("image/png", MediaKind.Binary)]
    [InlineData("application/octet-stream", MediaKind.Binary)]
    public void A_type_is_classified_by_what_it_is(string header, MediaKind expected) =>
        Assert.Equal(expected, MediaType.Parse(header).Kind);

    /// <summary>
    /// XHTML matches the <c>+xml</c> suffix as well, so the order of the checks decides
    /// the answer. HTML is the more useful of the two for something a browser would
    /// render.
    /// </summary>
    [Fact]
    public void Xhtml_is_html_rather_than_xml() =>
        Assert.Equal(MediaKind.Html, MediaType.Parse("application/xhtml+xml").Kind);

    /// <summary>
    /// Without a length check, a subtype of exactly <c>+json</c> satisfies
    /// <c>EndsWith</c> and claims a format for a string that names none.
    /// </summary>
    /// <remarks>
    /// The assertion names the kind each row would wrongly claim. An earlier version
    /// asserted <c>NotEqual(Json)</c> for both, which is vacuous for the <c>+xml</c> row —
    /// without the guard it classifies as <c>Xml</c>, which is still not <c>Json</c>, so
    /// the row passed either way and covered nothing.
    /// </remarks>
    [Theory]
    [InlineData("application/+json", MediaKind.Json)]
    [InlineData("application/+xml", MediaKind.Xml)]
    public void A_bare_structured_suffix_claims_nothing(string header, MediaKind wouldBeWrong)
    {
        var kind = MediaType.Parse(header).Kind;

        Assert.NotEqual(wouldBeWrong, kind);
        Assert.Equal(MediaKind.Binary, kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("/")]
    [InlineData("/json")]
    public void Anything_unparseable_yields_nothing_rather_than_throwing(string? header)
    {
        var media = MediaType.Parse(header);

        Assert.Equal(MediaKind.Unknown, media.Kind);
        Assert.Equal(string.Empty, media.Essence);
        Assert.Null(media.Charset);
    }

    [Fact]
    public void Casing_and_whitespace_are_normalised_away()
    {
        var media = MediaType.Parse("  APPLICATION/JSON ; CHARSET=UTF-8  ");

        Assert.Equal("application/json", media.Essence);
        Assert.Equal("utf-8", media.Charset);
    }

    [Theory]
    [InlineData("text/plain; charset=\"utf-8\"", "utf-8")]
    [InlineData("text/plain; boundary=x; charset=iso-8859-1", "iso-8859-1")]
    [InlineData("text/plain", null)]
    [InlineData("text/plain; charset=", null)]
    public void The_charset_parameter_is_read_when_it_is_there(string header, string? expected) =>
        Assert.Equal(expected, MediaType.Parse(header).Charset);

    /// <summary>
    /// A semicolon inside a quoted parameter value is not a separator. Splitting on it
    /// would read the tail of one value as the start of another parameter.
    /// </summary>
    [Fact]
    public void A_semicolon_inside_a_quoted_value_does_not_split_the_header()
    {
        var media = MediaType.Parse("""multipart/form-data; boundary="a;b"; charset=utf-8""");

        Assert.Equal("multipart/form-data", media.Essence);
        Assert.Equal("utf-8", media.Charset);
    }

    /// <summary>
    /// An unterminated quote swallows the rest of the string, which is the conservative
    /// answer: the alternative is splitting a header in a place it does not split. It
    /// must not hang or throw, which is the property that actually matters for input a
    /// server chose.
    /// </summary>
    [Theory]
    [InlineData("""text/plain; charset="utf-8""")]
    [InlineData("""text/plain; charset="\""")]
    [InlineData("text/plain;;;;")]
    [InlineData("text/plain; =value")]
    public void A_malformed_parameter_section_still_yields_the_essence(string header) =>
        Assert.Equal("text/plain", MediaType.Parse(header).Essence);

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("text/plain", true)]
    [InlineData("image/png", false)]
    [InlineData(null, false)]
    public void Textual_means_worth_putting_in_an_editor(string? header, bool expected) =>
        Assert.Equal(expected, MediaType.Parse(header).IsTextual);
}
