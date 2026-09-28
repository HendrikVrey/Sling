using System.Windows;
using System.Windows.Media;
using Sling.Core.Documents;

namespace Sling.App.Editor;

/// <summary>
/// The two colours a diagnostic is marked in: one for a request that will not send, one
/// for a request that will send with a remark.
/// </summary>
/// <remarks>
/// <para>
/// Held to the same legibility floor as every other computed colour on these panes, and for
/// a reason that is not only tidiness: a squiggle is a hairline, so it is the mark on the
/// window most easily lost against the card behind it, and one nobody can see is a
/// diagnostic that was never raised.
/// </para>
/// <para>
/// <b>The two are the same two Sling already has.</b> A diagnostic has exactly two levels -
/// a request can be sent or it cannot - and the response status pill already colours "you
/// got it wrong" amber and "it went wrong" red. Reusing that vocabulary rather than
/// inventing a second one means the amber under a line and the amber on a pill mean the
/// same class of thing.
/// </para>
/// <para>
/// The seeds are copied by eye rather than referenced, exactly as <c>StatusPalette</c>
/// copies <c>MethodPalette</c>'s: they are the same hues, and a verb, a status and a
/// diagnostic are three vocabularies that should be free to move apart.
/// </para>
/// </remarks>
internal static class DiagnosticPalette
{
    /// <summary>
    /// The readability floor, in WCAG contrast ratio.
    /// </summary>
    /// <remarks>
    /// 3:1, the AA floor for a graphical object rather than the 4.5:1 for body text. These
    /// are marks and not glyphs; nothing has to be read out of the squiggle itself, only
    /// noticed.
    /// </remarks>
    internal const double MinimumContrast = 3.0;

    /// <summary>The request cannot be sent.</summary>
    private static readonly Color ErrorSeed = Color.FromRgb(0xF4, 0x87, 0x71);

    /// <summary>It can, and something about it is worth saying.</summary>
    private static readonly Color WarningSeed = Color.FromRgb(0xD7, 0xA5, 0x5B);

    /// <summary>
    /// A frozen brush per severity, drawn on <paramref name="resources"/>' page colour.
    /// </summary>
    /// <remarks>
    /// Frozen because a background renderer draws these on every layout pass of a scrolling
    /// document, and an unfrozen brush is checked for changes each time it is used.
    /// </remarks>
    internal static IReadOnlyDictionary<DiagnosticSeverity, Brush> Build(ResourceDictionary? resources)
    {
        var page = SyntaxPalette.Page(resources);

        return new Dictionary<DiagnosticSeverity, Brush>
        {
            [DiagnosticSeverity.Error] = Frozen(ErrorSeed, page),
            [DiagnosticSeverity.Warning] = Frozen(WarningSeed, page),
        };
    }

    /// <summary>The brush for one severity, falling back to the error colour.</summary>
    /// <remarks>
    /// The stricter of the two is the safe fallback: a new severity drawn as an error is
    /// noticed and asked about, where one drawn as a warning is glanced past.
    /// </remarks>
    internal static Brush For(IReadOnlyDictionary<DiagnosticSeverity, Brush> brushes, DiagnosticSeverity severity)
    {
        ArgumentNullException.ThrowIfNull(brushes);

        return brushes.TryGetValue(severity, out var brush) ? brush : brushes[DiagnosticSeverity.Error];
    }

    private static SolidColorBrush Frozen(Color seed, Color page)
    {
        var brush = new SolidColorBrush(Contrast.Legible(seed, page, MinimumContrast));

        brush.Freeze();

        return brush;
    }
}
