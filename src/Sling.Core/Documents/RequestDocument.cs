namespace Sling.Core.Documents;

/// <summary>
/// A parsed <c>.http</c> document: its file-scoped variables, its requests in source
/// order, and everything the parser could not make sense of.
/// </summary>
public sealed record RequestDocument(
    IReadOnlyList<VariableDefinition> Variables,
    IReadOnlyList<RequestBlock> Requests,
    IReadOnlyList<ParseDiagnostic> Diagnostics)
{
    /// <summary>True when nothing in the document stops a request being sent.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>
    /// The request a caret on <paramref name="line"/> belongs to.
    /// </summary>
    /// <remarks>
    /// A caret between requests — on a <c>###</c> separator, a comment or a variable
    /// definition — resolves forward to the next request rather than to nothing, because
    /// pressing send with the caret on the separator above a request means that request.
    /// A caret past the last request resolves back to it for the same reason.
    /// </remarks>
    public RequestBlock? BlockAtLine(int line)
    {
        if (Requests.Count == 0)
        {
            return null;
        }

        foreach (var request in Requests)
        {
            if (line >= request.StartLine && line <= request.EndLine)
            {
                return request;
            }
        }

        return Requests.FirstOrDefault(r => r.StartLine > line) ?? Requests[^1];
    }

    /// <summary>
    /// The request declared with <c># @name <paramref name="name"/></c>, if there is one.
    /// </summary>
    /// <remarks>
    /// Ordinal comparison: a chain reference is code, and matching <c>login</c> to
    /// <c>LOGIN</c> under some culture's casing rules would make resolution depend on
    /// the machine's locale.
    /// </remarks>
    public RequestBlock? BlockNamed(string name) =>
        Requests.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
}
