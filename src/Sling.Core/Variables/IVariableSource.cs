using System.Diagnostics.CodeAnalysis;

namespace Sling.Core.Variables;

/// <summary>
/// Variables supplied from outside the document — the selected environment — so
/// <c>Sling.Core</c> can resolve them without knowing that environments are JSON files
/// on disk.
/// </summary>
/// <remarks>
/// The same shape as <see cref="IResponseLookup"/>, and for the same reason: the pure
/// core states what it needs, and <c>Sling.Persistence</c> decides where it comes from.
/// </remarks>
public interface IVariableSource
{
    /// <summary>The value bound to <paramref name="name"/> in the selected environment.</summary>
    bool TryGet(string name, [NotNullWhen(true)] out string? value);
}

/// <summary>
/// The source used when no environment is selected. Resolves nothing, which sends every
/// <c>{{name}}</c> on to the document's own <c>@name = value</c> definitions.
/// </summary>
public sealed class NoVariables : IVariableSource
{
    public static NoVariables Instance { get; } = new();

    public bool TryGet(string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        return false;
    }
}
