using System.Windows.Controls;
using Etch.Core.Abstractions;
using Sling.App.Editor;
using Sling.Core.Auth;

namespace Sling.App;

/// <summary>
/// Noticing a JWT in a response, and offering to decode it.
/// </summary>
/// <remarks>
/// <para>
/// The decoder has been here since M2 and reaching it needed you to know it was there:
/// select the token, right-click, find it among the whole transform catalogue. Nothing
/// volunteered that the wall of base64 in front of you was a token at all.
/// </para>
/// <para>
/// <b>Nothing here says "valid".</b> The decoder puts <em>signature not verified</em> on its
/// own first line because the people reading a decoded token are the people who will act on
/// it, and this row is held to the same rule: it offers to show what is inside, and says
/// nothing about whether anyone should believe it.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// The transform that decodes one, found by id.
    /// </summary>
    /// <remarks>
    /// By id rather than by name: a display name is text somebody may reword, and a row that
    /// silently stops appearing when it is reworded is a row nobody notices has gone.
    /// </remarks>
    private static ITransform? JwtDecoder =>
        BodyTransforms.All.FirstOrDefault(
            t => t.Id.Contains("jwt", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds the decode row when the caret is on something that reads like a JWT.
    /// </summary>
    /// <remarks>
    /// Absent rather than disabled when it is not. A permanently greyed row says the feature
    /// is broken; an absent one says it does not apply here, which is what is true.
    /// </remarks>
    private void AddJwtItem(ContextMenu menu)
    {
        if (JwtDecoder is not { } decoder)
        {
            return;
        }

        var text = ResponsePane.Text;

        if (!Jwt.TryFindAt(text, ResponsePane.TextArea.Caret.Offset, out var start, out var length))
        {
            return;
        }

        var item = new MenuItem
        {
            Header = "Decode this token",
            ToolTip = Jwt.TryReadExpiry(text.Substring(start, length), out var expires)
                ? $"A JWT. Its 'exp' claim says {expires.ToLocalTime():yyyy-MM-dd HH:mm}. "
                    + "Decoding shows the claims and checks no signature."
                : "A JWT with no 'exp' claim. Decoding shows the claims and checks no signature.",
        };

        item.Click += (_, _) =>
        {
            // Selected first, because a transform applies to the selection when there is one
            // and to the whole buffer otherwise - and decoding the whole body is not what
            // pointing at one string in it means.
            ResponsePane.Select(start, length);
            _ = RunTransformAsync(decoder);
        };

        menu.Items.Add(item);
        menu.Items.Add(new Separator());
    }
}
