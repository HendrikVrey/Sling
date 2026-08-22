namespace Sling.Core.Variables;

/// <summary>
/// Everything outside the document that resolving a request may need: the responses of
/// requests already sent, the selected environment, and the files a body may import.
/// </summary>
/// <remarks>
/// <para>
/// A record with defaults rather than a constructor with three arguments, so a caller
/// supplies only the parts that exist in its situation — a unit test resolving a
/// self-contained document supplies none of them, and gets the honest answer that every
/// chain reference is unsent and every import unavailable.
/// </para>
/// <para>
/// The defaults are all refusals rather than nulls. That is what keeps
/// <see cref="RequestResolver"/> free of null checks around three collaborators, and it
/// means forgetting to wire one produces a diagnostic the user can read rather than a
/// <see cref="NullReferenceException"/>.
/// </para>
/// </remarks>
public sealed record ResolutionContext
{
    /// <summary>Responses of named requests already sent this session.</summary>
    public IResponseLookup Responses { get; init; } = NoResponses.Instance;

    /// <summary>
    /// The selected environment's variables, which take precedence over the document's
    /// own <c>@name = value</c> definitions.
    /// </summary>
    public IVariableSource Environment { get; init; } = NoVariables.Instance;

    /// <summary>Where a <c>&lt; ./file</c> body import gets its bytes.</summary>
    public IRequestFileSource Files { get; init; } = NoRequestFiles.Instance;
}
