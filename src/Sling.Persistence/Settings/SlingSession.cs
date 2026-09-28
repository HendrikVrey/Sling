namespace Sling.Persistence.Settings;

/// <summary>
/// Where somebody was when they last closed Sling.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a workspace format, and the distinction is the one <c>Sling.md</c> §1 turns on.</b>
/// Nothing here is a fact about the user's API: it is which folder was open, which file was
/// on screen, where the caret was and how wide the two panes were. Delete this file and
/// every request still runs, every environment still resolves and the folder is still a
/// folder of <c>.http</c> files - which is the test the collections rail was held to and is
/// the same test here.
/// </para>
/// <para>
/// It lives beside the settings in <c>%LOCALAPPDATA%</c> rather than in the workspace for
/// the reason <see cref="Persistence.LocalData"/> gives: a workspace is somebody's git
/// checkout, and where their caret was has no business in their diff.
/// </para>
/// <para>
/// <b>What is deliberately not here: whether the rail was hidden.</b> That toggle is a "get
/// out of my way for a minute" control rather than a preference, and a rail that stayed
/// hidden across restarts would put the application back into the state whose invisibility
/// was a reported bug.
/// </para>
/// </remarks>
public sealed record SlingSession
{
    /// <summary>An empty session, which is what a machine with no session file gets.</summary>
    public static SlingSession None { get; } = new();

    /// <summary>How many folders the recent list holds.</summary>
    /// <remarks>
    /// A menu, not a history. Past about eight entries the list stops being scannable and
    /// the thing being looked for is quicker to reach through the folder dialog.
    /// </remarks>
    public const int MaxRecentFolders = 8;

    /// <summary>The narrowest the request column may be restored to, as a share of the two.</summary>
    /// <remarks>
    /// A stored extreme would restore a pane the user cannot see and may not realise is
    /// there. The columns carry their own minimum widths, but those are in pixels and this
    /// is about the ratio being sensible on a window of any size.
    /// </remarks>
    public const double MinimumSplit = 0.15;

    /// <inheritdoc cref="MinimumSplit"/>
    public const double MaximumSplit = 0.85;

    /// <summary>The folder that was open, or null.</summary>
    public string? WorkspaceRoot { get; init; }

    /// <summary>The document that was on screen, or null for an untitled buffer.</summary>
    public string? DocumentPath { get; init; }

    /// <summary>The caret's 1-based line, or zero for the top of the file.</summary>
    public int CaretLine { get; init; }

    /// <summary>The caret's 1-based column.</summary>
    public int CaretColumn { get; init; }

    /// <summary>The request column's share of the two panes, from 0 to 1.</summary>
    /// <remarks>
    /// A ratio rather than a pixel width, because the window is not necessarily reopened at
    /// the size it was closed at - a stored 700 px is most of a laptop screen and a third of
    /// a monitor.
    /// </remarks>
    public double Split { get; init; } = 0.5;

    /// <summary>Folders opened before, most recent first.</summary>
    public IReadOnlyList<string> RecentFolders { get; init; } = [];

    /// <summary>
    /// This session with the split brought inside its allowed range.
    /// </summary>
    /// <remarks>
    /// Applied on load and on save, so a hand-edited file cannot put a ratio into force that
    /// the splitter would not let anybody drag to. A zero - what a file missing the key
    /// parses as - becomes the minimum rather than a pane of no width.
    /// </remarks>
    public SlingSession Clamped() => this with
    {
        Split = double.IsFinite(Split) ? Math.Clamp(Split, MinimumSplit, MaximumSplit) : 0.5,
        CaretLine = Math.Max(0, CaretLine),
        CaretColumn = Math.Max(0, CaretColumn),
        RecentFolders = [.. RecentFolders.Take(MaxRecentFolders)],
    };

    /// <summary>
    /// This session with <paramref name="folder"/> at the top of the recent list.
    /// </summary>
    /// <remarks>
    /// Deduplicated case-insensitively, because Windows paths are, and because the same
    /// folder reached through the dialog twice would otherwise fill the menu with itself.
    /// </remarks>
    public SlingSession WithRecent(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return this with
        {
            RecentFolders =
            [
                folder,
                .. RecentFolders
                    .Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))
                    .Take(MaxRecentFolders - 1),
            ],
        };
    }
}
