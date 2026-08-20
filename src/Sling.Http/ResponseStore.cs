using Sling.Core.Documents;
using Sling.Core.Variables;

namespace Sling.Http;

/// <summary>
/// The responses of named requests sent so far in this session, which is what
/// <c>{{login.response.body.$.token}}</c> reads from.
/// </summary>
/// <remarks>
/// In memory only, and not persisted. A response body is the most likely place in the
/// whole application for a credential to be sitting, and history — which is persisted —
/// stores redacted copies instead (<c>Sling.md</c> §5.4, M3).
/// </remarks>
/// <remarks>
/// <para>
/// Locked rather than plain. Sending uses <c>ConfigureAwait(false)</c> throughout, so a
/// store happens on whatever pool thread the response arrived on, and the type would
/// otherwise be silently thread-affine while nothing in its shape said so.
/// </para>
/// </remarks>
internal sealed class ResponseStore : IResponseLookup
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ResponseSnapshot> _byName = new(StringComparer.Ordinal);

    public ResponseSnapshot? Find(string requestName)
    {
        lock (_gate)
        {
            return _byName.GetValueOrDefault(requestName);
        }
    }

    public void Store(string requestName, ResponseSnapshot response)
    {
        lock (_gate)
        {
            _byName[requestName] = response;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byName.Clear();
        }
    }
}
