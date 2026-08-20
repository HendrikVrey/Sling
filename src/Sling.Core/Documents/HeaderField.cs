namespace Sling.Core.Documents;

/// <summary>
/// A header as written in a request document, carrying the line it came from so a
/// resolution failure can be reported against the text the user actually typed.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ResponseHeader"/> on purpose: a response header has no
/// source line, and a type that carries a meaningless zero would invite someone to
/// report an error against line 0.
/// </remarks>
public sealed record HeaderField(string Name, string Value, int Line);

/// <summary>A header returned by a server. No source line exists for one of these.</summary>
public sealed record ResponseHeader(string Name, string Value);
