using System.Windows.Media;
using Sling.App.Editor;

namespace Sling.App.Tests;

/// <summary>
/// The Ctrl+F match marker, and the two things it has to be at once: visible against the
/// pane, and quiet enough that the text drawn on top of it stays readable.
/// </summary>
/// <remarks>
/// AvalonEdit's default marker is <c>LightGreen</c>, which fails the second on any dark
/// pane - the matched word is the one word on screen you cannot read. These assert the pair
/// (marker and the text over it), not the seed, for the reason
/// <see cref="StatusPaletteTests"/> gives: a floor measured against the pane would be a floor
/// for a surface the text never touches.
/// </remarks>
public sealed class FindPaletteTests
{
    /// <summary>The floor the palette claims, restated rather than imported.</summary>
    /// <remarks>
    /// A literal on purpose: reading the constant out of the class under test would make
    /// this pass for whatever value it is changed to.
    /// </remarks>
    private const double MinimumContrast = 4.5;

    private static readonly Color Page = SyntaxPalette.FallbackPage;

    private static readonly Color Text = SyntaxPalette.FallbackText;

    [Fact]
    public void Text_stays_readable_on_the_marker()
    {
        var marker = ((SolidColorBrush)FindPalette.Marker(Page, Text)).Color;

        var ratio = Contrast.Ratio(Text, marker);

        Assert.True(
            ratio >= MinimumContrast,
            $"A highlighted match must stay readable. The pane's text is at {ratio:0.00}:1 "
                + $"against the marker, which needs {MinimumContrast}:1.");
    }

    /// <summary>
    /// A marker nobody can see is a find bar that does not work, so this is as much a
    /// requirement as the floor above.
    /// </summary>
    /// <remarks>
    /// 1.5:1 rather than a text floor: this is "a block of colour is visibly there", not
    /// "a glyph is legible", and holding a highlight to 4.5:1 against the page would leave
    /// nothing that also clears 4.5:1 for the text on top of it.
    /// </remarks>
    [Fact]
    public void The_marker_is_visible_against_the_pane()
    {
        var marker = ((SolidColorBrush)FindPalette.Marker(Page, Text)).Color;

        var ratio = Contrast.Ratio(marker, Page);

        Assert.True(
            ratio >= 1.5,
            $"A match must be findable by looking. The marker is at {ratio:0.00}:1 against "
                + "the pane, which is not a highlight.");
    }

    [Fact]
    public void The_marker_is_opaque()
    {
        // AvalonEdit fills the rectangle with this brush directly. A translucent one would
        // composite over whatever is behind it, and the ratio asserted above would be a
        // statement about a colour that is never drawn.
        var marker = (SolidColorBrush)FindPalette.Marker(Page, Text);

        Assert.Equal(255, marker.Color.A);
    }

    [Fact]
    public void The_marker_is_frozen()
    {
        // The render thread touches it. A brush a UI thread still owns is an exception
        // nobody can reproduce.
        Assert.True(FindPalette.Marker(Page, Text).IsFrozen);
    }

    /// <summary>
    /// The promise has to survive a theme, not just this one, so it is checked against pages
    /// from black to white with the foreground each of those pages would actually use.
    /// </summary>
    [Theory]
    [InlineData(0x00, 0xFF)]
    [InlineData(0x20, 0xFF)]
    [InlineData(0x3A, 0xFF)]
    [InlineData(0xF3, 0x00)]
    [InlineData(0xFF, 0x00)]
    public void Any_page_gets_a_marker_its_own_text_survives(byte page, byte text)
    {
        var surface = Color.FromRgb(page, page, page);
        var foreground = Color.FromRgb(text, text, text);

        var marker = ((SolidColorBrush)FindPalette.Marker(surface, foreground)).Color;

        // Not the floor: on a mid-grey page no tint of any seed can hold 4.5:1 for text that
        // is itself barely legible there, and claiming otherwise would be a test asserting
        // something the method cannot deliver. What must hold everywhere is that the marker
        // never makes the text worse than the page already did.
        Assert.True(
            Contrast.Ratio(foreground, marker) >= Math.Min(MinimumContrast, Contrast.Ratio(foreground, surface)),
            $"A marker on #{page:X2} must not cost the text more contrast than the page "
                + "already leaves it.");
    }
}
