using Etch.Core.Abstractions;
using Etch.Core.Palette;
using Etch.Core.Transforms;
using ICSharpCode.AvalonEdit;

namespace Sling.App.Editor;

/// <summary>What running a transform did, in the terms the status bar needs.</summary>
/// <param name="Applied">False when the transform refused the input.</param>
/// <param name="Message">What to tell the user. Never null.</param>
/// <param name="ErrorOffset">
/// Where in the document the failure was, or null. Already translated out of the
/// transform's own coordinates into the document's.
/// </param>
internal readonly record struct TransformOutcome(bool Applied, string Message, int? ErrorOffset);

/// <summary>
/// Runs <c>Etch.Core</c> transforms over the response body.
/// </summary>
/// <remarks>
/// <para>
/// The differentiator, and the reason `Etch.Core` is a dependency rather than an
/// inspiration. A response arrives base64-encoded inside a JSON field often enough that
/// "decode, then format, then sort the keys" is a real workflow — and in Sling it is
/// three clicks in the pane the response landed in, with no copying into another tool.
/// </para>
/// <para>
/// Transforms chain because they apply <b>in place</b>: each one rewrites the buffer, the
/// format is detected again, and the next suggestion is computed from what is now there.
/// Nothing here has to know about chaining; it falls out of applying to the buffer rather
/// than to a copy.
/// </para>
/// </remarks>
internal static class BodyTransforms
{
    /// <summary>
    /// How many ready transforms the context menu offers before the full list.
    /// </summary>
    /// <remarks>
    /// Four, matching <c>PaletteRanking.SuggestedTop</c>'s own default. A menu that opens
    /// with a dozen equally-plausible rows is a menu nobody reads.
    /// </remarks>
    internal const int SuggestedCount = 4;

    /// <summary>Everything Sling can do to a body, for the full menu.</summary>
    internal static IReadOnlyList<ITransform> All => TransformRegistry.All;

    /// <summary>
    /// The transforms worth offering first for <paramref name="detection"/>.
    /// </summary>
    internal static IReadOnlyList<ITransform> Suggested(
        in DetectionResult detection,
        IReadOnlyList<string>? recentIds) =>
        PaletteRanking.SuggestedTop(detection, recentIds, SuggestedCount);

    /// <summary>
    /// Applies <paramref name="transform"/> to the selection, or to the whole buffer when
    /// there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The read-only flag on the pane is about typing, not about this. A response is not
    /// something to edit by hand — a stray keystroke in a body you are reading is pure
    /// loss — but a transform is a deliberate act with a visible result and one
    /// <c>Ctrl+Z</c> behind it. <c>TextDocument.Replace</c> goes under the read-only
    /// section provider, which only guards the editing commands.
    /// </para>
    /// <para>
    /// The whole edit is one undo group, so undo returns the body to what the server sent
    /// rather than unpicking a transform in pieces.
    /// </para>
    /// </remarks>
    internal static async Task<TransformOutcome> ApplyAsync(
        TextEditor editor,
        ITransform transform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(transform);

        var document = editor.Document;

        if (document is null)
        {
            return new TransformOutcome(Applied: false, "There is nothing to transform.", null);
        }

        // Captured before the edit, because the replace moves the selection and both the
        // re-selection below and the transform's own WasSelection flag are about what the
        // user had chosen, not about what the document looks like afterwards.
        var hadSelection = editor.SelectionLength > 0;

        var selection = hadSelection
            ? (Start: editor.SelectionStart, Length: editor.SelectionLength)
            : (Start: 0, Length: document.TextLength);

        var input = document.GetText(selection.Start, selection.Length);

        if (input.Length == 0 && transform.NeedsInput)
        {
            return new TransformOutcome(Applied: false, $"{transform.Name} needs some text.", null);
        }

        // The only part that runs off the UI thread, and the only part that can be slow.
        // Everything above reads the document and everything below writes it, both of
        // which AvalonEdit requires on the thread that owns it — so the boundary is drawn
        // exactly around the pure function.
        //
        // ConfigureAwait(true) is load-bearing rather than incidental: the continuation
        // *must* come back to the dispatcher before touching the document again.
        var result = await Task
            .Run(
                () => transform.Apply(
                    new TransformInput(input, hadSelection, new TransformOptions()),
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(true);

        // The buffer can have moved while the transform ran — a response arriving, or the
        // pane being reset. Applying an answer computed from text that is no longer there
        // would write a transform of one body over another.
        if (document.TextLength < selection.Start + selection.Length
            || !string.Equals(document.GetText(selection.Start, selection.Length), input, StringComparison.Ordinal))
        {
            return new TransformOutcome(
                Applied: false,
                $"{transform.Name} was abandoned — the buffer changed while it ran.",
                null);
        }

        if (!result.Success)
        {
            // The offset the transform reports is relative to the text it was handed,
            // which is the selection when there was one. Only the caller knows where that
            // started, so only the caller can turn it into a document offset — reporting
            // the raw number would put the caret in the wrong place on every selection.
            var offset = result.ErrorOffset is { } relative
                ? Math.Clamp(selection.Start + relative, 0, document.TextLength)
                : (int?)null;

            return new TransformOutcome(
                Applied: false,
                result.Error ?? $"{transform.Name} could not be applied.",
                offset);
        }

        var replacement = result.Text ?? string.Empty;

        using (document.RunUpdate())
        {
            document.Replace(selection.Start, selection.Length, replacement);
        }

        // Re-select what was produced, so a chain of transforms over a selection keeps
        // operating on the same region instead of silently widening to the whole buffer
        // after the first one. Nothing is selected when the transform took the whole
        // buffer, because selecting everything would then make the *next* transform's
        // "selection or buffer" question answer differently for no reason the user caused.
        if (hadSelection)
        {
            editor.Select(selection.Start, replacement.Length);
        }

        return new TransformOutcome(
            Applied: true,
            result.Message ?? $"{transform.Name} applied.",
            null);
    }
}
