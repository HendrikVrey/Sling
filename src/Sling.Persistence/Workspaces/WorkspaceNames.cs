using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Sling.Persistence.Workspaces;

/// <summary>
/// Turns a name somebody typed into the rail into one path segment on disk.
/// </summary>
/// <remarks>
/// <para>
/// <b>A whitelist, deliberately, and for the same reason the Postman importer uses one.</b>
/// A segment keeps Unicode letters and digits, <c>-</c>, <c>_</c> and spaces, and turns
/// everything else into <c>-</c>. That makes <c>..</c>, <c>/</c>, <c>\</c>, <c>:</c>, NUL,
/// a trailing dot and a trailing space impossible <em>by construction</em> rather than by a
/// list of things to refuse - rejecting characters is a deny-list in disguise, and the one
/// you forget is the one that matters.
/// </para>
/// <para>
/// <b>It matters even though the user typed it.</b> The threat model is weaker than the
/// importer's - nobody is attacking their own workspace - but "New collection" writes a
/// directory at a path built from free text, and a person who pastes a name out of a
/// browser tab is not thinking about what <c>../</c> does. The failure would be a folder
/// created outside the workspace, which is not a security incident and is still a bug that
/// costs an afternoon to understand.
/// </para>
/// <para>
/// <b>The result is shown back, never assumed.</b> Slugging silently is how a user ends up
/// with a collection whose name is not the one they typed and no idea why, so every caller
/// reports the segment it actually created.
/// </para>
/// <para>
/// Case survives, unlike the importer's slug. That lower-cases so two checkouts of the same
/// collection agree byte for byte; a name typed by hand has no second checkout to agree
/// with, and "Orders" reading back as "orders" is a small rudeness with nothing bought for
/// it.
/// </para>
/// </remarks>
public static class WorkspaceNames
{
    /// <summary>
    /// How long one segment may be.
    /// </summary>
    /// <remarks>
    /// A bound on total path length, which is otherwise free: collections nest, and Windows'
    /// limit arrives as an exception from the write rather than as anything a person can
    /// connect to what they typed.
    /// </remarks>
    public const int MaxSegmentLength = 60;

    /// <summary>The extension a new request document gets.</summary>
    public const string DocumentExtension = ".http";

    /// <summary>Extensions stripped off a typed name before it becomes a stem.</summary>
    /// <remarks>
    /// Somebody typing "orders.http" means a file called <c>orders.http</c>, not one called
    /// <c>orders-http.http</c>. The dot cannot survive <see cref="TryToSegment"/>, so this
    /// has to happen before it rather than after.
    /// </remarks>
    private static readonly string[] StrippedExtensions = [".http", ".rest"];

    /// <summary>
    /// The DOS device names, which Windows still resolves ahead of a file of the same
    /// stem - <c>con.http</c> opens the console, whatever directory it sits in.
    /// </summary>
    /// <remarks>
    /// Checked against the stem rather than the whole file name, because that is how
    /// Windows checks it. Escaped with a leading underscore rather than refused: the name
    /// is the one somebody chose and losing it entirely helps nobody.
    /// </remarks>
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com0", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt0", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>
    /// Reduces <paramref name="typed"/> to one usable path segment.
    /// </summary>
    /// <param name="segment">The segment, when there is one.</param>
    /// <param name="reason">
    /// Why there is not, phrased for the status bar. The only failure is a name with
    /// nothing in it a file name may keep - which is worth saying rather than silently
    /// substituting a default, because the user is looking at the box they typed it into.
    /// </param>
    public static bool TryToSegment(
        string? typed,
        [NotNullWhen(true)] out string? segment,
        [NotNullWhen(false)] out string? reason)
    {
        segment = null;

        var text = new StringBuilder(MaxSegmentLength);

        // At most one separator is ever owed, and it is written only when a keepable rune
        // follows it. That is what collapses runs and drops leading and trailing ones in a
        // single pass: "Orders - refunds (v2)" becomes "Orders - refunds v2" rather than
        // "Orders----refunds--v2-". A dash outranks a space, so a run containing anything
        // illegal reads as a replacement rather than as a word break that was always there.
        char? owed = null;

        // Runes rather than chars. char.IsLetterOrDigit is false for both halves of every
        // surrogate pair, so a name written in an astral script would come out as dashes,
        // the same defect that once deleted the ideograph out of Etch's word splitter.
        foreach (var rune in (typed ?? string.Empty).EnumerateRunes())
        {
            if (text.Length >= MaxSegmentLength)
            {
                break;
            }

            if (Rune.IsLetterOrDigit(rune) || rune.Value is '-' or '_')
            {
                if (owed is { } separator && text.Length > 0)
                {
                    text.Append(separator);
                }

                owed = null;
                text.Append(rune.ToString());
                continue;
            }

            // A space stays a space rather than folding to a dash - it is legal on every
            // file system Sling runs on, and "Order management" reads better than
            // "Order-management".
            owed = rune.Value == ' ' && owed != '-' ? ' ' : '-';
        }

        // Not '_'. It is on the whitelist, and trimming it here made a typed '_shared'
        // silently become 'shared' - a name coming back different for a reason nothing on
        // screen explains. The device-name escape below prepends its own underscore after
        // this, so it is unaffected.
        var name = text.ToString().Trim('-', ' ');

        if (name.Length == 0)
        {
            reason = "That name has nothing in it a file name can keep. Letters, digits, "
                + "spaces, '-' and '_' survive; everything else is replaced.";

            return false;
        }

        segment = DeviceNames.Contains(name) ? "_" + name : name;
        reason = null;
        return true;
    }

    /// <summary>
    /// Reduces <paramref name="typed"/> to the stem of a request document, without its
    /// extension.
    /// </summary>
    public static bool TryToDocumentStem(
        string? typed,
        [NotNullWhen(true)] out string? stem,
        [NotNullWhen(false)] out string? reason)
    {
        var text = (typed ?? string.Empty).Trim();

        foreach (var extension in StrippedExtensions)
        {
            if (text.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^extension.Length];
                break;
            }
        }

        // Said separately, because the general refusal is technically true and useless
        // here: somebody who typed '.http' did type letters, and being told that letters
        // survive does not explain what went wrong.
        if (text.Length == 0)
        {
            stem = null;
            reason = $"That is only an extension - Sling adds '{DocumentExtension}' itself. "
                + "Give the file a name.";

            return false;
        }

        return TryToSegment(text, out stem, out reason);
    }
}
