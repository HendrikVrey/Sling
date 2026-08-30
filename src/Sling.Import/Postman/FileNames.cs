using System.Globalization;
using System.Text;

namespace Sling.Import.Postman;

/// <summary>
/// Turns the folder and request names inside a collection into paths on disk, and is the
/// only thing that may build an <see cref="ImportedFile"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a security boundary, not a tidying pass.</b> Every name here comes out of a
/// JSON file that arrived from somewhere else - a colleague, a public API's published
/// collection, a download. A folder called <c>..\..\..\Windows\System32</c> is two
/// keystrokes to write and, without this, would decide where a file lands. The importer
/// itself does no I/O, so the escape would not happen here; it would happen in
/// <c>Sling.Persistence</c>, several layers from the JSON that caused it.
/// </para>
/// <para>
/// <b>The rule is a whitelist, deliberately.</b> A slug keeps Unicode letters and digits,
/// <c>-</c> and <c>_</c>, and turns everything else into <c>-</c>. That makes <c>..</c>,
/// <c>/</c>, <c>\</c>, <c>:</c>, NUL, a trailing dot and a trailing space impossible by
/// construction rather than by a list of things to refuse - rejecting characters is a
/// deny-list in disguise, and the one you forget is the one that matters. Unicode letters
/// survive because a collection whose folders are named in Japanese should not import as a
/// tree of dashes.
/// </para>
/// <para>
/// Containment is checked again in <c>Sling.Persistence</c> before anything is written.
/// That is a second line, not a duplicate: the two would have to be wrong in the same way
/// on the same day.
/// </para>
/// </remarks>
internal sealed class FileNames
{
    /// <summary>
    /// How long one path segment may be.
    /// </summary>
    /// <remarks>
    /// A Postman folder name is occasionally a whole sentence, and several of those in one
    /// path reach Windows' limit - where the failure arrives as an exception from the
    /// write rather than as anything a person could connect to a name in their collection.
    /// </remarks>
    private const int MaxSegment = 60;

    /// <summary>
    /// How many directory levels a file may sit under.
    /// </summary>
    /// <remarks>
    /// Not a safety limit - <see cref="MaxSegment"/> and the containment check cover that,
    /// but a bound on total path length, which is otherwise the product of two things the
    /// collection chooses. Folders deeper than this land in the deepest directory allowed,
    /// each still getting a file of its own through the numeric suffix below. The walk that
    /// builds the tree says so in a note; silently flattening would leave two files whose
    /// names no longer say where they came from.
    /// </remarks>
    internal const int MaxDepth = 6;

    /// <summary>What a name that slugs away to nothing becomes.</summary>
    private const string Fallback = "requests";

    /// <summary>
    /// The DOS device names, which Windows still resolves ahead of a file of the same
    /// stem - <c>con.http</c> opens the console, whatever directory it sits in.
    /// </summary>
    /// <remarks>
    /// Checked against the stem rather than the whole file name, because that is how
    /// Windows checks it. Escaped with a leading underscore rather than refused: the name
    /// is somebody's folder and losing it entirely helps nobody.
    /// </remarks>
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com0", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt0", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>
    /// Paths already handed out, compared the way Windows compares them.
    /// </summary>
    /// <remarks>
    /// Ordinal-ignore-case, because two Postman folders called "Orders" and "orders" are
    /// different folders to Postman and the same file to Windows - and the second silently
    /// overwriting the first is the kind of data loss an import must not have.
    /// </remarks>
    private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a file, giving it a path nothing else in this import has.
    /// </summary>
    /// <param name="folders">The Postman folder names it sits under, outermost first.</param>
    /// <param name="name">The name the file itself takes.</param>
    /// <param name="extension">Including the dot.</param>
    /// <param name="text">The file's content.</param>
    public ImportedFile Create(
        IReadOnlyList<string> folders,
        string name,
        string extension,
        string text) =>
        new(Reserve(folders, name, extension), text);

    /// <summary>
    /// Builds a file at the root of the import, under a name that is not slugged.
    /// </summary>
    /// <remarks>
    /// For the two environment files, whose names are fixed by the format
    /// (<c>Sling.md</c> §8) rather than taken from the collection. Nothing untrusted
    /// reaches this, which is why it may bypass <see cref="Slug"/> - and it still goes
    /// through <see cref="_taken"/>, so an export that somehow produced two of them cannot
    /// have the second silently replace the first.
    /// </remarks>
    public ImportedFile CreateFixed(string fileName, string text)
    {
        if (!_taken.Add(fileName))
        {
            throw new InvalidOperationException($"'{fileName}' was already produced by this import.");
        }

        return new ImportedFile(fileName, text);
    }

    private string Reserve(IReadOnlyList<string> folders, string name, string extension)
    {
        var path = new StringBuilder();

        foreach (var folder in folders.Take(MaxDepth))
        {
            path.Append(Slug(folder, "folder")).Append('/');
        }

        var stem = Slug(name, Fallback);
        var candidate = path.ToString() + stem + extension;

        // A numeric suffix rather than a hash: two folders called "Orders" and "orders "
        // both slug to "orders", and "orders-2.http" is something a person can look at and
        // understand, where "orders-a3f1.http" is something they have to decode.
        for (var n = 2; !_taken.Add(candidate); n++)
        {
            candidate = path.ToString()
                + stem
                + "-"
                + n.ToString(CultureInfo.InvariantCulture)
                + extension;
        }

        return candidate;
    }

    /// <summary>
    /// Reduces one name to one path segment.
    /// </summary>
    /// <remarks>
    /// Walks runes rather than chars. <c>char.IsLetterOrDigit</c> is false for both halves
    /// of every surrogate pair, so a name written in an astral script would slug away to
    /// dashes - the same defect that once deleted the ideograph out of Etch's word splitter.
    /// </remarks>
    public static string Slug(string? name, string fallback)
    {
        var slug = new StringBuilder(MaxSegment);
        var pendingSeparator = false;

        foreach (var rune in (name ?? string.Empty).EnumerateRunes())
        {
            if (slug.Length >= MaxSegment)
            {
                break;
            }

            if (Rune.IsLetterOrDigit(rune) || rune.Value is '-' or '_')
            {
                // Collapsed rather than emitted as they arrive: "Orders - refunds (v2)"
                // would otherwise become "orders----refunds--v2-".
                if (pendingSeparator && slug.Length > 0)
                {
                    slug.Append('-');
                }

                pendingSeparator = false;

                // Invariant lower-casing, never culture-aware: a locale-dependent fold is
                // where the Turkish dotless i lives, and a file name that differs by
                // machine locale is a file name two checkouts disagree about.
                slug.Append(Rune.ToLowerInvariant(rune).ToString());

                continue;
            }

            pendingSeparator = true;
        }

        var text = slug.ToString().Trim('-', '_');

        if (text.Length == 0)
        {
            return fallback;
        }

        return DeviceNames.Contains(text) ? "_" + text : text;
    }
}
