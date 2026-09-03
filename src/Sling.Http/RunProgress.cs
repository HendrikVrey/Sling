using Sling.Core.Documents;

namespace Sling.Http;

/// <summary>
/// Reported as each request in a run is about to go out, so the window can say what it is
/// waiting for rather than only that it is waiting.
/// </summary>
/// <remarks>
/// <para>
/// <b>The request as the document wrote it, not as it resolved.</b> A resolved target can
/// carry a substituted <c>{{token}}</c> in its query string, and a progress line is drawn on
/// screen and photographed into bug reports. The unresolved block is already what the
/// collections rail and the command bar show, so this is both the safe answer and the
/// consistent one.
/// </para>
/// <para>
/// <see cref="Total"/> is what the caller asked for rather than what will happen.
/// <see cref="Number"/> can exceed it, because a chain pulls in dependencies nobody listed -
/// which is exactly the case worth showing, and why <see cref="Role"/> travels with it.
/// </para>
/// </remarks>
/// <param name="Request">The request block about to be sent, as written.</param>
/// <param name="Number">Its 1-based position among the requests this run has started.</param>
/// <param name="Total">How many requests the caller asked for.</param>
/// <param name="Role">Why this one is going out.</param>
public sealed record RunProgress(RequestBlock Request, int Number, int Total, ExchangeRole Role);
