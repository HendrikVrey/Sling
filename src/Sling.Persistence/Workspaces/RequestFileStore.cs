using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sling.Persistence.Workspaces;

/// <summary>
/// What a file looks like from the outside, without reading it.
/// </summary>
/// <param name="Length">Its size in bytes.</param>
/// <param name="LastWriteUtc">When it was last written.</param>
/// <remarks>
/// The cheap half of "has this changed underneath us". Two facts a stat answers, taken
/// together because either alone is easy to leave unmoved: a same-length edit keeps the
/// length, and a write within a file system's timestamp resolution keeps the time. When
/// they agree with what Sling last saw, nothing is read; when they do not, the content is
/// the only thing that can settle it, because a checkout touches the write time of every
/// file it visits whether or not it changed one.
/// </remarks>
public readonly record struct DocumentPeek(long Length, DateTime LastWriteUtc);

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

    /// <summary>
    /// What the file at <paramref name="path"/> looks like from the outside, or null when it
    /// is not there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Synchronous, and meant to be called off the dispatcher. A stat is fast on a local disk
    /// and can take the whole of a network timeout on a share that has gone away, and this
    /// runs whenever the window comes forward.
    /// </para>
    /// <para>
    /// Anything that stops the facts being read is reported as "not there", deliberately.
    /// The caller's question is "may the buffer still be trusted as a copy of this file", and
    /// a file that cannot be stat'd is not an answer of yes.
    /// </para>
    /// </remarks>
    public static DocumentPeek? Peek(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var info = new FileInfo(path);

            return info.Exists ? new DocumentPeek(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A fingerprint of a document's text, for telling a real change from a touched
    /// timestamp.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Over the decoded text rather than the file's bytes, which is the comparison actually
    /// wanted: two files holding the same document differ in their bytes if one carries a
    /// byte order mark, and that is not a change anybody made to a request.
    /// </para>
    /// <para>
    /// A hash rather than a kept copy of the text. The window has to remember what it
    /// believes is on disk in order to answer this at all, and remembering thirty-two bytes
    /// instead of up to sixteen megabytes is the difference between a check that is free to
    /// run on every activation and one that is not.
    /// </para>
    /// </remarks>
    public static string HashOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Convert.ToHexStringLower(SHA256.HashData(FileEncoding.GetBytes(text)));
    }

    /// <summary>
    /// The fingerprint of the file at <paramref name="path"/>, or the empty string when it
    /// cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The empty string cannot equal any real fingerprint, so a caller adopting this as a
    /// baseline fails towards asking again rather than towards a dismissal that would hide
    /// the next change.
    /// </para>
    /// <para>
    /// <b>Held to the same ceiling as <see cref="ReadAsync"/>, and it has to be.</b> Without
    /// it, this is a whole-file read with no bound at all - pointed at a database dump beside
    /// the request files it would allocate the file twice over and, past about two gigabytes
    /// of text, throw <see cref="OutOfMemoryException"/> out of a method whose whole contract
    /// is that it answers rather than throws.
    /// </para>
    /// </remarks>
    public static string HashOnDisk(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (new FileInfo(path).Length > MaxDocumentBytes)
            {
                return string.Empty;
            }

            using var reader = new StreamReader(path, FileEncoding, detectEncodingFromByteOrderMarks: true);

            return HashOf(reader.ReadToEnd());
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
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
