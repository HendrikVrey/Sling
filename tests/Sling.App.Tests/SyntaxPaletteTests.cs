using System.Windows.Media;
using Sling.App.Editor;

namespace Sling.App.Tests;

/// <summary>
/// The promise that every syntax colour is readable, held to by a test rather than by a
/// screenshot.
/// </summary>
/// <remarks>
/// AvalonEdit's grammars carry colours chosen for a white page - comments are green,
/// strings are blue, several are plain black. Sling's panes are dark. Without the
/// legibility clamp a JSON response would render several of its runs in colours between
/// muddy and invisible, and nothing about that looks like a bug worth reporting; it looks
/// like the app.
/// </remarks>
public sealed class SyntaxPaletteTests
{
    /// <summary>
    /// Pages to hold the palette against.
    /// </summary>
    /// <remarks>
    /// The real one, plus the two extremes it could plausibly move to if the theme
    /// dictionary changes. A palette that only clears the floor on today's exact
    /// background is a palette that breaks the day someone adjusts a token.
    /// </remarks>
    private static readonly Color[] Pages =
    [
        SyntaxPalette.FallbackPage,
        Color.FromRgb(0x00, 0x00, 0x00),
        Color.FromRgb(0x2B, 0x2B, 0x2B),
    ];

    /// <summary>
    /// A [Fact] holding its table in the body, not a [Theory] over SyntaxRole.
    /// InternalsVisibleTo grants access but not accessibility, so a public test method
    /// cannot take an internal enum as a parameter - that is CS0051, and it is a mistake
    /// worth only making once.
    /// </summary>
    [Fact]
    public void Every_role_clears_the_readability_floor_on_every_plausible_page()
    {
        foreach (var page in Pages)
        {
            foreach (var role in SyntaxPalette.Roles)
            {
                var colour = SyntaxPalette.ForRole(role, page);

                Assert.NotNull(colour);

                var ratio = Contrast.Ratio(colour.Value, page);

                Assert.True(
                    ratio >= SyntaxPalette.MinimumContrast,
                    $"{role} on {page} is {ratio:F2}:1, below the {SyntaxPalette.MinimumContrast:F1}:1 floor.");
            }
        }
    }

    /// <summary>
    /// A contrast test cannot see this failure, and Etch shipped it: Tag and Keyword came
    /// out byte-identical in dark mode, because both were borrowed from editors where no
    /// document shows markup and code at once - which HTML does. The legibility floor can
    /// itself cause the collision by dragging two nearby hues onto one value.
    /// </summary>
    /// <remarks>
    /// The threshold is a tripwire well below any real minimum. Asserting the true
    /// separation would turn every deliberate adjustment into a failing test.
    /// </remarks>
    [Fact]
    public void Roles_do_not_collapse_onto_the_same_colour()
    {
        const int MinimumSeparation = 24;

        foreach (var page in Pages)
        {
            var byRole = SyntaxPalette.Roles
                .Select(role => (Role: role, Colour: SyntaxPalette.ForRole(role, page)!.Value))
                .ToList();

            for (var i = 0; i < byRole.Count; i++)
            {
                for (var j = i + 1; j < byRole.Count; j++)
                {
                    var distance = Distance(byRole[i].Colour, byRole[j].Colour);

                    Assert.True(
                        distance >= MinimumSeparation,
                        $"{byRole[i].Role} and {byRole[j].Role} are {distance} apart on {page}.");
                }
            }
        }
    }

    [Fact]
    public void A_colour_that_already_reads_well_is_left_exactly_as_it_was()
    {
        var page = Color.FromRgb(0x20, 0x20, 0x20);
        var readable = Color.FromRgb(0xFF, 0xFF, 0xFF);

        Assert.Equal(readable, Contrast.Legible(readable, page, 4.5));
    }

    /// <summary>
    /// The rescue path, for the long tail of grammar colour names that map to no role.
    /// Black on a near-black page is the case that motivates the whole file.
    /// </summary>
    [Fact]
    public void An_unreadable_colour_is_dragged_up_to_the_floor()
    {
        var page = Color.FromRgb(0x20, 0x20, 0x20);
        var rescued = SyntaxPalette.Rescue(Color.FromRgb(0, 0, 0), page);

        Assert.True(Contrast.Ratio(rescued, page) >= SyntaxPalette.MinimumContrast);
    }

    /// <summary>
    /// The bisection must return a value that <em>passes</em>, not whichever midpoint the
    /// loop happened to end on, and it must decide on the byte-rounded colour rather than
    /// rounding after the fact. Both mistakes produce colours that miss the floor by a
    /// thousandth - invisible to the eye and fatal to the promise.
    /// </summary>
    [Fact]
    public void The_clamp_holds_at_every_starting_hue()
    {
        var page = SyntaxPalette.FallbackPage;

        for (var red = 0; red <= 255; red += 17)
        {
            for (var green = 0; green <= 255; green += 17)
            {
                for (var blue = 0; blue <= 255; blue += 51)
                {
                    var start = Color.FromRgb((byte)red, (byte)green, (byte)blue);
                    var ratio = Contrast.Ratio(Contrast.Legible(start, page, 4.5), page);

                    Assert.True(ratio >= 4.5, $"{start} clamped to {ratio:F4}:1.");
                }
            }
        }
    }

    /// <summary>
    /// A mid-grey page cannot reach the floor towards white at all, so the blend target
    /// has to be measured rather than inferred from "the theme is dark". Getting this
    /// wrong means bisecting towards a pole that can never satisfy the requirement.
    /// </summary>
    [Fact]
    public void A_mid_grey_page_is_rescued_towards_whichever_pole_can_reach()
    {
        var page = Color.FromRgb(0x80, 0x80, 0x80);
        var rescued = Contrast.Legible(Color.FromRgb(0x7F, 0x7F, 0x7F), page, 4.5);

        // Black is the only pole that clears 4.5:1 against mid grey; white tops out well
        // below it.
        Assert.True(Contrast.Ratio(rescued, page) >= 4.5);
        Assert.True(rescued.R < 0x40, $"Expected a dark rescue, got {rescued}.");
    }

    [Fact]
    public void Grammar_colour_names_map_to_the_role_they_describe()
    {
        Assert.Equal(SyntaxRole.Comment, SyntaxPalette.RoleOf("DocComment"));
        Assert.Equal(SyntaxRole.String, SyntaxPalette.RoleOf("XmlString"));
        Assert.Equal(SyntaxRole.Tag, SyntaxPalette.RoleOf("HtmlTag"));

        // The suffix rule, which is what keeps the table from being an inventory of every
        // grammar's keyword group.
        Assert.Equal(SyntaxRole.Keyword, SyntaxPalette.RoleOf("ExceptionKeywords"));
        Assert.Equal(SyntaxRole.Keyword, SyntaxPalette.RoleOf("IterationStatements"));

        // Case-insensitive, because grammar files are not consistent about it.
        Assert.Equal(SyntaxRole.Comment, SyntaxPalette.RoleOf("comment"));

        // Null is the normal case: many grammar rules specify a colour inline and name it
        // nothing at all.
        Assert.Equal(SyntaxRole.Unmapped, SyntaxPalette.RoleOf(null));
        Assert.Equal(SyntaxRole.Unmapped, SyntaxPalette.RoleOf("SomethingNobodyHasWrittenYet"));
    }

    /// <summary>
    /// A missing resource must fall back to a plausible page, never to
    /// <c>Colors.Transparent</c> - against which every colour clears every floor and the
    /// whole guarantee silently evaporates.
    /// </summary>
    [Fact]
    public void A_missing_theme_resource_falls_back_to_an_opaque_page()
    {
        var page = SyntaxPalette.Page(null);

        Assert.Equal(SyntaxPalette.FallbackPage, page);
        Assert.Equal(byte.MaxValue, page.A);
    }

    private static int Distance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
