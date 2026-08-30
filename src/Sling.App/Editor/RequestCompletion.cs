using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Sling.Core.Documents;

namespace Sling.App.Editor;

/// <summary>
/// What <c>Ctrl+Space</c> offers in the request pane.
/// </summary>
/// <remarks>
/// <para>
/// The dialect has to be memorised otherwise: six directive names for an auth block, the
/// rule that any of them without <c># @auth oauth2</c> above it is an error, and a JSONPath
/// typed by hand from a body two panes away. <b>A dialect nobody can remember is a dialect
/// people abandon for the tool that has a form</b>, and this is how a text-first tool
/// answers that without becoming one.
/// </para>
/// <para>
/// Everything offered is read from the document and the selected environment at the moment
/// the window opens. Nothing is indexed and nothing is stored, so the list cannot go stale
/// and there is no cache to invalidate when a file changes underneath it.
/// </para>
/// <para>
/// AvalonEdit ships the completion window, so this is wiring and a word list rather than an
/// invention.
/// </para>
/// </remarks>
internal static class RequestCompletion
{
    /// <summary>The directives the dialect understands, each with what it is for.</summary>
    /// <remarks>
    /// The auth block's six are the reason this exists, so they carry the rule that catches
    /// people out: without <c># @auth oauth2</c> above them they are an error rather than a
    /// comment that quietly does nothing.
    /// </remarks>
    private static readonly (string Name, string Description)[] Directives =
    [
        ("name", "Names this request, so a later one can read its response as {{name.response.body.$.field}}."),
        ("auth oauth2", "Opens a client-credentials block. The directives below only mean something under it."),
        ("token-url", "The token endpoint. Sling refuses to follow a redirect away from it."),
        ("client-id", "The client identifier."),
        ("client-secret", "The client secret. Reference it as {{client_secret}} and keep the value in the secrets file."),
        ("scope", "Space-separated scopes. Leave it out rather than writing it empty."),
        ("audience", "Which API the token is for. Auth0 and several others need it; most do not."),
        ("client-auth", "'basic' (the default) or 'body', for a server that will not accept HTTP Basic."),
    ];

    /// <summary>The verbs a request line can start with.</summary>
    private static readonly string[] Methods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE"];

    /// <summary>Header names worth offering, with the ones auth uses first.</summary>
    private static readonly string[] Headers =
    [
        "Authorization",
        "X-API-Key",
        "Accept",
        "Content-Type",
        "User-Agent",
        "Accept-Encoding",
        "Accept-Language",
        "Cache-Control",
        "Cookie",
        "If-Match",
        "If-None-Match",
        "Origin",
        "Referer",
    ];

    /// <summary>
    /// Builds the completion window for the caret's position, or null when there is nothing
    /// worth offering there.
    /// </summary>
    /// <param name="area">The editor's text area, which the window attaches to.</param>
    /// <param name="document">The document as it currently parses, for its names.</param>
    /// <param name="variables">Every variable the selected environment defines.</param>
    internal static CompletionWindow? Build(
        TextArea area,
        RequestDocument document,
        IReadOnlyList<string> variables)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(variables);

        var caret = area.Caret.Offset;
        var line = area.Document.GetLineByOffset(caret);
        var before = area.Document.GetText(line.Offset, caret - line.Offset);

        var items = Offer(before, document, variables, out var replaced);

        if (items.Count == 0)
        {
            return null;
        }

        var window = new CompletionWindow(area)
        {
            // The part already typed, so the list filters as more of it arrives and the
            // chosen item replaces what was there rather than being appended to it.
            StartOffset = caret - replaced,
            CloseAutomatically = true,
        };

        foreach (var item in items)
        {
            window.CompletionList.CompletionData.Add(item);
        }

        return window;
    }

    /// <summary>
    /// What to offer after <paramref name="before"/>, and how much of it the choice replaces.
    /// </summary>
    /// <remarks>
    /// Read from the text left of the caret rather than from the parse. The line being typed
    /// is half-written by definition, and a parse of half a line answers questions about a
    /// document that does not exist yet.
    /// </remarks>
    private static List<Item> Offer(
        string before,
        RequestDocument document,
        IReadOnlyList<string> variables,
        out int replaced)
    {
        // A reference wins over everything else, wherever it appears - in a URL, in a header
        // value, in a body. An unclosed '{{' left of the caret is unambiguous.
        var reference = before.LastIndexOf("{{", StringComparison.Ordinal);

        if (reference >= 0 && before.IndexOf("}}", reference, StringComparison.Ordinal) < 0)
        {
            replaced = before.Length - reference - 2;
            return References(document, variables);
        }

        var trimmed = before.TrimStart();
        var directive = trimmed.LastIndexOf('@');

        if (directive >= 0 && (trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal)))
        {
            replaced = trimmed.Length - directive - 1;

            return [.. Directives.Select(d => new Item(d.Name, d.Description, "directive"))];
        }

        // A header value: the name is settled and what follows is text. Offering header names
        // again there would be offering the thing that cannot come next.
        if (before.Contains(':', StringComparison.Ordinal))
        {
            replaced = 0;
            return [];
        }

        replaced = WordLength(before);

        return
        [
            .. Methods.Select(m => new Item(m, "A request line starts with a verb and a URL.", "verb")),
            .. Headers.Select(h => new Item(h + ": ", "A request header.", "header")),
        ];
    }

    /// <summary>
    /// Everything a <c>{{reference}}</c> could name.
    /// </summary>
    /// <remarks>
    /// Three sources, in the order they shadow each other at resolution time: the selected
    /// environment first, because it wins; then the document's own <c>@name = value</c>
    /// definitions; then a chain stub per named request, which is the part nobody remembers
    /// the shape of.
    /// </remarks>
    private static List<Item> References(RequestDocument document, IReadOnlyList<string> variables)
    {
        var items = new List<Item>();

        foreach (var variable in variables)
        {
            items.Add(new Item(variable, "From the selected environment.", "environment"));
        }

        foreach (var definition in document.Variables)
        {
            if (!variables.Contains(definition.Name, StringComparer.Ordinal))
            {
                items.Add(new Item(definition.Name, "Defined in this file.", "variable"));
            }
        }

        foreach (var request in document.Requests)
        {
            if (request.Name is not { Length: > 0 } name)
            {
                continue;
            }

            items.Add(new Item(
                name + ".response.body.$.",
                $"A value out of '{name}''s response body, by JSONPath.",
                "chain"));

            items.Add(new Item(
                name + ".response.headers.",
                $"A header out of '{name}''s response.",
                "chain"));
        }

        return items;
    }

    /// <summary>How much of a word sits immediately left of the caret.</summary>
    private static int WordLength(string before)
    {
        var length = 0;

        while (length < before.Length)
        {
            var c = before[^(length + 1)];

            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                break;
            }

            length++;
        }

        return length;
    }

    /// <summary>One row in the list.</summary>
    /// <remarks>
    /// <see cref="ICompletionData.Content"/> and <see cref="ICompletionData.Text"/> are
    /// deliberately different for a header: the row reads <c>Accept:</c> and inserting it
    /// also puts the space after the colon in, because a header with no space after the colon
    /// parses and looks wrong to everyone who reads the file afterwards.
    /// </remarks>
    private sealed class Item(string text, string description, string kind) : ICompletionData
    {
        public ImageSource? Image => null;

        public string Text { get; } = text;

        public object Content { get; } = text.TrimEnd();

        public object Description { get; } = description;

        /// <summary>
        /// Ordering within the list. Left flat on purpose: the groups are already added in
        /// the order they matter, and a priority that disagreed with that order would produce
        /// a list whose reason for its order is in two places.
        /// </summary>
        public double Priority => 0;

        /// <summary>What kind of thing this is, for anything that wants to group the list.</summary>
        public string Kind { get; } = kind;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            ArgumentNullException.ThrowIfNull(textArea);
            ArgumentNullException.ThrowIfNull(completionSegment);

            textArea.Document.Replace(completionSegment, Text);
        }
    }
}
