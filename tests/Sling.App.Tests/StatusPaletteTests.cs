using System.Windows.Media;
using Sling.App.Editor;

namespace Sling.App.Tests;

/// <summary>
/// The response status pill's colours, and the promise that the status is readable on the
/// tint it is drawn on rather than on the pane behind the tint.
/// </summary>
/// <remarks>
/// The same concern <see cref="MethodPaletteTests"/> and <see cref="SyntaxPaletteTests"/>
/// cover, with one extra hazard of its own: the pill has a background, so a contrast floor
/// measured against the pane would be a floor for a surface the text never touches.
/// </remarks>
public sealed class StatusPaletteTests
{
    /// <summary>The floor the palette claims, restated rather than imported.</summary>
    /// <remarks>
    /// Deliberately a literal, for the reason <see cref="MethodPaletteTests"/> gives:
    /// reading the constant out of the class under test makes this pass for any value it is
    /// changed to.
    /// </remarks>
    private const double MinimumContrast = 4.5;

    private static readonly Color Page = SyntaxPalette.FallbackPage;

    /// <summary>One status from each class, both edges of each range, and two impossible ones.</summary>
    private static readonly int[] Statuses =
        [100, 199, 200, 204, 299, 300, 301, 399, 400, 404, 499, 500, 503, 599, 0, 999];

    /// <summary>One status from each class that must not share a colour with the others.</summary>
    private static readonly int[] DistinctClasses = [200, 301, 404, 500];

    [Fact]
    public void Every_status_is_legible_on_its_own_pill()
    {
        var illegible = new List<string>();

        foreach (var status in Statuses)
        {
            var tone = StatusPalette.For(status, Page);

            var ratio = Contrast.Ratio(
                ((SolidColorBrush)tone.Foreground).Color,
                ((SolidColorBrush)tone.Background).Color);

            if (ratio < MinimumContrast)
            {
                illegible.Add($"{status} at {ratio:0.00}:1");
            }
        }

        Assert.True(
            illegible.Count == 0,
            $"Every status must clear {MinimumContrast}:1 against its own pill, not against "
                + $"the pane behind it. These do not: {string.Join(", ", illegible)}");
    }

    [Fact]
    public void Every_pill_is_opaque()
    {
        // The fill is composited over the pane by StatusPalette precisely so the contrast
        // above is a statement about what is rendered. A translucent brush would put that
        // back in the hands of whatever happens to be behind the window.
        foreach (var status in Statuses)
        {
            var tone = StatusPalette.For(status, Page);

            Assert.Equal(255, ((SolidColorBrush)tone.Background).Color.A);
            Assert.Equal(255, ((SolidColorBrush)tone.Border).Color.A);
            Assert.Equal(255, ((SolidColorBrush)tone.Foreground).Color.A);
        }
    }

    [Fact]
    public void Every_brush_is_frozen()
    {
        // Handed to controls that may render them off the UI thread, exactly as
        // MethodPalette's are.
        foreach (var status in Statuses)
        {
            var tone = StatusPalette.For(status, Page);

            Assert.True(tone.Foreground.IsFrozen);
            Assert.True(tone.Background.IsFrozen);
            Assert.True(tone.Border.IsFrozen);
        }
    }

    [Fact]
    public void The_four_classes_are_told_apart()
    {
        // A palette that clamps 200 and 500 to the same colour passes every contrast test and
        // defeats the point: the reason for colouring a status at all is that a failure
        // should not look like a success.
        var colours = DistinctClasses
            .Select(status => ((SolidColorBrush)StatusPalette.For(status, Page).Foreground).Color)
            .Distinct()
            .Count();

        Assert.Equal(DistinctClasses.Length, colours);
    }

    [Theory]
    [InlineData(200, 204)]
    [InlineData(301, 302)]
    [InlineData(400, 451)]
    [InlineData(500, 599)]
    public void A_class_is_one_colour(int first, int second)
    {
        // Every 4xx is the same amber. Grading within a class would be a second vocabulary
        // to learn for no gain — the number itself is right there.
        Assert.Equal(
            ((SolidColorBrush)StatusPalette.For(first, Page).Foreground).Color,
            ((SolidColorBrush)StatusPalette.For(second, Page).Foreground).Color);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(600)]
    [InlineData(-1)]
    public void A_status_that_cannot_come_off_the_wire_falls_back_to_neutral(int statusCode)
    {
        // It can still come out of a mangled response line, and a palette that threw there
        // would turn a malformed reply into a crash rather than into a grey pill.
        Assert.Equal(
            ((SolidColorBrush)StatusPalette.For(100, Page).Foreground).Color,
            ((SolidColorBrush)StatusPalette.For(statusCode, Page).Foreground).Color);
    }
}
