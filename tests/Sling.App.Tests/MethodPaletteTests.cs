using System.Windows.Media;
using Sling.App.Collections;
using Sling.App.Editor;

namespace Sling.App.Tests;

/// <summary>
/// The verb colours in the collections rail, and the promise that every one of them is
/// readable on the pane it is drawn on.
/// </summary>
/// <remarks>
/// The same concern <see cref="SyntaxPaletteTests"/> covers, and here for the same reason:
/// <see cref="MethodPalette"/>'s doc comment claims the AA floor, and a colour promise held
/// by a comment is not held. A seed edited carelessly, or a pane colour that moves, must
/// fail here rather than in somebody's eyes.
/// </remarks>
public sealed class MethodPaletteTests
{
    /// <summary>The floor the palette claims, restated rather than imported.</summary>
    /// <remarks>
    /// Deliberately a literal. Reading the constant out of the class under test would make
    /// this pass for any value it was changed to, which is the whole failure mode.
    /// </remarks>
    private const double MinimumContrast = 4.5;

    private static readonly Color Page = SyntaxPalette.FallbackPage;

    /// <summary>The verbs whose colours have to be told apart from one another.</summary>
    private static readonly string[] DistinctVerbs =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];

    [Fact]
    public void Every_verb_is_legible_on_the_pane()
    {
        var brushes = MethodPalette.Build(Page);
        var illegible = new List<string>();

        foreach (var (method, brush) in brushes)
        {
            var ratio = Contrast.Ratio(((SolidColorBrush)brush).Color, Page);

            if (ratio < MinimumContrast)
            {
                illegible.Add($"{(method.Length == 0 ? "(neutral)" : method)} at {ratio:0.00}:1");
            }
        }

        Assert.True(
            illegible.Count == 0,
            $"Every verb must clear {MinimumContrast}:1 against the rail. "
                + $"These do not: {string.Join(", ", illegible)}");
    }

    [Fact]
    public void Every_brush_is_frozen()
    {
        // They are handed to a data template that may render them off the UI thread, and an
        // unfrozen brush owned by one thread is the standard way that becomes an exception
        // nobody can reproduce.
        Assert.All(MethodPalette.Build(Page).Values, brush => Assert.True(brush.IsFrozen));
    }

    [Fact]
    public void The_verbs_worth_telling_apart_have_different_colours()
    {
        // A palette that clamps GET and DELETE to the same byte value passes a contrast test
        // and defeats the point — the reason for colouring verbs at all is that a
        // destructive one should not look like a safe one. This is the tripwire
        // SyntaxPalette.RolesAreDistinct is for, applied to the same hazard here.
        var brushes = MethodPalette.Build(Page);

        var distinct = DistinctVerbs
            .Select(method => ((SolidColorBrush)MethodPalette.For(brushes, method)).Color)
            .Distinct()
            .Count();

        Assert.Equal(DistinctVerbs.Length, distinct);
    }

    [Theory]
    [InlineData("PURGE")]
    [InlineData("LOCK")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_verb_falls_back_to_the_neutral_colour(string? method)
    {
        // The honest answer, rather than a colour that means something it does not. An API
        // that invented its own verb should not have it read as destructive.
        var brushes = MethodPalette.Build(Page);

        Assert.Equal(
            ((SolidColorBrush)brushes[string.Empty]).Color,
            ((SolidColorBrush)MethodPalette.For(brushes, method)).Color);
    }

    [Fact]
    public void The_lookup_is_the_casing_the_parser_produces()
    {
        // RequestDocumentParser upper-cases the verb, so the ordinal table finds it. If that
        // ever stops being true every row goes neutral and nothing else notices.
        var brushes = MethodPalette.Build(Page);

        Assert.NotEqual(
            ((SolidColorBrush)brushes[string.Empty]).Color,
            ((SolidColorBrush)MethodPalette.For(brushes, "DELETE")).Color);
    }
}
