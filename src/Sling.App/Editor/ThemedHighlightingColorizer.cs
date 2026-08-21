using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace Sling.App.Editor;

/// <summary>
/// Draws a grammar's highlighting in Sling's colours instead of its own.
/// </summary>
/// <remarks>
/// <para>
/// The interception point is <see cref="ApplyColorToElement"/>, which every highlighted
/// run passes through on its way to the visual line. Substituting there rather than
/// editing the definition is what keeps this local: AvalonEdit's grammars are frozen,
/// process-wide singletons, so recolouring one in place would change it for every editor
/// in the process and could not be undone. Read-only use of a shared object is the only
/// safe use of one.
/// </para>
/// <para>
/// <b>Only the foreground is replaced.</b> Bold and italic are the grammar's judgement
/// about emphasis rather than about colour. A grammar colour with no foreground at all is
/// passed straight through — inventing one where the author deliberately left the text
/// alone would be a change to the grammar, not to the palette.
/// </para>
/// </remarks>
internal sealed class ThemedHighlightingColorizer : HighlightingColorizer
{
    /// <summary>
    /// Translations already worked out, keyed by the grammar's colour object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ApplyColorToElement"/> runs once per highlighted run per redraw, so this
    /// sits on the path of every scroll. A grammar hands out a small fixed set of shared
    /// instances, so the cache turns a table scan plus a contrast bisection into a
    /// dictionary probe.
    /// </para>
    /// <para>
    /// Keyed by <em>reference</em>. <see cref="HighlightingColor.GetHashCode"/> hashes the
    /// brushes and font properties, which is slower and would merge two colours that
    /// happen to agree today into one entry — harmless now, and exactly the aliasing that
    /// becomes a puzzle later.
    /// </para>
    /// </remarks>
    private readonly Dictionary<HighlightingColor, HighlightingColor> _translated =
        new(ReferenceEqualityComparer.Instance);

    private readonly Color _page;

    /// <param name="definition">The grammar to highlight with.</param>
    /// <param name="page">The opaque colour every contrast ratio is measured against.</param>
    internal ThemedHighlightingColorizer(IHighlightingDefinition definition, Color page)
        : base(definition)
    {
        _page = page;
    }

    /// <inheritdoc/>
    protected override void ApplyColorToElement(VisualLineElement element, HighlightingColor color)
    {
        ArgumentNullException.ThrowIfNull(color);

        base.ApplyColorToElement(element, Translate(color));
    }

    private HighlightingColor Translate(HighlightingColor color)
    {
        if (_translated.TryGetValue(color, out var cached))
        {
            return cached;
        }

        var translated = Build(color) ?? color;

        _translated[color] = translated;

        return translated;
    }

    /// <summary>
    /// The recoloured equivalent, or null when the original should be used as it is.
    /// </summary>
    /// <remarks>
    /// The clone is frozen before it is handed out. <see cref="HighlightingColor"/> is
    /// freezable precisely so that a shared instance cannot be edited from under a
    /// renderer, and a cached one reachable from every redraw is the case that protects.
    /// </remarks>
    private HighlightingColor? Build(HighlightingColor color)
    {
        if (color.Foreground is not { } foreground)
        {
            // Nothing to recolour. Weight and style still apply, so the grammar's emphasis
            // survives — this is a colour that only ever meant "make this bold".
            return null;
        }

        var role = SyntaxPalette.RoleOf(color.Name);

        Color? replacement;

        if (role != SyntaxRole.Unmapped)
        {
            replacement = SyntaxPalette.ForRole(role, _page);
        }
        else
        {
            // A null context is the supported case — HighlightingBrush.GetColor documents
            // it as "context can be null!" — so this is not a gamble. A null *answer* is
            // still possible: only SimpleHighlightingBrush is guaranteed to resolve
            // without a text view, and a colour that will not say what it is cannot be
            // made legible. Left alone, which at worst leaves one run in the grammar's own
            // colour.
            replacement = foreground.GetColor(null) is { } original
                ? SyntaxPalette.Rescue(original, _page)
                : null;
        }

        if (replacement is not { } chosen)
        {
            return null;
        }

        var clone = color.Clone();

        clone.Foreground = new SimpleHighlightingBrush(chosen);
        clone.Freeze();

        return clone;
    }
}
