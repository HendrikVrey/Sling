using System.Diagnostics.CodeAnalysis;
using Sling.Core.Documents;
using Sling.Core.Parsing;

namespace Sling.Core.Variables;

/// <summary>
/// Turns a parsed <see cref="RequestBlock"/> into a <see cref="ResolvedRequest"/>: every
/// variable substituted, every chained value checked, and the target validated as an
/// absolute HTTP URL.
/// </summary>
/// <remarks>
/// The only route from a document to something sendable. Nothing in <c>Sling.Http</c>
/// accepts a request that has not been through here, which is what makes the injection
/// rules in <see cref="VariableExpander"/> impossible to bypass at a call site.
/// </remarks>
public static class RequestResolver
{
    public static ResolutionResult Resolve(RequestDocument document, RequestBlock request, IResponseLookup responses)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responses);

        var expander = new VariableExpander(document.Variables, responses);
        var errors = new List<ParseDiagnostic>();

        var target = expander.Expand(request.Target, request.StartLine, FieldKind.Target);
        var headers = ResolveHeaders(request, expander, errors);
        var body = request.Body is null ? null : expander.Expand(request.Body, request.StartLine, FieldKind.Body);

        errors.AddRange(expander.Errors);

        // Reported before the URL is examined. A target still holding an unresolved
        // reference will fail Uri parsing too, and "that is not a valid URL" would bury
        // the reason under a symptom.
        if (errors.Count > 0 || expander.MissingResponses.Count > 0)
        {
            return new ResolutionResult(null, expander.MissingResponses, errors);
        }

        if (!TryBuildUrl(target, request.Target, request.StartLine, errors, out var url))
        {
            return new ResolutionResult(null, [], errors);
        }

        return new ResolutionResult(
            new ResolvedRequest(request.Name, request.Method, url, headers, body, request.Version),
            [],
            []);
    }

    private static List<HeaderField> ResolveHeaders(
        RequestBlock request,
        VariableExpander expander,
        List<ParseDiagnostic> errors)
    {
        var headers = new List<HeaderField>(request.Headers.Count);

        foreach (var header in request.Headers)
        {
            var name = expander.Expand(header.Name, header.Line, FieldKind.HeaderName);
            var value = expander.Expand(header.Value, header.Line, FieldKind.HeaderValue);

            // The parser lets a header name through if it holds a reference, because it
            // cannot know what the reference will become. This is where that is settled.
            if (!HttpSyntax.IsToken(name))
            {
                // Reports the name as written, still braced. Reporting the substituted
                // name would put a resolved secret in a message that goes on screen —
                // '{{token}}' resolves before this check runs, and ParseDiagnostic
                // promises its messages never carry one.
                errors.Add(ParseDiagnostic.Error(
                    $"'{header.Name}' is not a valid header name once its variables are substituted.",
                    header.Line));
                continue;
            }

            headers.Add(new HeaderField(name, value, header.Line));
        }

        return headers;
    }

    /// <summary>
    /// Validates the resolved target and turns it into a <see cref="Uri"/>.
    /// </summary>
    /// <param name="target">The resolved text, which may contain secrets.</param>
    /// <param name="asWritten">
    /// The same target still braced, which is what any diagnostic quotes. A resolved
    /// target routinely holds a token — the very reason to quote the unresolved form.
    /// </param>
    private static bool TryBuildUrl(
        string target,
        string asWritten,
        int line,
        List<ParseDiagnostic> errors,
        [NotNullWhen(true)] out Uri? url)
    {
        url = null;

        if (!Uri.TryCreate(target, UriKind.Absolute, out var parsed))
        {
            errors.Add(ParseDiagnostic.Error(
                $"'{asWritten}' is not an absolute URL. Write the scheme and host in full — "
                    + "reusable base URLs arrive with environments.",
                line));
            return false;
        }

        // Schemes are an allow-list, not a deny-list. Sling is a tool for talking to HTTP
        // APIs; letting a document name file:// would turn a request that looks like a
        // network call into a local read.
        if (!parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.Ordinal)
            && !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            errors.Add(ParseDiagnostic.Error(
                $"'{parsed.Scheme}' is not a scheme Sling sends. Use http or https.",
                line));
            return false;
        }

        // Userinfo is refused outright. It is the syntax that makes
        // https://api.example.com@evil.example.com/x send to evil.example.com while
        // reading as the opposite, which is why a substituted response value is
        // percent-encoded before it ever gets here. This is the second line of that
        // defence and it also catches the user's own literal text, where the same URL is
        // very likely a mistake and is deprecated regardless.
        if (parsed.UserInfo.Length > 0)
        {
            errors.Add(ParseDiagnostic.Error(
                "A URL may not carry a username or password before the host — the part before "
                    + "the '@'. It hides which host the request actually goes to. Send credentials "
                    + "in an Authorization header instead.",
                line));
            return false;
        }

        url = parsed;
        return true;
    }
}
