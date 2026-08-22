using System.Globalization;
using System.Windows;
using Sling.Core.Cookies;
using Sling.Core.Rendering;
using Sling.Http;
using Sling.Persistence;
using Sling.Persistence.History;
using Sling.Persistence.Settings;

namespace Sling.App;

/// <summary>
/// The settings overlay, the local history, and the cookie jar — the three things Sling
/// keeps <em>about</em> requests rather than in them.
/// </summary>
/// <remarks>
/// <para>
/// Settings apply as they are changed and are saved immediately; there is no OK, no
/// Cancel and nothing to revert. Every value is a bounded integer or a switch, so there is
/// no half-typed state to guard against, and an apply/revert model would be a state
/// machine built for a form that does not need one.
/// </para>
/// <para>
/// History and the cookie jar are shown <em>in the response buffer</em> rather than in
/// windows of their own. The buffer already searches with <c>Ctrl+F</c>, folds, scrolls
/// and copies; two more panels would be all of that rebuilt worse, in a product whose
/// pitch is that there are no panels.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// Both stores are pointed at <see cref="LocalData.DefaultFolder"/> here — the one
    /// place in the application that decides where Sling's own state lives, which is what
    /// lets their tests point them somewhere disposable.
    /// </summary>
    private readonly SettingsStore _settingsStore = new(LocalData.DefaultFolder);

    private readonly HistoryStore _historyStore = new(LocalData.DefaultFolder);

    private SlingSettings _settings = SlingSettings.Default;

    /// <summary>True while the overlay is being filled, so its own writes are not saves.</summary>
    private bool _loadingSettings;

    private bool SettingsAreOpen => SettingsOverlay.Visibility == Visibility.Visible;

    /// <summary>Loads the settings and puts them in force. Called once, from the constructor.</summary>
    private void InitializeSettings()
    {
        _settings = _settingsStore.Load(out var problem);
        ApplySettings();

        SettingsPath.Text = _settingsStore.FilePath;
        SettingsPath.ToolTip = _settingsStore.FilePath;

        if (problem is not null)
        {
            StatusLeft.Text = problem;
        }
    }

    /// <summary>
    /// Pushes the current settings into the runner and the cookie jar.
    /// </summary>
    /// <remarks>
    /// The jar is created and destroyed by this method rather than merely ignored when
    /// cookies are switched off. A jar that is kept but not consulted would still hold
    /// whatever it collected before the switch, and turning cookies back on would replay a
    /// session the user believed they had stopped.
    /// </remarks>
    private void ApplySettings()
    {
        _runner.Options = new SendOptions
        {
            Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds),
            MaxBodyBytes = _settings.MaxResponseBodyMegabytes * 1024L * 1024L,
            MaxRedirects = _settings.MaxRedirects,
        };

        if (!_settings.CookiesEnabled)
        {
            _runner.Cookies = null;
            return;
        }

        _runner.Cookies ??= new CookieJar();
    }

    /// <summary>
    /// Throws away the current jar and starts a new one.
    /// </summary>
    /// <remarks>
    /// Called whenever the environment changes or a different document is opened, beside
    /// <see cref="RequestRunner.ForgetSession"/> and for the same reason: a session cookie
    /// from staging is a valid-looking credential, and <c>Sling.md</c> §5.6 scopes the jar
    /// per environment. Replacing it rather than clearing it means an in-flight send that
    /// still holds the old jar cannot write into the new one.
    /// </remarks>
    private void ResetCookieJar() =>
        _runner.Cookies = _settings.CookiesEnabled ? new CookieJar() : null;

    private void ShowSettings()
    {
        _loadingSettings = true;

        try
        {
            TimeoutBox.Value = _settings.TimeoutSeconds;
            BodyCapBox.Value = _settings.MaxResponseBodyMegabytes;
            RedirectsBox.Value = _settings.MaxRedirects;
            CookiesToggle.IsChecked = _settings.CookiesEnabled;
            HistoryToggle.IsChecked = _settings.HistoryEnabled;
            HistoryEntriesBox.Value = _settings.HistoryMaxEntries;
        }
        finally
        {
            _loadingSettings = false;
        }

        SettingsOverlay.Visibility = Visibility.Visible;
        TimeoutBox.Focus();
    }

    private void CloseSettings()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        RequestPane.Focus();
    }

    private void OnCloseSettings(object sender, RoutedEventArgs e) => CloseSettings();

    private void OnSettingChanged(object? sender, RoutedEventArgs e) => SaveSettings();

    /// <summary>
    /// A second handler because <c>NumberBox.ValueChanged</c> and <c>ToggleSwitch</c>'s
    /// <c>Checked</c> carry different argument types, and one signature cannot serve both.
    /// </summary>
    private void OnSettingToggled(object sender, RoutedEventArgs e) => SaveSettings();

    /// <summary>
    /// Reads the overlay, puts the result in force, and writes it out.
    /// </summary>
    /// <remarks>
    /// Each control's own value is read back rather than accumulated from the event, so a
    /// value the user typed and one clamped by <see cref="SlingSettings.Clamped"/> cannot
    /// drift apart. The controls carry the same bounds, so clamping should never actually
    /// change anything — which is the point of stating them twice.
    /// </remarks>
    private void SaveSettings()
    {
        if (_loadingSettings)
        {
            return;
        }

        var previousCookies = _settings.CookiesEnabled;

        _settings = new SlingSettings
        {
            TimeoutSeconds = ReadNumber(TimeoutBox.Value, _settings.TimeoutSeconds),
            MaxResponseBodyMegabytes = ReadNumber(BodyCapBox.Value, _settings.MaxResponseBodyMegabytes),
            MaxRedirects = ReadNumber(RedirectsBox.Value, _settings.MaxRedirects),
            CookiesEnabled = CookiesToggle.IsChecked == true,
            HistoryEnabled = HistoryToggle.IsChecked == true,
            HistoryMaxEntries = ReadNumber(HistoryEntriesBox.Value, _settings.HistoryMaxEntries),
        }.Clamped();

        ApplySettings();

        // Switching cookies off must drop whatever the jar already held, not merely stop
        // adding to it. ApplySettings does that on the way down; on the way back up it
        // creates an empty one.
        if (previousCookies != _settings.CookiesEnabled)
        {
            ResetCookieJar();
        }

        if (_settingsStore.Save(_settings) is { } problem)
        {
            StatusLeft.Text = problem;
        }
    }

    /// <summary>
    /// A <c>NumberBox</c> whose text has been cleared reports null, which means "nothing
    /// typed yet" rather than zero — keeping the previous value is the only answer that is
    /// not a silent edit.
    /// </summary>
    private static int ReadNumber(double? value, int fallback) =>
        value is { } number && !double.IsNaN(number) ? (int)Math.Round(number) : fallback;

    /// <summary>Puts the local history in the response buffer.</summary>
    private async Task ShowHistoryAsync()
    {
        var entries = await _historyStore.ReadAsync(CancellationToken.None).ConfigureAwait(true);

        ShowMessage(HistoryRenderer.Render(entries));

        StatusLeft.Text = _settings.HistoryEnabled
            ? _historyStore.FilePath
            : $"History is switched off; this is what was recorded before. {_historyStore.FilePath}";

        StatusRight.Text = string.Empty;
    }

    /// <summary>Puts the current environment's cookie jar in the response buffer.</summary>
    private void ShowCookies()
    {
        CloseSettings();

        if (_runner.Cookies is not { } jar)
        {
            ShowMessage("Cookies are switched off.\n\nTurn them on in settings (Ctrl+,) if a request "
                + "needs a session to be carried between calls.");

            StatusLeft.Text = ReadyHint;
            StatusRight.Text = string.Empty;
            return;
        }

        ShowMessage(HistoryRenderer.RenderCookies(jar.Snapshot(DateTimeOffset.UtcNow), _selectedEnvironment));

        StatusLeft.Text = ReadyHint;
        StatusRight.Text = string.Empty;
    }

    private void OnShowCookies(object sender, RoutedEventArgs e) => ShowCookies();

    private void OnClearCookies(object sender, RoutedEventArgs e)
    {
        ResetCookieJar();
        StatusLeft.Text = "Cookies cleared.";
    }

    private void OnClearHistory(object sender, RoutedEventArgs e) =>
        StatusLeft.Text = _historyStore.Clear() ?? "History cleared.";

    /// <summary>
    /// Records a completed run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The redactor is built here, at the one point that knows both halves of what counts
    /// as a secret: the values the private environment file supplied, and the access tokens
    /// this session has fetched. Neither is knowable inside <c>Sling.Core</c>, and neither
    /// is optional — <see cref="Core.History.HistoryEntry.Record"/> will not build an entry
    /// without a redactor at all.
    /// </para>
    /// <para>
    /// Fire and forget, deliberately: a request has completed and its result is on screen,
    /// and making the user wait on a log write would be the wrong trade. The failure is
    /// reported to the status bar rather than swallowed, because history that silently
    /// stopped recording looks exactly like history that has nothing in it.
    /// </para>
    /// </remarks>
    private async Task RecordHistoryAsync(IReadOnlyList<Exchange> exchanges)
    {
        if (!_settings.HistoryEnabled || exchanges.Count == 0)
        {
            return;
        }

        var redactor = new Core.Redaction.Redactor(
            [.. _environments.Select(_selectedEnvironment).SecretValues(), .. _runner.AcquiredTokens()]);

        var entries = exchanges
            .Select(x => Core.History.HistoryEntry.Record(
                x.Request,
                x.Response,
                x.SentUtc,
                _selectedEnvironment,
                redactor))
            .ToList();

        var problem = await _historyStore
            .AppendAsync(entries, _settings.HistoryMaxEntries, CancellationToken.None)
            .ConfigureAwait(true);

        if (problem is not null && !_closed)
        {
            StatusLeft.Text = problem;
        }
    }

    /// <summary>
    /// One line describing whatever a run had to say beyond its response.
    /// </summary>
    /// <remarks>
    /// The first note in full and a count for the rest, matching how diagnostics are
    /// summarised — a status bar cannot hold a list, and truncating each of several
    /// messages leaves several half-sentences instead of one whole one.
    /// </remarks>
    private static string SummariseNotes(IReadOnlyList<string> notes) =>
        notes.Count == 1
            ? notes[0]
            : $"{notes[0]}  (+{(notes.Count - 1).ToString(CultureInfo.InvariantCulture)} more)";
}
