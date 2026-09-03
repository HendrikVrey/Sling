using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Sling.App.Collections;
using Sling.Core.Parsing;

namespace Sling.App.Editor;

/// <summary>
/// Colours the request pane: the <c>.http</c> document somebody is writing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not an AvalonEdit grammar, and the reason is structural rather than a preference.</b>
/// A <c>.xshd</c> definition is a set of regular expressions with a span stack, and this
/// format cannot be read that way: whether a line is a header or body text depends on a
/// blank line arbitrarily far above it. A grammar would paint the first line of every JSON
/// body as a header, in the pane where somebody is looking for the mistake in their request.
/// So the classification is <see cref="HttpLineClassifier"/>'s, which walks the document the
/// way the parser does, and this class is only the part that turns kinds into colours.
/// </para>
/// <para>
/// <b>Nothing in Etch could have supplied this.</b> Etch.Core recolours grammars it does not
/// own, which is what the response pane needs and what
/// <see cref="ThemedHighlightingColorizer"/> does; <c>.http</c> is Sling's format and Etch
/// has no business knowing it. What is reused is the part worth reusing - the seeds and the
/// legibility clamp, through <see cref="RequestPalette"/>.
/// </para>
/// <para>
/// <b>The whole view is redrawn on every edit, and it has to be.</b> AvalonEdit repaints
/// only the lines an edit touched, but one character can change what every line below it
/// means - typing a blank line after a header turns the rest of the request into a body.
/// Repainting the changed line alone leaves the document drawn as it used to be.
/// </para>
/// </remarks>
internal sealed class RequestSyntax : DocumentColorizingTransformer, IDisposable
{
    /// <summary>
    /// The document size past which the pane is left uncoloured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately a quarter of the window's <c>MaxLiveRefreshLength</c>, and reusing that
    /// number would have been the mistake.</b> The rail and the send-target label stop at
    /// 256 KiB, but their work is behind a 400 ms debounce, so a big document costs them one
    /// parse after somebody stops typing. This runs on every keystroke by construction: a
    /// highlighter that showed the document as it was 400 ms ago would be showing the wrong
    /// colours for the character being typed, which is the one thing it exists to get right.
    /// The same ceiling for undebounced work is not the same trade.
    /// </para>
    /// <para>
    /// Past it the pane is plain text, which is what every other editor does with a file this
    /// size and is a better answer than a window that thinks between keystrokes.
    /// </para>
    /// </remarks>
    internal const int DefaultMaximumLength = 64 * 1024;

    private readonly TextEditor _editor;
    private readonly IReadOnlyDictionary<HttpTokenKind, Brush> _brushes;
    private readonly IReadOnlyDictionary<string, Brush> _methods;
    private readonly int _maximumLength;

    private IReadOnlyList<IReadOnlyList<HttpToken>> _tokens = [];
    private ITextSourceVersion? _version;
    private bool _installed;
    private bool _disposed;

    /// <param name="editor">The pane to colour.</param>
    /// <param name="resources">
    /// The application's resource dictionary, which the palette reads the page and text
    /// colours from. Null in a test.
    /// </param>
    /// <param name="methods">
    /// The verb brushes, passed in rather than built here so that the document and the
    /// collections rail draw a <c>DELETE</c> in the same red. Two builds of the same table
    /// would agree today and be a puzzle the day one of them is adjusted.
    /// </param>
    /// <param name="maximumLength">
    /// The document size past which the pane is left uncoloured, normally
    /// <see cref="DefaultMaximumLength"/>. A parameter rather than a constant read directly
    /// so a test can set a small one.
    /// </param>
    internal RequestSyntax(
        TextEditor editor,
        ResourceDictionary? resources,
        IReadOnlyDictionary<string, Brush> methods,
        int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(methods);

        // Checked here rather than trusted. MethodPalette.For indexes the neutral entry
        // unconditionally for a verb it does not know, and a KeyNotFoundException from inside
        // ColorizeLine is thrown during a paint - which in WPF closes the application rather
        // than drawing the wrong colour.
        if (!methods.ContainsKey(string.Empty))
        {
            throw new ArgumentException(
                "The verb table has no neutral entry under the empty key, which is what an "
                    + "extension method falls back to.",
                nameof(methods));
        }

        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _brushes = RequestPalette.Build(resources);
        _methods = methods;
        _maximumLength = maximumLength;
    }

    /// <summary>Starts colouring, and keeps the view in step with the document.</summary>
    internal void Install()
    {
        if (_installed || _disposed)
        {
            return;
        }

        _editor.TextArea.TextView.LineTransformers.Add(this);
        _editor.TextChanged += OnTextChanged;
        _installed = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_installed)
        {
            return;
        }

        _editor.TextChanged -= OnTextChanged;
        _editor.TextArea.TextView.LineTransformers.Remove(this);
        _installed = false;
    }

    /// <inheritdoc/>
    protected override void ColorizeLine(DocumentLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var lines = Classification();
        var index = line.LineNumber - 1;

        if (index < 0 || index >= lines.Count)
        {
            return;
        }

        foreach (var token in lines[index])
        {
            // Clamped although the version check above should make it unnecessary. A run
            // drawn past the end of a line is an exception during a paint, which in WPF is
            // an application that closes rather than a colour that looks wrong.
            if (token.Start >= line.Length)
            {
                continue;
            }

            var end = Math.Min(token.Start + token.Length, line.Length);
            var brush = BrushFor(token, line);
            var bold = RequestPalette.IsBold(token.Kind);

            if (brush is null && !bold)
            {
                continue;
            }

            ChangeLinePart(
                line.Offset + token.Start,
                line.Offset + end,
                element => Apply(element, brush, bold));
        }
    }

    private static void Apply(VisualLineElement element, Brush? brush, bool bold)
    {
        if (brush is not null)
        {
            element.TextRunProperties.SetForegroundBrush(brush);
        }

        if (!bold)
        {
            return;
        }

        var typeface = element.TextRunProperties.Typeface;

        element.TextRunProperties.SetTypeface(new Typeface(
            typeface.FontFamily,
            typeface.Style,
            FontWeights.Bold,
            typeface.Stretch));
    }

    /// <summary>
    /// The brush for one token, which for a verb depends on which verb it is.
    /// </summary>
    /// <remarks>
    /// The verb's colour comes from <see cref="MethodPalette"/> so that the editor and the
    /// collections rail agree about what a <c>DELETE</c> looks like. Two implementations of
    /// that would drift, and a tree saying one thing while the document a few centimetres
    /// away says another is a difference nobody can explain.
    /// </remarks>
    private Brush? BrushFor(HttpToken token, DocumentLine line)
    {
        if (token.Kind != HttpTokenKind.Method)
        {
            return _brushes.GetValueOrDefault(token.Kind);
        }

        var verb = CurrentContext.Document
            .GetText(line.Offset + token.Start, Math.Min(token.Length, line.Length - token.Start))
            .ToUpperInvariant();

        return MethodPalette.For(_methods, verb);
    }

    /// <summary>
    /// The current document's classification, recomputed only when the document has moved.
    /// </summary>
    /// <remarks>
    /// Keyed on the document's version rather than on a dirty flag. A version is the
    /// document's own statement about whether it has changed, and it cannot be left set by
    /// a path that forgot to clear it.
    /// </remarks>
    private IReadOnlyList<IReadOnlyList<HttpToken>> Classification()
    {
        var document = CurrentContext.Document;
        var version = document.Version;

        if (_version is not null
            && version is not null
            && version.BelongsToSameDocumentAs(_version)
            && version.CompareAge(_version) == 0)
        {
            return _tokens;
        }

        _version = version;

        // The same ceiling the rail and the send-target label honour. Past it the document
        // is not something to walk on the dispatcher between keystrokes, and no colour is a
        // better answer than a window that stops responding while somebody types.
        if (document.TextLength > _maximumLength)
        {
            _tokens = [];
            return _tokens;
        }

        var lines = new List<string>(document.LineCount);

        foreach (var line in document.Lines)
        {
            lines.Add(document.GetText(line));
        }

        _tokens = HttpLineClassifier.Classify(lines);

        return _tokens;
    }

    /// <remarks>
    /// A whole-view redraw rather than the changed lines, because an edit on one line can
    /// change the meaning of every line under it. See the note on the class.
    /// <para>
    /// Above the ceiling there is nothing to redraw <em>for</em>: every line would be
    /// classified as empty. Skipping it there leaves AvalonEdit's own partial invalidation
    /// alone, which is the behaviour a large document should have had all along.
    /// </para>
    /// </remarks>
    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (_disposed || _editor.Document is not { } document || document.TextLength > _maximumLength)
        {
            return;
        }

        _editor.TextArea.TextView.Redraw();
    }
}
