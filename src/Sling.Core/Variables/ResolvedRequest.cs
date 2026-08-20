using Sling.Core.Documents;

namespace Sling.Core.Variables;

/// <summary>
/// A request with every variable substituted and every field validated — the only shape
/// <c>Sling.Http</c> will send.
/// </summary>
/// <remarks>
/// Making this a separate type from <see cref="RequestBlock"/> is what stops an
/// unresolved request reaching the network by accident: the sender takes one of these
/// and there is no way to construct it except through
/// <see cref="RequestResolver.Resolve"/>, which is where §5.7's injection checks live.
/// </remarks>
/// <param name="Name">The <c>@name</c> the response should be stored under, if any.</param>
/// <param name="Version">The HTTP version the document asked for, if it asked.</param>
public sealed record ResolvedRequest(
    string? Name,
    string Method,
    Uri Url,
    IReadOnlyList<HeaderField> Headers,
    string? Body,
    string? Version);
