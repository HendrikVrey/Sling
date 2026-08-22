using System.Globalization;
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
/// one is sent first, automatically, once — the workflow <c>Sling.md</c> §2 calls the
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
/// against the wrong response. The caller holds that invariant — the UI does it with a
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

    private readonly RequestSender _sender;
    private readonly ResponseStore _responses = new();

    public RequestRunner(SendOptions? options = null)
        : this(new RequestSender(options))
    {
    }

    internal RequestRunner(RequestSender sender) => _sender = sender;

    /// <summary>
    /// Forgets every stored response, so the next send re-runs its chain from the start.
    /// </summary>
    public void ForgetResponses() => _responses.Clear();

    /// <param name="context">
    /// The selected environment and the files a body may import. Its
    /// <see cref="ResolutionContext.Responses"/> is replaced with this runner's own store
    /// — the caller has no business supplying one, and the chain would not work if it did.
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

        var state = new RunState([], [], new HashSet<string>(StringComparer.Ordinal));

        await RunOneAsync(
            document,
            request,
            context with { Responses = _responses },
            state,
            cancellationToken).ConfigureAwait(false);

        return new RunResult(state.Exchanges, state.Errors);
    }

    public void Dispose() => _sender.Dispose();

    /// <summary>
    /// What one call to <see cref="RunAsync"/> accumulates as it walks a chain.
    /// </summary>
    /// <param name="InProgress">
    /// Named requests currently on the stack, which is how a chain that depends on itself
    /// is told apart from a diamond that merely visits the same login twice.
    /// </param>
    private sealed record RunState(
        List<Exchange> Exchanges,
        List<ParseDiagnostic> Errors,
        HashSet<string> InProgress);

    private async Task<bool> RunOneAsync(
        RequestDocument document,
        RequestBlock request,
        ResolutionContext context,
        RunState state,
        CancellationToken cancellationToken)
    {
        // Only a named request can be depended on, so only a named request can close a
        // cycle. The name is released again on the way out, which lets a diamond — two
        // requests both needing the same login — resolve from the store on the second
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
                    return await SendAsync(resolution.Request, request, state, cancellationToken)
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

            if (!await RunOneAsync(document, dependency, context, state, cancellationToken)
                .ConfigureAwait(false))
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
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _sender.SendAsync(resolved, cancellationToken).ConfigureAwait(false);

            state.Exchanges.Add(new Exchange(resolved, response));

            if (resolved.Name is not null)
            {
                _responses.Store(resolved.Name, response);
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
            // — so it is not an HttpRequestException and was escaping to nowhere.
            state.Errors.Add(ParseDiagnostic.Error($"The connection failed while reading the response: {ex.Message}", source.StartLine));
            return false;
        }
        catch (UriFormatException ex)
        {
            // A URL that Uri.TryCreate accepted and the transport then rejected — a host
            // holding a character that is illegal under IDN is the way in. Resolution
            // cannot pre-empt it without reimplementing the transport's own rules.
            state.Errors.Add(ParseDiagnostic.Error($"The URL cannot be used: {ex.Message}", source.StartLine));
            return false;
        }
        catch (InvalidOperationException ex)
        {
            // Raised when the message itself is not sendable — a header that cannot go
            // where it was put, or a method a body is not allowed with.
            state.Errors.Add(ParseDiagnostic.Error($"The request could not be sent: {ex.Message}", source.StartLine));
            return false;
        }
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
