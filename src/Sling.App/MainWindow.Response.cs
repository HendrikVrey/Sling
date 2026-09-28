using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Etch.Core.Abstractions;
using Etch.Core.Documents;
using ICSharpCode.AvalonEdit.Search;
using Sling.App.Editor;
using Sling.Core.Documents;
using Sling.Core.Rendering;
using Sling.Http;

namespace Sling.App;

/// <summary>
/// The response side of the window: what came back, which exchange is being looked at,
/// and the editor features that make the body a buffer rather than a viewport.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The size rules the response body is subject to.
    /// </summary>
    /// <remarks>
    /// <c>Etch.Core</c>'s shipped defaults - no highlighting above ten mebibytes, no
    /// folding above two - and they arrive for free with the package. This is the first
    /// concrete answer to <c>Sling.md</c> §8's "what happens to a response larger than
    /// memory": the transport already caps the body at sixteen mebibytes, and above these
    /// thresholds the editor stops doing per-character work on what is left. Neither is
    /// the full answer, and neither had to be written.
    /// </remarks>
    private static readonly DocumentSizePolicy SizePolicy = DocumentSizePolicy.Default;

    private readonly List<Exchange> _exchanges = [];

    /// <summary>Which exchange the pane is showing, or -1 when it holds a message.</summary>
    /// <remarks>
    /// The picker's own selection cannot answer this: it is collapsed when there is only one
    /// exchange, which is the overwhelmingly common case and the one anything reading the
    /// pane cares about most.
    /// </remarks>
    private int _boundExchange = -1;

    private ResponseSyntax? _syntax;

    private RequestSyntax? _requestSyntax;

    /// <summary>The find bars, one per pane, kept so the window's keymap can open them.</summary>
    private SearchPanel? _requestFind;

    private SearchPanel? _responseFind;

    /// <summary>Transform ids most recently applied, newest first.</summary>
    /// <remarks>
    /// In memory only, and deliberately so until M3 gives Sling somewhere to persist
    /// settings. It still earns its place within a session: the second time you decode a
    /// token, the transform is already at the top of the menu.
    /// </remarks>
    private IReadOnlyList<string> _recentTransformIds = [];

    /// <summary>What the body currently in the pane was detected as.</summary>
    private DetectionResult _detection = DetectionResult.PlainText;

    /// <summary>True while the picker is being repopulated, so its event is not acted on.</summary>
    private bool _rebuildingPicker;

    /// <summary>
    /// Everything needed to put the response pane back the way an inspector found it.
    /// </summary>
    /// <param name="Exchanges">A copy, because the live list is cleared by what comes next.</param>
    /// <param name="Bound">Which of them was in the buffer.</param>
    /// <param name="Message">The pane's text when it held a message rather than an exchange.</param>
    /// <param name="StatusLeft">The status bar, which describes the response as much as the pane does.</param>
    /// <param name="StatusRight">The verdict: status, time, size.</param>
    /// <param name="Listed">
    /// The diagnostics the pane was listing, if it was. Without it the rows come back
    /// rendered and inert - a list you can look at and not click, which reads as broken
    /// rather than as a list that has been put back.
    /// </param>
    private sealed record PaneState(
        IReadOnlyList<Exchange> Exchanges,
        int Bound,
        string Message,
        string StatusLeft,
        string StatusRight,
        IReadOnlyList<ParseDiagnostic> Listed);

    /// <summary>What the pane was showing before an inspector borrowed it, or null.</summary>
    private PaneState? _inspectorReturn;

    /// <summary>Which inspector is up, so its own chord can put it away again.</summary>
    private string? _inspectorTitle;

    /// <summary>
    /// True while <see cref="SetBody"/> is assigning, so its own edit does not come back
    /// round as a change to re-analyse from scratch.
    /// </summary>
    private bool _settingBody;

    /// <summary>
    /// Wires the response pane's editor features. Called once, from the constructor.
    /// </summary>
    private void InitializeResponseView()
    {
        DisableHyperlinks(RequestPane);
        DisableHyperlinks(ResponsePane);

        _syntax = new ResponseSyntax(ResponsePane, SyntaxPalette.Page(Application.Current?.Resources));
        _syntax.Faulted += error => StatusLeft.Text = $"Folding stopped: {error.GetType().Name}: {error.Message}";

        // Every change to the buffer re-reads it, whatever caused the change. Handling
        // only the transform path left Ctrl+Z showing a base64 body still highlighted as
        // JSON, with the menu still offering JSON transforms - the undo had put the buffer
        // back and nothing had told the pane. One subscription covers transforms, undo and
        // redo alike; SetBody suppresses it because it has the Content-Type and can do
        // better than a sniff.
        ResponsePane.TextChanged += OnResponseTextChanged;

        // AvalonEdit's own find panel, on both panes. Ctrl+F, no replace - which is
        // exactly the right feature set for a pane you cannot type into, and a better
        // answer than building a find bar to match. The request pane gets it too: finding
        // a header in a long document is the same need.
        //
        // Both panels are kept, for two things installing them does not give. The chord is
        // resolved on the window's tunnelling pass with the rest of the keymap and needs
        // something to open (see ShowFind), and the match marker has to be themed: the
        // default is LightGreen, drawn as a block behind text that keeps this pane's
        // near-white foreground. The panel's own chrome is hardcoded to system colours and
        // cannot be reached from here at all; Theme/FindBar.xaml retemplates it.
        _responseFind = SearchPanel.Install(ResponsePane);
        _requestFind = SearchPanel.Install(RequestPane);

        var marker = FindPalette.Marker(Page, SyntaxPalette.Text(Application.Current?.Resources));

        _responseFind.MarkerBrush = marker;
        _requestFind.MarkerBrush = marker;

        // The request pane's own colours. Not a grammar, because this format cannot be
        // read by one: whether a line is a header or body text depends on a blank line
        // arbitrarily far above it. See RequestSyntax.
        //
        // It shares the rail's verb brushes rather than building a second set, so a DELETE
        // is the same red in the tree and in the document.
        _requestSyntax = new RequestSyntax(
            RequestPane,
            Application.Current?.Resources,
            MethodBrushes,
            RequestSyntax.DefaultMaximumLength);

        _requestSyntax.Install();

        InstallResponseContextMenu();
        InstallRequestContextMenu();
        InstallChainAffordance();
    }

    /// <summary>The exchange the pane is showing, or null when it holds a message.</summary>
    private Exchange? SelectedExchange() =>
        _boundExchange >= 0 && _boundExchange < _exchanges.Count ? _exchanges[_boundExchange] : null;

    /// <summary>Opens the find bar over whichever pane the user is working in.</summary>
    /// <remarks>
    /// <para>
    /// <b>The chord belongs to the window, not to the editor.</b> AvalonEdit binds
    /// <c>Ctrl+F</c> through <c>ApplicationCommands.Find</c> on the <c>TextArea</c>, so it
    /// only fires while the keyboard focus is inside one of the two editors: click a folder
    /// in the collections rail, or any button on the command bar, and the key did nothing at
    /// all. Every other chord in Sling is resolved on the window's tunnelling pass and works
    /// from anywhere, and a keymap where one entry silently depends on where you last
    /// clicked is worse than one without it.
    /// </para>
    /// <para>
    /// The two behaviours below are AvalonEdit's own, reproduced rather than inherited:
    /// taking the chord onto the window means its <c>ExecuteFind</c> no longer runs, and
    /// dropping either would be a regression nobody asked for.
    /// </para>
    /// </remarks>
    private void ShowFind()
    {
        // The bar's own focus counts as the response side. It is an adorner beside the
        // TextArea rather than inside it, so once the caret is in the search box the editor
        // no longer reports the focus - and a second Ctrl+F would otherwise jump to the
        // other pane while the user is looking at this one.
        var onResponse = ResponsePane.TextArea.IsKeyboardFocusWithin
            || _responseFind?.IsKeyboardFocusWithin == true;

        // Anywhere else - the rail, the command bar, nothing focused at all - means the
        // request document, which is the one being written.
        var editor = onResponse ? ResponsePane : RequestPane;
        var panel = onResponse ? _responseFind : _requestFind;

        if (panel is null)
        {
            return;
        }

        panel.Open();

        // A selection on one line is almost always the thing about to be searched for.
        var selection = editor.TextArea.Selection;

        if (!selection.IsEmpty && !selection.IsMultiline)
        {
            panel.SearchPattern = selection.GetText();
        }

        // Dispatched rather than called. Open() hands the bar to an adorner layer and its
        // template has not been applied yet, so the search box Reactivate would focus does
        // not exist until the next layout pass - calling it here leaves the caret in the
        // document and the first thing typed goes into the buffer instead of the search box.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, panel.Reactivate);
    }

    /// <summary>
    /// Turns off AvalonEdit's automatic hyperlink rendering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A security decision before it is a visual one, and it is on by default.</b>
    /// AvalonEdit finds anything URL-shaped in a buffer and makes it clickable. In the
    /// response pane, every URL in the buffer came from a server the user does not control
    /// - a body full of attacker-chosen links, one ctrl-click from a browser, in a tool
    /// whose whole promise is that a response body never reaches something that can act on
    /// it (<c>Sling.md</c> §5.5). The request pane is turned off for consistency: an
    /// accidental navigation while editing a URL is nobody's intent either.
    /// </para>
    /// <para>
    /// It is also unreadable, which is how it was noticed. Link text is drawn in a fixed
    /// blue by a built-in element generator that never passes through
    /// <see cref="ThemedHighlightingColorizer"/>, so the legibility floor the palette
    /// guarantees does not apply to it - on a dark pane a JSON body of URLs rendered as
    /// dark blue underlined runs. A colour that bypasses the palette is a colour nothing
    /// can promise anything about.
    /// </para>
    /// </remarks>
    private static void DisableHyperlinks(ICSharpCode.AvalonEdit.TextEditor editor) =>
        editor.Options.EnableHyperlinks = editor.Options.EnableEmailHyperlinks = false;

    /// <summary>
    /// Shows everything a completed run produced, selecting the last exchange.
    /// </summary>
    /// <remarks>
    /// The last, not the first: the chain exists to satisfy the request the user actually
    /// asked for, and that is the one at the end. The earlier ones are visible in the
    /// picker rather than in the buffer.
    /// </remarks>
    private void ShowExchanges(IReadOnlyList<Exchange> exchanges)
    {
        _exchanges.Clear();
        _exchanges.AddRange(exchanges);

        RebuildExchangePicker();

        if (_exchanges.Count == 0)
        {
            return;
        }

        BindExchange(_exchanges.Count - 1);
    }

    private void RebuildExchangePicker()
    {
        _rebuildingPicker = true;

        try
        {
            ExchangePicker.Items.Clear();

            // One exchange is the overwhelmingly common case and needs no chooser; the
            // request line below already names it.
            if (_exchanges.Count < 2)
            {
                ExchangePicker.Visibility = Visibility.Collapsed;
                return;
            }

            for (var i = 0; i < _exchanges.Count; i++)
            {
                ExchangePicker.Items.Add(ResponseRenderer.DescribeExchange(
                    i + 1,
                    _exchanges[i].Request,
                    _exchanges[i].Response,
                    _exchanges[i].Role));
            }

            ExchangePicker.SelectedIndex = _exchanges.Count - 1;
            ExchangePicker.Visibility = Visibility.Visible;
        }
        finally
        {
            _rebuildingPicker = false;
        }
    }

    private void OnExchangeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingPicker || ExchangePicker.SelectedIndex < 0)
        {
            return;
        }

        BindExchange(ExchangePicker.SelectedIndex);
    }

    /// <summary>
    /// Puts one exchange into the pane: its request line, its headers, and its body as a
    /// highlighted, foldable buffer.
    /// </summary>
    private void BindExchange(int index)
    {
        if (index < 0 || index >= _exchanges.Count)
        {
            return;
        }

        _boundExchange = index;

        // A response body is not a list of rows, whatever was in the pane before it.
        ForgetListedDiagnostics();

        var (request, response) = (_exchanges[index].Request, _exchanges[index].Response);

        RequestLine.Text = ResponseRenderer.RenderRequestLine(request, response);
        RequestLine.Visibility = Visibility.Visible;

        HeadersText.Text = ResponseRenderer.RenderHeaders(response);
        HeadersExpander.Header = $"Headers ({response.Headers.Count.ToString(CultureInfo.InvariantCulture)})";
        HeadersExpander.Visibility = response.Headers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var body = ResponseRenderer.RenderBody(response);

        // "(no body)" is Sling's own words, not the server's. Detecting it, colouring it,
        // or offering a transform for it would be describing a sentence this application
        // wrote.
        var analysis = ResponseRenderer.IsPlaceholderBody(response)
            ? BodyAnalysis.None
            : BodyLanguage.Analyse(response.Header("Content-Type"), body);

        SetBody(body, analysis);

        ShowStatusPill(response);

        StatusLeft.Text = $"{request.Method} {request.Url}";
        StatusRight.Text = ResponseRenderer.Summarize(response);
    }

    /// <summary>
    /// Shows a message rather than a response: a parse error, a cancellation, a failure.
    /// </summary>
    /// <remarks>
    /// The head and the picker are hidden, and the buffer gets no language. A diagnostic
    /// is prose, and highlighting it as whatever the previous response happened to be
    /// would be actively misleading.
    /// <para>
    /// This one really does replace the response, which is why it drops anything an
    /// inspector was holding: every caller of it is answering the question the user just
    /// asked, and the previous response is not the answer to it.
    /// </para>
    /// </remarks>
    private void ShowMessage(string text)
    {
        if (_closed)
        {
            return;
        }

        ClearInspector();
        ShowText(text);
    }

    /// <summary>Puts text in the pane and takes down everything that describes a response.</summary>
    /// <remarks>
    /// The half <see cref="ShowMessage"/> and <see cref="ShowInspector"/> share. Separate
    /// from both so that lending the pane out and replacing what is in it cannot be confused
    /// for one another - they differ in exactly one thing, whether the response survives.
    /// </remarks>
    private void ShowText(string text)
    {
        // A send in flight when the window closes still has a continuation to run. It has
        // nowhere useful to put its result, and the controls it would write to belong to a
        // window that is gone.
        if (_closed)
        {
            return;
        }

        _exchanges.Clear();
        _boundExchange = -1;
        RebuildExchangePicker();

        // Whatever goes in next is not the list of diagnostics that was there, so a
        // double-click in it must not be read as a click on a row. ShowDiagnostics records
        // the new listing straight after calling through here, which is the only path that
        // is allowed to.
        ForgetListedDiagnostics();

        RequestLine.Visibility = Visibility.Collapsed;
        HeadersExpander.Visibility = Visibility.Collapsed;
        HideStatusPill();

        SetBody(text, BodyAnalysis.None);
    }

    /// <summary>
    /// Lends the response pane to something that is not a response, keeping the response.
    /// </summary>
    /// <param name="title">
    /// What the pane is showing, in the header's place. A pane still headed <c>RESPONSE</c>
    /// while it holds a cookie jar is the window saying the wrong thing about itself.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The four inspectors used to destroy the response and there was no way back.</b>
    /// History, the cookie jar, an import report and the file as it is on disk all went
    /// through <see cref="ShowMessage"/>, which empties the exchange list - so checking what
    /// a cookie was called threw away the 401 that raised the question, and the only way to
    /// see it again was to send it again. Against an API that charges, or one that is not
    /// idempotent, that is worse than an inconvenience.
    /// </para>
    /// <para>
    /// <c>??=</c> rather than an assignment: opening a second inspector from behind the first
    /// must still come back to the <em>response</em>, not to the first inspector.
    /// </para>
    /// </remarks>
    private void ShowInspector(string title, string text)
    {
        if (_closed)
        {
            return;
        }

        _inspectorReturn ??= new PaneState(
            [.. _exchanges],
            _boundExchange,
            _boundExchange < 0 ? ResponsePane.Text : string.Empty,
            StatusLeft.Text,
            StatusRight.Text,
            _listedDiagnostics);

        _inspectorTitle = title;

        ShowText(text);

        ResponseLabel.Text = title.ToUpperInvariant();

        // Named for what comes back rather than for the act, and it says which of the two
        // things it is: "Back to response" over a pane that never held one would be a
        // promise the click cannot keep.
        InspectorChip.Content = _inspectorReturn.Exchanges.Count > 0 ? "Back to response" : "Back";
        InspectorChip.ToolTip = _inspectorReturn.Exchanges.Count > 0
            ? "Put the last response back in this pane. It was kept."
            : "Go back to what this pane was showing.";

        InspectorChip.Visibility = Visibility.Visible;
    }

    /// <summary>Puts the response back, exactly as it was.</summary>
    private void ReturnToResponse()
    {
        if (_closed || _inspectorReturn is not { } state)
        {
            return;
        }

        ClearInspector();

        if (state.Exchanges.Count == 0)
        {
            ShowText(state.Message);
        }
        else
        {
            _exchanges.Clear();
            _exchanges.AddRange(state.Exchanges);

            RebuildExchangePicker();

            // Clamped rather than trusted. The list came back from the snapshot, so the index
            // is in range by construction today - and BindExchange is the one method here
            // that would silently do nothing if it ever stopped being.
            BindExchange(Math.Clamp(state.Bound, 0, _exchanges.Count - 1));
        }

        // After the restore: BindExchange writes both of these from the exchange it binds,
        // and what the user was told before the inspector is the line that belongs here.
        StatusLeft.Text = state.StatusLeft;
        StatusRight.Text = state.StatusRight;

        // Last, because both branches above clear it. A listing that comes back as text and
        // not as rows is a list that has visibly stopped working.
        _listedDiagnostics = state.Listed;
    }

    /// <summary>Forgets a held response, for anything that legitimately replaces it.</summary>
    private void ClearInspector()
    {
        _inspectorReturn = null;
        _inspectorTitle = null;

        if (_closed)
        {
            return;
        }

        ResponseLabel.Text = "RESPONSE";
        InspectorChip.Visibility = Visibility.Collapsed;
        InspectorChip.Content = string.Empty;
        InspectorChip.ToolTip = null;
    }

    private void OnBackToResponseClicked(object sender, RoutedEventArgs e) => ReturnToResponse();

    /// <summary>Whether the pane is currently lent to the inspector called <paramref name="title"/>.</summary>
    /// <remarks>
    /// So the chord that opened one closes it again. Pressing <c>Ctrl+H</c> twice meaning
    /// "show me the history, no, put it back" is the reading anybody has of a key that opened
    /// a panel, and it costs one comparison.
    /// </remarks>
    private bool IsInspecting(string title) =>
        string.Equals(_inspectorTitle, title, StringComparison.Ordinal);

    /// <summary>
    /// Replaces the buffer's contents and applies the editor features it should have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The undo stack is cleared afterwards, deliberately. Assigning <c>Text</c> is an
    /// undoable edit, so without this a <c>Ctrl+Z</c> in a freshly-arrived response would
    /// restore the *previous* response - a body from a different request appearing under a
    /// request line that still names this one. Undo exists here to unwind transforms, and
    /// the arrival of a new response is where its history should start.
    /// </para>
    /// <para>
    /// Text is assigned into the existing document rather than swapping in a new one,
    /// which is what keeps the fold manager valid across responses. See
    /// <see cref="ResponseSyntax"/>.
    /// </para>
    /// </remarks>
    private void SetBody(string text, BodyAnalysis analysis)
    {
        _detection = analysis.Detection;
        _settingBody = true;

        try
        {
            ResponsePane.Text = text;
            ResponsePane.Document.UndoStack.ClearAll();
            ResponsePane.ScrollToHome();
        }
        finally
        {
            _settingBody = false;
        }

        // Byte count rather than character count: the thresholds are about memory, and a
        // UTF-16 length understates a body of CJK or emoji by half.
        var capabilities = SizePolicy.Evaluate(Encoding.UTF8.GetByteCount(text));

        _syntax?.Apply(analysis.Language, capabilities);
    }

    /// <summary>
    /// Any change to the buffer that <see cref="SetBody"/> did not make - a transform, an
    /// undo, a redo.
    /// </summary>
    private void OnResponseTextChanged(object? sender, EventArgs e)
    {
        if (_settingBody || _closed)
        {
            return;
        }

        ReanalyseBody();
    }

    /// <summary>
    /// Re-reads the buffer after a transform has rewritten it.
    /// </summary>
    /// <remarks>
    /// This is what makes transforms chain. A transform applies in place, so the buffer is
    /// now something else - base64 that became JSON - and the language and the next
    /// suggestion both have to be recomputed from what is actually there. Nothing in
    /// <see cref="Editor.BodyTransforms"/> knows about chaining; it falls out of applying
    /// to the buffer and then asking again.
    /// <para>
    /// The <c>Content-Type</c> is deliberately <b>not</b> consulted here. It described what
    /// the server sent, and after a transform that is no longer what the buffer holds,
    /// believing it would keep a decoded JSON payload highlighted as the
    /// <c>text/plain</c> it arrived as.
    /// </para>
    /// </remarks>
    private void ReanalyseBody()
    {
        // The buffer is no longer the text that was rendered, so a row's position in it no
        // longer names a diagnostic. A transform runs over whatever is in the pane, including
        // a diagnostics listing, and Ctrl+Z puts back something that is not it either.
        ForgetListedDiagnostics();

        var text = ResponsePane.Text;
        var analysis = BodyLanguage.Analyse(contentType: null, text);

        _detection = analysis.Detection;

        _syntax?.Apply(analysis.Language, SizePolicy.Evaluate(Encoding.UTF8.GetByteCount(text)));
    }
}
