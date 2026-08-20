using Sling.Core.Documents;
using Sling.Core.Variables;

namespace Sling.Http;

/// <summary>One request as it was actually sent, paired with what came back.</summary>
public sealed record Exchange(ResolvedRequest Request, ResponseSnapshot Response);

/// <summary>
/// The outcome of pressing send: every exchange that happened, in the order it happened,
/// and anything that stopped the run.
/// </summary>
/// <remarks>
/// <see cref="Exchanges"/> is populated even when <see cref="Errors"/> is not empty. A
/// chain that logged in successfully and then failed on the request that needed the
/// token has one useful exchange and one error, and throwing away the first would hide
/// the evidence needed to understand the second.
/// </remarks>
public sealed record RunResult(IReadOnlyList<Exchange> Exchanges, IReadOnlyList<ParseDiagnostic> Errors)
{
    public bool Succeeded => Errors.Count == 0 && Exchanges.Count > 0;
}
