using System.Text;
using Sling.Import.Postman;

namespace Sling.Persistence.Workspaces;

/// <summary>What writing an import actually did.</summary>
/// <param name="Written">Paths written, relative to the destination, in the order they were written.</param>
/// <param name="Refused">
/// Paths that were not written, each with the reason. Never empty for a silent reason: a
/// file that does not arrive has to be accounted for, or the import looks complete and is
/// not.
/// </param>
public sealed record ImportWriteResult(IReadOnlyList<string> Written, IReadOnlyList<string> Refused);

/// <summary>
/// Writes the files an import produced into a folder the user chose.
/// </summary>
/// <remarks>
/// <para>
/// <b>Containment is checked here as well as in the importer, and that is deliberate rather
/// than redundant.</b> Every path comes from folder and request names inside somebody else's
/// JSON file. <c>FileNames</c> makes a traversal impossible by construction — the slug
/// whitelist has no <c>.</c>, <c>/</c> or <c>\</c> in it — and this checks the finished path
/// against the destination anyway, because the two would have to be wrong in the same way on
/// the same day for a file to escape. It is the same shape as the curl importer stripping
/// control characters where a note is built <em>and</em> commenting every line where one is
/// written.
/// </para>
/// <para>
/// <b>Nothing is ever overwritten.</b> An import lands in a folder the user picked from a
/// dialog, and picking the wrong one is a single mis-click; replacing a request file
/// somebody wrote by hand — or an <c>http-client.private.env.json</c> holding their real
/// tokens — is not recoverable from inside Sling. A refusal is reported and the rest of the
/// import still lands.
/// </para>
/// </remarks>
public static class ImportStore
{
    /// <summary>
    /// The encoding the files are written in.
    /// </summary>
    /// <remarks>
    /// Constructed rather than <see cref="Encoding.UTF8"/>, whose singleton emits a byte
    /// order mark — and a <c>.http</c> file that starts with one is a file whose first
    /// request line does not parse in half the tools that read the format. The same reason
    /// <see cref="RequestFileStore"/> does it.
    /// </remarks>
    private static readonly UTF8Encoding FileEncoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The largest export that will be read.
    /// </summary>
    /// <remarks>
    /// A Postman collection is JSON a person accumulated request by request, and a large one
    /// is a few megabytes. This admits every real export and still refuses a file selected by
    /// mistake, which would otherwise be read whole into memory on the way to the parser.
    /// </remarks>
    public const long MaxExportBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Reads the exports the user selected.
    /// </summary>
    /// <param name="refusals">
    /// One sentence per file that could not be read. Reported rather than skipped: a file
    /// somebody deliberately selected has to be accounted for, or an import silently comes
    /// out missing an environment.
    /// </param>
    /// <remarks>
    /// A file's name is carried alongside its text because the importer quotes it in
    /// diagnostics and falls back to it for an environment whose export forgot its own name.
    /// It is never used to build an output path — those come from inside the collection, and
    /// only through <c>FileNames</c>.
    /// </remarks>
    public static async Task<IReadOnlyList<PostmanSource>> ReadAsync(
        IReadOnlyList<string> paths,
        List<string> refusals,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(refusals);

        var sources = new List<PostmanSource>();

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(path);

            try
            {
                var length = new FileInfo(path).Length;

                if (length > MaxExportBytes)
                {
                    refusals.Add(
                        $"'{name}' is larger than {MaxExportBytes / (1024 * 1024)} MB, which is far "
                            + "past any real export.");

                    continue;
                }

                sources.Add(new PostmanSource(
                    name,
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)));
            }
            catch (IOException ex)
            {
                refusals.Add($"'{name}' could not be read: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                refusals.Add($"'{name}' could not be read: {ex.Message}");
            }
        }

        return sources;
    }

    /// <summary>
    /// Writes <paramref name="files"/> under <paramref name="destination"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The destination is empty.</exception>
    /// <exception cref="IOException">The destination could not be created.</exception>
    public static async Task<ImportWriteResult> WriteAsync(
        string destination,
        IReadOnlyList<ImportedFile> files,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(files);

        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);

        var written = new List<string>();
        var refused = new List<string>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Resolve(root, file.RelativePath) is not { } full)
            {
                refused.Add($"{file.RelativePath} — that path does not stay inside the folder.");
                continue;
            }

            if (File.Exists(full) || Directory.Exists(full))
            {
                refused.Add($"{file.RelativePath} — something is already there, and it was left alone.");
                continue;
            }

            try
            {
                var directory = Path.GetDirectoryName(full);

                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // No temporary-file dance here, unlike a save: there is nothing to lose. The
                // target is known not to exist, so a failed write leaves a partial file that
                // is reported rather than a good file replaced by a bad one.
                await File.WriteAllTextAsync(full, file.Text, FileEncoding, cancellationToken)
                    .ConfigureAwait(false);

                written.Add(file.RelativePath);
            }
            catch (IOException ex)
            {
                refused.Add($"{file.RelativePath} — {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                refused.Add($"{file.RelativePath} — {ex.Message}");
            }
        }

        return new ImportWriteResult(written, refused);
    }

    /// <summary>
    /// Turns a relative path into a full one, or refuses it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compared through <see cref="Path.GetRelativePath"/> rather than with a string prefix,
    /// for the reason <c>WorkspaceFileSource</c> records: a prefix test says
    /// <c>C:\work\api-secrets</c> is inside <c>C:\work\api</c>.
    /// </para>
    /// <para>
    /// Links are not walked here, and that is a real difference from the body-import guard
    /// rather than an oversight. This writes into a folder the user chose in a dialog moments
    /// ago, creating files that do not exist yet — there is no existing entry to follow, and
    /// a directory the user themselves linked into that folder is theirs. The property that
    /// matters is the one checked: a name out of the collection cannot decide where the file
    /// lands.
    /// </para>
    /// </remarks>
    internal static string? Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        string full;

        try
        {
            full = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }

        var back = Path.GetRelativePath(root, full);

        if (Path.IsPathRooted(back)
            || back == ".."
            || back.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        return full;
    }
}
