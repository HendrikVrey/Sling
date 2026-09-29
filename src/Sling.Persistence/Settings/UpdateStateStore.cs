using System.Globalization;
using System.Text.Json;

namespace Sling.Persistence.Settings;

/// <summary>
/// What the update check remembers between runs: when GitHub was last asked, and which
/// version the user chose to skip.
/// </summary>
/// <param name="LastCheckedUtc">When GitHub was last asked, or null if it never has been.</param>
/// <param name="SkippedVersion">The version the user said to skip, or null.</param>
public sealed record UpdateState(DateTimeOffset? LastCheckedUtc, string? SkippedVersion)
{
    /// <summary>Nothing checked, nothing skipped.</summary>
    public static UpdateState Empty { get; } = new(null, null);
}

/// <summary>
/// Reads and writes <c>%LOCALAPPDATA%\Sling\update.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// A file of its own rather than two more fields in <c>settings.json</c>, because it is
/// written once a day with nobody touching anything, and the settings file is one people
/// edit by hand: rewriting it on a timer would put back every value a hand edit had
/// changed, since the running copy is what gets written.
/// </para>
/// <para>
/// Nothing here throws and nothing is reported, as with <see cref="SessionStore"/>: the worst
/// a lost file costs is one extra check, or a skipped version offered again.
/// </para>
/// </remarks>
public sealed class UpdateStateStore
{
    /// <summary>The file's name inside the folder.</summary>
    public const string FileName = "update.json";

    private const string TemporarySuffix = ".sling-tmp";
    private const long MaxBytes = 16L * 1024;

    /// <summary>
    /// One save at a time: a check's save and a Skip click can land together, and the
    /// window starts each save off the dispatcher without waiting for the last.
    /// </summary>
    private readonly Lock _gate = new();

    /// <param name="folder">Where to keep the file; a test passes a disposable one.</param>
    public UpdateStateStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Folder = folder;
        FilePath = Path.Combine(folder, FileName);
    }

    /// <summary>The folder Sling keeps its own state in.</summary>
    public string Folder { get; }

    /// <summary>The file's full path, whether or not it exists.</summary>
    public string FilePath { get; }

    /// <summary>Loads the state, falling back to the empty one.</summary>
    public UpdateState Load()
    {
        try
        {
            var info = new FileInfo(FilePath);

            if (!info.Exists || info.Length == 0 || info.Length > MaxBytes)
            {
                return UpdateState.Empty;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return UpdateState.Empty;
            }

            DateTimeOffset? lastChecked = root.TryGetProperty("lastCheckedUtc", out var checkedElement)
                && checkedElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    checkedElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsed)
                    ? parsed
                    : null;

            var skipped = root.TryGetProperty("skippedVersion", out var skippedElement)
                && skippedElement.ValueKind == JsonValueKind.String
                    ? skippedElement.GetString()
                    : null;

            return new UpdateState(lastChecked, skipped is { Length: > 0 and <= 64 } ? skipped : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return UpdateState.Empty;
        }
    }

    /// <summary>Writes the state, atomically.</summary>
    /// <returns>True when it was written.</returns>
    public bool Save(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Named per save, so a second Sling saving at the same moment cannot delete or
        // replace this one's file before it is moved into place.
        var temporary = $"{FilePath}.{Guid.NewGuid():N}{TemporarySuffix}";

        lock (_gate)
        {
            return Write(state, temporary);
        }
    }

    private bool Write(UpdateState state, string temporary)
    {
        try
        {
            Directory.CreateDirectory(Folder);

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();

                if (state.LastCheckedUtc is { } lastChecked)
                {
                    writer.WriteString("lastCheckedUtc", lastChecked.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                }

                if (state.SkippedVersion is { Length: > 0 and <= 64 } skipped)
                {
                    writer.WriteString("skippedVersion", skipped);
                }

                writer.WriteEndObject();
            }

            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception sweep) when (sweep is IOException or UnauthorizedAccessException)
            {
                // The failure being reported is the write, not the sweep.
            }

            return false;
        }
    }
}
