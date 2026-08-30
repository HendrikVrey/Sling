using Etch.Core.Abstractions;
using Etch.Core.Documents;
using Sling.App.Editor;

namespace Sling.App.Tests;

/// <summary>
/// What a response body is highlighted as, and what the transform menu is offered for.
/// </summary>
public sealed class BodyLanguageTests
{
    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/vnd.github+json")]
    [InlineData("APPLICATION/JSON")]
    public void A_declared_json_type_settles_the_language(string contentType) =>
        Assert.Equal(SyntaxLanguage.Json, BodyLanguage.Analyse(contentType, "{}").Language);

    [Theory]
    [InlineData("text/html", SyntaxLanguage.Html)]
    [InlineData("application/xhtml+xml", SyntaxLanguage.Html)]
    [InlineData("application/xml", SyntaxLanguage.Xml)]
    [InlineData("image/svg+xml", SyntaxLanguage.Xml)]
    [InlineData("text/css", SyntaxLanguage.Css)]
    [InlineData("application/javascript", SyntaxLanguage.JavaScript)]
    [InlineData("text/markdown", SyntaxLanguage.Markdown)]
    public void Other_declared_types_settle_it_too(string contentType, SyntaxLanguage expected) =>
        Assert.Equal(expected, BodyLanguage.Analyse(contentType, "x").Language);

    /// <summary>
    /// The case that makes this worth writing. Mislabelling is routine - a gateway that
    /// did not look, or a service that never set the header - and a body that is plainly
    /// JSON should be highlighted as JSON regardless of what it was called.
    /// </summary>
    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense-that-is-not-a-media-type")]
    public void An_unhelpful_content_type_hands_the_decision_to_the_detector(string? contentType) =>
        Assert.Equal(
            SyntaxLanguage.Json,
            BodyLanguage.Analyse(contentType, """{"ok":true,"items":[1,2,3]}""").Language);

    /// <summary>
    /// The other direction, and the reason detection runs even when the header is
    /// believed: a JWT has no grammar, so it contributes nothing to highlighting - but
    /// recognising it is exactly what puts "Decode JWT" at the top of the menu.
    /// </summary>
    [Fact]
    public void A_body_with_no_grammar_still_gets_a_detection_for_the_transform_menu()
    {
        const string Jwt =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"
            + ".eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIn0"
            + ".dQw4w9WgXcQdQw4w9WgXcQdQw4w9WgXcQdQw4w9WgXc";

        var analysis = BodyLanguage.Analyse("text/plain", Jwt);

        Assert.Equal(SyntaxLanguage.None, analysis.Language);
        Assert.Equal(FormatId.Jwt, analysis.Detection.Format);
        Assert.True(analysis.Detection.IsRecognised);
    }

    /// <summary>
    /// The confidence must be the detector's own. Fabricating one - an earlier draft of
    /// this code assumed High - would let a low-confidence guess drive the menu as
    /// forcefully as a certainty.
    /// </summary>
    [Fact]
    public void The_detection_confidence_is_the_detectors_own_and_not_invented()
    {
        var prose = BodyLanguage.Analyse("text/plain", "just some words that are not any format at all");

        Assert.Equal(FormatId.PlainText, prose.Detection.Format);
        Assert.False(prose.Detection.IsRecognised);
        Assert.Equal(DetectionConfidence.None, prose.Detection.Confidence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void An_empty_body_is_analysed_as_nothing(string body)
    {
        var analysis = BodyLanguage.Analyse("application/json", body);

        Assert.Equal(SyntaxLanguage.None, analysis.Language);
        Assert.False(analysis.Detection.IsRecognised);
    }
}
