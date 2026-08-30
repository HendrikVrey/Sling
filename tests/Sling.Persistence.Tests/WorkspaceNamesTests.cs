using Sling.Persistence.Workspaces;

namespace Sling.Persistence.Tests;

/// <summary>
/// The rule that turns a typed collection name into a path segment.
/// </summary>
/// <remarks>
/// The tests that matter here are the refusals. A whitelist is only worth having if the
/// characters that build a traversal genuinely cannot survive it, and "cannot" is a claim a
/// test has to make rather than a comment.
/// </remarks>
public sealed class WorkspaceNamesTests
{
    /// <summary>The illegal character the sanitiser is meant to replace.</summary>
    /// <remarks>
    /// A constant rather than a literal in the string below. Hendrik's rule is that no
    /// em dash appears in anything written here, and a sweep enforcing that would
    /// silently turn the one test that needs the character into a test of a hyphen.
    /// </remarks>
    private const char EmDash = (char)0x2014;

    [Fact]
    public void An_ordinary_name_survives_unchanged()
    {
        Assert.True(WorkspaceNames.TryToSegment("Orders", out var segment, out _));
        Assert.Equal("Orders", segment);
    }

    [Fact]
    public void Case_is_kept()
    {
        // Unlike the importer's slug, which lower-cases so two checkouts of one collection
        // agree byte for byte. A name typed by hand has no second checkout to agree with.
        Assert.True(WorkspaceNames.TryToSegment("BillingAPI", out var segment, out _));
        Assert.Equal("BillingAPI", segment);
    }

    [Fact]
    public void Spaces_are_kept_and_runs_of_them_collapse()
    {
        Assert.True(WorkspaceNames.TryToSegment("Order   management", out var segment, out _));
        Assert.Equal("Order management", segment);
    }

    [Fact]
    public void Anything_else_becomes_one_dash()
    {
        // A dash outranks a space in a mixed run, so a separator that contained something
        // illegal reads as a replacement rather than as a word break that was always there.
        //
        // THE EM DASH IN THIS STRING IS DATA, NOT PROSE. It is the illegal character the
        // test exists to sanitise, and a find-and-replace that treats it as writing turns
        // this into an assertion about a hyphen, which the whitelist already allows. Leave
        // it alone. It is built from a char constant rather than typed, so a sweep
        // over this file cannot reach it in the first place.
        Assert.True(WorkspaceNames.TryToSegment($"Orders {EmDash} refunds (v2)", out var segment, out _));
        Assert.Equal("Orders-refunds-v2", segment);
    }

    [Theory]
    [InlineData("_shared", "_shared")]
    [InlineData("shared_", "shared_")]
    [InlineData("___", "___")]
    public void An_underscore_is_on_the_whitelist_and_survives_at_the_ends(string typed, string expected)
    {
        // It used to be trimmed off both ends although it is a keepable character, so
        // '_shared' came back as 'shared' for a reason nothing on screen explained.
        Assert.True(WorkspaceNames.TryToSegment(typed, out var segment, out _));
        Assert.Equal(expected, segment);
    }

    [Theory]
    [InlineData(".http")]
    [InlineData(".rest")]
    [InlineData("  .HTTP  ")]
    public void A_name_that_is_only_an_extension_says_so(string typed)
    {
        // The general refusal talks about which characters survive, which is true and
        // useless to somebody who typed '.http' - they did type letters.
        Assert.False(WorkspaceNames.TryToDocumentStem(typed, out _, out var reason));
        Assert.Contains("only an extension", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Leading_and_trailing_separators_are_trimmed()
    {
        // A trailing space or dot is a name Windows silently truncates, and the truncation
        // is how two different collections become one directory.
        Assert.True(WorkspaceNames.TryToSegment("  Orders.  ", out var segment, out _));
        Assert.Equal("Orders", segment);
    }

    [Theory]
    [InlineData("../../Windows/System32", "Windows-System32")]
    [InlineData(@"..\..\etc", "etc")]
    [InlineData("a/b", "a-b")]
    [InlineData(@"a\b", "a-b")]
    [InlineData("C:", "C")]
    [InlineData("..", null)]
    [InlineData(".", null)]
    [InlineData("/", null)]
    public void A_traversal_cannot_survive(string typed, string? expected)
    {
        // The point of the whitelist: '.', '/', '\' and ':' are not on it, so there is no
        // spelling of a traversal that comes out the other side as one. Names that are
        // nothing but punctuation come out as no name at all.
        var ok = WorkspaceNames.TryToSegment(typed, out var segment, out _);

        Assert.Equal(expected is not null, ok);
        Assert.Equal(expected, segment);
    }

    [Fact]
    public void A_segment_never_contains_a_separator()
    {
        Assert.True(WorkspaceNames.TryToSegment("one/two\\three:four", out var segment, out _));

        Assert.DoesNotContain('/', segment);
        Assert.DoesNotContain('\\', segment);
        Assert.DoesNotContain(':', segment);
        Assert.DoesNotContain('.', segment);
    }

    [Fact]
    public void A_control_character_cannot_survive()
    {
        Assert.True(WorkspaceNames.TryToSegment("a\0b\nc", out var segment, out _));
        Assert.Equal("a-b-c", segment);
    }

    [Theory]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("lpt1")]
    public void A_device_name_is_escaped_rather_than_refused(string typed)
    {
        // Windows resolves these ahead of a file of the same stem - con.http opens the
        // console, whatever directory it sits in. Losing the name entirely helps nobody.
        Assert.True(WorkspaceNames.TryToSegment(typed, out var segment, out _));
        Assert.Equal("_" + typed, segment);
    }

    [Fact]
    public void A_long_name_is_cut_to_the_ceiling()
    {
        Assert.True(WorkspaceNames.TryToSegment(new string('a', 500), out var segment, out _));
        Assert.Equal(WorkspaceNames.MaxSegmentLength, segment.Length);
    }

    [Fact]
    public void An_astral_script_is_not_reduced_to_dashes()
    {
        // char.IsLetterOrDigit is false for both halves of every surrogate pair, so walking
        // chars would turn a name written in one into punctuation. This is the ideograph
        // Etch's word splitter once deleted.
        const string Name = "\U00020BB7";

        Assert.True(WorkspaceNames.TryToSegment(Name, out var segment, out _));
        Assert.Equal(Name, segment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("///")]
    [InlineData(null)]
    public void A_name_with_nothing_keepable_is_refused_with_a_reason(string? typed)
    {
        Assert.False(WorkspaceNames.TryToSegment(typed, out _, out var reason));
        Assert.NotEmpty(reason);
    }

    [Theory]
    [InlineData("orders.http", "orders")]
    [InlineData("orders.HTTP", "orders")]
    [InlineData("orders.rest", "orders")]
    [InlineData("orders", "orders")]
    [InlineData("orders.json", "orders-json")]
    public void A_typed_extension_is_stripped_before_the_dot_is(string typed, string expected)
    {
        // Somebody typing "orders.http" means a file called orders.http, not one called
        // orders-http.http. The dot cannot survive the segment rule, so the strip has to
        // happen first.
        Assert.True(WorkspaceNames.TryToDocumentStem(typed, out var stem, out _));
        Assert.Equal(expected, stem);
    }

    [Fact]
    public void Only_one_extension_is_stripped()
    {
        Assert.True(WorkspaceNames.TryToDocumentStem("orders.http.http", out var stem, out _));
        Assert.Equal("orders-http", stem);
    }
}
