namespace Sling.Core.Documents;

/// <summary>
/// An inclusive run of 1-based line numbers.
/// </summary>
/// <remarks>
/// Lines rather than offsets, because everything that consumes one is talking to a person
/// or to an editor: a diagnostic names a line, the rail carries a line, and an editor
/// collapses lines. <see cref="TextEdit"/> is the offset-shaped type and stays that way -
/// it is applied to text, where a line number would have to be resolved first.
/// </remarks>
/// <param name="First">The first line in the run.</param>
/// <param name="Last">
/// The last line in the run. Less than <paramref name="First"/> for an empty run, which is
/// what a complement can produce and is easier to carry than a null.
/// </param>
public readonly record struct LineRange(int First, int Last)
{
    /// <summary>How many lines the run covers. Zero when it is empty.</summary>
    public int Count => Last < First ? 0 : Last - First + 1;

    /// <summary>True when the run covers no lines at all.</summary>
    public bool IsEmpty => Last < First;

    /// <summary>Whether <paramref name="line"/> falls inside the run.</summary>
    public bool Contains(int line) => line >= First && line <= Last;
}
