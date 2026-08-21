using Etch.Core.Abstractions;
using Etch.Core.Detection;
using Etch.Core.Documents;
using Sling.Core.Rendering;

namespace Sling.App.Editor;

/// <summary>
/// What a response body is: the language to highlight it as, and the format to rank
/// transforms against.
/// </summary>
/// <param name="Language">The grammar to apply, or <see cref="SyntaxLanguage.None"/>.</param>
/// <param name="Detection">
/// <c>Etch.Core</c>'s verdict on the bytes, carried whole. Its confidence is part of the
/// answer and is what stops a low-confidence guess from driving the transform menu.
/// </param>
internal readonly record struct BodyAnalysis(SyntaxLanguage Language, DetectionResult Detection)
{
    /// <summary>Nothing to show: an empty body, or Sling's own placeholder text.</summary>
    internal static BodyAnalysis None { get; } = new(SyntaxLanguage.None, DetectionResult.PlainText);
}

/// <summary>
/// Decides what a response body is.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place Sling knows something Etch cannot: an HTTP response says what it
/// is. Etch has to guess from the bytes because a scratch buffer has no metadata; a
/// response carries a <c>Content-Type</c>, and a server's own declaration outranks a
/// sniff of its payload.
/// </para>
/// <para>
/// <b>But only when the declaration is useful.</b> Mislabelling is common enough to plan
/// for — JSON served as <c>text/plain</c>, or as <c>application/octet-stream</c> by a
/// gateway that did not look — so a header that resolves to plain text or to nothing at
/// all hands the decision to the detector rather than ending it.
/// </para>
/// <para>
/// <b>Detection runs regardless of the header, and both halves are returned together.</b>
/// The two questions have different right answers: <c>Content-Type: application/jwt</c>
/// settles the highlighting at "no grammar", while the detector recognising a JWT is
/// precisely what puts "Decode JWT" at the top of the menu. Returning one struct also
/// means the body is scanned once rather than once per caller.
/// </para>
/// </remarks>
internal static class BodyLanguage
{
    /// <summary>
    /// Analyses a body.
    /// </summary>
    /// <param name="contentType">The raw <c>Content-Type</c> header value, or null.</param>
    /// <param name="body">The body as it will appear in the editor.</param>
    internal static BodyAnalysis Analyse(string? contentType, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return BodyAnalysis.None;
        }

        // Bounded by construction: FormatDetection.Detect examines a leading sample and
        // reports WasSampled, so a sixteen-mebibyte body costs the same as a small one and
        // this is safe to call on the dispatcher.
        var detection = FormatDetection.Detect(body);

        var language = FromMedia(MediaType.Parse(contentType).Kind)
            // The detector answers a format, not a language — a body can be a JWT or
            // base64 or a timestamp, none of which has a grammar. FromFormat is the
            // mapping that already knows which of them do.
            ?? LanguageSelector.FromFormat(detection.Format);

        return new BodyAnalysis(language, detection);
    }

    /// <summary>
    /// The language a declared media type implies, or null when the declaration does not
    /// settle it.
    /// </summary>
    private static SyntaxLanguage? FromMedia(MediaKind kind) => kind switch
    {
        MediaKind.Json => SyntaxLanguage.Json,
        MediaKind.Xml => SyntaxLanguage.Xml,
        MediaKind.Html => SyntaxLanguage.Html,
        MediaKind.Css => SyntaxLanguage.Css,
        MediaKind.JavaScript => SyntaxLanguage.JavaScript,
        MediaKind.Markdown => SyntaxLanguage.Markdown,

        // Csv has no grammar; PlainText, Binary and Unknown all mean "the header did not
        // say", and each is a case where sniffing the body is the better answer.
        _ => null,
    };
}
