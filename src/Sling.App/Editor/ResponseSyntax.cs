using System.Windows.Media;
using Etch.Core.Documents;
using Etch.Core.Text;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Sling.App.Editor;

/// <summary>
/// Owns the response pane's highlighting and folding, and the size rules that switch them
/// off.
/// </summary>
/// <remarks>
/// <para>
/// This is where a response stops being a viewport and becomes a buffer. The pane holds
/// the body and nothing else — the request line, the status and the headers live beside
/// it — which is what lets a whole-document grammar and a whole-document fold scan be
/// correct rather than approximately correct.
/// </para>
/// <para>
/// <b>Simpler than Etch's equivalent, and for one structural reason: the editor's
/// <see cref="TextDocument"/> is never replaced.</b> Etch swaps documents on every tab
/// switch, which is what forces it to tear the fold manager down first —
/// <c>FoldingManager</c> binds to the document at <c>Install</c>, and uninstalling after a
/// swap dereferences a height tree whose root the swap has already nulled. Sling has one
/// pane and assigns text into the same document, so that hazard does not exist here. It is
/// written down because the day someone adds a second response document, it comes back.
/// </para>
/// <para>
/// Nothing here runs at startup. <c>HighlightingManager</c> is not touched until a
/// response with a recognised language arrives, which matters more than it looks: in a
/// <c>Debug</c> build AvalonEdit parses <em>all</em> its grammars the first time that
/// singleton is read.
/// </para>
/// </remarks>
internal sealed class ResponseSyntax : IDisposable
{
    /// <summary>
    /// Grammar names as AvalonEdit registers them.
    /// </summary>
    /// <remarks>
    /// By name, never by extension. <c>GetDefinitionByExtension(".md")</c> answers
    /// <c>MarkDownWithFontSize</c>, because <c>.md</c> is registered twice and the later
    /// registration wins — and that variant scales heading text, which in a fixed-width
    /// pane looks like a rendering fault.
    /// </remarks>
    private static readonly Dictionary<SyntaxLanguage, string> GrammarNames = new()
    {
        [SyntaxLanguage.Json] = "Json",
        [SyntaxLanguage.Xml] = "XML",
        [SyntaxLanguage.Html] = "HTML",
        [SyntaxLanguage.Css] = "CSS",
        [SyntaxLanguage.JavaScript] = "JavaScript",
        [SyntaxLanguage.Markdown] = "MarkDown",
    };

    /// <summary>
    /// Languages <see cref="BraceFolding"/> is actually correct for.
    /// </summary>
    /// <remarks>
    /// A closed list, matching the lexical model <c>BraceFolding</c> documents: quoted
    /// strings with backslash escapes, <c>//</c> to end of line, <c>/* */</c> across
    /// lines. CSS is deliberately absent although it uses braces — it has no <c>//</c>
    /// comment, so the <c>//</c> in <c>url(http://…)</c> would hide the rest of that line
    /// including a closing brace, producing a fold over the wrong region. It still gets
    /// highlighting; it gets no fold margin, which is the honest answer.
    /// </remarks>
    private static readonly HashSet<SyntaxLanguage> BraceFolded =
    [
        SyntaxLanguage.Json,
        SyntaxLanguage.JavaScript,
    ];

    private readonly TextEditor _editor;
    private readonly Color _page;

    private ThemedHighlightingColorizer? _colorizer;
    private FoldingManager? _folding;

    private SyntaxLanguage _language = SyntaxLanguage.None;
    private bool _disposed;

    internal ResponseSyntax(TextEditor editor, Color page)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _page = page;
    }

    /// <summary>
    /// Raised on the UI thread when a background fold scan throws.
    /// </summary>
    /// <remarks>
    /// A fold scan failing is not fatal — the margin simply stops updating — but it must
    /// not be invisible. The alternative is a feature that quietly stops working and an
    /// exception that reappears minutes later at garbage collection, attached to nothing.
    /// </remarks>
    internal event Action<Exception>? Faulted;

    /// <summary>The language currently applied, for the status bar and for tests.</summary>
    internal SyntaxLanguage Language => _language;

    /// <summary>Whether a fold margin is currently installed.</summary>
    internal bool IsFolding => _folding is not null;

    /// <summary>
    /// Applies the highlighting and folding this body should have.
    /// </summary>
    /// <param name="language">The language chosen by <see cref="BodyLanguage"/>.</param>
    /// <param name="capabilities">What the body's size permits.</param>
    internal void Apply(SyntaxLanguage language, DocumentCapabilities capabilities)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The size rule collapses onto the language: a body too large to highlight is a
        // body with no language, so there is one path through the rest of this rather than
        // a size check beside every step.
        var effective = capabilities.SyntaxHighlighting ? language : SyntaxLanguage.None;

        if (effective != _language)
        {
            _language = effective;
            InstallHighlighting(effective);
        }

        var wantsFolding = capabilities.Folding && BraceFolded.Contains(effective);

        SetFolding(wantsFolding);

        // Unconditional when folding is wanted, because this is called once per response
        // and the text is new every time — unlike Etch, where the same method runs on a
        // debounce while somebody types and a scan per keystroke would be the cost the
        // check exists to avoid.
        if (wantsFolding)
        {
            RefreshFoldings();
        }
    }

    /// <summary>
    /// Recomputes fold regions from the current text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scan runs on the thread pool over an immutable snapshot. It matters here: a
    /// response body is capped at sixteen mebibytes, and materialising one is a
    /// thirty-two-megabyte allocation on the large object heap. Doing that on the
    /// dispatcher would be a visible freeze immediately after a send — the worst possible
    /// moment, because it looks like the request is still running.
    /// </para>
    /// <para>
    /// The result is discarded unless the manager and the document <em>version</em> are
    /// both still the ones scanned. Two scans can overlap and nothing makes them finish in
    /// order; fold offsets applied to a document that has moved on address the wrong text,
    /// which shows up as a fold margin pointing at nothing rather than as an error.
    /// </para>
    /// </remarks>
    internal void RefreshFoldings()
    {
        if (_folding is not { } manager || _editor.Document is not { } document)
        {
            return;
        }

        // Taken on the UI thread, which owns the document; everything after this point
        // works on the immutable snapshot and is safe anywhere.
        var snapshot = document.CreateSnapshot();

        _ = Task.Run(() => BraceFolding.Scan(snapshot.Text)).ContinueWith(
            task =>
            {
                if (task.IsFaulted)
                {
                    // Read, not just tested. Checking IsFaulted does not *observe* the
                    // exception, so it would resurface at GC time as an
                    // UnobservedTaskException with no connection to the fold margin that
                    // silently stopped updating. Observed here and reported once, where
                    // it can be read beside the buffer it is about.
                    Faulted?.Invoke(task.Exception!.GetBaseException());
                    return;
                }

                if (_disposed || !ReferenceEquals(_folding, manager))
                {
                    return;
                }

                if (_editor.Document is not { } current
                    || snapshot.Version is not { } scanned
                    || !scanned.BelongsToSameDocumentAs(current.Version)
                    || scanned.CompareAge(current.Version) != 0)
                {
                    return;
                }

                manager.UpdateFoldings(
                    task.Result.Select(static region =>
                        new NewFolding(region.StartOffset, region.EndOffset) { Name = region.Label }),
                    firstErrorOffset: -1);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        InstallHighlighting(SyntaxLanguage.None);
        SetFolding(wanted: false);
    }

    private void InstallHighlighting(SyntaxLanguage language)
    {
        if (_colorizer is not null)
        {
            _editor.TextArea.TextView.LineTransformers.Remove(_colorizer);
            _colorizer = null;
        }

        if (Grammar(language) is not { } definition)
        {
            return;
        }

        _colorizer = new ThemedHighlightingColorizer(definition, _page);

        // Index 0, matching what TextEditor.OnSyntaxHighlightingChanged does with the
        // colorizer it would have installed.
        //
        // TextEditor.SyntaxHighlighting stays null throughout: setting it would install a
        // second colorizer, in the grammar's own light-theme colours, on top of this one.
        _editor.TextArea.TextView.LineTransformers.Insert(0, _colorizer);
    }

    /// <summary>Puts the fold margin in or takes it out. Idempotent.</summary>
    private void SetFolding(bool wanted)
    {
        if (wanted == (_folding is not null))
        {
            return;
        }

        if (wanted)
        {
            _folding = FoldingManager.Install(_editor.TextArea);
            return;
        }

        FoldingManager.Uninstall(_folding!);
        _folding = null;
    }

    /// <summary>
    /// The grammar for a language, or null when there is none to apply.
    /// </summary>
    /// <remarks>
    /// A missing grammar is not an error worth reporting. It means this build of AvalonEdit
    /// does not carry that definition, and the honest response is an unhighlighted body
    /// rather than a message about a library the user did not choose.
    /// </remarks>
    private static IHighlightingDefinition? Grammar(SyntaxLanguage language) =>
        GrammarNames.TryGetValue(language, out var name)
            ? HighlightingManager.Instance.GetDefinition(name)
            : null;
}
