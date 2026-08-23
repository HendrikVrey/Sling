using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Sling.Import.Postman;
using Sling.Persistence.Workspaces;

namespace Sling.App;

/// <summary>
/// Importing a Postman export: <c>Ctrl+I</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two dialogs and nothing else — pick the exports, pick a folder, and the workspace opens
/// on the result. There is no wizard, no mapping screen and no preview, because every one
/// of those would be a decision the user cannot make yet: what an import turns into is
/// readable text in files they are about to have open, and reading it there is a better
/// review than any dialog could be.
/// </para>
/// <para>
/// The collection and its environment exports go in the same dialog. Postman writes them
/// as separate files and a collection alone is full of <c>{{base_url}}</c> references
/// whose values are in the other one, so asking for them together is what makes the import
/// actually run.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private const string ExportFilter =
        "Postman exports (*.json)|*.json|All files (*.*)|*.*";

    private async Task ImportPostmanAsync()
    {
        if (IsSending)
        {
            return;
        }

        // Asked before the dialogs rather than after them: picking files and a folder and
        // then being asked about unsaved work reads as though the import is about to be
        // cancelled, and Cancel here has to mean the whole thing stops.
        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return;
        }

        var chooseExports = new OpenFileDialog
        {
            Title = "Choose a Postman collection, and any environment exports beside it",
            Filter = ExportFilter,
            Multiselect = true,
            InitialDirectory = _workspace?.Root ?? string.Empty,
        };

        if (chooseExports.ShowDialog(this) != true || chooseExports.FileNames.Length == 0)
        {
            return;
        }

        var chooseFolder = new OpenFolderDialog
        {
            Title = "Choose a folder to import into",
            InitialDirectory = _workspace?.Root ?? string.Empty,
        };

        if (chooseFolder.ShowDialog(this) != true)
        {
            return;
        }

        await ImportAsync(chooseExports.FileNames, chooseFolder.FolderName).ConfigureAwait(true);
    }

    private async Task ImportAsync(IReadOnlyList<string> exports, string destination)
    {
        // The same token the send path uses, so Esc cancels an import and a second one
        // cannot start on top of the first. Reading and writing honour it directly; the
        // conversion between them is bounded rather than cancellable, which is why the
        // limits in PostmanImport and BodyConverter are a correctness property and not
        // tidiness.
        using var cancellation = new CancellationTokenSource();
        _inFlight = cancellation;

        try
        {
            await ImportAsync(exports, destination, cancellation.Token).ConfigureAwait(true);
        }
        finally
        {
            _inFlight = null;
        }
    }

    private async Task ImportAsync(
        IReadOnlyList<string> exports,
        string destination,
        CancellationToken cancellationToken)
    {
        StatusLeft.Text = "Importing …";

        var refusals = new List<string>();
        var sources = await ImportStore.ReadAsync(exports, refusals, cancellationToken)
            .ConfigureAwait(true);

        // Off the dispatcher: converting a large collection is a lot of synchronous work over
        // untrusted input, and doing it on the UI thread is a freeze rather than a wait.
        var result = await Task.Run(() => PostmanImport.Convert(sources), cancellationToken)
            .ConfigureAwait(true);

        if (!result.Recognized)
        {
            ShowMessage(Report("Nothing here was a Postman export.", result.Notes, refusals, written: []));
            StatusLeft.Text = "Nothing imported.";
            return;
        }

        var write = await ImportStore.WriteAsync(destination, result.Files, cancellationToken)
            .ConfigureAwait(true);

        refusals.AddRange(write.Refused);

        var notes = result.Notes.ToList();

        // Said here rather than left to the status bar. SetWorkspace reports the .gitignore
        // entry it added by writing StatusLeft, and the import's own summary overwrites that
        // line a moment later — so the one message about editing somebody's repository was
        // the one message nobody saw.
        if (write.Written.Contains(Workspace.PrivateEnvironmentFileName, StringComparer.Ordinal))
        {
            notes.Add(
                $"{Workspace.PrivateEnvironmentFileName} holds the credentials. Sling adds it to "
                    + ".gitignore if it is not already there.");
        }

        // The workspace opens even when nothing was written, because the folder is still
        // where the user was working and the environment files may already be there. It also
        // runs the .gitignore guard, which is what keeps a freshly imported secrets file out
        // of the next commit (Sling.md §5.1).
        SetWorkspace(Workspace.Open(destination));

        await OpenFirstImportedFileAsync(destination, write.Written).ConfigureAwait(true);

        ShowMessage(Report(
            $"Imported {Count(write.Written.Count, "file")} into {destination}.",
            notes,
            refusals,
            write.Written));

        StatusLeft.Text = refusals.Count == 0
            ? $"Imported {Count(write.Written.Count, "file")}. The details are in the response pane."
            : $"Imported {Count(write.Written.Count, "file")}, and "
                + $"{Count(refusals.Count, "file")} could not be written — see the response pane.";
    }

    /// <summary>
    /// Opens the first imported document, so the import ends on something to look at.
    /// </summary>
    /// <remarks>
    /// The first <c>.http</c> file rather than the first file written, because the
    /// environment files are also written and opening one of those would put a wall of JSON
    /// in the request pane — including, on the private one, the credentials the import just
    /// took care to move out of the documents.
    /// </remarks>
    private async Task OpenFirstImportedFileAsync(string destination, IReadOnlyList<string> written)
    {
        var first = written.FirstOrDefault(f => f.EndsWith(".http", StringComparison.OrdinalIgnoreCase));

        if (first is not null)
        {
            await LoadDocumentAsync(Path.Combine(destination, first)).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The whole account of an import, for the response buffer.
    /// </summary>
    /// <remarks>
    /// It goes in the buffer rather than a dialog because it is a list, and a list in a
    /// message box is a list you scroll with the keyboard and cannot copy. The buffer
    /// already searches, folds and copies — the same argument that put history and the
    /// cookie jar there.
    /// </remarks>
    private static string Report(
        string headline,
        IReadOnlyList<string> notes,
        IReadOnlyList<string> refusals,
        IReadOnlyList<string> written)
    {
        var text = new StringBuilder();

        text.Append(headline).Append("\n\n");

        Section(text, "Written", written);
        Section(text, "Not written", refusals);
        Section(text, "Worth knowing", notes);

        if (written.Count > 0)
        {
            text.Append(
                "Every request the importer could not convert exactly is marked with a comment "
                    + "in the file it belongs to. Nothing was dropped silently.\n");
        }

        return text.ToString();
    }

    private static void Section(StringBuilder text, string heading, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        text.Append(heading).Append('\n');

        foreach (var line in lines)
        {
            text.Append("  ").Append(line).Append('\n');
        }

        text.Append('\n');
    }

    private static string Count(int n, string noun) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? string.Empty : "s");
}
