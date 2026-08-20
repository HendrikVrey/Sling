using System.Diagnostics.CodeAnalysis;

namespace Sling.Core.Variables;

/// <summary>Which part of an earlier response a chain reference reads.</summary>
internal enum ChainPart
{
    /// <summary>A JSONPath into the response body.</summary>
    Body,

    /// <summary>A response header, by name.</summary>
    Header,
}

/// <summary>
/// A parsed <c>{{login.response.body.$.access_token}}</c> reference: the name of an
/// earlier request, and what to read out of its response.
/// </summary>
/// <remarks>
/// This is the mechanism that makes "log in, extract the token, use it in every
/// subsequent request" work without a scripting runtime — which is why <c>Sling.md</c>
/// §2 puts it in M1 rather than deferring it. Extraction is data-only by construction:
/// there is no expression to evaluate, so there is nothing to sandbox.
/// </remarks>
internal sealed record ChainReference(string RequestName, ChainPart Part, string Path)
{
    private const string ResponseSegment = ".response.";

    /// <summary>
    /// Recognises the chain grammar. Returns false — rather than reporting an error —
    /// for anything else, because "not a chain reference" is the normal case: most
    /// <c>{{names}}</c> are plain variables.
    /// </summary>
    public static bool TryParse(string reference, [NotNullWhen(true)] out ChainReference? result)
    {
        result = null;

        var marker = reference.IndexOf(ResponseSegment, StringComparison.Ordinal);
        if (marker <= 0)
        {
            return false;
        }

        var name = reference[..marker];
        var rest = reference[(marker + ResponseSegment.Length)..];

        if (string.Equals(rest, "body", StringComparison.Ordinal))
        {
            result = new ChainReference(name, ChainPart.Body, "$");
            return true;
        }

        if (rest.StartsWith("body.", StringComparison.Ordinal))
        {
            var path = rest["body.".Length..];

            // A trailing dot is not a way to spell "the whole body" — '…body' already is.
            // Left alone it walked zero steps and returned the root, so
            // 'Authorization: Bearer {{login.response.body.}}' quietly sent the entire
            // login response, every secret in it, as a header value.
            if (path.Length == 0)
            {
                return false;
            }

            result = new ChainReference(name, ChainPart.Body, path);
            return true;
        }

        if (rest.StartsWith("headers.", StringComparison.Ordinal))
        {
            var header = rest["headers.".Length..];
            if (header.Length == 0)
            {
                return false;
            }

            result = new ChainReference(name, ChainPart.Header, header);
            return true;
        }

        return false;
    }

    /// <summary>How the reference was written, for use in a diagnostic.</summary>
    public override string ToString() =>
        Part == ChainPart.Body
            ? $"{RequestName}.response.body.{Path}"
            : $"{RequestName}.response.headers.{Path}";
}
