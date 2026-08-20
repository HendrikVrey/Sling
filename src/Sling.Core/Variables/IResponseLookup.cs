using Sling.Core.Documents;

namespace Sling.Core.Variables;

/// <summary>
/// Supplies the responses of named requests already sent in this session, so chain
/// references can be resolved without <c>Sling.Core</c> knowing that a network exists.
/// </summary>
public interface IResponseLookup
{
    /// <summary>The stored response for <paramref name="requestName"/>, or null.</summary>
    ResponseSnapshot? Find(string requestName);
}

/// <summary>
/// A lookup that has nothing. Resolving against it reports every chain reference as
/// missing, which is what makes it useful: it is how a caller asks "what would this
/// request need before it could be sent?".
/// </summary>
public sealed class NoResponses : IResponseLookup
{
    public static NoResponses Instance { get; } = new();

    public ResponseSnapshot? Find(string requestName) => null;
}
