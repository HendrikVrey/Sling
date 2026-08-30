using System.Windows.Media;
using Sling.App.Editor;

namespace Sling.App.Collections;

/// <summary>
/// The colour a verb is drawn in beside a request in the collections rail.
/// </summary>
/// <remarks>
/// <para>
/// Colour-coded verbs are the one thing about Postman's tree that is genuinely good: the
/// shape of a collection is legible at a glance because <c>DELETE</c> does not look like
/// <c>GET</c>. It costs nothing to keep, so it is kept.
/// </para>
/// <para>
/// <b>Nothing here is trusted to be legible on faith.</b> Every seed goes through
/// <see cref="Contrast.Legible"/> against the pane colour, exactly as
/// <see cref="SyntaxPalette"/> does - a rail label at 10 px is body text, and the AA floor
/// applies to it whether or not anybody would think to check. The seeds are the same
/// family the response pane already uses so the two surfaces look like one product.
/// </para>
/// <para>
/// Dark only, because Sling is dark only. The note on <see cref="SyntaxPalette"/> covers
/// what changes if that ever stops being true.
/// </para>
/// </remarks>
internal static class MethodPalette
{
    /// <summary>
    /// The readability floor, in WCAG contrast ratio.
    /// </summary>
    /// <remarks>
    /// 4.5:1 - the AA requirement for body text. The 3:1 large-text allowance does not
    /// apply: the verb is drawn small and bold, and bold is not large.
    /// </remarks>
    private const double MinimumContrast = 4.5;

    /// <summary>
    /// The colour each verb starts from, before the legibility clamp.
    /// </summary>
    /// <remarks>
    /// Green reads as safe, amber as writing, red as destructive - the convention every
    /// HTTP tool already uses, and one users arrive with rather than have to learn. A verb
    /// not in the table gets the neutral colour rather than a colour of its own: the point
    /// is to make the destructive ones stand out, and a palette where everything is
    /// coloured makes nothing stand out.
    /// </remarks>
    private static readonly Dictionary<string, Color> Seeds = new(StringComparer.Ordinal)
    {
        ["GET"] = Color.FromRgb(0x6A, 0x99, 0x55),
        ["HEAD"] = Color.FromRgb(0x6A, 0x99, 0x55),
        ["OPTIONS"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["POST"] = Color.FromRgb(0xDC, 0xDC, 0xAA),
        ["PUT"] = Color.FromRgb(0x56, 0x9C, 0xD6),
        ["PATCH"] = Color.FromRgb(0xC5, 0x86, 0xC0),
        ["DELETE"] = Color.FromRgb(0xF4, 0x87, 0x71),
    };

    private static readonly Color Neutral = Color.FromRgb(0xC8, 0xC8, 0xC8);

    /// <summary>A frozen brush per verb, built once against <paramref name="page"/>.</summary>
    /// <remarks>
    /// Frozen because these are handed to a data template that may render them on the
    /// render thread, and an unfrozen <see cref="SolidColorBrush"/> owned by the UI thread
    /// is the standard way that becomes an <see cref="InvalidOperationException"/> nobody
    /// can reproduce. Cached because a workspace can hold hundreds of requests and a brush
    /// per row is a brush per row.
    /// </remarks>
    internal static IReadOnlyDictionary<string, Brush> Build(Color page)
    {
        var brushes = new Dictionary<string, Brush>(StringComparer.Ordinal);

        foreach (var (method, seed) in Seeds)
        {
            brushes[method] = Frozen(Contrast.Legible(seed, page, MinimumContrast));
        }

        brushes[string.Empty] = Frozen(Contrast.Legible(Neutral, page, MinimumContrast));

        return brushes;
    }

    /// <summary>
    /// The brush for <paramref name="method"/>, falling back to the neutral one.
    /// </summary>
    /// <remarks>
    /// The lookup is ordinal and the parser already upper-cases the verb, so a document
    /// written with <c>get</c> still finds the entry. An extension method - <c>PURGE</c>,
    /// <c>LOCK</c>, whatever the API invented - lands on neutral, which is the honest
    /// answer rather than a colour that means something it does not.
    /// </remarks>
    internal static Brush For(IReadOnlyDictionary<string, Brush> brushes, string? method)
    {
        ArgumentNullException.ThrowIfNull(brushes);

        return method is not null && brushes.TryGetValue(method, out var brush)
            ? brush
            : brushes[string.Empty];
    }

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();

        return brush;
    }
}
