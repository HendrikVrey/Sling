using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Sling.App.Editor;
using Sling.Core.Auth;
using Sling.Persistence.Tokens;

namespace Sling.App;

/// <summary>
/// The token chip, and the list behind it.
/// </summary>
/// <remarks>
/// <para>
/// Tokens are cached by grant and dropped on an environment switch. Both are right, and
/// both were entirely unobservable - so a 401 could not distinguish a stale token from a
/// wrong scope from a token fetched against the other environment. All three produce the
/// same status code and are told apart only by knowing what is held.
/// </para>
/// <para>
/// <b>Never a token value, anywhere on this surface.</b> The grant and the clock are what
/// answer "why did that 401"; the token itself answers nothing, and a panel is a place a
/// screenshot comes from. The projection this reads carries neither the token nor the
/// client secret, so there is no route from here to either.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>How often the chip's countdown is redrawn.</summary>
    /// <remarks>
    /// <para>
    /// A clock rather than a poll: it is not asking whether anything changed, it is redrawing
    /// a number that is already known and is decreasing on its own. Thirty seconds is under
    /// the resolution the chip shows above a minute, so the number is never seen wrong by
    /// more than it is rounded by.
    /// </para>
    /// <para>
    /// It only runs while there is a token with a stated lifetime, and it stops itself when
    /// there is not. A timer left running on an idle window is the thing this codebase has
    /// already had to take out of one.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan TokenChipTick = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _tokenClock = new() { Interval = TokenChipTick };

    private bool TokensAreOpen => TokensOverlay.Visibility == Visibility.Visible;

    private void InitializeTokens()
    {
        _tokenClock.Tick += OnTokenClockTick;
        RefreshTokenChip();
    }

    /// <summary>
    /// The slot the current workspace and environment read and write.
    /// </summary>
    /// <remarks>
    /// Recomputed rather than cached, because both halves of it change under the window and
    /// a stale scope is the one bug this feature must not have: it would read a token
    /// belonging to a different deployment.
    /// </remarks>
    private string TokenScope => TokenStore.ScopeOf(_workspace?.Root, _selectedEnvironment);

    /// <summary>
    /// Puts previously stored tokens back into the cache for the current scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called wherever the scope changes - a folder opened, an environment selected - and
    /// after the session is forgotten, because forgetting is what makes room for them.
    /// </para>
    /// <para>
    /// A spent token is dropped rather than restored, so what comes back is only what would
    /// still have been usable had the process never stopped.
    /// </para>
    /// </remarks>
    private void RestoreRememberedTokens()
    {
        if (!_settings.RememberTokens)
        {
            return;
        }

        var restored = _runner.RestoreTokens(_tokenStore.Load(TokenScope));

        if (restored > 0)
        {
            RefreshTokenChip();
        }
    }

    /// <summary>Writes the cache back to the current scope's store.</summary>
    /// <remarks>
    /// The whole cache each time rather than an append. It is a handful of entries, and a
    /// store that accumulates would keep tokens for grants the document no longer has.
    /// </remarks>
    private void SaveRememberedTokens()
    {
        if (!_settings.RememberTokens)
        {
            return;
        }

        _tokenStore.Save(TokenScope, _runner.ExportTokens());
    }

    private void OnTokenClockTick(object? sender, EventArgs e)
    {
        if (_closed)
        {
            _tokenClock.Stop();
            return;
        }

        RefreshTokenChip();
    }

    /// <summary>
    /// Redraws the chip, and starts or stops the clock behind it.
    /// </summary>
    /// <remarks>
    /// Called after every send, after an environment change and on every tick - the three
    /// ways the answer changes. The chip is hidden rather than reading "no token" when
    /// nothing has been fetched: a control that is always there saying nothing is a control
    /// people stop reading.
    /// </remarks>
    private void RefreshTokenChip()
    {
        var tokens = _runner.HeldTokens();
        var now = DateTimeOffset.UtcNow;

        if (tokens.Count == 0)
        {
            TokenChip.Visibility = Visibility.Collapsed;
            _tokenClock.Stop();

            if (TokensAreOpen)
            {
                RefreshTokensCard();
            }

            return;
        }

        TokenChip.Content = TokenSummary.Chip(tokens, now);
        TokenChip.ToolTip = "Every access token held this session, by grant. Never the token itself.";
        TokenChip.Visibility = Visibility.Visible;

        // Only where there is a countdown to count. A token with no stated lifetime is not
        // cached at all, and one that is already spent has nothing left to tick down.
        if (tokens.Any(t => t.Remaining(now) is { } left && left > TimeSpan.Zero))
        {
            _tokenClock.Start();
        }
        else
        {
            _tokenClock.Stop();
        }

        if (TokensAreOpen)
        {
            RefreshTokensCard();
        }
    }

    private void OnTokenChipClicked(object sender, RoutedEventArgs e) => ShowTokens();

    private void OnCloseTokens(object sender, RoutedEventArgs e) => CloseTokens();

    private void ShowTokens()
    {
        RefreshTokensCard();
        Overlays.Reveal(TokensOverlay, TokensCard);
    }

    private void CloseTokens() => Overlays.Hide(TokensOverlay);

    private void RefreshTokensCard()
    {
        var now = DateTimeOffset.UtcNow;

        var rows = _runner.HeldTokens()
            .OrderBy(t => t.ExpiresUtc ?? DateTimeOffset.MaxValue)
            .Select(t => new
            {
                Grant = t.Describe(),
                Endpoint = t.TokenUrl,
                Clock = t.Clock(now),
            })
            .ToList();

        TokensList.ItemsSource = rows;
        TokensEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var under = _selectedEnvironment is { } environment
            ? $"Fetched under '{environment}'. "
            : string.Empty;

        TokensSubtitle.Text = under
            + (_settings.RememberTokens
                ? "Remembered across restarts, encrypted under your account and scoped to this "
                    + "folder and environment. Switching environment leaves them behind."
                : "Held in memory for this session only. Switching environment or closing Sling "
                    + "drops them all.");
    }

    /// <summary>
    /// Drops every cached token.
    /// </summary>
    /// <remarks>
    /// The thing the plan called "finding something to poke", given a button. It is not the
    /// 401 retry's job: this is for the case where somebody knows the token is wrong before
    /// the server says so - a secret they have just rotated at the far end.
    /// </remarks>
    private void OnForgetTokens(object sender, RoutedEventArgs e)
    {
        var held = _runner.HeldTokens().Count;

        // ForgetSession, not a token-only clear. The stored responses were resolved under the
        // same session and a chain reading one of them after the tokens have gone would be
        // reading an answer the next send cannot reproduce.
        _runner.ForgetSession();

        // The store too, and not only the cache. "Forget them" that leaves them on disk to be
        // read back at the next start is not forgetting.
        _tokenStore.Clear(TokenScope);

        RefreshTokenChip();
        RefreshTokensCard();

        StatusLeft.Text = held == 1
            ? "Forgot the cached token. The next send fetches a new one."
            : $"Forgot {held.ToString(CultureInfo.InvariantCulture)} cached tokens. The next send "
                + "fetches new ones.";
    }
}
