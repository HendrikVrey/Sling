using System.Windows;
using Sling.Import.Curl;

namespace Sling.App;

/// <summary>
/// Paste a curl command into the request pane and get a request.
/// </summary>
/// <remarks>
/// The escape hatch for everything not yet migrated (<c>Sling.md</c> §4b). Postman copies
/// as curl, browsers copy as curl, and API documentation is written in curl — so this is
/// the shortest path from anywhere into Sling.
/// </remarks>
public partial class MainWindow
{
    private void InstallCurlPaste() =>
        DataObject.AddPastingHandler(RequestPane, OnRequestPanePasting);

    private void RemoveCurlPaste() =>
        DataObject.RemovePastingHandler(RequestPane, OnRequestPanePasting);

    /// <summary>
    /// Intercepts a paste and converts it when — and only when — it is a curl command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Anything that is not recognisably curl is left completely alone.</b> A paste
    /// handler that rewrites text is a hostile thing to build if it ever guesses: the user
    /// pasted something they had in their hand, and getting it back changed is worse than
    /// not having the feature. <see cref="CurlImport.Convert"/> answers "not curl" rather
    /// than trying, and this returns without touching the event in that case.
    /// </para>
    /// <para>
    /// The conversion replaces the clipboard content <em>for this paste only</em>, by
    /// swapping the data object WPF is about to apply. The system clipboard is not
    /// modified — pasting the same command into another application afterwards still
    /// yields the curl command, which is what anyone would expect.
    /// </para>
    /// </remarks>
    private void OnRequestPanePasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.SourceDataObject?.GetData(DataFormats.UnicodeText) is not string pasted)
        {
            return;
        }

        var result = CurlImport.Convert(pasted);

        if (!result.Recognized || result.Http.Length == 0)
        {
            return;
        }

        var replacement = new DataObject();
        replacement.SetData(DataFormats.UnicodeText, result.Http);

        e.DataObject = replacement;

        StatusLeft.Text = result.Notes.Count == 0
            ? "Converted a curl command."
            : $"Converted a curl command — {Plural(result.Notes.Count)} noted in the document.";
    }

    private static string Plural(int count) =>
        count == 1 ? "1 thing" : $"{count} things";
}
