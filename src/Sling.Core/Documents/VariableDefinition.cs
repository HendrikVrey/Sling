namespace Sling.Core.Documents;

/// <summary>
/// A file-scoped <c>@name = value</c> definition. The value is stored raw, because it
/// may itself contain <c>{{references}}</c> that cannot be resolved until send time.
/// </summary>
public sealed record VariableDefinition(string Name, string Value, int Line);
