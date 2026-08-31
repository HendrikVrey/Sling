namespace Sling.Core.Documents;

/// <summary>
/// A replacement of one range of a document's text.
/// </summary>
/// <remarks>
/// <para>
/// How <c>Sling.Core</c> changes a document: it computes edits, and the editor applies
/// them. Returning new text instead would work and would cost the two things an editor
/// exists to keep - the caret, and an undo history with one entry per thing the user did
/// rather than one per whole-buffer replacement.
/// </para>
/// <para>
/// Offsets are into the text the edits were computed from, so a list of them is only valid
/// against that text and only when applied <b>last first</b>. Each edit shifts everything
/// after it; going the other way silently applies the second edit at the wrong place.
/// <see cref="Apply"/> is the ordering, written once.
/// </para>
/// </remarks>
/// <param name="Offset">Where the replaced range starts.</param>
/// <param name="Length">How much is replaced. Zero for an insertion.</param>
/// <param name="Text">What goes there. Empty for a deletion.</param>
public sealed record TextEdit(int Offset, int Length, string Text)
{
    /// <summary>
    /// Applies <paramref name="edits"/> to <paramref name="text"/>, last one first.
    /// </summary>
    /// <remarks>
    /// For testing and for callers that hold a plain string. An editor applies them to its
    /// own document instead, in this same order, so the undo stack keeps them together.
    /// </remarks>
    public static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(edits);

        foreach (var edit in edits.OrderByDescending(e => e.Offset))
        {
            text = text[..edit.Offset] + edit.Text + text[(edit.Offset + edit.Length)..];
        }

        return text;
    }
}
