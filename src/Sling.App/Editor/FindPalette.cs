using System.Windows.Media;

namespace Sling.App.Editor;

/// <summary>
/// The colour a Ctrl+F match is highlighted in.
/// </summary>
/// <remarks>
/// <para>
/// AvalonEdit's default is <c>LightGreen</c>, chosen for a white page and never themed. It
/// is drawn as a rectangle <em>behind</em> text that keeps the pane's own foreground, so on
/// a dark pane the result is near-white text on a pale green block: the one thing on screen
/// the user is looking for is the least readable thing on it.
/// </para>
/// <para>
/// <b>A marker has two jobs that pull against each other</b>, which is why this is computed
/// rather than picked. It has to stand out from the page, or a match is invisible; and the
/// text on top of it has to stay readable, or a found match cannot be read. So the answer is
/// the most saturated tint of the seed over the page that still clears the AA floor for the
/// pane's primary foreground, found by search rather than by eye, and the test asserts the
/// pair rather than the seed - the same rule <see cref="StatusPalette"/> follows for the
/// status pill.
/// </para>
/// </remarks>
internal static class FindPalette
{
    /// <summary>
    /// The readability floor, in WCAG contrast ratio.
    /// </summary>
    /// <remarks>
    /// 4.5:1, matching <see cref="SyntaxPalette"/>: the text drawn over a marker is the same
    /// 13 px body text, and highlighting it must not cost it its legibility.
    /// </remarks>
    internal const double MinimumContrast = 4.5;

    /// <summary>
    /// The strongest tint considered, as a fraction of the seed over the page.
    /// </summary>
    /// <remarks>
    /// A cap rather than a target. Above roughly this the marker stops reading as a
    /// highlight of the pane and starts reading as a swatch pasted over it, which is the
    /// complaint against the stock green quite apart from the contrast.
    /// </remarks>
    private const double MaximumTint = 0.55;

    /// <summary>How finely the tint is searched between the page and <see cref="MaximumTint"/>.</summary>
    private const double Steps = 22;

    /// <summary>
    /// The hue a match is tinted with.
    /// </summary>
    /// <remarks>
    /// Amber, and declared here rather than borrowed from <see cref="StatusPalette"/>.
    /// The two are close by eye and are different vocabularies: a 4xx status and a search
    /// hit have nothing to do with each other, and sharing the constant would mean changing
    /// the meaning of one by adjusting the other.
    /// </remarks>
    private static readonly Color Seed = Color.FromRgb(0xE0, 0xA8, 0x2E);

    /// <summary>
    /// The marker to draw matches with on <paramref name="page"/>, under text drawn in
    /// <paramref name="foreground"/>.
    /// </summary>
    internal static SolidColorBrush Marker(Color page, Color foreground)
    {
        // Walked down from the strongest tint rather than bisected: contrast against the
        // foreground is not monotonic in the blend amount for an arbitrary seed and page, so
        // a bisection can settle on a passing candidate with a stronger passing one above
        // it. Each step is a fraction of a byte per channel, and the whole loop is
        // arithmetic on a handful of colours.
        for (var step = Steps; step > 0; step--)
        {
            var candidate = Contrast.Blend(page, Seed, MaximumTint * step / Steps);

            if (Contrast.Ratio(foreground, candidate) >= MinimumContrast)
            {
                return Frozen(candidate);
            }
        }

        // No tint of this seed leaves the text at the floor. The faintest one is the
        // cheapest, and a marker that is hard to read still beats one nobody can see: a
        // find bar whose matches are invisible is a find bar that does not work.
        return Frozen(Contrast.Blend(page, Seed, MaximumTint / Steps));
    }

    /// <summary>
    /// Frozen for the reason every brush in this folder is: a brush the render thread
    /// touches while a UI thread owns it is an exception nobody can reproduce.
    /// </summary>
    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();

        return brush;
    }
}
