using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sling.Core.Json;
using Sling.Core.Parsing;
using Sling.Persistence.Workspaces;

namespace Sling.App;

/// <summary>
/// Building a chain reference by pointing at the value in the response.
/// </summary>
/// <remarks>
/// <para>
/// Chaining is the mechanism that makes "log in, take the token, use it in every request
/// after" work without a scripting runtime, and the part of it nobody remembers is the
/// JSONPath - typed by hand, from a body in the other pane, against a request whose
/// <c># @name</c> has to match. This turns all three into a right-click.
/// </para>
/// <para>
/// <b>It writes nothing on its own.</b> The reference goes to the clipboard, and the one
/// case where it does edit the document - naming a request that has none - is asked for
/// explicitly and says which request it is about to name. A reference against an unnamed
/// request is the commonest way this fails, so declining to mention it would leave the
/// feature broken in exactly its most common case.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private void InstallChainAffordance() =>
        ResponsePane.TextArea.PreviewMouseRightButtonDown += OnResponseRightButtonDown;

    private void RemoveChainAffordance() =>
        ResponsePane.TextArea.PreviewMouseRightButtonDown -= OnResponseRightButtonDown;

    /// <summary>
    /// Moves the caret to where the right button went down, before the menu opens.
    /// </summary>
    /// <remarks>
    /// AvalonEdit moves the caret on the left button only, so without this the menu would
    /// answer about wherever the caret happened to be last - which is the same defect the
    /// collections rail shipped, where every context command resolved its target from the
    /// last thing that had been left-clicked.
    /// </remarks>
    private void OnResponseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        var view = ResponsePane.TextArea.TextView;
        var point = e.GetPosition(view) + view.ScrollOffset;

        // Null when the click is below the last line or right of it. Leaving the caret alone
        // is the right answer there: there is nothing under the pointer to be about.
        if (view.GetPosition(point) is { } position)
        {
            ResponsePane.TextArea.Caret.Position = position;
        }
    }

    /// <summary>
    /// Adds the chain row to the response menu, when there is something to chain.
    /// </summary>
    /// <remarks>
    /// Nothing is added when the body is not JSON, or when the caret is between values
    /// rather than on one. A permanently disabled row would say the feature is broken; an
    /// absent one says it does not apply here, which is true.
    /// </remarks>
    private void AddChainItem(ContextMenu menu)
    {
        if (SelectedExchange() is not { } exchange
            || !JsonPathLocator.TryLocate(ResponsePane.Text, ResponsePane.TextArea.Caret.Offset, out var path))
        {
            return;
        }

        var named = exchange.Request.Name;

        var item = new MenuItem
        {
            Header = named is { Length: > 0 }
                ? "Copy as chain reference"
                : "Name this request, and copy the reference",
            ToolTip = named is { Length: > 0 }
                ? $"{{{{{named}.response.body.{path}}}}}"
                : "A reference has to name the request it reads, and this one has no '# @name'.",
        };

        item.Click += (_, _) => RunGuarded(() => CopyChainReferenceAsync(named, path));

        menu.Items.Add(item);
        menu.Items.Add(new Separator());
    }

    /// <summary>Puts the reference on the clipboard, naming the request first if it has none.</summary>
    private async Task CopyChainReferenceAsync(string? named, string path)
    {
        var name = named;

        if (string.IsNullOrEmpty(name))
        {
            if (await NameRequestUnderCaretAsync().ConfigureAwait(true) is not { } given)
            {
                return;
            }

            name = given;
        }

        var reference = $"{{{{{name}.response.body.{path}}}}}";

        try
        {
            Clipboard.SetText(reference);
        }
        catch (ExternalException ex)
        {
            // Another application is holding the clipboard open. Nothing to do about it
            // except say so, and say what the reference was, so it can still be typed.
            StatusLeft.Text = $"{reference} - it could not be copied ({ex.Message}).";
            return;
        }

        StatusLeft.Text = $"Copied {reference}.";
    }

    /// <summary>
    /// Adds a <c># @name</c> to the request the caret is in, and answers with the name.
    /// </summary>
    /// <remarks>
    /// The request under the caret, and the prompt says which one that is. It is the request
    /// that was just sent in every ordinary case, and guessing silently about which request a
    /// reference is written against is precisely the mistake this feature exists to remove.
    /// </remarks>
    private async Task<string?> NameRequestUnderCaretAsync()
    {
        var text = RequestPane.Text;
        var document = RequestDocumentParser.Parse(text);
        var block = document.BlockAtLine(RequestPane.TextArea.Caret.Line);

        if (block is null)
        {
            StatusLeft.Text = "Put the caret in the request whose response this is, and try again.";
            return null;
        }

        if (block.Name is { Length: > 0 } already)
        {
            return already;
        }

        var name = await PromptForNameAsync(
            "Name this request",
            $"Writes '# @name' above {block.Method} {block.Target}, so other requests can read its response.",
            ValidateRequestName).ConfigureAwait(true);

        if (name is null)
        {
            return null;
        }

        // Above the request line rather than at the top of the block: '###' and the comments
        // over it are the user's, and a directive Sling inserts belongs immediately over the
        // line it is about.
        var offset = RequestPane.Document.GetLineByNumber(block.StartLine).Offset;
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        RequestPane.Document.Insert(offset, "# @name " + name + newLine);

        RefreshOpenDocumentRequests();
        UpdateSendTarget(reparse: true);

        return name;
    }

    /// <summary>
    /// Checks a request name.
    /// </summary>
    /// <remarks>
    /// The same segment rule collections and files are held to, and for a sharper reason
    /// here: this name is written back into the document as a directive, so a name holding a
    /// newline or a <c>#</c> would be a second directive rather than a name. The whitelist has
    /// none of them, so the injection is impossible by construction rather than by escaping.
    /// </remarks>
    private string? ValidateRequestName(string typed)
    {
        if (!WorkspaceNames.TryToSegment(typed, out var segment, out var reason))
        {
            return reason;
        }

        var document = RequestDocumentParser.Parse(RequestPane.Text);

        return document.BlockNamed(segment) is null
            ? null
            : $"Another request in this file is already named '{segment}'.";
    }
}
