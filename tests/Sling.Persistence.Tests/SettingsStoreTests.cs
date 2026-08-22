using Sling.Persistence.Settings;

namespace Sling.Persistence.Tests;

/// <summary>
/// Settings survive a round trip, a hand-edited file cannot put an unusable value in
/// force, and a broken file is never a reason not to start.
/// </summary>
public sealed class SettingsStoreTests
{
    [Fact]
    public void With_no_file_the_defaults_are_in_force()
    {
        using var folder = new TemporaryFolder();

        var settings = new SettingsStore(folder.Path).Load(out var problem);

        Assert.Null(problem);
        Assert.Equal(SlingSettings.Default, settings);
    }

    [Fact]
    public void Settings_survive_a_round_trip()
    {
        using var folder = new TemporaryFolder();
        var store = new SettingsStore(folder.Path);

        var written = new SlingSettings
        {
            TimeoutSeconds = 45,
            MaxResponseBodyMegabytes = 32,
            MaxRedirects = 3,
            CookiesEnabled = false,
            HistoryEnabled = false,
            HistoryMaxEntries = 25,
        };

        Assert.Null(store.Save(written));
        Assert.Equal(written, store.Load(out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void A_value_out_of_range_is_clamped_rather_than_refused()
    {
        using var folder = new TemporaryFolder();
        folder.Write(SettingsStore.FileName, """{"timeoutSeconds": 999999, "maxRedirects": -4}""");

        var settings = new SettingsStore(folder.Path).Load(out var problem);

        // A hand-edited file with an absurd number is not a reason to refuse to start.
        Assert.Null(problem);
        Assert.Equal(3600, settings.TimeoutSeconds);
        Assert.Equal(0, settings.MaxRedirects);
    }

    [Fact]
    public void A_missing_key_keeps_its_default()
    {
        using var folder = new TemporaryFolder();
        folder.Write(SettingsStore.FileName, """{"timeoutSeconds": 30}""");

        var settings = new SettingsStore(folder.Path).Load(out _);

        Assert.Equal(30, settings.TimeoutSeconds);
        Assert.Equal(SlingSettings.Default.MaxRedirects, settings.MaxRedirects);
    }

    [Fact]
    public void A_key_of_the_wrong_type_keeps_its_default_rather_than_throwing()
    {
        // JsonElement.TryGetInt32 throws when the element is not a Number — the Try only
        // suppresses a malformed number — so the kind has to be checked first.
        using var folder = new TemporaryFolder();
        folder.Write(SettingsStore.FileName, """{"timeoutSeconds": "thirty", "cookiesEnabled": 1}""");

        var settings = new SettingsStore(folder.Path).Load(out _);

        Assert.Equal(SlingSettings.Default.TimeoutSeconds, settings.TimeoutSeconds);
        Assert.True(settings.CookiesEnabled);
    }

    [Fact]
    public void Comments_and_a_trailing_comma_are_tolerated()
    {
        // The file is one people are invited to edit. Refusing to read one over a trailing
        // comma is the sort of strictness that gets an application uninstalled.
        using var folder = new TemporaryFolder();
        folder.Write(SettingsStore.FileName, """
            {
              // how long a request may take
              "timeoutSeconds": 20,
            }
            """);

        Assert.Equal(20, new SettingsStore(folder.Path).Load(out var problem).TimeoutSeconds);
        Assert.Null(problem);
    }

    [Fact]
    public void A_malformed_file_says_so_and_falls_back()
    {
        using var folder = new TemporaryFolder();
        folder.Write(SettingsStore.FileName, "{ this is not json");

        var settings = new SettingsStore(folder.Path).Load(out var problem);

        // Surfaced rather than swallowed: settings that silently reverted look exactly like
        // settings that were never saved.
        Assert.NotNull(problem);
        Assert.Equal(SlingSettings.Default, settings);
    }

    [Fact]
    public void A_json_document_that_is_not_an_object_falls_back()
    {
        using var folder = new TemporaryFolder();
        folder.Write(SettingsStore.FileName, "[1, 2, 3]");

        Assert.Equal(SlingSettings.Default, new SettingsStore(folder.Path).Load(out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_behind()
    {
        using var folder = new TemporaryFolder();
        var store = new SettingsStore(folder.Path);

        store.Save(SlingSettings.Default);

        // A stale temporary is where the next save wants to write.
        Assert.Equal([SettingsStore.FileName], Directory.GetFiles(folder.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Saving_clamps_before_writing()
    {
        // So the file on disk cannot disagree with the running application.
        using var folder = new TemporaryFolder();
        var store = new SettingsStore(folder.Path);

        store.Save(SlingSettings.Default with { TimeoutSeconds = 0 });

        Assert.Equal(1, store.Load(out _).TimeoutSeconds);
    }
}
