using Sling.Core.Auth;

namespace Sling.Core.Tests;

/// <summary>
/// What the token chip says, and the two things it must never say.
/// </summary>
/// <remarks>
/// The chip exists because a 401 could not distinguish a stale token from a wrong scope
/// from a token fetched against the other environment. Its wording is therefore about the
/// grant and the clock, and it carries neither the token nor the client secret - the type
/// has no field for either, which is the point.
/// </remarks>
public sealed class TokenSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void With_nothing_held_the_chip_says_so()
    {
        Assert.Equal("no token", TokenSummary.Chip([], Now));
    }

    [Fact]
    public void One_live_token_reads_as_the_time_it_has_left()
    {
        var chip = TokenSummary.Chip([Held(Now.AddMinutes(13))], Now);

        Assert.Contains("token", chip, StringComparison.Ordinal);
        Assert.Contains("12 min", chip, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same margin the cache applies, so what the chip says and what the next send does
    /// cannot disagree - a chip reading "20 s left" over a cache that has already given up on
    /// the token is worse than no chip.
    /// </summary>
    [Fact]
    public void The_countdown_stops_where_the_cache_stops_reusing_it()
    {
        var summary = Held(Now + OAuth2Token.ExpiryMargin);

        Assert.True(summary.IsSpent(Now));
        Assert.Equal("token spent", TokenSummary.Chip([summary], Now));
    }

    [Fact]
    public void Several_tokens_count_down_from_the_one_that_goes_first()
    {
        var chip = TokenSummary.Chip(
            [Held(Now.AddHours(2)), Held(Now.AddMinutes(5)), Held(Now.AddMinutes(30))],
            Now);

        Assert.Contains("3 tokens", chip, StringComparison.Ordinal);
        Assert.Contains("4 min", chip, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rounded down, so a number that has already gone is never shown as one that has not.
    /// </summary>
    [Fact]
    public void The_countdown_rounds_down()
    {
        var chip = TokenSummary.Chip([Held(Now.AddSeconds(149) + OAuth2Token.ExpiryMargin)], Now);

        Assert.Contains("2 min", chip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spent_token_is_described_as_spent_rather_than_as_anything_about_trust()
    {
        var clock = Held(Now.AddMinutes(-5)).Clock(Now);

        Assert.Contains("spent", clock, StringComparison.Ordinal);
        Assert.DoesNotContain("valid", clock, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invalid", clock, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Two tokens for the same client and different scopes look identical without the scope,
    /// and telling them apart is the whole reason the list exists.
    /// </summary>
    [Fact]
    public void The_description_carries_what_distinguishes_two_grants()
    {
        var read = Held(Now.AddMinutes(10), scope: "orders.read").Describe();
        var write = Held(Now.AddMinutes(10), scope: "orders.write").Describe();

        Assert.NotEqual(read, write);
        Assert.Contains("orders.read", read, StringComparison.Ordinal);
    }

    private static TokenSummary Held(DateTimeOffset expires, string? scope = null) =>
        new("https://auth.example.com/token", "my-client", scope, null, Now.AddMinutes(-1), expires);
}
