using Sling.Core.Documents;

namespace Sling.Core.Variables;

/// <summary>
/// The outcome of resolving one request: it is ready, it needs an earlier request to run
/// first, or it is wrong.
/// </summary>
/// <remarks>
/// <see cref="MissingResponses"/> is not an error. It is the resolver telling the caller
/// which named requests have not been sent yet, which is exactly the information needed
/// to run a chain — and keeping it separate from <see cref="Errors"/> is what lets the
/// chain logic stay a loop over a list rather than a search through error text.
/// </remarks>
public sealed record ResolutionResult(
    ResolvedRequest? Request,
    IReadOnlyList<string> MissingResponses,
    IReadOnlyList<ParseDiagnostic> Errors)
{
    /// <summary>True when the request can be sent as it stands.</summary>
    public bool IsReady => Request is not null;
}
