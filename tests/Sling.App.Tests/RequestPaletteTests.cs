using System.Windows.Media;
using Sling.App.Collections;
using Sling.App.Editor;
using Sling.Core.Parsing;

namespace Sling.App.Tests;

/// <summary>
/// The request pane's colours: every one readable, and the ones that share a line
/// distinguishable.
/// </summary>
/// <remarks>
/// A contrast test alone would pass a palette in which a <c>{{reference}}</c> and the target
/// it sits inside are the same colour, which is the failure that matters most here - a
/// reference is the thing people get wrong, and it is invisible if it does not stand out
/// from what surrounds it. Etch shipped the same shape of bug in dark mode, where two roles
/// clamped to one byte value; <c>SyntaxPaletteTests</c> is the tripwire that caught it, and
/// this is the same tripwire for the request pane.
/// </remarks>
public sealed class RequestPaletteTests
{
    /// <summary>
    /// Pages to hold the palette against: the real one, plus the two extremes the theme
    /// dictionary could plausibly move to.
    /// </summary>
    private static readonly Color[] Pages =
    [
        SyntaxPalette.FallbackPage,
        Color.FromRgb(0x00, 0x00, 0x00),
        Color.FromRgb(0x2B, 0x2B, 0x2B),
    ];

    private static readonly Color Text = SyntaxPalette.FallbackText;

    [Fact]
    public void Every_kind_clears_the_readability_floor_on_every_plausible_page()
    {
        foreach (var page in Pages)
        {
            foreach (var kind in Enum.GetValues<HttpTokenKind>())
            {
                if (RequestPalette.For(kind, page, Text) is not { } colour)
                {
                    // Only a verb, whose colour is MethodPalette's and tested there.
                    Assert.Equal(HttpTokenKind.Method, kind);
                    continue;
                }

                var ratio = Contrast.Ratio(colour, page);

                Assert.True(
                    ratio >= RequestPalette.MinimumContrast,
                    $"{kind} on {page} is {ratio:F2}:1, below the {RequestPalette.MinimumContrast:F1}:1 floor.");
            }
        }
    }

    /// <summary>
    /// Kinds that can appear on one line have to be told apart, and the grouping below is
    /// the format's own: a request line, a header, a variable definition, a metadata line, a
    /// separator, an import.
    /// </summary>
    /// <remarks>
    /// Grouped by line rather than compared globally, because four kinds share the value
    /// colour on purpose - a directive's argument, a target, a header value and an import
    /// path are one role - and none of the four can appear beside another.
    /// </remarks>
    [Fact]
    public void Kinds_that_share_a_line_are_visually_distinct()
    {
        HttpTokenKind[][] lines =
        [
            [HttpTokenKind.Comment, HttpTokenKind.Title],
            [HttpTokenKind.Comment, HttpTokenKind.Directive, HttpTokenKind.DirectiveValue, HttpTokenKind.Reference],
            [HttpTokenKind.VariableName, HttpTokenKind.Operator, HttpTokenKind.HeaderValue, HttpTokenKind.Reference],
            [HttpTokenKind.Target, HttpTokenKind.Version, HttpTokenKind.Reference],
            [HttpTokenKind.HeaderName, HttpTokenKind.Operator, HttpTokenKind.HeaderValue, HttpTokenKind.Reference],
            [HttpTokenKind.ImportMarker, HttpTokenKind.ImportPath, HttpTokenKind.Reference],
        ];

        foreach (var page in Pages)
        {
            foreach (var line in lines)
            {
                for (var i = 0; i < line.Length; i++)
                {
                    for (var j = i + 1; j < line.Length; j++)
                    {
                        AssertDistinct(line[i], line[j], page);
                    }
                }
            }
        }
    }

    /// <summary>
    /// A reference is drawn inside a target, a header value, a variable's value and a body,
    /// so it has to differ from every colour on the pane rather than only from its
    /// neighbours - including the six verb colours it sits beside on a request line.
    /// </summary>
    [Fact]
    public void A_reference_is_distinct_from_every_other_colour_including_the_verbs()
    {
        foreach (var page in Pages)
        {
            foreach (var kind in Enum.GetValues<HttpTokenKind>())
            {
                if (kind != HttpTokenKind.Reference)
                {
                    AssertDistinct(HttpTokenKind.Reference, kind, page);
                }
            }

            var reference = RequestPalette.For(HttpTokenKind.Reference, page, Text)!.Value;

            foreach (var (verb, brush) in MethodPalette.Build(page))
            {
                var colour = ((SolidColorBrush)brush).Color;
                var distance = Distance(reference, colour);

                Assert.True(
                    distance > MinimumSeparation,
                    $"a reference and the '{verb}' verb are {distance} apart on {page}.");
            }
        }
    }

    /// <summary>
    /// A tripwire well below any real minimum. Asserting the true separation would turn
    /// every deliberate adjustment into a failing test.
    /// </summary>
    private const int MinimumSeparation = 40;

    private static void AssertDistinct(HttpTokenKind a, HttpTokenKind b, Color page)
    {
        if (RequestPalette.For(a, page, Text) is not { } first
            || RequestPalette.For(b, page, Text) is not { } second)
        {
            return;
        }

        var distance = Distance(first, second);

        Assert.True(distance > MinimumSeparation, $"{a} and {b} are {distance} apart on {page}.");
    }

    /// <summary>Manhattan distance in RGB, which is enough for a tripwire.</summary>
    private static int Distance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
