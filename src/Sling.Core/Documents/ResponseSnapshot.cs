namespace Sling.Core.Documents;

/// <summary>
/// Everything Sling keeps about one completed exchange. Lives in <c>Sling.Core</c>
/// rather than in the sending project because chain resolution reads response bodies
/// and headers, and chain resolution is a pure function.
/// </summary>
/// <param name="StatusCode">The numeric status of the final response, after any redirects.</param>
/// <param name="BodyByteCount">
/// Bytes of body held. Not the decoded string's length, and - when
/// <paramref name="BodyTruncated"/> is set - not the response's true length either, but
/// the cap: the point of the cap is that the rest is never read.
/// </param>
/// <param name="BodyTruncated">True when the body hit the read cap and what is held is a prefix.</param>
/// <param name="FinalUrl">Where the response actually came from, which is not the requested URL if a redirect was followed.</param>
/// <param name="RedirectTrail">Each hop taken, in order. Empty for the common case.</param>
public sealed record ResponseSnapshot(
    int StatusCode,
    string ReasonPhrase,
    string HttpVersion,
    IReadOnlyList<ResponseHeader> Headers,
    string Body,
    long BodyByteCount,
    bool BodyTruncated,
    TimeSpan Elapsed,
    Uri FinalUrl,
    IReadOnlyList<Uri> RedirectTrail)
{
    /// <summary>
    /// The first value of <paramref name="name"/>, or null. Case-insensitive, because
    /// header names are case-insensitive on the wire and servers disagree about casing.
    /// </summary>
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>True for 2xx.</summary>
    public bool IsSuccess => StatusCode is >= 200 and <= 299;
}
