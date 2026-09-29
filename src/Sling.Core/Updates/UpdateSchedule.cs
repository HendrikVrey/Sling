namespace Sling.Core.Updates;

/// <summary>
/// When to ask GitHub, and whether an answer is worth showing.
/// </summary>
/// <remarks>
/// Kept apart from the window so the rules can be read, and tested, in one place.
/// </remarks>
public static class UpdateSchedule
{
    /// <summary>How often an automatic check may run.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>Whether an automatic check is due.</summary>
    /// <remarks>
    /// A last check in the future means the clock was moved back; the check is due rather
    /// than postponed until the clock catches up, which could be months.
    /// </remarks>
    public static bool IsDue(DateTimeOffset? lastChecked, DateTimeOffset now) =>
        lastChecked is not { } last || last > now || now - last >= Interval;

    /// <summary>
    /// Whether <paramref name="latest"/> should be offered to someone running
    /// <paramref name="current"/>.
    /// </summary>
    /// <param name="current">The running version.</param>
    /// <param name="latest">The newest release.</param>
    /// <param name="skipped">The version the user said to skip, or null.</param>
    /// <param name="userAsked">
    /// True when the user pressed "Check now": a skipped version is offered again then,
    /// because asking is how somebody changes their mind.
    /// </param>
    public static bool ShouldOffer(ReleaseVersion current, ReleaseVersion latest, string? skipped, bool userAsked) =>
        latest > current
        && (userAsked || !string.Equals(latest.ToString(), skipped, StringComparison.Ordinal));
}
