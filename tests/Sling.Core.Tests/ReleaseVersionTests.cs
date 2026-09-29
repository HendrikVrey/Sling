using Sling.Core.Updates;

namespace Sling.Core.Tests;

/// <summary>
/// <see cref="ReleaseVersion"/> and <see cref="UpdateSchedule"/>: which version is newer,
/// and when to ask.
/// </summary>
public class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.0.2", "1.0.3")]
    [InlineData("v1.0.2", "1.0.10")]
    [InlineData("1.9.9", "2.0.0")]
    [InlineData("1.0.3-dev.12", "1.0.3")]
    [InlineData("1.0.3-dev.9", "1.0.3-dev.10")]
    [InlineData("1.0.3-alpha", "1.0.3-beta")]
    [InlineData("1.0.3-dev.99", "1.0.4-dev.1")]
    public void The_second_is_newer(string older, string newer)
    {
        var left = Parse(older);
        var right = Parse(newer);

        Assert.True(right > left);
        Assert.True(left < right);
    }

    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("v1.0.2", "1.0.2")]
    [InlineData("1.0.2+3a90575", "1.0.2")]
    [InlineData("1.0.3-dev.12+abc", "1.0.3-dev.12")]
    public void These_are_the_same_version(string left, string right)
    {
        Assert.Equal(Parse(left), Parse(right));
        Assert.Equal(Parse(left).GetHashCode(), Parse(right).GetHashCode());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.x.2")]
    [InlineData("1.0.2-")]
    [InlineData("1.0.2-dev..1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("99999999999.0.0")]
    public void These_are_not_versions(string? text) => Assert.Null(ReleaseVersion.TryParse(text));

    [Fact]
    public void Text_drops_the_v_and_the_build_metadata() =>
        Assert.Equal("1.0.3-dev.12", Parse("v1.0.3-dev.12+3a90575").ToString());

    [Fact]
    public void The_running_version_is_readable() =>
        Assert.NotEqual("0.0.0-unknown", ReleaseVersion.Of(typeof(ReleaseVersion).Assembly).ToString());

    [Fact]
    public void A_rolling_build_is_not_offered_the_release_it_follows() =>
        Assert.False(UpdateSchedule.ShouldOffer(Parse("1.0.3-dev.12"), Parse("1.0.2"), skipped: null, userAsked: true));

    [Fact]
    public void A_rolling_build_is_offered_the_release_it_leads_to() =>
        Assert.True(UpdateSchedule.ShouldOffer(Parse("1.0.3-dev.12"), Parse("1.0.3"), skipped: null, userAsked: false));

    [Fact]
    public void A_skipped_version_is_not_offered_again_automatically() =>
        Assert.False(UpdateSchedule.ShouldOffer(Parse("1.0.2"), Parse("1.0.3"), skipped: "1.0.3", userAsked: false));

    [Fact]
    public void A_skipped_version_is_offered_when_the_user_asks() =>
        Assert.True(UpdateSchedule.ShouldOffer(Parse("1.0.2"), Parse("1.0.3"), skipped: "1.0.3", userAsked: true));

    [Fact]
    public void Skipping_one_version_does_not_skip_the_next() =>
        Assert.True(UpdateSchedule.ShouldOffer(Parse("1.0.2"), Parse("1.0.4"), skipped: "1.0.3", userAsked: false));

    [Fact]
    public void The_same_version_is_never_offered() =>
        Assert.False(UpdateSchedule.ShouldOffer(Parse("1.0.3"), Parse("1.0.3"), skipped: null, userAsked: true));

    [Fact]
    public void A_check_is_due_when_there_has_never_been_one() =>
        Assert.True(UpdateSchedule.IsDue(null, DateTimeOffset.UtcNow));

    [Fact]
    public void A_check_is_not_due_within_a_day()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.False(UpdateSchedule.IsDue(now.AddHours(-23), now));
        Assert.True(UpdateSchedule.IsDue(now.AddHours(-25), now));
    }

    [Fact]
    public void A_last_check_in_the_future_means_the_clock_moved_and_a_check_is_due()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(UpdateSchedule.IsDue(now.AddDays(30), now));
    }

    private static ReleaseVersion Parse(string text) =>
        ReleaseVersion.TryParse(text) ?? throw new InvalidOperationException($"'{text}' did not parse.");
}
