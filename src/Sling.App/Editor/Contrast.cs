using System.Windows.Media;

namespace Sling.App.Editor;

/// <summary>
/// WCAG relative luminance and contrast ratio, and the one operation Sling needs on top
/// of them: dragging a colour up to a readability floor without losing its hue.
/// </summary>
/// <remarks>
/// <para>
/// AvalonEdit's built-in grammars carry colours chosen for a white page — comments are
/// <c>Green</c>, strings are <c>Blue</c>, several are plain <c>Black</c>. Sling's panes
/// are dark. Rendering a grammar unchanged would put black text on a near-black card,
/// which is not a styling complaint but an unreadable response body.
/// </para>
/// <para>
/// The formula is the standard one from WCAG 2.1 (relative luminance, then
/// <c>(L1 + 0.05) / (L2 + 0.05)</c>), not an approximation of it. It is here rather than
/// borrowed because it is fifteen lines of arithmetic every published implementation
/// agrees on.
/// </para>
/// </remarks>
internal static class Contrast
{
    private static readonly Color Black = Color.FromRgb(0, 0, 0);
    private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>The WCAG contrast ratio between two opaque colours, from 1 to 21.</summary>
    /// <remarks>
    /// Alpha is ignored, and callers are expected to have resolved it already. A
    /// translucent colour over a Mica surface has no contrast that can be stated at all —
    /// it composites against the user's wallpaper — so the honest input here is an opaque
    /// stand-in for the surface, not the surface's own ARGB.
    /// </remarks>
    internal static double Ratio(Color a, Color b)
    {
        var (high, low) = Order(Luminance(a), Luminance(b));

        return (high + 0.05) / (low + 0.05);
    }

    /// <summary>
    /// <paramref name="colour"/> if it already clears <paramref name="minimum"/> against
    /// <paramref name="page"/>; otherwise the closest colour to it that does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A colour that already reads well is returned untouched, which is the outcome to
    /// want: a grammar author who picked something legible keeps their choice.
    /// </para>
    /// <para>
    /// <b>Three things here are deliberate, and each is a way of getting this wrong.</b>
    /// </para>
    /// <para>
    /// The blend target is <em>measured</em> rather than assumed from whether the page
    /// looks dark. A mid-grey page cannot reach a high ratio towards white at all, and
    /// picking the wrong pole means bisecting towards a colour that can never satisfy the
    /// requirement.
    /// </para>
    /// <para>
    /// The search runs on the <em>quantised</em> colour — the ratio is measured on the
    /// byte-rounded candidate, not on a real-valued luminance that is rounded afterwards.
    /// Rounding after the decision lands on whichever side of the boundary the eighth bit
    /// falls, which produces colours that miss the floor by a thousandth and a test suite
    /// that fails for reasons nobody can see.
    /// </para>
    /// <para>
    /// The last candidate <em>known to clear</em> is carried, rather than whatever the
    /// loop happens to end on. A bisection's final midpoint is not necessarily a passing
    /// one.
    /// </para>
    /// </remarks>
    internal static Color Legible(Color colour, Color page, double minimum)
    {
        if (Ratio(colour, page) >= minimum)
        {
            return colour;
        }

        var pole = Ratio(Black, page) >= Ratio(White, page) ? Black : White;

        // Even the extreme cannot clear the floor. Returning it is the best available
        // answer and a better one than returning something that fails by more.
        if (Ratio(pole, page) < minimum)
        {
            return pole;
        }

        var best = pole;
        double low = 0;
        double high = 1;

        // Twelve halvings resolve to under a 4096th of the blend, far below one byte of
        // any channel. More iterations cannot change the answer once every candidate
        // quantises to the same colour.
        for (var i = 0; i < 12; i++)
        {
            var middle = (low + high) / 2;
            var candidate = Blend(colour, pole, middle);

            if (Ratio(candidate, page) >= minimum)
            {
                best = candidate;
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        return best;
    }

    /// <summary>Mixes <paramref name="from"/> towards <paramref name="to"/>, quantised to bytes.</summary>
    private static Color Blend(Color from, Color to, double amount) =>
        Color.FromRgb(
            Channel(from.R, to.R, amount),
            Channel(from.G, to.G, amount),
            Channel(from.B, to.B, amount));

    private static byte Channel(byte from, byte to, double amount) =>
        (byte)Math.Clamp(Math.Round(from + ((to - from) * amount)), 0, 255);

    private static double Luminance(Color colour) =>
        (0.2126 * Linear(colour.R)) + (0.7152 * Linear(colour.G)) + (0.0722 * Linear(colour.B));

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;

        return value <= 0.03928
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static (double High, double Low) Order(double a, double b) =>
        a >= b ? (a, b) : (b, a);
}
