using System.Globalization;
using System.Text;
using System.Text.Json;
using Sling.Core.History;

namespace Sling.Persistence.History;

/// <summary>
/// The local record of what was sent, at
/// <c>%LOCALAPPDATA%\Sling\history.jsonl</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Outside the workspace, and that is a security decision rather than tidiness.</strong>
/// A workspace is a git checkout. Writing a request log into it means the log is one
/// <c>git add -A</c> from being published, and defending that with a <c>.gitignore</c>
/// entry is defending it with a file the user can delete. Keeping it out of the repository
/// entirely means there is nothing to defend.
/// </para>
/// <para>
/// <strong>JSON Lines, one object per line.</strong> Appending is a single write with no
/// read; recovering from a truncated write costs one line rather than the file; and the
/// format is greppable, which matters for something whose purpose is to be looked through.
/// </para>
/// <para>
/// Everything written here has already been through <see cref="Core.Redaction.Redactor"/>
/// - a <see cref="HistoryEntry"/> cannot be built any other way. This class does not
/// redact and must not start to: two redaction points is one too many, and the second one
/// is where the rule quietly diverges.
/// </para>
/// </remarks>
public sealed class HistoryStore
{
    /// <summary>The file's name inside the folder.</summary>
    public const string FileName = "history.jsonl";

    /// <summary>
    /// A conservative lower bound on the bytes one entry occupies, used to skip the read
    /// when the file is too small to hold too many.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Trimming reads the whole file and rewrites it, which is more work than an append
    /// deserves - but the setting is called "history entries kept", so the cap has to be a
    /// count and has to be exact. This makes it both: if the file is smaller than the cap
    /// times this number, it <em>cannot</em> hold more than the cap, because no entry is
    /// this small.
    /// </para>
    /// <para>
    /// The bound holds because the fixed JSON - the eight keys that are always present,
    /// plus a round-tripped timestamp of thirty-three characters - is already past 150
    /// bytes before any URL, status or header appears. A test asserts it rather than
    /// trusting this paragraph, because a field removed from
    /// <see cref="Serialize"/> would otherwise turn an exact cap into an approximate one
    /// without anything noticing.
    /// </para>
    /// </remarks>
    internal const long MinimumBytesPerEntry = 120;

    /// <summary>
    /// A ceiling on one entry's line. An entry is a URL and two header lists; anything past
    /// this is a server sending something absurd, and one line must not be able to make the
    /// file unreadable.
    /// </summary>
    private const int MaxLineBytes = 64 * 1024;

    private static readonly UTF8Encoding FileEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _folder;

    /// <param name="folder">
    /// Where to keep the file. Taken rather than read from the environment so a test can
    /// point it at a disposable directory instead of at the profile of whoever runs it.
    /// </param>
    public HistoryStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        _folder = folder;
        FilePath = Path.Combine(folder, FileName);
    }

    /// <summary>The history file's full path, whether or not it exists.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Appends <paramref name="entries"/> and trims the file if it has grown past its
    /// allowance.
    /// </summary>
    /// <returns>Null when it was written, or a sentence saying why it was not.</returns>
    public async Task<string?> AppendAsync(
        IReadOnlyList<HistoryEntry> entries,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return null;
        }

        // GetByteCount, not Length: the constant is named and reasoned about in bytes, and
        // a UTF-16 length understates a line of non-ASCII header values by up to three
        // times, which would let one through at three times the cap it is meant to hold.
        var all = entries.Select(Serialize).ToList();
        var lines = all.Where(line => FileEncoding.GetByteCount(line) <= MaxLineBytes).ToList();
        var dropped = all.Count - lines.Count;

        if (lines.Count == 0)
        {
            return DroppedNote(dropped);
        }

        try
        {
            Directory.CreateDirectory(_folder);
            await File.AppendAllLinesAsync(FilePath, lines, FileEncoding, cancellationToken).ConfigureAwait(false);

            await TrimIfOversizeAsync(maxEntries, cancellationToken).ConfigureAwait(false);
            return DroppedNote(dropped);
        }
        catch (IOException ex)
        {
            return $"Could not record history: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Could not record history: {ex.Message}";
        }
    }

    /// <summary>
    /// Says so when an entry was too large to record.
    /// </summary>
    /// <remarks>
    /// An entry dropped in silence makes this method report success for a run it did not
    /// record, which is the one thing a log must never do - history that quietly has a
    /// hole in it is worse than history that says it has one.
    /// </remarks>
    private static string? DroppedNote(int dropped) =>
        dropped == 0
            ? null
            : $"{dropped.ToString(CultureInfo.InvariantCulture)} exchange"
                + (dropped == 1 ? " was" : "s were")
                + " too large to record in history.";

    /// <summary>
    /// Reads the history, oldest first.
    /// </summary>
    /// <remarks>
    /// A line that will not parse is skipped rather than fatal. The file is appended to by
    /// a process that can be killed mid-write, so a truncated last line is an expected
    /// state and not a corrupt file.
    /// </remarks>
    public async Task<IReadOnlyList<HistoryEntry>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        string[] lines;

        try
        {
            lines = await File.ReadAllLinesAsync(FilePath, FileEncoding, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        var entries = new List<HistoryEntry>(lines.Length);

        foreach (var line in lines)
        {
            if (TryDeserialize(line, out var entry))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>Deletes the history file.</summary>
    /// <returns>Null when it is gone, or a sentence saying why it is not.</returns>
    public string? Clear()
    {
        try
        {
            File.Delete(FilePath);
            return null;
        }
        catch (IOException ex)
        {
            return $"Could not clear history: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Could not clear history: {ex.Message}";
        }
    }

    /// <summary>
    /// Drops the oldest entries once the file holds more than <paramref name="maxEntries"/>.
    /// </summary>
    /// <remarks>
    /// The size check is only a way to avoid the read. When it does not settle the
    /// question the file is read and the count decided exactly, so "history entries kept"
    /// means what it says rather than approximately what it says.
    /// </remarks>
    private async Task TrimIfOversizeAsync(int maxEntries, CancellationToken cancellationToken)
    {
        var info = new FileInfo(FilePath);

        if (!info.Exists || info.Length <= maxEntries * MinimumBytesPerEntry)
        {
            return;
        }

        var lines = await File.ReadAllLinesAsync(FilePath, FileEncoding, cancellationToken).ConfigureAwait(false);

        if (lines.Length <= maxEntries)
        {
            return;
        }

        // Written through a temporary and moved over, so a crash mid-trim cannot leave the
        // history as a partial file. Losing history is not serious; losing it silently and
        // discovering it later is worse.
        var temporary = FilePath + ".sling-tmp";

        try
        {
            await File.WriteAllLinesAsync(
                temporary,
                lines[^maxEntries..],
                FileEncoding,
                cancellationToken).ConfigureAwait(false);

            File.Move(temporary, FilePath, overwrite: true);
        }
        catch
        {
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

    private static string Serialize(HistoryEntry entry)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sent", entry.SentUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            writer.WriteString("method", entry.Method);
            writer.WriteString("url", entry.Url);
            writer.WriteNumber("status", entry.StatusCode);
            writer.WriteString("reason", entry.ReasonPhrase);
            writer.WriteNumber("elapsedMs", entry.Elapsed.TotalMilliseconds);
            writer.WriteNumber("requestBytes", entry.RequestBodyBytes);
            writer.WriteNumber("responseBytes", entry.ResponseBodyBytes);

            if (entry.EnvironmentName is { } environment)
            {
                writer.WriteString("environment", environment);
            }

            WriteHeaders(writer, "requestHeaders", entry.RequestHeaders);
            WriteHeaders(writer, "responseHeaders", entry.ResponseHeaders);

            writer.WriteEndObject();
        }

        return FileEncoding.GetString(buffer.ToArray());
    }

    private static void WriteHeaders(Utf8JsonWriter writer, string name, IReadOnlyList<HistoryHeader> headers)
    {
        writer.WriteStartArray(name);

        foreach (var header in headers)
        {
            writer.WriteStartObject();
            writer.WriteString("name", header.Name);
            writer.WriteString("value", header.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static bool TryDeserialize(string line, out HistoryEntry entry)
    {
        entry = null!;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            entry = HistoryEntry.FromStorage(
                ReadTimestamp(root),
                ReadString(root, "method"),
                ReadString(root, "url"),
                ReadInt(root, "status"),
                ReadString(root, "reason"),
                TimeSpan.FromMilliseconds(ReadDouble(root, "elapsedMs")),
                ReadLong(root, "requestBytes"),
                ReadLong(root, "responseBytes"),
                root.TryGetProperty("environment", out var environment) && environment.ValueKind == JsonValueKind.String
                    ? environment.GetString()
                    : null,
                ReadHeaders(root, "requestHeaders"),
                ReadHeaders(root, "responseHeaders"));

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static DateTimeOffset ReadTimestamp(JsonElement root) =>
        root.TryGetProperty("sent", out var element)
            && element.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                // A round-tripped "o" string carries its own offset. AssumeUniversal covers
                // the case of a hand-edited line that dropped it - without which the value
                // would be read as machine-local and the entry would move by hours.
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value)
                ? value
                : 0;

    private static long ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out var value)
                ? value
                : 0;

    private static double ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out var value)
                ? value
                : 0;

    private static List<HistoryHeader> ReadHeaders(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var headers = new List<HistoryHeader>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            headers.Add(new HistoryHeader(ReadString(element, "name"), ReadString(element, "value")));
        }

        return headers;
    }
}
