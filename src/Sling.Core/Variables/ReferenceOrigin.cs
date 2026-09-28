using Sling.Core.Documents;

namespace Sling.Core.Variables;

/// <summary>Where a <c>{{reference}}</c> takes its value from.</summary>
public enum ReferenceSource
{
    /// <summary>An earlier response in the same document, named with <c># @name</c>.</summary>
    Response,

    /// <summary>A chain reference to a request name the document does not declare.</summary>
    MissingRequest,

    /// <summary>The selected environment, or the shared values under it.</summary>
    Environment,

    /// <summary>An <c>@name = value</c> line in the document itself.</summary>
    File,

    /// <summary>Nowhere: sending the request would fail on this reference.</summary>
    Undefined,
}

/// <summary>
/// Where one <c>{{reference}}</c> resolves from, found in the same order the resolver looks.
/// </summary>
/// <param name="Source">Which kind of place.</param>
/// <param name="RequestName">The request a chain reference reads, for the two chain sources.</param>
/// <param name="Line">
/// The 1-based line of the named request or the file variable; zero for the environment and
/// for a reference that resolves nowhere.
/// </param>
public sealed record ReferenceOrigin(ReferenceSource Source, string? RequestName, int Line)
{
    /// <summary>Locates <paramref name="reference"/> the way sending the request would.</summary>
    /// <remarks>
    /// The order is <see cref="VariableExpander"/>'s, and it has to be: a panel that looked
    /// only in the environment told people a chained token "is not defined" and offered to
    /// define it, which would have shadowed nothing and fixed nothing. Chain references come
    /// first because their grammar is unambiguous; then the environment, which deliberately
    /// outranks the file (see <c>docs/http-dialect.md</c>); then the file's own variables,
    /// where the last definition wins.
    /// </remarks>
    /// <param name="reference">The text between the braces, without them.</param>
    public static ReferenceOrigin Locate(string reference, RequestDocument document, IVariableSource environment)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(environment);

        if (ChainReference.TryParse(reference, out var chain))
        {
            return document.BlockNamed(chain.RequestName) is { } named
                ? new ReferenceOrigin(ReferenceSource.Response, chain.RequestName, named.StartLine)
                : new ReferenceOrigin(ReferenceSource.MissingRequest, chain.RequestName, 0);
        }

        if (environment.TryGet(reference, out _))
        {
            return new ReferenceOrigin(ReferenceSource.Environment, null, 0);
        }

        var definition = document.Variables.LastOrDefault(
            v => string.Equals(v.Name, reference, StringComparison.Ordinal));

        return definition is null
            ? new ReferenceOrigin(ReferenceSource.Undefined, null, 0)
            : new ReferenceOrigin(ReferenceSource.File, null, definition.Line);
    }
}
