using System.Globalization;

namespace Sling.Core.Documents;

/// <summary>
/// What a request is called, wherever it is named.
/// </summary>
/// <remarks>
/// <para>
/// One home for the rule, because four surfaces name the same request at the same moment: a
/// row in the collections rail, the send target beside the toolbar buttons, a row in Quick
/// Open, and the exchange picker after it has run. The rail saying <c>login</c> while the
/// toolbar says the URL is a difference nobody can explain, and it is the kind that appears
/// the day a fifth surface is added rather than the day the rule is written.
/// </para>
/// <para>
/// In <c>Sling.Core</c> for the same reason <see cref="Rendering.ResponseRenderer"/> is: it
/// is a rule with a right answer, and a label built inline in a code-behind is a label
/// nothing can check.
/// </para>
/// </remarks>
public static class RequestNaming
{
    /// <summary>
    /// How long a label may be.
    /// </summary>
    /// <remarks>
    /// The label is a <c>###</c> title or a target out of a document, which is untrusted
    /// content of no particular length. Every surface that shows one trims, so this is about
    /// not handing a layout a megabyte-long string to measure.
    /// </remarks>
    public const int MaxLabelLength = 160;

    /// <summary>
    /// What a request is called, preferring what somebody wrote about it.
    /// </summary>
    /// <remarks>
    /// The <c>###</c> title first, because that is the line written to describe it; then the
    /// <c># @name</c> handle, which exists for chaining rather than for reading; then the
    /// target, which is always there. A request with none of the three is named by its line,
    /// because a row with no label is a row nobody can pick out.
    /// </remarks>
    public static string Describe(RequestBlock request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            return Clamp(request.Title.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            return Clamp(request.Name);
        }

        return string.IsNullOrWhiteSpace(request.Target)
            ? "line " + request.StartLine.ToString(CultureInfo.InvariantCulture)
            : Clamp(request.Target);
    }

    /// <summary>Cuts <paramref name="text"/> to <see cref="MaxLabelLength"/>, saying so.</summary>
    public static string Clamp(string? text) =>
        text is null || text.Length <= MaxLabelLength ? text ?? string.Empty : text[..MaxLabelLength] + "…";
}
