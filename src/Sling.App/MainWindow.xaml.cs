using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows.Input;
using Sling.Core.Documents;
using Sling.Core.Parsing;
using Sling.Core.Rendering;
using Sling.Core.Variables;
using Sling.Http;
using Wpf.Ui.Controls;

namespace Sling.App;

// FluentWindow, not Window: the XAML root is ui:FluentWindow, and a partial class whose
// halves name different base types is CS0263. FluentWindow is what gives the Mica
// backdrop and the rounded corners declared in MainWindow.xaml.
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "A WPF Window cannot implement IDisposable - the framework owns its "
        + "lifetime and never calls Dispose. OnClosed is the disposal point WPF actually "
        + "provides, and _runner is released there.")]
public partial class MainWindow : FluentWindow
{
    /// <summary>
    /// Seeded into the request pane on first run so the window is never empty and the
    /// <c>.http</c> dialect is visible immediately. Replaced by a real document store in
    /// M3; it is a literal here rather than a file on disk because there is no
    /// persistence layer to load it from yet.
    /// </summary>
    /// <remarks>
    /// Deliberately a chain against a public API that needs no credentials: pressing
    /// <c>Ctrl+Enter</c> on the second request sends the first one too, which is the one
    /// behaviour in Sling that has to be seen rather than described. The
    /// <c>User-Agent</c> is not decoration - GitHub rejects a request without one.
    /// </remarks>
    private const string SampleRequest = """
        @base = https://api.github.com

        ### a request is a document, not a form
        # @name repo
        GET {{base}}/repos/dotnet/runtime
        Accept: application/vnd.github+json
        User-Agent: Sling

        ### a value from that response flows into this one - send this and both run
        GET {{base}}/users/{{repo.response.body.$.owner.login}}
        Accept: application/vnd.github+json
        User-Agent: Sling
        """;

    private const string ReadyHint = "Ctrl+Enter sends the request under the caret · Esc cancels";

    private readonly RequestRunner _runner = new();

    private CancellationTokenSource? _inFlight;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();

        RequestPane.Text = SampleRequest;

        // The hint first, so anything the initialisers below have to say survives. It used
        // to be assigned at the end, which meant a settings file that would not parse
        // reported the problem and then had it overwritten one line later - a silent
        // revert to the defaults, which is exactly what the message exists to prevent.
        StatusLeft.Text = ReadyHint;
        StatusRight.Text = string.Empty;

        InitializeResponseView();
        InstallCurlPaste();

        // Before the workspace, because the cookie jar the workspace resets is created
        // from the settings: whether there is a jar at all is one, and resetting one that
        // does not exist yet would build a jar the user had switched off.
        InitializeSettings();

        // After the sample is seeded, so seeding it is not counted as an edit: nobody
        // should be asked whether to save text they did not write.
        InitializeWorkspace();

        // Last: the command bar reads the document and the dirty flag, both of which the
        // workspace has just settled.
        InitializeChrome();

        ShowMessage("Nothing sent yet.");
    }

    private bool IsSending => _inFlight is not null;

    /// <summary>
    /// Resolved on the window's tunnelling pass rather than through an
    /// <c>InputBinding</c>. Input bindings are matched in <c>PostProcessInput</c>, after
    /// the focused control has already had the key, so a chord that overlaps an editor
    /// key is decided by subscription order the application does not control.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // The name prompt is modal in intent, so it has to be modal in fact: while it is up
        // it swallows every chord below. Without this, Ctrl+O behind the scrim would open a
        // file dialog on top of a prompt whose task nothing would ever complete.
        if (NamePromptIsOpen)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseNamePrompt(null);
                return;
            }

            base.OnPreviewKeyDown(e);
            return;
        }

        // IsRepeat: holding the chord down would otherwise queue a send per auto-repeat
        // tick, at roughly 30 Hz, against whatever server the caret happens to be on.
        if (e.Key == Key.Enter && !e.IsRepeat)
        {
            if (e.KeyboardDevice.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                _ = SendCurrentRequestAsync();
                return;
            }

            if (e.KeyboardDevice.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                e.Handled = true;
                _ = SendWholeDocumentAsync();
                return;
            }
        }

        if (e.Key == Key.Escape)
        {
            // The overlay first. It is the thing most recently put on screen, and while it
            // is up it is what Escape visibly refers to - cancelling a send from behind it
            // would look like the key did nothing.
            if (SettingsAreOpen)
            {
                e.Handled = true;
                CloseSettings();
                return;
            }

            if (IsSending)
            {
                e.Handled = true;
                _inFlight?.Cancel();
                return;
            }
        }

        // IsRepeat again, and for a sharper reason than the send: holding Ctrl+S opens a
        // Save As dialog per tick behind the one already up.
        if (!e.IsRepeat && TryHandleViewKey(e))
        {
            e.Handled = true;
            return;
        }

        if (!e.IsRepeat && TryHandleDocumentKey(e))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// The chords that show something rather than change something: settings, the local
    /// history, and the collections rail.
    /// </summary>
    private bool TryHandleViewKey(KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers != ModifierKeys.Control)
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.OemComma:
                if (SettingsAreOpen)
                {
                    CloseSettings();
                }
                else
                {
                    ShowSettings();
                }

                return true;

            case Key.H:
                CloseSettings();
                RunGuarded(ShowHistoryAsync);
                return true;

            case Key.B:
                ToggleRail();
                return true;

            default:
                return false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // A Window cannot be IDisposable, so anything owning unmanaged or cancellable
        // work has to be released here. The flag goes up first: disposing the runner
        // underneath a send in flight makes it fail, and its continuation must then find
        // a window it knows is gone rather than write into dead controls.
        _closed = true;
        _inFlight?.Cancel();
        _runner.Dispose();
        _syntax?.Dispose();

        // A DispatcherTimer is rooted by the dispatcher, not by this window, so one left
        // running keeps the window alive and ticks into controls that are gone.
        _requestRefresh.Stop();
        _requestRefresh.Tick -= OnRequestRefreshTick;

        // The caret belongs to the editor's TextArea, not to this window, so the handler is
        // not collected with it. Two things listen to it - the rail's highlight and the
        // command bar's send target - and both have to come off.
        RequestPane.TextArea.Caret.PositionChanged -= OnCaretMoved;
        RemoveChromeHandlers();

        // Anything still awaiting the prompt gets an answer rather than a task that never
        // completes - the continuation is a command that would otherwise sit on the heap
        // holding the closed window.
        CloseNamePrompt(null);

        // The pasting handler is attached to a control rather than to this window, so it
        // is not collected with the window on its own.
        RemoveCurlPaste();

        base.OnClosed(e);
    }

    private async Task SendCurrentRequestAsync()
    {
        if (IsSending)
        {
            return;
        }

        var document = RequestDocumentParser.Parse(RequestPane.Text);
        var block = document.BlockAtLine(RequestPane.TextArea.Caret.Line);

        if (block is null)
        {
            ShowMessage("There is no request here yet. Write a method and a URL, then press Ctrl+Enter.");
            StatusLeft.Text = ReadyHint;
            return;
        }

        // Only this request's own problems concern it. A malformed request further down
        // the file is not a reason to refuse to send the one under the caret - a document
        // is half-written most of the time it is looked at.
        //
        // The window runs from FirstLine, not StartLine: '# @name' and the comments above
        // the request line belong to it, and filtering from StartLine discarded every
        // diagnostic they raised - including "'@name' needs a name", after which the
        // request would send unnamed and every chain against it fail for an unrelated
        // reason.
        var mine = document.Diagnostics
            .Where(d => d.Line >= block.FirstLine && d.Line <= block.EndLine)
            .ToList();

        var blocking = mine.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (blocking.Count > 0)
        {
            ShowMessage(ResponseRenderer.RenderDiagnostics(blocking));
            StatusLeft.Text = "Not sent.";
            StatusRight.Text = string.Empty;
            return;
        }

        await RunAsync(
            $"Sending {block.Method} …",
            (context, token) => _runner.RunAsync(document, block, context, token),
            mine).ConfigureAwait(true);
    }

    /// <summary>
    /// Sends every request in the document that can be sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A request whose own lines hold an error is left out rather than attempted, and the
    /// diagnostic that excluded it is reported - the same filter <c>Ctrl+Enter</c> applies
    /// to the one request under the caret, applied to each of them in turn. Deciding a
    /// request cannot be sent is the document's business, which is why the runner takes the
    /// list rather than the document.
    /// </para>
    /// <para>
    /// No confirmation prompt. The chord is deliberate and the audience is developers who
    /// know what is in their own file; a dialog on every run-all would be answered without
    /// being read within a day, which makes it worse than nothing.
    /// </para>
    /// </remarks>
    private async Task SendWholeDocumentAsync()
    {
        if (IsSending)
        {
            return;
        }

        var document = RequestDocumentParser.Parse(RequestPane.Text);

        if (document.Requests.Count == 0)
        {
            ShowMessage("There are no requests in this file yet.");
            StatusLeft.Text = ReadyHint;
            return;
        }

        var sendable = new List<RequestBlock>();
        var notes = new List<ParseDiagnostic>();

        foreach (var block in document.Requests)
        {
            var mine = document.Diagnostics
                .Where(d => d.Line >= block.FirstLine && d.Line <= block.EndLine)
                .ToList();

            notes.AddRange(mine);

            if (!mine.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                sendable.Add(block);
            }
        }

        if (sendable.Count == 0)
        {
            ShowMessage(ResponseRenderer.RenderDiagnostics(
                notes.Where(d => d.Severity == DiagnosticSeverity.Error).ToList()));

            StatusLeft.Text = "Nothing in this file can be sent.";
            StatusRight.Text = string.Empty;
            return;
        }

        var count = sendable.Count.ToString(CultureInfo.InvariantCulture);

        await RunAsync(
            $"Sending {count} request{(sendable.Count == 1 ? string.Empty : "s")} …",
            (context, token) => _runner.RunAllAsync(document, sendable, context, token),
            notes).ConfigureAwait(true);
    }

    /// <param name="run">
    /// What to run, given the resolution context and the run's token. A delegate rather
    /// than two overloads of this method, because everything around the call - the
    /// in-flight token, the status text, the five catch clauses and the history write - is
    /// identical for one request and for all of them, and a second copy of it is a second
    /// place for the last-resort catch to go missing.
    /// </param>
    private async Task RunAsync(
        string sendingMessage,
        Func<ResolutionContext, CancellationToken, Task<RunResult>> run,
        IReadOnlyList<ParseDiagnostic> warnings)
    {
        using var cancellation = new CancellationTokenSource();
        _inFlight = cancellation;

        // The Send button is Cancel for as long as this token lives, so it is refreshed
        // wherever the token changes rather than at the call sites - a send started from the
        // keyboard has to change the button too.
        UpdateToolbar();

        // The pill describes the response in the pane, and there is about to not be one.
        // Leaving the last send's status up while the next is in flight reads as an answer
        // to the question just asked - found by watching a slow request run under a 500 the
        // previous one had produced.
        HideStatusPill();

        StatusLeft.Text = sendingMessage;
        StatusRight.Text = string.Empty;

        try
        {
            // Snapshotted here rather than read from fields on the pool thread, and inside
            // the try: this path is fire-and-forget, so anything thrown outside it vanishes
            // and leaves the status bar reading "Sending …" for ever. It is also the right
            // answer semantically - switching environment mid-flight must not change what
            // the request in flight resolved to.
            var context = CreateResolutionContext();

            // Task.Run because resolution is synchronous and unbounded before the first
            // await: a document whose variables reference each other can expand to
            // megabytes, and doing that on the dispatcher thread freezes the window
            // rather than merely being slow.
            var result = await Task
                .Run(() => run(context, cancellation.Token), cancellation.Token)
                .ConfigureAwait(true);

            Show(result, warnings);

            // After the response is on screen. The write is small and the user is not
            // waiting on it, and putting it first would delay the thing they asked for
            // behind a log.
            await RecordHistoryAsync(result.Exchanges).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Cancelled.");
            StatusLeft.Text = ReadyHint;
            StatusRight.Text = string.Empty;
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Last resort, and deliberately broad. This path is reached from
            // fire-and-forget (OnPreviewKeyDown discards the task), so anything not caught
            // here vanishes: no message, no response, and a status bar left reading
            // "Sending …" for ever. A wrong-looking error beats a window that has quietly
            // stopped working. Sling.Http maps every failure it knows about to a
            // diagnostic; this catches the ones nobody has met yet.
            ShowMessage($"Sling could not complete the request.\n\n{ex.GetType().Name}: {ex.Message}");
            StatusLeft.Text = "Failed.";
            StatusRight.Text = string.Empty;
        }
        finally
        {
            _inFlight = null;
            UpdateToolbar();
        }
    }

    /// <summary>
    /// Puts a completed run into the response pane.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every exchange goes into the picker, including the ones Sling sent on the user's
    /// behalf to satisfy a chain reference. A tool that makes network calls nobody asked
    /// for has to show them, and since M2 each one is a buffer of its own rather than a
    /// section of a transcript.
    /// </para>
    /// <para>
    /// Diagnostics are shown even on a successful send - that is what
    /// <c>docs/http-dialect.md</c> promises about an unsupported directive - but they no
    /// longer displace the body. Putting them in the status bar rather than in the buffer
    /// is the consequence of the buffer now being the response and nothing else: a
    /// warning appended to a JSON body would make the body stop being JSON.
    /// </para>
    /// </remarks>
    private void Show(RunResult result, IReadOnlyList<ParseDiagnostic> warnings)
    {
        if (_closed)
        {
            return;
        }

        var notes = result.Errors.Concat(warnings).ToList();

        if (result.Exchanges.Count == 0)
        {
            // Nothing came back at all, so the diagnostics are the entire answer and the
            // buffer is the right place for them.
            ShowMessage(notes.Count > 0
                ? ResponseRenderer.RenderDiagnostics(notes)
                : "Nothing was sent.");

            StatusLeft.Text = "Not sent.";
            StatusRight.Text = string.Empty;
            return;
        }

        // Sets StatusLeft and StatusRight from the selected exchange.
        ShowExchanges(result.Exchanges);

        if (notes.Count > 0)
        {
            StatusLeft.Text = Summarise(notes);
        }
        else if (result.Notes.Count > 0)
        {
            // Only when nothing louder needs the line. A refused cookie matters, and it
            // matters less than a diagnostic about the request itself - this is the one
            // place in the window where the two compete for the same row.
            StatusLeft.Text = SummariseNotes(result.Notes);
        }
    }

    /// <summary>
    /// One line describing what went wrong, for a run that produced a response anyway.
    /// </summary>
    /// <remarks>
    /// The first diagnostic in full, and a count for the rest. A status bar cannot hold a
    /// list, and truncating every message to fit would leave several half-sentences
    /// instead of one whole one.
    /// </remarks>
    private static string Summarise(List<ParseDiagnostic> notes)
    {
        var first = notes[0].Message;

        return notes.Count == 1
            ? first
            : $"{first}  (+{(notes.Count - 1).ToString(CultureInfo.InvariantCulture)} more)";
    }
}
