using System.Text.Json;

namespace Sling.Persistence.Settings;

/// <summary>
/// Reads and writes <c>%LOCALAPPDATA%\Sling\settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Outside the workspace, deliberately. A workspace is somebody's repository; Sling's
/// preferences are not a fact about their API and have no business appearing in their
/// diff. It is also the same place Etch keeps its settings, so the two behave alike on the
/// same machine.
/// </para>
/// <para>
/// Walked with <see cref="JsonDocument"/> and written with <see cref="Utf8JsonWriter"/>
/// rather than serialised, matching <c>EnvironmentFile</c>. The file is small, it is
/// hand-editable, and doing it this way keeps the project clear of source generation,
/// which it needs to stay AOT-compatible.
/// </para>
/// <para>
/// Nothing here throws for a bad file. Settings that cannot be read fall back to the
/// defaults with a sentence saying so: a malformed preferences file is never a reason for
/// an application not to start.
/// </para>
/// </remarks>
public sealed class SettingsStore
{
    /// <summary>The file's name inside the folder, for messages that need to name it.</summary>
    public const string FileName = "settings.json";

    private const string TemporarySuffix = ".sling-tmp";

    /// <summary>
    /// A ceiling on the settings file. It holds six values; anything at this size is not a
    /// settings file, and reading it whole on the dispatcher would be a freeze.
    /// </summary>
    private const long MaxBytes = 256L * 1024;

    /// <param name="folder">
    /// Where to keep the file. Taken rather than read from the environment so a test can
    /// point it at a disposable directory instead of at the profile of whoever runs it.
    /// </param>
    public SettingsStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Folder = folder;
        FilePath = Path.Combine(folder, FileName);
    }

    /// <summary>The folder Sling keeps its own state in.</summary>
    public string Folder { get; }

    /// <summary>The settings file's full path, whether or not it exists.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Loads the settings, falling back to the defaults.
    /// </summary>
    /// <param name="problem">
    /// What went wrong, or null. Surfaced rather than swallowed: settings that silently
    /// reverted to their defaults look exactly like settings that were never saved.
    /// </param>
    public SlingSettings Load(out string? problem)
    {
        problem = null;

        if (!File.Exists(FilePath))
        {
            return SlingSettings.Default;
        }

        string text;

        try
        {
            if (new FileInfo(FilePath).Length > MaxBytes)
            {
                problem = $"'{FileName}' is unexpectedly large and was ignored.";
                return SlingSettings.Default;
            }

            text = File.ReadAllText(FilePath);
        }
        catch (IOException ex)
        {
            problem = $"Could not read '{FileName}': {ex.Message}";
            return SlingSettings.Default;
        }
        catch (UnauthorizedAccessException ex)
        {
            problem = $"Could not read '{FileName}': {ex.Message}";
            return SlingSettings.Default;
        }

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                // A settings file people are invited to edit will grow comments and a
                // trailing comma. Refusing to read one over that is the sort of strictness
                // that gets an application uninstalled.
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = $"'{FileName}' is not a JSON object and was ignored.";
                return SlingSettings.Default;
            }

            var defaults = SlingSettings.Default;

            return new SlingSettings
            {
                TimeoutSeconds = ReadInt(root, "timeoutSeconds", defaults.TimeoutSeconds),
                MaxResponseBodyMegabytes = ReadInt(root, "maxResponseBodyMegabytes", defaults.MaxResponseBodyMegabytes),
                MaxRedirects = ReadInt(root, "maxRedirects", defaults.MaxRedirects),
                CookiesEnabled = ReadBool(root, "cookiesEnabled", defaults.CookiesEnabled),
                HistoryEnabled = ReadBool(root, "historyEnabled", defaults.HistoryEnabled),
                HistoryMaxEntries = ReadInt(root, "historyMaxEntries", defaults.HistoryMaxEntries),
            }.Clamped();
        }
        catch (JsonException ex)
        {
            problem = $"'{FileName}' is not valid JSON ({ex.Message}); the defaults are in force.";
            return SlingSettings.Default;
        }
        catch (ArgumentException ex)
        {
            // Parse transcodes to UTF-8 first, so a lone surrogate arrives here.
            problem = $"'{FileName}' is not valid text ({ex.Message}); the defaults are in force.";
            return SlingSettings.Default;
        }
    }

    /// <summary>
    /// Writes the settings, atomically.
    /// </summary>
    /// <remarks>
    /// Temporary file beside the target and then a move over it, the same shape
    /// <c>RequestFileStore</c> uses. A settings file half-written by a crash is a file that
    /// reverts every preference on the next start, which is a bad way to find out the
    /// machine lost power.
    /// </remarks>
    /// <returns>Null when it was written, or a sentence saying why it was not.</returns>
    public string? Save(SlingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var clamped = settings.Clamped();
        var temporary = FilePath + TemporarySuffix;

        try
        {
            Directory.CreateDirectory(Folder);

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("timeoutSeconds", clamped.TimeoutSeconds);
                writer.WriteNumber("maxResponseBodyMegabytes", clamped.MaxResponseBodyMegabytes);
                writer.WriteNumber("maxRedirects", clamped.MaxRedirects);
                writer.WriteBoolean("cookiesEnabled", clamped.CookiesEnabled);
                writer.WriteBoolean("historyEnabled", clamped.HistoryEnabled);
                writer.WriteNumber("historyMaxEntries", clamped.HistoryMaxEntries);
                writer.WriteEndObject();
            }

            File.Move(temporary, FilePath, overwrite: true);
            return null;
        }
        catch (IOException ex)
        {
            Sweep(temporary);
            return $"Could not save settings: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            Sweep(temporary);
            return $"Could not save settings: {ex.Message}";
        }
    }

    private static void Sweep(string temporary)
    {
        // This method's own litter, whatever went wrong. Leaving one behind means the next
        // save finds a stale file where it wants to write. Swallowed on purpose: the
        // failure being reported is the write, not the sweep.
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

    private static int ReadInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value)
                ? value
                : fallback;

    private static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : fallback;
}
