namespace Sling.Persistence.Settings;

/// <summary>
/// The handful of things about Sling that are worth changing, with the bounds each one is
/// held to.
/// </summary>
/// <remarks>
/// <para>
/// Every value here exists because someone hits its default and needs it moved: an API
/// that takes four minutes to answer, a response larger than the cap, a redirect chain
/// deeper than ten. Nothing here is a preference — the product has no themes, no font
/// pickers and no layout options, because it has one layout and it is dark.
/// </para>
/// <para>
/// <strong>What is deliberately absent: any way to switch off TLS validation.</strong>
/// <c>Sling.md</c> §5.3 allows a bypass only per request, with loud and persistent
/// indication, and the surest way to hold that line is for the setting that could weaken
/// it not to exist. A global "ignore certificate errors" is checked once in frustration
/// and left on for a year.
/// </para>
/// <para>
/// A value out of range is clamped rather than rejected. This is a JSON file someone edits
/// by hand; refusing to start because a number is too large would be a worse answer than
/// starting with the largest number that works.
/// </para>
/// </remarks>
public sealed record SlingSettings
{
    /// <summary>The defaults, which is what a machine with no settings file gets.</summary>
    public static SlingSettings Default { get; } = new();

    /// <summary>How long a whole exchange, including redirects, may take.</summary>
    public int TimeoutSeconds { get; init; } = 100;

    /// <summary>
    /// The cap on a response body held in memory, in mebibytes. A larger response is kept
    /// as a prefix and flagged.
    /// </summary>
    public int MaxResponseBodyMegabytes { get; init; } = 16;

    /// <summary>
    /// How many redirects to follow before handing back the 3xx itself. Zero means follow
    /// none, which is a legitimate way to inspect what a redirect actually says.
    /// </summary>
    public int MaxRedirects { get; init; } = 10;

    /// <summary>
    /// Whether cookies are stored and replayed at all.
    /// </summary>
    /// <remarks>
    /// Worth being able to switch off. Debugging an authentication problem is much easier
    /// when the only credentials in play are the ones written in the document, and a
    /// session cookie picked up three requests ago is invisible in a way a header is not.
    /// </remarks>
    public bool CookiesEnabled { get; init; } = true;

    /// <summary>Whether completed exchanges are recorded to disk.</summary>
    public bool HistoryEnabled { get; init; } = true;

    /// <summary>How many entries history keeps before the oldest are dropped.</summary>
    public int HistoryMaxEntries { get; init; } = 500;

    /// <summary>
    /// This instance with every value brought inside its allowed range.
    /// </summary>
    /// <remarks>
    /// Applied on load and again on save, so a hand-edited file cannot put a value into
    /// force that the panel would not let anyone type — and so the file gets rewritten in
    /// the corrected form rather than silently disagreeing with the running application.
    /// </remarks>
    public SlingSettings Clamped() => new()
    {
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, 3600),
        MaxResponseBodyMegabytes = Math.Clamp(MaxResponseBodyMegabytes, 1, 512),
        MaxRedirects = Math.Clamp(MaxRedirects, 0, 20),
        CookiesEnabled = CookiesEnabled,
        HistoryEnabled = HistoryEnabled,
        HistoryMaxEntries = Math.Clamp(HistoryMaxEntries, 10, 10_000),
    };
}
