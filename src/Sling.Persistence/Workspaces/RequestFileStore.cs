using System.Globalization;
using System.Text;

namespace Sling.Persistence.Workspaces;

/// <summary>
/// Reads and writes the <c>.http</c> documents themselves.
/// </summary>
/// <remarks>
/// <para>
/// Saving is explicit - <c>Ctrl+S</c>, with a dirty marker - and that is a deliberate
/// split from Etch, which auto-saves continuously and has no save command at all. The
/// difference is what the file is: an Etch buffer is scratch that belongs to Etch, while
/// a <c>.http</c> file is a git artifact that belongs to a repository. Rewriting one
/// continuously would move the working tree under a reviewer mid-diff.
/// </para>
/// <para>
/// A write goes to a temporary file beside the target and is then moved over it, so a
/// crash or a full disk leaves the previous version intact rather than a half-written
/// one. The temporary file is a sibling on purpose: a move across volumes is a copy, and
/// a copy is not atomic.
/// </para>
/// </remarks>
public static class RequestFileStore
{
    private const string TemporarySuffix = ".sling-tmp";

    /// <summary>
    /// The largest document Sling will open.
    /// </summary>
    /// <remarks>
    /// The open dialog offers "All files", so what this guards against is somebody pointing
    /// it at a database dump - otherwise read whole, on the dispatcher, into an editor
    /// buffer. A request file is text a person wrote; a generous ceiling still excludes
    /// everything that is not one.
    /// </remarks>
    public const long MaxDocumentBytes = 16L * 1024 * 1024;

    /// <summary>
    /// The encoding a document is written in. Constructed rather than
    /// <see cref="Encoding.UTF8"/>, whose singleton emits a byte order mark - and a
    /// <c>.http</c> file that starts with one is a file whose first request line does not
    /// parse in half the tools that read the format.
    /// </summary>
    private static readonly UTF8Encoding FileEncoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Reads a document, honouring a byte order mark if the file carries one.</summary>
    /// <exception cref="IOException">
    /// The file is larger than <see cref="MaxDocumentBytes"/>. An <see cref="IOException"/>
    /// because callers already handle one, and because this genuinely is "the file cannot
    /// be read" rather than a programming error.
    /// </exception>
    public static async Task<string> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        if (length > MaxDocumentBytes)
        {
            throw new IOException(
                $"it is {(length / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB, and "
                    + $"Sling opens request files up to "
                    + $"{(MaxDocumentBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB.");
        }

        // The BOM check StreamReader performs comes from the encoding instance it is
        // given, not from detectEncodingFromByteOrderMarks alone - and passing the
        // Encoding.UTF8 singleton makes "had a BOM" indistinguishable from "did not".
        using var reader = new StreamReader(path, FileEncoding, detectEncodingFromByteOrderMarks: true);

        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a document, replacing whatever was there, atomically.</summary>
    public static async Task SaveAsync(string path, string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = full + TemporarySuffix;

        try
        {
            await File.WriteAllTextAsync(temporary, text, FileEncoding, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, full, overwrite: true);
        }
        catch
        {
            // The temporary file is this method's litter whatever went wrong, and leaving
            // one behind means the next save finds a stale file where it wants to write.
            // Swallowed on purpose: the failure being reported is the write, not the sweep.
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }
}
