namespace Sling.Http;

/// <summary>
/// The limits every send is subject to. Bounds rather than tuning knobs: each one exists
/// because the unbounded version has a failure mode.
/// </summary>
public sealed record SendOptions
{
    /// <summary>
    /// How many redirects to follow before handing back the 3xx itself.
    /// </summary>
    /// <remarks>
    /// Sling follows redirects by hand rather than letting the handler do it, because
    /// that is the only place credential headers can be stripped on a cross-origin hop
    /// (<c>Sling.md</c> §5.2). Following by hand means owning the loop bound too.
    /// </remarks>
    public int MaxRedirects { get; init; } = 10;

    /// <summary>
    /// The cap on a response body held in memory. A response above it is kept as a
    /// prefix and flagged, rather than being allowed to exhaust the process.
    /// </summary>
    /// <remarks>
    /// A streaming endpoint answers a GET with a body that never ends; without a cap the
    /// first person to point Sling at one loses the application. The real answer for very
    /// large responses — Etch has large-file modes — is an open question in
    /// <c>Sling.md</c> §8; this is the bound that keeps the question from being urgent.
    /// </remarks>
    public long MaxBodyBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>How long a whole exchange, including redirects, may take.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(100);
}
