using System.Windows.Media;

namespace Sling.App.Editor;

/// <summary>
/// The three brushes the response status pill is drawn with, for one status class.
/// </summary>
/// <param name="Foreground">The status text.</param>
/// <param name="Background">The pill's fill - opaque, already composited over the pane.</param>
/// <param name="Border">The pill's outline.</param>
internal sealed record StatusTone(Brush Foreground, Brush Background, Brush Border);

/// <summary>
/// The colour a response status is shown in, by class: 2xx, 3xx, 4xx, 5xx.
/// </summary>
/// <remarks>
/// <para>
/// A three-digit number is the first thing anybody looks for after a send, and reading it
/// as a number is slower than seeing it. The classes are the ones every HTTP tool colours
/// the same way - green succeeded, blue moved, amber you got it wrong, red they did - and
/// users arrive with them rather than having to learn them.
/// </para>
/// <para>
/// <b>The tint is composited before the text is clamped, and that is the whole reason this
/// is a class rather than four literals in a style.</b> The pill is not drawn on the pane;
/// it is drawn on a wash of its own colour over the pane. Clamping the text against the
/// pane would state a contrast ratio for a background the text never sits on. So the fill
/// is computed as an opaque blend first, and the foreground is dragged to the AA floor
/// against <em>that</em> - which, unlike a colour picked by eye, is a number a test can
/// check.
/// </para>
/// <para>
/// Dark only, as with <see cref="SyntaxPalette"/>. The seeds are the same family the
/// collections rail and the response grammar use, so the surfaces look like one product.
/// </para>
/// </remarks>
internal static class StatusPalette
{
    /// <summary>
    /// The readability floor, in WCAG contrast ratio.
    /// </summary>
    /// <remarks>
    /// 4.5:1 - the AA requirement for body text. The pill's text is small and bold, and
    /// bold is not large, so the 3:1 large-text allowance does not apply.
    /// </remarks>
    internal const double MinimumContrast = 4.5;

    /// <summary>How much of the seed colour the pill's fill carries.</summary>
    /// <remarks>
    /// Low on purpose. The pill has to read as a tint of the pane rather than as a solid
    /// swatch - a saturated block behind three characters is a badge, and a badge beside a
    /// pane header is louder than the header it annotates.
    /// </remarks>
    private const double FillAmount = 0.16;

    /// <summary>How much of the seed colour the pill's outline carries.</summary>
    private const double OutlineAmount = 0.45;

    /// <summary>
    /// The colour each class starts from, before the legibility clamp.
    /// </summary>
    /// <remarks>
    /// Shared with <c>MethodPalette</c> by eye rather than by reference: they are the same
    /// four hues, but a verb and a status are different vocabularies and fusing them would
    /// make a change to one silently change the other.
    /// </remarks>
    private static readonly Color Success = Color.FromRgb(0x6A, 0x99, 0x55);
    private static readonly Color Redirect = Color.FromRgb(0x56, 0x9C, 0xD6);
    private static readonly Color ClientError = Color.FromRgb(0xD7, 0xA5, 0x5B);
    private static readonly Color ServerError = Color.FromRgb(0xF4, 0x87, 0x71);
    private static readonly Color Neutral = Color.FromRgb(0xC8, 0xC8, 0xC8);

    /// <summary>The tone for <paramref name="statusCode"/>, drawn on <paramref name="page"/>.</summary>
    /// <remarks>
    /// A status outside 1xx - 5xx cannot come off the wire, but it can come out of a mangled
    /// response line, so it lands on neutral rather than throwing.
    /// </remarks>
    internal static StatusTone For(int statusCode, Color page) => Tone(Seed(statusCode), page);

    private static Color Seed(int statusCode) => statusCode switch
    {
        >= 200 and <= 299 => Success,
        >= 300 and <= 399 => Redirect,
        >= 400 and <= 499 => ClientError,
        >= 500 and <= 599 => ServerError,
        _ => Neutral,
    };

    private static StatusTone Tone(Color seed, Color page)
    {
        // The fill first: everything else is measured against what the text will actually
        // sit on, which is this and not the pane.
        var fill = Contrast.Blend(page, seed, FillAmount);

        return new StatusTone(
            Frozen(Contrast.Legible(seed, fill, MinimumContrast)),
            Frozen(fill),
            Frozen(Contrast.Blend(page, seed, OutlineAmount)));
    }

    /// <summary>
    /// Frozen for the same reason <c>MethodPalette</c>'s brushes are: a brush that reaches
    /// the render thread while a UI thread owns it is an exception nobody can reproduce.
    /// </summary>
    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();

        return brush;
    }
}
