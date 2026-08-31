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

    /// <summary>
    /// Keyed by fingerprint rather than by the key itself.
    /// </summary>
    /// <remarks>
    /// <see cref="TokenCacheKey"/> holds the client secret, because rotating one has to
    /// invalidate the token at once. A dictionary keyed by it therefore keeps every client
    /// secret used this session alive for the life of the process, and could never be written
    /// down. The fingerprint discriminates identically and holds none of the secret.
    /// </remarks>
    private readonly Dictionary<string, Held> _tokens = new(StringComparer.Ordinal);

    /// <summary>A cached token, what it is for, and when it arrived.</summary>
    /// <remarks>
    /// The fetch time is not on <see cref="OAuth2Token"/> because a token does not have one -
    /// it has an expiry, which is the server's. When Sling got hold of it is this cache's
    /// fact, and it is half of what makes "why did that 401" answerable.
    /// </remarks>
    private readonly record struct Held(OAuth2Token Token, DateTimeOffset FetchedUtc, TokenIdentity Identity);

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
            if (!_tokens.TryGetValue(key.Fingerprint, out var held))
            {
                return null;
            }

            if (held.Token.IsUsableAt(nowUtc))
            {
                return held.Token;
            }

            // Dropped rather than left to be re-tested on every request. A spent token is
            // never going to become usable again, and keeping it means the dictionary only
            // ever grows across a long session.
            _tokens.Remove(key.Fingerprint);
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
    public void Record(TokenCacheKey key, OAuth2Token token, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            _minted.Add(token.AccessToken);

            if (token.ExpiresUtc is not null)
            {
                _tokens[key.Fingerprint] = new Held(token, nowUtc, key.Identity);
            }
        }
    }

    /// <summary>
    /// What can safely be said about every token held, for the chip and its list.
    /// </summary>
    /// <remarks>
    /// <b>A second accessor rather than a second use of <see cref="AccessTokens"/>.</b> That
    /// one exists to hand raw token values to the redactor, and fusing "what may be shown"
    /// onto "what must be recognised" is precisely how an uncached token reached the history
    /// file in clear. Nothing here carries a token value or a client secret.
    /// </remarks>
    public IReadOnlyList<TokenSummary> Summaries()
    {
        lock (_gate)
        {
            return
            [
                .. _tokens.Values.Select(held => new TokenSummary(
                    held.Identity.TokenUrl,
                    held.Identity.ClientId,
                    held.Identity.Scope,
                    held.Identity.Audience,
                    held.FetchedUtc,
                    held.Token.ExpiresUtc)),
            ];
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
            _tokens.Remove(key.Fingerprint);
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

    /// <summary>
    /// Every cached token in the form it can be written down as.
    /// </summary>
    /// <remarks>
    /// <b>The only accessor that hands out token values for a purpose other than redaction</b>,
    /// and it exists so a token can outlive the process. Its caller encrypts what it returns
    /// before any of it reaches a disk; nothing here writes anything.
    /// </remarks>
    public IReadOnlyList<PersistedToken> Export()
    {
        lock (_gate)
        {
            return
            [
                .. _tokens
                    .Where(entry => entry.Value.Token.ExpiresUtc is not null)
                    .Select(entry => new PersistedToken(
                        entry.Key,
                        entry.Value.Identity,
                        entry.Value.Token.AccessToken,
                        entry.Value.Token.TokenType,
                        entry.Value.Token.ExpiresUtc!.Value,
                        entry.Value.FetchedUtc)),
            ];
        }
    }

    /// <summary>
    /// Puts previously stored tokens back, dropping any that are already spent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every token goes back through <see cref="OAuth2Token.TryCreate"/>, which is the only
    /// way to make one and which refuses anything that could not go in a header. A store
    /// somebody has edited is untrusted input exactly like a token response, and there is no
    /// route from one into an <c>Authorization</c> header that skips the check.
    /// </para>
    /// <para>
    /// Restored tokens join the minted set as well, because redaction has to recognise a
    /// token whether it was fetched this session or the last one.
    /// </para>
    /// </remarks>
    /// <returns>How many were usable.</returns>
    public int Import(IEnumerable<PersistedToken> tokens, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var restored = 0;

        lock (_gate)
        {
            foreach (var stored in tokens)
            {
                if (!OAuth2Token.TryCreate(
                        stored.AccessToken,
                        stored.TokenType,
                        stored.ExpiresUtc,
                        out var token,
                        out _)
                    || !token.IsUsableAt(nowUtc))
                {
                    continue;
                }

                _minted.Add(token.AccessToken);
                _tokens[stored.Fingerprint] = new Held(token, stored.FetchedUtc, stored.Identity);
                restored++;
            }
        }

        return restored;
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
