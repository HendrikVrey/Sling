using System.IO;
using System.Windows;
using Sling.App.Updates;
using Sling.Core.Updates;
using Sling.Http;
using Sling.Persistence;
using Sling.Persistence.Settings;

namespace Sling.App;

/// <summary>
/// Telling the user a new version of Sling is out, and installing it when they say so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in, and asked once.</b> The plan promised no update ping (<c>Sling.md</c> §5
/// item 9, amended by Hendrik on 2026-09-28 for exactly this). So until the user answers,
/// nothing is sent: the first launch of a build that has this code shows a strip asking
/// whether Sling may check, and the answer is kept in <c>settings.json</c> and can be
/// changed in the settings panel.
/// </para>
/// <para>
/// <b>A strip, not a card</b>, the same call the changed-on-disk notice made: an update is
/// never urgent enough to hold the window hostage.
/// </para>
/// <para>
/// <b>At most once a day</b>, when Sling starts or is brought to the front, never on a
/// timer. A failed automatic check says nothing; one the user asked for says what went
/// wrong.
/// </para>
/// <para>
/// <b>The installer runs visibly.</b> Once it is downloaded and matches the checksum GitHub
/// published, unsaved work is dealt with through the usual Cancel / Discard / Save card
/// <em>before</em> anything is started, so answering Cancel leaves Sling exactly as it was.
/// Then the installer is started and Sling closes.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>How long after the first frame the first automatic check waits.</summary>
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(5);

    /// <summary>How often a failed automatic check may be retried.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    private static readonly ReleaseVersion RunningVersion = ReleaseVersion.Of(typeof(MainWindow).Assembly);

    private readonly UpdateStateStore _updateStore = new(LocalData.DefaultFolder);
    private UpdateState _updateState = UpdateState.Empty;
    private UpdateBarMode _updateBarMode;
    private LatestRelease? _offeredRelease;
    private CancellationTokenSource? _updateWork;
    private DateTimeOffset? _lastUpdateAttempt;
    private bool _updatesStarted;

    /// <summary>
    /// True from the end of a download until the installer starts or the user backs out:
    /// the unsaved-work card may be up in between, and no other check may start.
    /// </summary>
    private bool _installPending;

    private enum UpdateBarMode
    {
        Hidden,
        Asking,
        Offering,
        Downloading,
        ReadyToInstall,
    }

    private bool IsUpdateWorkRunning => _updateWork is not null || _installPending;

    /// <summary>Wires the update behaviour. Called once, from the constructor.</summary>
    private void InitializeUpdates()
    {
        Loaded += OnLoadedStartUpdates;
        Activated += (_, _) => CheckForUpdatesIfDue();
        Closed += (_, _) => _updateWork?.Cancel();
    }

    private void OnLoadedStartUpdates(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedStartUpdates;
        RunGuarded(StartUpdatesAsync);
    }

    private async Task StartUpdatesAsync()
    {
        _updateState = await Task.Run(_updateStore.Load).ConfigureAwait(true);
        _updatesStarted = true;

        switch (_settings.CheckForUpdates)
        {
            case null:
                ShowUpdateBar(UpdateBarMode.Asking);
                break;

            case true when UpdateSchedule.IsDue(_updateState.LastCheckedUtc, DateTimeOffset.UtcNow):
                await Task.Delay(FirstCheckDelay).ConfigureAwait(true);
                CheckForUpdatesIfDue();
                break;
        }
    }

    /// <summary>Runs an automatic check if one is due and the user has allowed it.</summary>
    private void CheckForUpdatesIfDue()
    {
        var now = DateTimeOffset.UtcNow;

        if (!_updatesStarted
            || _closed
            || _settings.CheckForUpdates != true
            || IsUpdateWorkRunning
            || _updateBarMode != UpdateBarMode.Hidden
            || !UpdateSchedule.IsDue(_updateState.LastCheckedUtc, now)
            || (_lastUpdateAttempt is { } attempt && now - attempt < RetryAfterFailure))
        {
            return;
        }

        RunGuarded(() => CheckForUpdatesAsync(userAsked: false));
    }

    /// <summary>Asks GitHub for the newest release, and offers it when it is newer.</summary>
    private async Task CheckForUpdatesAsync(bool userAsked)
    {
        if (IsUpdateWorkRunning)
        {
            return;
        }

        using var work = new CancellationTokenSource();
        _updateWork = work;
        _lastUpdateAttempt = DateTimeOffset.UtcNow;

        if (userAsked)
        {
            StatusLeft.Text = "Checking GitHub for a new version of Sling…";
        }

        try
        {
            await Task.Run(() => UpdateInstaller.SweepOldDownloads(DateTimeOffset.UtcNow), work.Token).ConfigureAwait(true);

            LatestRelease latest;

            using (var client = new UpdateClient(RunningVersion))
            {
                latest = await client.GetLatestAsync(work.Token).ConfigureAwait(true);
            }

            _updateState = _updateState with { LastCheckedUtc = DateTimeOffset.UtcNow };
            SaveUpdateState();

            if (_closed)
            {
                return;
            }

            if (UpdateSchedule.ShouldOffer(RunningVersion, latest.Version, _updateState.SkippedVersion, userAsked))
            {
                _offeredRelease = latest;
                ShowUpdateBar(UpdateBarMode.Offering);
            }
            else if (userAsked)
            {
                StatusLeft.Text = latest.Version >= RunningVersion
                    ? $"Sling {RunningVersion} is the newest version."
                    : $"Sling {RunningVersion} is newer than the newest release ({latest.Version}).";
            }
        }
        catch (UpdateCheckException ex)
        {
            if (userAsked && !_closed)
            {
                StatusLeft.Text = ex.Message;
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        finally
        {
            _updateWork = null;
        }
    }

    /// <summary>
    /// Downloads the offered installer, settles unsaved work, starts the installer and
    /// closes Sling.
    /// </summary>
    private async Task DownloadAndInstallAsync(LatestRelease release)
    {
        if (IsUpdateWorkRunning)
        {
            return;
        }

        if (!OnlyCopyOfSlingRunning())
        {
            return;
        }

        // Before the work is marked as running: if the temp folder cannot be made, the
        // failure is reported by RunGuarded and nothing is left believing a download is on.
        var directory = UpdateInstaller.CreateDownloadDirectory();
        string installer;

        using (var work = new CancellationTokenSource())
        {
            _updateWork = work;

            ShowUpdateBar(UpdateBarMode.Downloading);
            SetUpdateNoticeText($"Downloading Sling {release.Version} ({release.DescribeSize()})…");

            var progress = new Progress<double>(fraction =>
            {
                if (_updateBarMode == UpdateBarMode.Downloading)
                {
                    SetUpdateNoticeText($"Downloading Sling {release.Version}… {fraction:P0}");
                }
            });

            try
            {
                using var client = new UpdateClient(RunningVersion);
                installer = await client.DownloadAsync(release, directory, progress, work.Token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is UpdateCheckException or OperationCanceledException or IOException or UnauthorizedAccessException)
            {
                UpdateInstaller.Discard(directory);

                if (!_closed)
                {
                    ShowUpdateBar(UpdateBarMode.Offering);

                    if (ex is not OperationCanceledException)
                    {
                        StatusLeft.Text = ex.Message;
                    }
                }

                return;
            }
            finally
            {
                _updateWork = null;
            }
        }

        _installPending = true;
        ShowUpdateBar(UpdateBarMode.ReadyToInstall);

        try
        {
            // Before anything is started: answering Cancel here must leave Sling as it was,
            // not with an installer open behind a window that refused to close.
            if (_dirty)
            {
                var answer = await AskAboutUnsavedAsync("before updating").ConfigureAwait(true);

                if (answer == UnsavedChoice.Cancel
                    || (answer == UnsavedChoice.Save && !await SaveAsync(adoptWorkspace: false).ConfigureAwait(true)))
                {
                    UpdateInstaller.Discard(directory);
                    ShowUpdateBar(UpdateBarMode.Offering);
                    StatusLeft.Text = "Update postponed. Nothing was changed.";
                    return;
                }
            }

            // Again, because a window opened while this one downloaded counts as much as one
            // that was open before.
            if (_closed || !OnlyCopyOfSlingRunning())
            {
                UpdateInstaller.Discard(directory);
                ShowUpdateBar(UpdateBarMode.Offering);
                return;
            }

            UpdateInstaller.Launch(installer, release.Sha256);
        }
        catch (UpdateCheckException ex)
        {
            UpdateInstaller.Discard(directory);
            ShowUpdateBar(UpdateBarMode.Offering);
            StatusLeft.Text = ex.Message;
            return;
        }
        finally
        {
            _installPending = false;
        }

        HideUpdateBar();
        StatusLeft.Text = $"The Sling {release.Version} installer is open. Sling is closing so it can be updated…";

        // The unsaved work was settled above, so the close must not ask again.
        _closeConfirmed = true;
        Close();
    }

    /// <summary>Records the answer to "may Sling check?", from the strip or from the settings panel.</summary>
    private void SetUpdateChecks(bool allowed)
    {
        _settings = _settings with { CheckForUpdates = allowed };

        if (_settingsStore.Save(_settings) is { } problem)
        {
            StatusLeft.Text = problem;
        }

        if (_updateBarMode == UpdateBarMode.Asking)
        {
            HideUpdateBar();
        }

        // Switching off stops a check in flight and takes down an offer already showing. A
        // download is left alone: somebody pressed Update for that one, and it has its own
        // Cancel.
        if (!allowed)
        {
            if (_updateBarMode == UpdateBarMode.Offering)
            {
                HideUpdateBar();
            }

            if (_updateBarMode != UpdateBarMode.Downloading)
            {
                _updateWork?.Cancel();
            }
        }

        if (allowed)
        {
            CheckForUpdatesIfDue();
        }
    }

    /// <summary>
    /// True when no other copy of Sling is running; otherwise says so and returns false.
    /// </summary>
    /// <remarks>
    /// The installer would close the others itself, and a document unsaved in one of them
    /// would be lost with it. See <see cref="UpdateInstaller.CountOtherInstances"/>.
    /// </remarks>
    private bool OnlyCopyOfSlingRunning()
    {
        var others = UpdateInstaller.CountOtherInstances();

        if (others == 0)
        {
            return true;
        }

        StatusLeft.Text = others == 1
            ? "Another Sling window is open. Close it (saving anything you need), then press Update again."
            : $"{others} other Sling windows are open. Close them (saving anything you need), then press Update again.";
        return false;
    }

    private void SetUpdateNoticeText(string text)
    {
        UpdateNoticeText.Text = text;
        UpdateNotice.ToolTip = text;
    }

    private void SaveUpdateState()
    {
        var state = _updateState;

        // Off the dispatcher; a lost write costs one extra check tomorrow and is not reported.
        _ = Task.Run(() => _updateStore.Save(state));
    }

    /// <summary>Puts the strip into <paramref name="mode"/> and fills in its words and buttons.</summary>
    private void ShowUpdateBar(UpdateBarMode mode)
    {
        if (_closed)
        {
            return;
        }

        _updateBarMode = mode;

        switch (mode)
        {
            case UpdateBarMode.Asking:
                SetUpdateNoticeText(
                    "Can Sling check GitHub once a day for a new version? Only the version number is asked for: nothing about your requests is sent.");
                SetUpdateButtons("Yes, check for updates", "No", null);
                break;

            case UpdateBarMode.Offering when _offeredRelease is { } release:
                SetUpdateNoticeText($"Sling {release.Version} is available. You have {RunningVersion}.");
                SetUpdateButtons("Update", "Later", "Skip this version");
                break;

            case UpdateBarMode.Downloading:
                SetUpdateButtons(null, "Cancel", null);
                break;

            case UpdateBarMode.ReadyToInstall when _offeredRelease is { } release:
                SetUpdateNoticeText($"Sling {release.Version} is downloaded and checked, and will install once Sling closes.");
                SetUpdateButtons(null, null, null);
                break;

            default:
                HideUpdateBar();
                return;
        }

        UpdateNotice.Visibility = Visibility.Visible;
    }

    private void HideUpdateBar()
    {
        _updateBarMode = UpdateBarMode.Hidden;
        UpdateNotice.Visibility = Visibility.Collapsed;
    }

    private void SetUpdateButtons(string? primary, string? secondary, string? tertiary)
    {
        Configure(UpdatePrimaryButton, primary);
        Configure(UpdateSecondaryButton, secondary);
        Configure(UpdateTertiaryButton, tertiary);

        static void Configure(System.Windows.Controls.Button button, string? label)
        {
            button.Content = label;
            button.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnUpdatePrimaryClick(object sender, RoutedEventArgs e)
    {
        switch (_updateBarMode)
        {
            case UpdateBarMode.Asking:
                SetUpdateChecks(true);
                break;

            case UpdateBarMode.Offering when _offeredRelease is { } release:
                RunGuarded(() => DownloadAndInstallAsync(release));
                break;
        }
    }

    private void OnUpdateSecondaryClick(object sender, RoutedEventArgs e)
    {
        switch (_updateBarMode)
        {
            case UpdateBarMode.Asking:
                SetUpdateChecks(false);
                StatusLeft.Text = "Sling will not check for updates. Turn it on in Settings whenever you like.";
                break;

            case UpdateBarMode.Offering:
                // Later: the check already ran today, so it is offered again tomorrow.
                HideUpdateBar();
                break;

            case UpdateBarMode.Downloading:
                _updateWork?.Cancel();
                break;
        }
    }

    private void OnUpdateTertiaryClick(object sender, RoutedEventArgs e)
    {
        if (_updateBarMode == UpdateBarMode.Offering && _offeredRelease is { } release)
        {
            _updateState = _updateState with { SkippedVersion = release.Version.ToString() };
            SaveUpdateState();
            HideUpdateBar();
            StatusLeft.Text = $"Sling {release.Version} skipped. Settings has Check now if you change your mind.";
        }
    }

    /// <summary>Handles the settings panel's update switch.</summary>
    private void OnUpdateCheckToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        SetUpdateChecks(UpdateCheckToggle.IsChecked == true);
    }

    /// <summary>Handles the settings panel's Check now button.</summary>
    private void OnCheckForUpdatesNow(object sender, RoutedEventArgs e)
    {
        CloseSettings();

        if (IsUpdateWorkRunning)
        {
            StatusLeft.Text = "Sling is already talking to GitHub.";
            return;
        }

        RunGuarded(() => CheckForUpdatesAsync(userAsked: true));
    }
}
