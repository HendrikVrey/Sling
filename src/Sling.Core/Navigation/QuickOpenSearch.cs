namespace Sling.Core.Navigation;

/// <summary>
/// Which Quick Open rows a query finds, and in what order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Substring, never fuzzy.</b> Fuzzy matching is impressive in a demo and infuriating in
/// use, because the reason a wrong row came top is never visible to the person looking at
/// it. A substring either is in the text or it is not, and somebody who cannot see why a row
/// matched can read the row and find out.
/// </para>
/// <para>
/// <b>Every word must match, and they may match different fields.</b> Splitting the query on
/// whitespace is what makes <c>post orders</c> and <c>orders staging</c> work without either
/// being a syntax anyone has to be told about - one term lands on the verb and the other on
/// the name, or one on the collection and the other on the URL.
/// </para>
/// <para>
/// <b>Where a term matched decides the order.</b> A match in what a request is called beats
/// one in the URL it happens to contain, which beats one in the name of the folder it sits
/// in - the same rule a command palette needs anywhere: a match in the name must beat a
/// match in the detail, or typing a word that appears in one row's title and forty rows'
/// paths buries the row that was wanted.
/// </para>
/// </remarks>
public static class QuickOpenSearch
{
    /// <summary>What a match in each field is worth.</summary>
    /// <remarks>
    /// Ordered by how specifically the field identifies the row. A name is what somebody
    /// chose to call this one request; a collection is shared by everything under a folder,
    /// so a match there says the least.
    /// </remarks>
    private const int NameScore = 100;

    private const int FileScore = 80;

    private const int MethodScore = 60;

    private const int TargetScore = 40;

    private const int CollectionScore = 20;

    /// <summary>
    /// Added when a term matches at the start of the field rather than inside it.
    /// </summary>
    /// <remarks>
    /// Smaller than the gap between two fields, on purpose: a prefix is evidence about how
    /// well a term fits a field, not a reason for a URL to outrank a name.
    /// </remarks>
    private const int PrefixBonus = 15;

    /// <summary>
    /// The rows <paramref name="query"/> finds, best first.
    /// </summary>
    /// <param name="entries">Everything in the workspace, in the order it should appear untyped.</param>
    /// <param name="query">What was typed. Empty means "everything", in the given order.</param>
    /// <param name="limit">The most rows to answer with.</param>
    /// <remarks>
    /// An empty query answers the list as given rather than nothing, because the palette is
    /// also a way to see what is there - and an empty popup on the first keypress reads as a
    /// feature that has not loaded.
    /// </remarks>
    public static IReadOnlyList<QuickOpenEntry> Rank(
        IReadOnlyList<QuickOpenEntry> entries,
        string query,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        var terms = (query ?? string.Empty).Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (terms.Length == 0)
        {
            return [.. entries.Take(limit)];
        }

        var scored = new List<(QuickOpenEntry Entry, int Score, int Order)>();

        for (var i = 0; i < entries.Count; i++)
        {
            if (Score(entries[i], terms) is { } score)
            {
                scored.Add((entries[i], score, i));
            }
        }

        return
        [
            .. scored
                .OrderByDescending(s => s.Score)

                // The given order breaks every tie, which keeps the list stable as somebody
                // types: a row that does not change its score must not move, or the thing
                // being reached for slides out from under the keystroke selecting it.
                .ThenBy(s => s.Order)
                .Take(limit)
                .Select(s => s.Entry),
        ];
    }

    /// <summary>
    /// What <paramref name="entry"/> scores against every term, or null if one is absent.
    /// </summary>
    /// <remarks>
    /// Null rather than zero for "no match", because zero is a legitimate score for a row
    /// that matched nothing worth points and the two must not be confused - a search that
    /// answers every row is a search nobody can use.
    /// </remarks>
    private static int? Score(QuickOpenEntry entry, string[] terms)
    {
        var total = 0;

        foreach (var term in terms)
        {
            var best = 0;

            best = Math.Max(best, Field(entry.Name, term, NameScore));
            best = Math.Max(best, Field(entry.File, term, FileScore));
            best = Math.Max(best, Field(entry.Method, term, MethodScore));
            best = Math.Max(best, Field(entry.Target, term, TargetScore));
            best = Math.Max(best, Field(entry.Collection, term, CollectionScore));

            if (best == 0)
            {
                return null;
            }

            total += best;
        }

        return total;
    }

    /// <summary>What one term is worth against one field.</summary>
    /// <remarks>
    /// Case-insensitive throughout, and ordinal: these are file names, verbs and URLs, none
    /// of which wants a culture's casing rules applied to it. Ordinal-ignore-case is also the
    /// comparison the rest of this codebase uses for paths.
    /// </remarks>
    private static int Field(string text, string term, int weight)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);

        return at switch
        {
            < 0 => 0,
            0 => weight + PrefixBonus,
            _ => weight,
        };
    }
}
