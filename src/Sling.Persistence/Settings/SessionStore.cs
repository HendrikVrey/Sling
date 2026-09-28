using System.Text.Json;

namespace Sling.Persistence.Settings;

/// <summary>
/// Reads and writes <c>%LOCALAPPDATA%\Sling\session.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="SettingsStore"/> - walked with <see cref="JsonDocument"/>,
/// written with <see cref="Utf8JsonWriter"/>, replaced through a temporary file - and for the
/// same reasons: the file is small, it is hand-editable, and doing it this way keeps the
/// project clear of source generation.
/// </para>
/// <para>
/// <b>Nothing here throws, and nothing here is reported.</b> Settings that will not parse are
/// worth a sentence in the status bar, because somebody chose those values and would want to
/// know they are not in force. A session is remembered rather than chosen: a machine that
/// cannot read one should open with an empty window and say nothing, exactly as it did before
/// the file existed.
/// </para>
/// </remarks>
public sealed class SessionStore
{
    /// <summary>The file's name inside the folder.</summary>
    public const string FileName = "session.json";

    private const string TemporarySuffix = ".sling-tmp";

    /// <summary>
    /// A ceiling on the session file.
    /// </summary>
    /// <remarks>
    /// It holds five values and up to eight paths. Anything at this size is not a session
    /// file, and reading it whole on the way to the first frame would be a freeze.
    /// </remarks>
    private const long MaxBytes = 64L * 1024;

    /// <param name="folder">
    /// Where to keep the file. Taken rather than read from the environment so a test can
    /// point it at a disposable directory instead of at the profile of whoever runs it.
    /// </param>
    public SessionStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Folder = folder;
        FilePath = Path.Combine(folder, FileName);
    }

    /// <summary>The folder Sling keeps its own state in.</summary>
    public string Folder { get; }

    /// <summary>The session file's full path, whether or not it exists.</summary>
    public string FilePath { get; }

    /// <summary>Loads the session, falling back to an empty one.</summary>
    public SlingSession Load()
    {
        if (!File.Exists(FilePath))
        {
            return SlingSession.None;
        }

        string text;

        try
        {
            if (new FileInfo(FilePath).Length > MaxBytes)
            {
                return SlingSession.None;
            }

            text = File.ReadAllText(FilePath);
        }
        catch (IOException)
        {
            return SlingSession.None;
        }
        catch (UnauthorizedAccessException)
        {
            return SlingSession.None;
        }

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return SlingSession.None;
            }

            return new SlingSession
            {
                WorkspaceRoot = ReadString(root, "workspaceRoot"),
                DocumentPath = ReadString(root, "documentPath"),
                CaretLine = ReadInt(root, "caretLine"),
                CaretColumn = ReadInt(root, "caretColumn"),
                Split = ReadDouble(root, "split", SlingSession.None.Split),
                RecentFolders = ReadStrings(root, "recentFolders"),
            }.Clamped();
        }
        catch (JsonException)
        {
            return SlingSession.None;
        }
        catch (ArgumentException)
        {
            // Parse transcodes to UTF-8 first, so a lone surrogate arrives here.
            return SlingSession.None;
        }
    }

    /// <summary>
    /// Writes the session, atomically.
    /// </summary>
    /// <returns>True when it was written.</returns>
    /// <remarks>
    /// The result is answered rather than a reason, because the only caller is a window on
    /// its way out and there is nowhere left to put a sentence. Atomic all the same: a
    /// session file half-written by a crash would be read as a corrupt one on the next start
    /// and silently discard the folder somebody had open for a month.
    /// </remarks>
    public bool Save(SlingSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var clamped = session.Clamped();
        var temporary = FilePath + TemporarySuffix;

        try
        {
            Directory.CreateDirectory(Folder);

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();

                WriteString(writer, "workspaceRoot", clamped.WorkspaceRoot);
                WriteString(writer, "documentPath", clamped.DocumentPath);

                writer.WriteNumber("caretLine", clamped.CaretLine);
                writer.WriteNumber("caretColumn", clamped.CaretColumn);
                writer.WriteNumber("split", clamped.Split);

                writer.WriteStartArray("recentFolders");

                foreach (var folder in clamped.RecentFolders)
                {
                    writer.WriteStringValue(folder);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            Sweep(temporary);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Sweep(temporary);
            return false;
        }
    }

    private static void Sweep(string temporary)
    {
        // This method's own litter, whatever went wrong. Swallowed on purpose: the failure
        // being reported is the write, not the sweep.
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
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is { Length: > 0 })
        {
            writer.WriteString(name, value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value)
                ? value
                : 0;

    private static double ReadDouble(JsonElement root, string name, double fallback) =>
        root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out var value)
                ? value
                : fallback;

    /// <summary>
    /// A string array, dropping anything in it that is not a string.
    /// </summary>
    /// <remarks>
    /// Dropping rather than refusing the whole file: one bad entry in a recent list is not a
    /// reason to forget the other seven, and this is a file people are free to edit.
    /// </remarks>
    private static List<string> ReadStrings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
            {
                values.Add(value);
            }
        }

        return values;
    }
}
