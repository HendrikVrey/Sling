using Sling.Persistence.Settings;

namespace Sling.Persistence.Tests;

/// <summary>
/// <see cref="UpdateStateStore"/>, and the settings switch that goes with it.
/// </summary>
public sealed class UpdateStateStoreTests
{
    [Fact]
    public void What_is_saved_is_what_is_loaded()
    {
        using var folder = new TemporaryFolder();
        var store = new UpdateStateStore(folder.Path);
        var state = new UpdateState(new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero), "1.1.2");

        Assert.True(store.Save(state));
        Assert.Equal(state, store.Load());
    }

    [Fact]
    public void A_missing_file_is_the_empty_state()
    {
        using var folder = new TemporaryFolder();

        Assert.Equal(UpdateState.Empty, new UpdateStateStore(folder.Path).Load());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"lastCheckedUtc\": 12}")]
    public void A_broken_file_is_the_empty_state_and_never_throws(string contents)
    {
        using var folder = new TemporaryFolder();
        File.WriteAllText(Path.Combine(folder.Path, UpdateStateStore.FileName), contents);

        Assert.Equal(UpdateState.Empty, new UpdateStateStore(folder.Path).Load());
    }

    [Fact]
    public void An_implausible_skipped_version_is_dropped()
    {
        using var folder = new TemporaryFolder();
        File.WriteAllText(
            Path.Combine(folder.Path, UpdateStateStore.FileName),
            "{\"skippedVersion\": \"" + new string('9', 500) + "\"}");

        Assert.Null(new UpdateStateStore(folder.Path).Load().SkippedVersion);
    }

    [Fact]
    public void A_settings_file_from_before_the_question_existed_has_not_answered_it()
    {
        // Opt-in: a file with no answer must read as "not asked", never as yes.
        using var folder = new TemporaryFolder();
        File.WriteAllText(
            Path.Combine(folder.Path, SettingsStore.FileName),
            "{ \"timeoutSeconds\": 45, \"cookiesEnabled\": false }");

        var settings = new SettingsStore(folder.Path).Load(out var problem);

        Assert.Null(problem);
        Assert.Equal(45, settings.TimeoutSeconds);
        Assert.Null(settings.CheckForUpdates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void The_answer_survives_a_save(bool? allowed)
    {
        using var folder = new TemporaryFolder();
        var store = new SettingsStore(folder.Path);

        Assert.Null(store.Save(SlingSettings.Default with { CheckForUpdates = allowed }));

        Assert.Equal(allowed, store.Load(out _).CheckForUpdates);
    }

    [Fact]
    public void Clamping_keeps_the_answer() =>
        Assert.True((SlingSettings.Default with { CheckForUpdates = true }).Clamped().CheckForUpdates);
}
