using System.Globalization;
using Sling.Core.Auth;
using Sling.Core.Cookies;
using Sling.Core.Documents;
using Sling.Core.Variables;

namespace Sling.Http;

/// <summary>
/// Sends a request, first sending whatever earlier requests it depends on.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes <c>{{login.response.body.$.access_token}}</c> work rather than
/// merely parse. Ask for the request that needs a token and the request that produces
/// one is sent first, automatically, once - the workflow <c>Sling.md</c> §2 calls the
/// single most common real-world API interaction, and the thing OAuth2
/// client-credentials is built out of in M3.
/// </para>
/// <para>
/// Dependencies are discovered rather than declared. <see cref="RequestResolver"/>
/// reports which named responses it was missing, so the graph comes out of the same
/// substitution pass that would have used them, and nothing has to be kept in step with
/// anything.
/// </para>
/// <para>
/// Every request sent this way appears in <see cref="RunResult.Exchanges"/>. A tool that
/// makes network calls the user did not explicitly ask for has to show them.
/// </para>
/// <para>
/// <strong>One run at a time.</strong> The stored responses that chaining reads are not
/// synchronised for concurrent <em>runs</em>: two overlapping calls to
/// <see cref="RunAsync"/> would interleave their chains and could resolve a reference
/// against the wrong response. The caller holds that invariant - the UI does it with a
/// single in-flight token. The store itself is locked so an overlap corrupts nothing,
/// but a corrupted dictionary was never the interesting failure here.
/// </para>
/// </remarks>
public sealed class RequestRunner : IDisposable
{
    /// <summary>
    /// How many times one request may be re-resolved after satisfying dependencies.
    /// Bounds a document whose chain is long or, in the pathological case, one whose
    /// dependencies never reduce.
    /// </summary>
    private const int MaxResolutionPasses = 16;

    /// <summary>The status that means the credential was refused rather than the request.</summary>
    private const int Unauthorized = 401;

    private readonly RequestSender _sender;
    private readonly ResponseStore _responses = new();
    private readonly TokenCache _tokens = new();

    public RequestRunner(SendOptions? options = null)
        : this(new RequestSender(options))
    {
    }

    internal RequestRunner(RequestSender sender) => _sender = sender;

    /// <summary>
    /// The bounds every send is subject to. Settable so the settings panel can change a
    /// timeout without the connection pool being rebuilt.
    /// </summary>
    public SendOptions Options
    {
        get => _sender.Options;
        set => _sender.Options = value;
    }

    /// <summary>
    /// The cookie jar in force, or null when cookies are switched off.
    /// </summary>
    /// <remarks>
    /// Owned by the caller and swapped when the environment changes, which is what makes
    /// the per-environment scoping in <c>Sling.md</c> §5.6 structural: two environments
    /// cannot share cookies because they do not share a jar.
    /// </remarks>
    public CookieJar? Cookies { get; set; }

    /// <summary>
    /// Forgets everything this session has accumulated about the document: stored
    /// responses and cached access tokens alike.
    /// </summary>
    /// <remarks>
    /// The two go together and must never be cleared separately. Both are keyed by things
    /// that mean different things under a different environment or a different file - a
    /// response is keyed by an <c>@name</c> that is per-file, and a token is keyed by a
    /// grant whose <c>{{client_secret}}</c> resolved under one environment. A token
    /// fetched against staging is a valid-looking bearer token; a request that reused it
    /// after a switch would send it to production.
    /// </remarks>
    public void ForgetSession()
    {
        _responses.Clear();
        _tokens.Clear();
    }

    /// <summary>
    /// Every access token minted this session, so redaction can recognise one wherever it
    /// appears.
    /// </summary>
    /// <remarks>
    /// Every token, not every <em>cached</em> token - a token with no stated lifetime is
    /// deliberately not cached and is exactly as sensitive as one that is. A token reaches
    /// history as the value of an <c>Authorization</c> header, which the header-name rule
    /// already removes; this is the second line, and it is the one that catches a token
    /// echoed back in a <c>Location</c> header or some header nobody has a rule for.
    /// </remarks>
    public IReadOnlyList<string> AcquiredTokens() => _tokens.AccessTokens();

    /// <summary>
    /// What can be shown about the tokens held this session: the grant, the clock, and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AcquiredTokens"/> on purpose. That returns raw values because
    /// redaction has to recognise them; this returns a projection that carries no token and
    /// no client secret. One accessor answering both questions is how the wrong one gets
    /// called from a panel.
    /// </remarks>
    public IReadOnlyList<TokenSummary> HeldTokens() => _tokens.Summaries();

    /// <summary>
    /// Every cached token in the form a store can write down.
    /// </summary>
    /// <remarks>
    /// The one accessor that hands out token values for something other than redaction, and
    /// it is named so that a call site reads as what it is. Whoever calls it is responsible
    /// for encrypting the result before it reaches a disk.
    /// </remarks>
    public IReadOnlyList<PersistedToken> ExportTokens() => _tokens.Export();

    /// <summary>
    /// Puts stored tokens back into the cache, dropping the ones that are already spent.
    /// </summary>
    /// <returns>How many were usable.</returns>
    public int RestoreTokens(IEnumerable<PersistedToken> tokens) =>
        _tokens.Import(tokens, DateTimeOffset.UtcNow);

    /// <param name="context">
    /// The selected environment and the files a body may import. Its
    /// <see cref="ResolutionContext.Responses"/> is replaced with this runner's own store
    /// - the caller has no business supplying one, and the chain would not work if it did.
    /// </param>
    public async Task<RunResult> RunAsync(
        RequestDocument document,
        RequestBlock request,
        ResolutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var state = new RunState([], [], [], new HashSet<string>(StringComparer.Ordinal));

        await RunOneAsync(
            document,
            request,
            context with { Responses = _responses },
            state,
            ExchangeRole.Requested,
            cancellationToken).ConfigureAwait(false);

        return new RunResult(state.Exchanges, state.Errors, state.Notes);
    }

    /// <summary>
    /// Sends <paramref name="requests"/> in order, in one run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One run, not several: the exchanges, the stored responses and the cycle guard are
    /// shared, so a chain dependency that has already been satisfied is not sent twice and
    /// every exchange lands in one picker in the order it happened.
    /// </para>
    /// <para>
    /// <strong>A failure does not stop the run.</strong> Half a document sent and half not
    /// is the worst outcome to be left with, and the reason someone presses run-all is
    /// usually to find out which requests are broken - stopping at the first would answer
    /// that one request at a time. Everything that failed is in
    /// <see cref="RunResult.Errors"/>, against the line it failed on.
    /// </para>
    /// <para>
    /// Cancellation <em>does</em> stop it, immediately: that is an instruction rather than
    /// a failure, and it propagates out of the loop untouched.
    /// </para>
    /// <para>
    /// The caller chooses which requests to include, because deciding a request cannot be
    /// sent is the document's business and not the runner's - the editor already filters
    /// out the ones whose own lines hold errors.
    /// </para>
    /// </remarks>
    public async Task<RunResult> RunAllAsync(
        RequestDocument document,
        IReadOnlyList<RequestBlock> requests,
        ResolutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(context);

        var state = new RunState([], [], [], new HashSet<string>(StringComparer.Ordinal));
        var resolved = context with { Responses = _responses };

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A named request already sent during this run was sent as somebody's
            // dependency, and its own turn is not a second reason to send it. Only within
            // this run: the response store outlives it, and a later run-all is a fresh
            // instruction that must send everything again.
            //
            // Without this, a document whose 'login' is declared *below* the request that
            // chains against it logs in twice - a duplicated POST against a live API, and
            // on an identity provider that rotates on issue, a token invalidated the moment
            // after the request that used it.
            if (request.Name is not null && state.Sent.Contains(request.Name))
            {
                continue;
            }

            await RunOneAsync(document, request, resolved, state, ExchangeRole.Requested, cancellationToken)
                .ConfigureAwait(false);
        }

        return new RunResult(state.Exchanges, state.Errors, state.Notes);
    }

    public void Dispose() => _sender.Dispose();

    /// <summary>
    /// What one call to <see cref="RunAsync"/> or <see cref="RunAllAsync"/> accumulates as
    /// it walks a chain.
    /// </summary>
    /// <param name="InProgress">
    /// Named requests currently on the stack, which is how a chain that depends on itself
    /// is told apart from a diamond that merely visits the same login twice.
    /// </param>
    /// <param name="Sent">
    /// Named requests this run has already sent, which is what stops <c>RunAllAsync</c>
    /// sending one a second time when its own turn comes round after it was pulled in as a
    /// dependency. Distinct from <paramref name="InProgress"/>, which empties as the stack
    /// unwinds; this one only grows.
    /// </param>
    private sealed record RunState(
        List<Exchange> Exchanges,
        List<ParseDiagnostic> Errors,
        List<string> Notes,
        HashSet<string> InProgress)
    {
        public HashSet<string> Sent { get; } = new(StringComparer.Ordinal);
    }

    /// <param name="role">
    /// Why this request is being sent. Carried down the chain rather than worked out at the
    /// bottom, because "the user asked for this one" is something only the caller knows -
    /// the same request block is the subject at the top and a dependency one level down.
    /// </param>
    private async Task<bool> RunOneAsync(
        RequestDocument document,
        RequestBlock request,
        ResolutionContext context,
        RunState state,
        ExchangeRole role,
        CancellationToken cancellationToken)
    {
        // Only a named request can be depended on, so only a named request can close a
        // cycle. The name is released again on the way out, which lets a diamond - two
        // requests both needing the same login - resolve from the store on the second
        // visit rather than being mistaken for a loop.
        if (request.Name is not null && !state.InProgress.Add(request.Name))
        {
            state.Errors.Add(ParseDiagnostic.Error(
                $"'{request.Name}' is part of a chain that depends on itself.",
                request.StartLine));
            return false;
        }

        try
        {
            for (var pass = 0; pass < MaxResolutionPasses; pass++)
            {
                var resolution = RequestResolver.Resolve(document, request, context);

                if (resolution.Errors.Count > 0)
                {
                    state.Errors.AddRange(resolution.Errors);
                    return false;
                }

                if (resolution.Request is not null)
                {
                    return await SendAsync(resolution.Request, request, state, role, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!await RunDependenciesAsync(
                        document,
                        request,
                        resolution.MissingResponses,
                        context,
                        state,
                        cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }

            state.Errors.Add(ParseDiagnostic.Error(
                $"Resolving this request still needed earlier responses after "
                    + $"{MaxResolutionPasses.ToString(CultureInfo.InvariantCulture)} attempts. "
                    + "Check the chain for a request that never provides what the next one asks for.",
                request.StartLine));

            return false;
        }
        finally
        {
            if (request.Name is not null)
            {
                state.InProgress.Remove(request.Name);
            }
        }
    }

    private async Task<bool> RunDependenciesAsync(
        RequestDocument document,
        RequestBlock request,
        IReadOnlyList<string> missing,
        ResolutionContext context,
        RunState state,
        CancellationToken cancellationToken)
    {
        foreach (var name in missing)
        {
            var dependency = document.BlockNamed(name);
            if (dependency is null)
            {
                state.Errors.Add(ParseDiagnostic.Error(
                    $"No request in this file is named '{name}'. Add '# @name {name}' above the "
                        + "request whose response this one needs.",
                    request.StartLine));
                return false;
            }

            if (!await RunOneAsync(
                    document,
                    dependency,
                    context,
                    state,
                    ExchangeRole.Dependency,
                    cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> SendAsync(
        ResolvedRequest resolved,
        RequestBlock source,
        RunState state,
        ExchangeRole role,
        CancellationToken cancellationToken)
    {
        try
        {
            var reused = false;

            // The grant is satisfied first, and inside the same try: a failure fetching a
            // token is a failure of this request, reported against the line that declared
            // the grant, and the request must not go out without the Authorization header
            // it asked for - one that quietly goes out unauthenticated fails at the API
            // with a message about permissions and no mention of the token.
            if (resolved.Auth is { } grant)
            {
                var acquired = await AcquireTokenAsync(grant, state, cancellationToken).ConfigureAwait(false);
                if (acquired.Token is null)
                {
                    return false;
                }

                reused = acquired.FromCache;
                resolved = WithBearer(resolved, acquired.Token);
            }

            NoteExpiredToken(resolved, state);

            var outcome = await _sender.SendAsync(resolved, Cookies, cancellationToken).ConfigureAwait(false);

            state.Exchanges.Add(new Exchange(resolved, outcome.Response, DateTimeOffset.UtcNow, role));
            state.Notes.AddRange(outcome.CookieNotes);

            if (reused && outcome.Response.StatusCode == Unauthorized)
            {
                outcome = await RetryWithFreshTokenAsync(resolved, state, cancellationToken)
                    .ConfigureAwait(false) ?? outcome;
            }

            if (resolved.Name is not null)
            {
                _responses.Store(resolved.Name, outcome.Response);
                state.Sent.Add(resolved.Name);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user asked for this. Not an error to report against a line.
            throw;
        }
        catch (OperationCanceledException)
        {
            state.Errors.Add(ParseDiagnostic.Error("The request timed out.", source.StartLine));
            return false;
        }
        catch (HttpRequestException ex)
        {
            state.Errors.Add(ParseDiagnostic.Error($"The request failed: {Innermost(ex).Message}", source.StartLine));
            return false;
        }
        catch (IOException ex)
        {
            // A connection reset while the body streams. Ordinary network weather, and
            // with ResponseHeadersRead it arrives from the read rather than from the send
            // - so it is not an HttpRequestException and was escaping to nowhere.
            state.Errors.Add(ParseDiagnostic.Error($"The connection failed while reading the response: {ex.Message}", source.StartLine));
            return false;
        }
        catch (UriFormatException ex)
        {
            // A URL that Uri.TryCreate accepted and the transport then rejected - a host
            // holding a character that is illegal under IDN is the way in. Resolution
            // cannot pre-empt it without reimplementing the transport's own rules.
            state.Errors.Add(ParseDiagnostic.Error($"The URL cannot be used: {ex.Message}", source.StartLine));
            return false;
        }
        catch (InvalidOperationException ex)
        {
            // Raised when the message itself is not sendable - a header that cannot go
            // where it was put, or a method a body is not allowed with.
            state.Errors.Add(ParseDiagnostic.Error($"The request could not be sent: {ex.Message}", source.StartLine));
            return false;
        }
    }

    /// <summary>
    /// Says so when the request is about to send a bearer token that has already expired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A note and never an error. <see cref="RunResult.Errors"/> is the list whose emptiness
    /// decides whether a run worked, and an expired token is a thing worth saying rather than
    /// a reason to refuse to send - the user may well be sending it precisely to see the 401.
    /// </para>
    /// <para>
    /// Only where Sling did not mint the token itself. A token from a grant was checked
    /// against the clock a few lines ago and refetched if it was spent, so warning about one
    /// here would be warning about something that cannot happen.
    /// </para>
    /// <para>
    /// It reads the token and says nothing about whether it is trustworthy. There is no
    /// signature check here and the message says as much: the word "valid" appears nowhere.
    /// </para>
    /// </remarks>
    private static void NoteExpiredToken(ResolvedRequest resolved, RunState state)
    {
        if (resolved.Auth is not null)
        {
            return;
        }

        var header = resolved.Headers.FirstOrDefault(
            h => h.Name.Equals(RequestAuth.AuthorizationHeader, StringComparison.OrdinalIgnoreCase));

        if (header is null)
        {
            return;
        }

        var value = header.Value;
        var space = value.IndexOf(' ', StringComparison.Ordinal);
        var credential = space > 0 ? value[(space + 1)..].Trim() : value.Trim();

        if (Jwt.DescribeIfExpired(credential, DateTimeOffset.UtcNow) is { } expired)
        {
            state.Notes.Add(expired);
        }
    }

    /// <summary>
    /// Sends a request again with a freshly fetched token, after a 401 on a cached one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A token refreshes on expiry and not otherwise, and a 401 is reported as a 401. In a
    /// working session that meant noticing, guessing that the token was the reason, and then
    /// finding something to poke to make Sling fetch another - most often a restart.
    /// </para>
    /// <para>
    /// <b>Three conditions, and each of them is the boundary of this being acceptable at
    /// all.</b> Only where Sling owns the token, so a bearer token the user typed is left
    /// alone and a 401 on it stays news rather than something papered over. Only when the
    /// token was reused from the cache, so a token minted seconds ago is never re-fetched in
    /// a loop against a server that is refusing it for some other reason. And only once.
    /// </para>
    /// <para>
    /// <b>The retry is shown, not hidden</b>, which is the answer to the objection that it
    /// buries the signal: both exchanges are in the picker and the second is labelled as a
    /// retry, so what the user sees is 401, refreshed, 200 rather than a success they cannot
    /// account for.
    /// </para>
    /// </remarks>
    /// <returns>The second outcome, or null when nothing was retried.</returns>
    private async Task<SendOutcome?> RetryWithFreshTokenAsync(
        ResolvedRequest resolved,
        RunState state,
        CancellationToken cancellationToken)
    {
        if (resolved.Auth is not { } grant)
        {
            return null;
        }

        _tokens.Invalidate(grant.CacheKey);

        var acquired = await AcquireTokenAsync(grant, state, cancellationToken).ConfigureAwait(false);

        if (acquired.Token is null)
        {
            // The diagnostic explaining why is already recorded, and the 401 is already in
            // the picker. Nothing further to say: the original answer stands.
            return null;
        }

        var retried = WithBearer(resolved, acquired.Token);
        var outcome = await _sender.SendAsync(retried, Cookies, cancellationToken).ConfigureAwait(false);

        state.Exchanges.Add(new Exchange(retried, outcome.Response, DateTimeOffset.UtcNow, ExchangeRole.Retry));
        state.Notes.AddRange(outcome.CookieNotes);
        state.Notes.Add("The access token was refused, so Sling fetched a new one and sent again.");

        return outcome;
    }

    /// <summary>
    /// Gets an access token for <paramref name="grant"/>, from the cache or from the
    /// authorization server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token request goes through <see cref="RequestSender"/> like any other request,
    /// so it gets the same redirect policy, the same cross-origin credential stripping and
    /// the same timeout - and it lands in <see cref="RunResult.Exchanges"/>, because a
    /// network call Sling made on the user's behalf has to be visible. That is the same
    /// rule chained dependencies follow.
    /// </para>
    /// <para>
    /// It carries no cookie jar. A token endpoint has no session to maintain, and a jar
    /// scoped to the API's own environment has no business sending cookies to the identity
    /// provider.
    /// </para>
    /// <para>
    /// Exceptions are deliberately not caught here. The caller's <c>try</c> already maps
    /// every network failure to a diagnostic, and catching them twice would produce two
    /// descriptions of one failure with the less specific one arriving second.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The token, or null with a diagnostic already recorded, and whether it came from the
    /// cache. The second half is what the 401 retry is gated on: a token fetched seconds ago
    /// and refused is a token the server is refusing for some reason a refresh will not fix.
    /// </returns>
    private async Task<(OAuth2Token? Token, bool FromCache)> AcquireTokenAsync(
        ResolvedOAuth2Grant grant,
        RunState state,
        CancellationToken cancellationToken)
    {
        var key = grant.CacheKey;

        if (_tokens.Find(key, DateTimeOffset.UtcNow) is { } cached)
        {
            return (cached, true);
        }

        var request = OAuth2TokenRequest.Build(grant);
        var outcome = await _sender.SendAsync(request, cookies: null, cancellationToken).ConfigureAwait(false);
        var response = outcome.Response;

        state.Exchanges.Add(new Exchange(request, response, DateTimeOffset.UtcNow, ExchangeRole.TokenRequest));

        // A redirect is not followed here - see ResolvedRequest.FollowRedirects - so it
        // arrives as the response. Named separately from any other unsuccessful status
        // because "the authorization server answered 307" reads like a server fault when
        // it is a token URL that needs correcting, and following it is precisely what
        // would hand the client secret to whoever the Location names.
        if (response.StatusCode is >= 300 and <= 399)
        {
            state.Errors.Add(ParseDiagnostic.Error(
                $"The token endpoint answered {response.StatusCode.ToString(CultureInfo.InvariantCulture)} "
                    + "with a redirect, which Sling does not follow for a token request - the client "
                    + "secret would go wherever it pointed. Put the final URL in '@token-url'.",
                grant.Line));

            return (null, false);
        }

        if (!response.IsSuccess)
        {
            // The status, not the body. An error body from an authorization server
            // routinely echoes the client id, and sometimes more.
            state.Errors.Add(ParseDiagnostic.Error(
                $"The authorization server answered {response.StatusCode.ToString(CultureInfo.InvariantCulture)} "
                    + $"{response.ReasonPhrase}. The token exchange is in the response list above.",
                grant.Line));

            return (null, false);
        }

        if (!OAuth2Token.TryParseResponse(response.Body, DateTimeOffset.UtcNow, out var token, out var error))
        {
            state.Errors.Add(ParseDiagnostic.Error($"The token could not be used: {error}.", grant.Line));
            return (null, false);
        }

        _tokens.Record(key, token, DateTimeOffset.UtcNow);
        return (token, false);
    }

    /// <summary>
    /// Attaches the token as an <c>Authorization</c> header.
    /// </summary>
    /// <remarks>
    /// Replaces any <c>Authorization</c> the document wrote rather than adding a second.
    /// Two of them is a request no server has a defined answer for, and the grant is the
    /// more specific instruction - a document that declares <c># @auth oauth2</c> and also
    /// writes the header has said the same thing twice, and the token is the one that is
    /// current.
    /// <para>
    /// The token's characters were checked when <see cref="OAuth2Token"/> was constructed,
    /// which is the only way to make one. There is no route from a JSON body to this header
    /// that skips it.
    /// </para>
    /// </remarks>
    private static ResolvedRequest WithBearer(ResolvedRequest request, OAuth2Token token)
    {
        var line = request.Auth?.Line ?? 0;

        var headers = request.Headers
            .Where(h => !h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            .Append(new HeaderField("Authorization", token.HeaderValue, line))
            .ToList();

        return request with { Headers = headers };
    }

    /// <summary>
    /// The innermost exception's message. A failed TLS handshake or a DNS failure arrives
    /// wrapped in a generic "An error occurred while sending the request", and the reason
    /// the user needs is always underneath it.
    /// </summary>
    private static Exception Innermost(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current;
    }
}
