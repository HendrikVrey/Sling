using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using Sling.App.Collections;
using Sling.Core.Documents;
using Sling.Http;

namespace Sling.App;

/// <summary>
/// What the window shows while a request is in flight.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed, pressing Send changed a button's label and one line of 12 px text at
/// the very bottom of the window. Everything in between - the pane being looked at - held
/// the previous response until the new one replaced it, so a slow API was indistinguishable
/// from a click that did not register, and the body on screen read as the answer to the
/// question just asked.
/// </para>
/// <para>
/// Three things carry the weight, in the order they matter. <b>The clock</b>, because a
/// number that keeps moving is the only proof that survives a request which produces no
/// other news for thirty seconds. <b>The request being sent</b>, because run-all and chains
/// send things nobody typed a chord for. <b>The ring</b>, which is the part that catches the
/// eye and the least informative of the three.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>How long a request may take before the card appears.</summary>
    /// <remarks>
    /// A request against a local server answers in tens of milliseconds. Showing the card
    /// immediately would flash it on every one of them, and a flash is a defect rather than
    /// feedback - the eye reads it as something going wrong. Long enough to skip the fast
    /// case, short enough that anything a person would call slow is covered.
    /// </remarks>
    private static readonly TimeSpan BusyRevealDelay = TimeSpan.FromMilliseconds(180);

    /// <summary>
    /// Ticks the clock, and reveals the card once <see cref="BusyRevealDelay"/> has passed.
    /// </summary>
    /// <remarks>
    /// One timer for both jobs rather than a second one-shot for the reveal: the tick has to
    /// run anyway, and two timers is two things to stop on the close path.
    /// </remarks>
    private readonly DispatcherTimer _busyClock = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private readonly Stopwatch _busyElapsed = new();

    /// <summary>What the run was asked to do, kept for the headline until the first report.</summary>
    private string _busyHeadline = string.Empty;

    private bool _busyShown;

    /// <summary>
    /// Whether the keyboard was in the response pane when the card replaced it.
    /// </summary>
    /// <remarks>
    /// Collapsing an element that contains the focus makes WPF drop the focus to the window,
    /// and nothing puts it back - so somebody who had clicked into the response buffer, or
    /// opened its find bar, would find their keystrokes going to the window's chord handler
    /// once the next response arrived.
    /// </remarks>
    private bool _busyTookFocus;

    private void InitializeBusyView() => _busyClock.Tick += OnBusyTick;

    private void RemoveBusyHandlers()
    {
        _busyClock.Stop();
        _busyClock.Tick -= OnBusyTick;
    }

    /// <summary>
    /// Starts the wait: the previous response comes off screen and the clock starts.
    /// </summary>
    /// <param name="headline">
    /// What was asked for, in one line. It stands until the runner names the first request,
    /// and it is what a run whose every request fails to resolve is left showing.
    /// </param>
    private void BeginBusy(string headline)
    {
        _busyHeadline = headline;
        _busyShown = false;

        BusyHeadline.Text = headline;
        BusyRequest.Visibility = Visibility.Collapsed;
        BusyDetail.Visibility = Visibility.Collapsed;
        BusyElapsed.Text = string.Empty;

        // The picker lists the previous run's exchanges, and it sits outside the pane this
        // card covers. Left up, it offers a chooser for responses that are no longer on
        // screen. Whatever finishes rebuilds it.
        ExchangePicker.Visibility = Visibility.Collapsed;

        _busyElapsed.Restart();
        _busyClock.Start();
    }

    /// <summary>Names the request now going out.</summary>
    /// <remarks>
    /// Reached through <see cref="Progress{T}"/>, which posts to the dispatcher, so a report
    /// can arrive after the run it belongs to has finished. The caller checks that the run
    /// is still the current one before this is called - see <c>RunAsync</c>.
    /// </remarks>
    private void ShowBusyRequest(RunProgress progress)
    {
        var request = progress.Request;

        // The gap between verb and label is part of the verb's own Run: whitespace between
        // two Runs declared in markup is collapsed away, and a third Run holding two spaces
        // is a stranger thing to read than this.
        BusyMethod.Text = request.Method + "  ";
        BusyMethod.Foreground = MethodPalette.For(MethodBrushes, request.Method);
        BusyTarget.Text = Describe(request);
        BusyRequest.ToolTip = Clamp($"{request.Method} {request.Target}");
        BusyRequest.Visibility = Visibility.Visible;

        var detail = DescribeProgress(progress);

        BusyDetail.Text = detail ?? string.Empty;
        BusyDetail.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;

        Announce();
    }

    /// <summary>
    /// Says the card's current state to a screen reader, once, when it changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replacing the response pane with a wait state is precisely the kind of change a live
    /// region exists for: nothing was clicked, and the part of the window that answers the
    /// question has been taken away and put back. Without this it is silent.
    /// </para>
    /// <para>
    /// <b>The clock is deliberately not in it.</b> It moves ten times a second, and a live
    /// region that re-announces on every tick is worse than one that says nothing.
    /// </para>
    /// <para>
    /// Guarded by <see cref="AutomationPeer.ListenerExists"/>, which is false when no
    /// assistive client is attached - so the peer tree is not built for a window nobody is
    /// listening to.
    /// </para>
    /// </remarks>
    private void Announce()
    {
        if (_closed || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        var request = BusyRequest.Visibility == Visibility.Visible
            ? $". {BusyMethod.Text}{BusyTarget.Text}"
            : string.Empty;

        var detail = BusyDetail.Visibility == Visibility.Visible ? $". {BusyDetail.Text}" : string.Empty;

        AutomationProperties.SetName(ResponseBusy, BusyHeadline.Text + request + detail);

        var peer = UIElementAutomationPeer.FromElement(ResponseBusy)
            ?? UIElementAutomationPeer.CreatePeerForElement(ResponseBusy);

        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>
    /// The line under the request: where the run has got to, or why Sling is sending
    /// something nobody asked for.
    /// </summary>
    /// <remarks>
    /// The role wins over the count. A chain sends requests that are not in the caller's
    /// list, so a dependency's number is not a position in it - "4 of 3" is worse than
    /// saying nothing, and saying what the request is *for* is better than either.
    /// </remarks>
    private static string? DescribeProgress(RunProgress progress) => progress.Role switch
    {
        ExchangeRole.Dependency => "Needed by the request you sent",
        ExchangeRole.TokenRequest => "Fetching an access token first",
        ExchangeRole.Retry => "Retrying with a fresh token",
        _ when progress.Total > 1 && progress.Number <= progress.Total =>
            $"Request {progress.Number.ToString(CultureInfo.InvariantCulture)}"
                + $" of {progress.Total.ToString(CultureInfo.InvariantCulture)}",
        _ => null,
    };

    /// <summary>Ends the wait, whatever ended it: a response, a failure, or Escape.</summary>
    /// <remarks>Idempotent - the run path calls it twice on the way to a response.</remarks>
    private void EndBusy()
    {
        _busyClock.Stop();
        _busyElapsed.Reset();
        _busyShown = false;

        // A send in flight when the window closes still has a continuation to run, and its
        // finally reaches here. The controls belong to a window that is gone.
        if (_closed)
        {
            return;
        }

        ResponseBusy.Visibility = Visibility.Collapsed;
        ResponseContent.Visibility = Visibility.Visible;

        // Stopped rather than merely hidden. The indeterminate ring is a storyboard, and one
        // left running on a collapsed element is an animation the compositor keeps servicing
        // for the life of the window.
        BusyRing.IsIndeterminate = false;

        RestoreFocusToResponse();
    }

    /// <summary>
    /// Puts the keyboard back in the response pane, if that is where the card took it from.
    /// </summary>
    /// <remarks>
    /// Only when nothing else has claimed it in the meantime. Collapsing the pane leaves the
    /// focus on the window itself; if the user has since clicked into the request pane or the
    /// rail, taking it back would be the application pulling the caret out from under them.
    /// </remarks>
    private void RestoreFocusToResponse()
    {
        if (!_busyTookFocus)
        {
            return;
        }

        _busyTookFocus = false;

        if (ReferenceEquals(Keyboard.FocusedElement, this))
        {
            _ = ResponsePane.Focus();
        }
    }

    private void OnBusyTick(object? sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        if (!_busyShown)
        {
            if (_busyElapsed.Elapsed < BusyRevealDelay)
            {
                return;
            }

            _busyShown = true;

            // Read before the collapse, which is what loses it.
            _busyTookFocus = ResponseContent.IsKeyboardFocusWithin;

            BusyHeadline.Text = _busyHeadline;
            ResponseContent.Visibility = Visibility.Collapsed;
            ResponseBusy.Visibility = Visibility.Visible;
            BusyRing.IsIndeterminate = true;

            Announce();
        }

        // The hint rides on the clock's line rather than taking one of its own. Escape
        // already cancels and the Send button already says Cancel; this is where somebody
        // looking at the wait will read it.
        BusyElapsed.Text = FormatElapsed(_busyElapsed.Elapsed) + "   ·   Esc to cancel";
    }

    /// <summary>
    /// The clock's text: tenths while that is the interesting digit, minutes and seconds
    /// once it is not.
    /// </summary>
    /// <remarks>
    /// Invariant, and the tenth of a second is deliberate. It moves on every tick, which is
    /// what makes it read as a live number rather than as a label that happens to say "3 s".
    /// </remarks>
    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalSeconds < 60
        ? elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : ((int)elapsed.TotalMinutes).ToString(CultureInfo.InvariantCulture)
            + ":" + elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture);
}
