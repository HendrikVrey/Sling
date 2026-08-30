using Sling.Core.Documents;
using Sling.Core.Variables;

namespace Sling.Http;

/// <summary>One request as it was actually sent, paired with what came back.</summary>
/// <param name="SentUtc">
/// When the exchange completed. Recorded here rather than stamped by whatever writes the
/// history, so a run of several requests keeps the order they actually happened in.
/// </param>
public sealed record Exchange(ResolvedRequest Request, ResponseSnapshot Response, DateTimeOffset SentUtc);

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
/// <param name="Notes">
/// Things worth saying that stopped nothing - a cookie the jar refused, so far.
/// <para>
/// A separate list rather than a warning appended to <paramref name="Errors"/>, because
/// <paramref name="Errors"/> is the list whose emptiness decides whether a run worked.
/// Putting a note there would turn "the server set a cookie Sling would not store" into
/// "the request failed", which is a different and false statement.
/// </para>
/// </param>
public sealed record RunResult(
    IReadOnlyList<Exchange> Exchanges,
    IReadOnlyList<ParseDiagnostic> Errors,
    IReadOnlyList<string> Notes)
{
    public bool Succeeded => Errors.Count == 0 && Exchanges.Count > 0;
}
