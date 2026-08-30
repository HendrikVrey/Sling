using Sling.Core.Auth;

namespace Sling.Http;

/// <summary>
/// The access tokens fetched this session, keyed by the grant that produced them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>In memory, for the life of the process, and written nowhere.</strong> An access
/// token is the single most valuable string Sling handles: it is a bearer credential, so
/// whoever holds it is the client. There is no token file, no cache directory and no
/// setting to add one; history stores tokens redacted, and this cache is emptied whenever
/// the response store is - switching environment, or opening a different document.
/// </para>
/// <para>
/// The cache is what makes the feature usable rather than merely correct. Without it every
/// request under a grant fetches a token first, which doubles the traffic, and an
/// authorization server that rate-limits token issuance will start refusing.
/// </para>
/// <para>
/// Locked for the same reason <c>ResponseStore</c> is: entries are written on whatever
/// pool thread the token response arrived on.
/// </para>
/// </remarks>
internal sealed class TokenCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TokenCacheKey, OAuth2Token> _tokens = [];

    /// <summary>
    /// Every access token minted this session, whether or not it was worth caching.
    /// </summary>
    /// <remarks>
    /// Separate from the cache, because <em>cacheable</em> and <em>known to redaction</em>
    /// are different questions and fusing them is a leak. A token with no stated lifetime
    /// is deliberately not cached - and it is still a bearer credential that must be
    /// recognised wherever it turns up in a history entry.
    /// </remarks>
    private readonly HashSet<string> _minted = new(StringComparer.Ordinal);

    /// <summary>
    /// The token for <paramref name="key"/>, if one is held and still usable at
    /// <paramref name="nowUtc"/>.
    /// </summary>
    public OAuth2Token? Find(TokenCacheKey key, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (!_tokens.TryGetValue(key, out var token))
            {
                return null;
            }

            if (token.IsUsableAt(nowUtc))
            {
                return token;
            }

            // Dropped rather than left to be re-tested on every request. A spent token is
            // never going to become usable again, and keeping it means the dictionary only
            // ever grows across a long session.
            _tokens.Remove(key);
            return null;
        }
    }

    /// <summary>
    /// Records <paramref name="token"/> as minted, and caches it if its lifetime is known.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two halves are deliberately separate. A token with no <c>expires_in</c> is not
    /// cached: RFC 6749 §5.1 only recommends the field, and inventing a lifetime for a
    /// server that did not state one produces the worst possible failure - a run of 401s
    /// starting partway through a session, from a cache the user cannot see.
    /// </para>
    /// <para>
    /// It is still recorded as minted. An earlier version returned before doing so, which
    /// meant the one kind of token Sling fetches most often - the un-cacheable kind - was
    /// invisible to redaction and reached the history file in clear.
    /// </para>
    /// </remarks>
    public void Record(TokenCacheKey key, OAuth2Token token)
    {
        lock (_gate)
        {
            _minted.Add(token.AccessToken);

            if (token.ExpiresUtc is not null)
            {
                _tokens[key] = token;
            }
        }
    }

    /// <summary>
    /// Drops the cached token for <paramref name="key"/>, if there is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the one case the clock cannot cover: a token the authorization server has stopped
    /// honouring before its stated expiry. A revoked client, a rotated secret, a session
    /// invalidated at the far end - all of them arrive as a 401 on a token this cache still
    /// believes in, and re-testing it against the clock forever would keep answering with
    /// the same dead token.
    /// </para>
    /// <para>
    /// The minted set is deliberately not touched. That is redaction's list, and a token
    /// that has stopped working is exactly as sensitive as one that still does - forgetting
    /// it here would let it reach a history entry in clear.
    /// </para>
    /// </remarks>
    public void Invalidate(TokenCacheKey key)
    {
        lock (_gate)
        {
            _tokens.Remove(key);
        }
    }

    /// <summary>Every token minted this session, for redaction. Values only - the keys hold secrets too.</summary>
    public IReadOnlyList<string> AccessTokens()
    {
        lock (_gate)
        {
            return [.. _minted];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _tokens.Clear();
            _minted.Clear();
        }
    }
}
