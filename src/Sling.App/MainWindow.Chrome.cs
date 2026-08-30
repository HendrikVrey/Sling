using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using Sling.App.Collections;
using Sling.App.Editor;
using Sling.Core.Documents;
using Sling.Core.Parsing;
using Wpf.Ui.Controls;

namespace Sling.App;

/// <summary>
/// The command bar, and the two things on screen that say what is about to happen and what
/// just did: the send target beside the buttons, and the status pill beside the response.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every command here already worked from the keyboard, and that was the problem.</b> A
/// tool whose entire surface is chords is one its author can use and nobody else can learn:
/// there is nothing to point at, nothing to discover by looking, and no way to find out that
/// "run every request in this file" exists at all. The toolbar does not replace the keymap —
/// each control names its own chord — it makes the keymap findable.
/// </para>
/// <para>
/// The handlers are thin on purpose. Each one routes to the same method the chord routes to,
/// through the same <see cref="RunGuarded"/> wrapper, so a button and its shortcut cannot
/// drift into doing different things.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Shown beside the buttons when the caret is not inside any request.</summary>
    private const string NoSendTarget = "no request under the caret";

    /// <summary>
    /// The parse the send target was read from, and the document version it was made of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cached against <see cref="ITextSourceVersion"/> rather than re-parsed per caret move.
    /// A caret move cannot change what the requests are, so the common case — arrowing
    /// around a file — costs a dictionary-free reference comparison and a line lookup.
    /// </para>
    /// <para>
    /// A keystroke <em>does</em> invalidate it, and the cache is deliberately <b>not</b>
    /// refilled on that path: re-parsing on every character is the cost the rail's idle timer
    /// exists to avoid, and paying it here would reintroduce it through a different door. The
    /// label goes stale for at most one idle interval, which is the same staleness the rail
    /// already accepts.
    /// </para>
    /// </remarks>
    private ITextSourceVersion? _sendTargetVersion;

    private RequestDocument? _sendTargetDocument;

    private Color? _page;

    /// <summary>Wires the command bar. Called once, from the constructor.</summary>
    private void InitializeChrome()
    {
        // A second subscription rather than a call from OnCaretMoved: that handler bails out
        // when no folder is open, and the send target has to be right in an untitled buffer
        // too — which is the state the application starts in.
        RequestPane.TextArea.Caret.PositionChanged += OnCaretMovedForSendTarget;

        UpdateSendTarget(reparse: true);
        UpdateToolbar();
    }

    private void RemoveChromeHandlers() =>
        RequestPane.TextArea.Caret.PositionChanged -= OnCaretMovedForSendTarget;

    /// <summary>The opaque colour every computed colour on this window is measured against.</summary>
    /// <remarks>
    /// The application's dictionary, not the window's: <see cref="ResourceDictionary"/>'s
    /// indexer searches the dictionary and what it merges, and does not walk up the element
    /// tree — so asking the window for a theme key finds nothing and silently takes the
    /// fallback.
    /// </remarks>
    private Color Page => _page ??= SyntaxPalette.Page(Application.Current?.Resources);

    private void OnSendClicked(object sender, RoutedEventArgs e)
    {
        // The button is Cancel for the duration of a request, so this is one control with two
        // meanings — and which one it has is read from the same field the label is drawn
        // from, rather than from a second flag that could disagree with it.
        if (IsSending)
        {
            _inFlight?.Cancel();
            return;
        }

        _ = SendCurrentRequestAsync();
    }

    private void OnRunAllClicked(object sender, RoutedEventArgs e) => _ = SendWholeDocumentAsync();

    /// <summary>Shows or hides the collections rail.</summary>
    /// <remarks>
    /// <para>
    /// Not persisted, and that is deliberate: this is a "get out of my way for a minute"
    /// control, not a preference. A rail that stayed hidden across restarts would put the
    /// application back in the state whose invisibility was the last thing Hendrik reported.
    /// </para>
    /// <para>
    /// The rail's own creation buttons are not disturbed — <see cref="ShowWorkspaceRail"/>
    /// owns those, and this only collapses the whole column, so whatever the rail was showing
    /// is what comes back.
    /// </para>
    /// </remarks>
    private void ToggleRail()
    {
        var hiding = FilesRail.Visibility == Visibility.Visible;

        FilesRail.Visibility = hiding ? Visibility.Collapsed : Visibility.Visible;

        RailToggleButton.Icon = new SymbolIcon
        {
            Symbol = hiding ? SymbolRegular.PanelLeftExpand24 : SymbolRegular.PanelLeftContract24,
        };

        RailToggleButton.ToolTip = hiding
            ? "Show the collections rail  (Ctrl+B)"
            : "Hide the collections rail  (Ctrl+B)";
    }

    private void OnToggleRail(object sender, RoutedEventArgs e) => ToggleRail();

    private void OnOpenDocumentClicked(object sender, RoutedEventArgs e) => RunGuarded(OpenDocumentAsync);

    private void OnNewEmptyDocumentClicked(object sender, RoutedEventArgs e) => RunGuarded(NewDocumentAsync);

    private void OnSaveClicked(object sender, RoutedEventArgs e) => RunGuarded(() => SaveAsync());

    private void OnSaveAsClicked(object sender, RoutedEventArgs e) => RunGuarded(() => SaveAsAsync());

    private void OnImportClicked(object sender, RoutedEventArgs e) => RunGuarded(ImportPostmanAsync);

    private void OnHistoryClicked(object sender, RoutedEventArgs e)
    {
        CloseSettings();
        RunGuarded(ShowHistoryAsync);
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (SettingsAreOpen)
        {
            CloseSettings();
        }
        else
        {
            ShowSettings();
        }
    }

    /// <summary>Puts the buttons in step with what the window is doing.</summary>
    /// <remarks>
    /// Called from <see cref="UpdateTitle"/> — which is already the one place the dirty
    /// marker is recomputed — and around the in-flight token, which are between them every
    /// state these controls read.
    /// </remarks>
    private void UpdateToolbar()
    {
        if (_closed)
        {
            return;
        }

        var sending = IsSending;

        SendButton.Content = sending ? "Cancel" : "Send";
        SendButton.Icon = new SymbolIcon { Symbol = sending ? SymbolRegular.Dismiss24 : SymbolRegular.Send24 };
        SendButton.Appearance = sending ? ControlAppearance.Danger : ControlAppearance.Primary;
        SendButton.ToolTip = sending
            ? "Stop the request in flight  (Esc)"
            : "Send the request under the caret  (Ctrl+Enter)";

        // Disabled rather than hidden. A control that moves while you are reaching for it is
        // worse than one that is greyed, and both of these come back within a second.
        RunAllButton.IsEnabled = !sending;
        SaveButton.IsEnabled = _dirty;
    }

    private void OnCaretMovedForSendTarget(object? sender, EventArgs e) => UpdateSendTarget(reparse: false);

    /// <summary>Refreshes the label that says which request Send would send.</summary>
    /// <param name="reparse">
    /// True where a parse is affordable — startup, a document load, the rail's idle tick.
    /// False on the caret path, which runs at key-repeat rate: there the cached parse is used
    /// if it still matches the buffer, and the label is left alone if it does not.
    /// </param>
    private void UpdateSendTarget(bool reparse)
    {
        if (_closed)
        {
            return;
        }

        // The same ceiling the rail honours, for the same reason: past it the parse is no
        // longer something to run on the dispatcher. Said out loud rather than left showing a
        // request that may not be the one under the caret any more.
        if (RequestPane.Document.TextLength > MaxLiveRefreshLength)
        {
            ShowSendTarget(null, "this document is too large to track as you type");
            return;
        }

        var version = RequestPane.Document.Version;

        if (!IsSendTargetFresh(version))
        {
            if (!reparse)
            {
                return;
            }

            _sendTargetDocument = RequestDocumentParser.Parse(RequestPane.Text);
            _sendTargetVersion = version;
        }

        ShowSendTarget(_sendTargetDocument?.BlockAtLine(RequestPane.TextArea.Caret.Line), NoSendTarget);
    }

    private bool IsSendTargetFresh(ITextSourceVersion? version) =>
        _sendTargetDocument is not null
            && _sendTargetVersion is not null
            && version is not null
            && version.BelongsToSameDocumentAs(_sendTargetVersion)
            && version.CompareAge(_sendTargetVersion) == 0;

    /// <param name="absent">What to say in place of a request. Never the empty string — a
    /// label that disappears reads as a rendering fault rather than as an answer.</param>
    private void ShowSendTarget(RequestBlock? block, string absent)
    {
        if (block is null)
        {
            SendTargetMethod.Visibility = Visibility.Collapsed;
            SendTargetLabel.Text = absent;
            SendTargetLabel.ToolTip = null;

            return;
        }

        SendTargetMethod.Text = block.Method;
        SendTargetMethod.Foreground = MethodPalette.For(MethodBrushes, block.Method);
        SendTargetMethod.Visibility = Visibility.Visible;

        // Describe and Clamp are the collections rail's, deliberately. The toolbar and the
        // tree name the same request at the same moment, a few centimetres apart, and two
        // implementations of "what is this request called" is a pair that drifts — the rail
        // saying 'login' while the bar says the URL is a difference nobody can explain.
        SendTargetLabel.Text = Describe(block);

        // The target as written, which is what somebody checking the label actually wants,
        // and clamped for the same reason every other document string here is.
        SendTargetLabel.ToolTip = Clamp($"{block.Method} {block.Target}");
    }

    /// <summary>Puts the response's status in the pill beside the RESPONSE label.</summary>
    private void ShowStatusPill(ResponseSnapshot response)
    {
        var tone = StatusPalette.For(response.StatusCode, Page);

        StatusPill.Background = tone.Background;
        StatusPill.BorderBrush = tone.Border;
        StatusPillText.Foreground = tone.Foreground;

        StatusPillText.Text = string.IsNullOrEmpty(response.ReasonPhrase)
            ? response.StatusCode.ToString(CultureInfo.InvariantCulture)
            : $"{response.StatusCode.ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase}";

        StatusPill.Visibility = Visibility.Visible;
    }

    /// <summary>Takes the pill down, for anything that is not a response.</summary>
    /// <remarks>
    /// A diagnostic, a cancellation or the history listing leaves no status behind, and a
    /// pill still reading <c>200 OK</c> over a parse error is worse than no pill at all.
    /// </remarks>
    private void HideStatusPill() => StatusPill.Visibility = Visibility.Collapsed;
}
