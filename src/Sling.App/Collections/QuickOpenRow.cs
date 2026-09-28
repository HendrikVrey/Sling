using System.Windows.Media;
using Sling.Core.Navigation;

namespace Sling.App.Collections;

/// <summary>
/// One Quick Open result, as the list binds to it.
/// </summary>
/// <remarks>
/// <para>
/// A view-model over <see cref="QuickOpenEntry"/> for the same reason
/// <see cref="CollectionItem"/> is one over a folder walk: the entry is a fact about the
/// workspace, and the brush a verb is drawn in is a fact about this window's theme. Nothing
/// here is mutable - the list is replaced on every keystroke rather than updated - so unlike
/// the rail's rows these need no change notification.
/// </para>
/// <para>
/// The verb brushes are the rail's, so a DELETE is the same red in the tree, in the document
/// and here.
/// </para>
/// </remarks>
internal sealed class QuickOpenRow
{
    internal required QuickOpenEntry Entry { get; init; }

    /// <summary>What the row is called: a request's name, or a file's name.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Where it is and what it hits, dimmed under the label.
    /// </summary>
    /// <remarks>
    /// Both halves, because the two questions somebody has after finding a candidate row are
    /// "is that the one in the collection I meant" and "is that the URL I meant", and a list
    /// that answers neither makes them open rows to find out.
    /// </remarks>
    public required string Detail { get; init; }

    /// <summary>The verb, on a request row. Empty on a file.</summary>
    public required string Method { get; init; }

    public required Brush MethodBrush { get; init; }

    /// <summary>Whether the verb chip is drawn, as a trigger can read it.</summary>
    public bool IsRequest => Entry.Kind == QuickOpenKind.Request;
}
