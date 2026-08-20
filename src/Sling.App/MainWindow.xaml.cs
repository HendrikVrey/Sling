using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Windows.Input;
using Sling.Core.Documents;
using Sling.Core.Parsing;
using Sling.Core.Rendering;
using Sling.Http;
using Wpf.Ui.Controls;

namespace Sling.App;

// FluentWindow, not Window: the XAML root is ui:FluentWindow, and a partial class whose
// halves name different base types is CS0263. FluentWindow is what gives the Mica
// backdrop and the rounded corners declared in MainWindow.xaml.
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "A WPF Window cannot implement IDisposable — the framework owns its "
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
    /// <c>User-Agent</c> is not decoration — GitHub rejects a request without one.
    /// </remarks>
    private const string SampleRequest = """
        @base = https://api.github.com

        ### a request is a document, not a form
        # @name repo
        GET {{base}}/repos/dotnet/runtime
        Accept: application/vnd.github+json
        User-Agent: Sling

        ### a value from that response flows into this one — send this and both run
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
        ResponsePane.Text = "Nothing sent yet.";

        StatusLeft.Text = ReadyHint;
        StatusRight.Text = string.Empty;
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

        // IsRepeat: holding the chord down would otherwise queue a send per auto-repeat
        // tick, at roughly 30 Hz, against whatever server the caret happens to be on.
        if (e.Key == Key.Enter && e.KeyboardDevice.Modifiers == ModifierKeys.Control && !e.IsRepeat)
        {
            e.Handled = true;
            _ = SendCurrentRequestAsync();
            return;
        }

        if (e.Key == Key.Escape && IsSending)
        {
            e.Handled = true;
            _inFlight?.Cancel();
            return;
        }

        base.OnPreviewKeyDown(e);
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
            ShowText("There is no request here yet. Write a method and a URL, then press Ctrl+Enter.");
            StatusLeft.Text = ReadyHint;
            return;
        }

        // Only this request's own problems concern it. A malformed request further down
        // the file is not a reason to refuse to send the one under the caret — a document
        // is half-written most of the time it is looked at.
        //
        // The window runs from FirstLine, not StartLine: '# @name' and the comments above
        // the request line belong to it, and filtering from StartLine discarded every
        // diagnostic they raised — including "'@name' needs a name", after which the
        // request would send unnamed and every chain against it fail for an unrelated
        // reason.
        var mine = document.Diagnostics
            .Where(d => d.Line >= block.FirstLine && d.Line <= block.EndLine)
            .ToList();

        var blocking = mine.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (blocking.Count > 0)
        {
            ShowText(ResponseRenderer.RenderDiagnostics(blocking));
            StatusLeft.Text = "Not sent.";
            StatusRight.Text = string.Empty;
            return;
        }

        await RunAsync(document, block, mine).ConfigureAwait(true);
    }

    private async Task RunAsync(
        RequestDocument document,
        RequestBlock block,
        IReadOnlyList<ParseDiagnostic> warnings)
    {
        using var cancellation = new CancellationTokenSource();
        _inFlight = cancellation;

        StatusLeft.Text = $"Sending {block.Method} …";
        StatusRight.Text = string.Empty;

        try
        {
            // Task.Run because resolution is synchronous and unbounded before the first
            // await: a document whose variables reference each other can expand to
            // megabytes, and doing that on the dispatcher thread freezes the window
            // rather than merely being slow.
            var result = await Task
                .Run(() => _runner.RunAsync(document, block, cancellation.Token), cancellation.Token)
                .ConfigureAwait(true);

            Show(result, warnings);
        }
        catch (OperationCanceledException)
        {
            ShowText("Cancelled.");
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
            ShowText($"Sling could not complete the request.\n\n{ex.GetType().Name}: {ex.Message}");
            StatusLeft.Text = "Failed.";
            StatusRight.Text = string.Empty;
        }
        finally
        {
            _inFlight = null;
        }
    }

    private void Show(RunResult result, IReadOnlyList<ParseDiagnostic> warnings)
    {
        var text = new StringBuilder();

        if (result.Exchanges.Count > 0)
        {
            text.Append(ResponseRenderer.RenderChain(
                result.Exchanges.Select(e => (e.Request, e.Response)).ToList()));
        }

        // Warnings are shown even on a successful send. Nothing rendered them before,
        // which quietly withdrew the promise in docs/http-dialect.md that an unsupported
        // directive is warned about rather than ignored — it was being ignored.
        var notes = result.Errors.Concat(warnings).ToList();
        if (notes.Count > 0)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(ResponseRenderer.RenderDiagnostics(notes));
        }

        ShowText(text.ToString());

        var last = result.Exchanges.Count > 0 ? result.Exchanges[^1] : null;
        if (last is null)
        {
            StatusLeft.Text = "Not sent.";
            StatusRight.Text = string.Empty;
            return;
        }

        StatusLeft.Text = $"{last.Request.Method} {last.Request.Url}";
        StatusRight.Text = ResponseRenderer.Summarize(last.Response);
    }

    /// <summary>
    /// Puts text in the response pane and scrolls it home.
    /// </summary>
    /// <remarks>
    /// Text, and only ever text. A response body is untrusted input: it never reaches a
    /// <c>WebBrowser</c> or <c>WebView2</c> control, and no such control exists anywhere
    /// in the application (<c>Sling.md</c> §5.5). <c>ArchitectureTests</c> enforces that
    /// rather than leaving it to be remembered.
    /// </remarks>
    private void ShowText(string text)
    {
        // A send in flight when the window closes still has a continuation to run. It has
        // nowhere useful to put its result, and the controls it would write to belong to
        // a window that is gone.
        if (_closed)
        {
            return;
        }

        ResponsePane.Text = text;
        ResponsePane.ScrollToHome();
    }
}
