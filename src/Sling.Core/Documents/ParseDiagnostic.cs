namespace Sling.Core.Documents;

/// <summary>
/// How much a diagnostic matters. There are deliberately only two levels: a request
/// either can be sent or it cannot.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>Something is unusual or unsupported, but the request can still be sent.</summary>
    Warning,

    /// <summary>The request cannot be sent until this is fixed.</summary>
    Error,
}

/// <summary>
/// A single problem found while parsing or resolving a request, anchored to the line it
/// came from so the editor can point at it.
/// </summary>
/// <param name="Severity">Whether this stops the request being sent.</param>
/// <param name="Message">Text shown to the user. Never contains a resolved secret value.</param>
/// <param name="Line">1-based line number in the source document.</param>
public sealed record ParseDiagnostic(DiagnosticSeverity Severity, string Message, int Line)
{
    public static ParseDiagnostic Error(string message, int line) =>
        new(DiagnosticSeverity.Error, message, line);

    public static ParseDiagnostic Warning(string message, int line) =>
        new(DiagnosticSeverity.Warning, message, line);
}
