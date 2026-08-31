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
    /// <summary>
    /// The variable this is about, when it is about one that does not resolve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The message already names it, and a message is not a thing an editor can act on
    /// without parsing its own prose back out again. Carrying the name means the editor can
    /// offer to define it, which turns the one diagnostic that was always a dead end - it
    /// names the three places a value could come from and can reach none of them - into the
    /// one place where the fix is a click.
    /// </para>
    /// <para>
    /// A name and never a value, like everything else on this type.
    /// </para>
    /// </remarks>
    public string? MissingVariable { get; init; }

    /// <summary>True when a credential is the likeliest thing the missing name stands for.</summary>
    /// <remarks>
    /// Set when the reference was written in an <c>Authorization</c> header or an auth
    /// directive, because a name missing from one of those is a credential far more often
    /// than not - which decides whether the editor offers to put it in the gitignored file.
    /// </remarks>
    public bool LooksLikeCredential { get; init; }

    public static ParseDiagnostic Error(string message, int line) =>
        new(DiagnosticSeverity.Error, message, line);

    public static ParseDiagnostic Warning(string message, int line) =>
        new(DiagnosticSeverity.Warning, message, line);
}
