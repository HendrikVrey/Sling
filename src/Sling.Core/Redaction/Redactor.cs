using System.Text;

namespace Sling.Core.Redaction;

/// <summary>
/// Removes credentials from text on its way into anything that outlives the request.
/// </summary>
/// <remarks>
/// <para>
/// <c>Sling.md</c> §5.4: redaction lives in <c>Sling.Core</c> so it cannot be forgotten at
/// a call site. The shape that actually delivers that is not this class being here - it is
/// that <see cref="History.HistoryEntry"/> has no constructor, only a factory that
/// <em>requires</em> one of these. There is no way to write a history entry that has not
/// been through it.
/// </para>
/// <para>
/// <strong>Two independent lines, because neither covers the other.</strong>
/// </para>
/// <para>
/// The first is <em>provenance</em>: every value the private environment file supplied,
/// plus every access token minted this session, is a known secret and is removed wherever
/// it appears - in a header, in a URL, in a query string, in the middle of a longer
/// string. This is the whitelist-shaped half. It is exact, it needs no guessing about
/// which header names are credentials, and it catches an API key pasted into a query
/// parameter that no name-based rule would recognise.
/// </para>
/// <para>
/// The second is <em>header name</em>: <c>Authorization</c> and its neighbours are
/// replaced whole, whatever their value. This is a deny-list and is admitted as one - it
/// is here to catch the credential that was typed straight into the document rather than
/// referenced from the secrets file. A credential typed into a header nobody has heard of
/// is not caught by either line, and that is stated in <c>docs/history.md</c> rather than
/// papered over: it is also a credential sitting in a file that gets committed, which is
/// the larger problem.
/// </para>
/// <para>
/// Redaction never shortens or hints. No prefix, no last four characters, no length. A
/// token's first eight characters are enough to identify which credential it is, and a
/// history file is a place a screenshot comes from.
/// </para>
/// </remarks>
public sealed class Redactor
{
    /// <summary>What a removed value is replaced with.</summary>
    public const string Marker = "[redacted]";

    /// <summary>
    /// The shortest value that provenance-based redaction will act on.
    /// </summary>
    /// <remarks>
    /// A secrets file legitimately holds short values - a port number, a tenant id of
    /// <c>1</c>, a feature flag of <c>true</c> - and redacting every occurrence of a
    /// two-character string turns a history entry into a row of markers with no
    /// information left in it. Anything genuinely secret is longer than this; anything
    /// shorter is not usefully secret and the header-name line still covers it where it
    /// counts.
    /// </remarks>
    private const int MinimumSecretLength = 8;

    /// <summary>
    /// Headers whose entire value is a credential. Ordinal-ignore-case, because header
    /// names are case-insensitive on the wire and servers disagree about casing.
    /// </summary>
    private static readonly string[] CredentialHeaders =
    [
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "Api-Key",
        "X-Auth-Token",
        "X-Access-Token",
        "X-Csrf-Token",
        "X-Amz-Security-Token",
    ];

    /// <summary>
    /// Query parameters whose value is a credential by convention.
    /// </summary>
    /// <remarks>
    /// Kept tight on purpose. A wide list here is worse than a narrow one: <c>key</c> and
    /// <c>id</c> name ordinary things in most APIs, and redacting them would empty a
    /// history of the detail it exists to hold while adding no safety the provenance line
    /// does not already give.
    /// </remarks>
    private static readonly string[] CredentialParameters =
    [
        "access_token",
        "refresh_token",
        "id_token",
        "client_secret",
        "api_key",
        "apikey",
        "password",
        "signature",
    ];

    private readonly string[] _secrets;

    /// <summary>
    /// Builds a redactor that knows <paramref name="secretValues"/> by sight.
    /// </summary>
    /// <param name="secretValues">
    /// Values from the private environment file, and any access token acquired this
    /// session. Anything too short to be a credential is ignored - see
    /// <see cref="MinimumSecretLength"/>.
    /// </param>
    public Redactor(IEnumerable<string>? secretValues) =>
        _secrets = secretValues is null
            ? []
            : [.. secretValues
                .Where(v => !string.IsNullOrEmpty(v) && v.Length >= MinimumSecretLength)
                .Distinct(StringComparer.Ordinal)
                // Longest first, so a secret that contains another secret is removed whole
                // rather than leaving the outer value half-redacted and still readable.
                .OrderByDescending(v => v.Length)];

    /// <summary>
    /// A redactor that knows no secret values - the header-name and parameter-name lines
    /// still apply.
    /// </summary>
    /// <remarks>
    /// Not "redaction off". There is deliberately no way to turn redaction off: a caller
    /// that has no secret list should still get the deny-list, because the alternative is
    /// a call site that quietly writes an <c>Authorization</c> header into a file.
    /// </remarks>
    public static Redactor WithoutKnownSecrets { get; } = new(null);

    /// <summary>Removes every known secret value from <paramref name="text"/>.</summary>
    public string Text(string? text)
    {
        if (string.IsNullOrEmpty(text) || _secrets.Length == 0)
        {
            return text ?? string.Empty;
        }

        var result = text;

        foreach (var secret in _secrets)
        {
            result = result.Replace(secret, Marker, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// The value to store for a header, which for a credential header is nothing at all.
    /// </summary>
    public string HeaderValue(string name, string? value) =>
        CredentialHeaders.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? Marker
            : Text(value);

    /// <summary>
    /// A URL safe to store: known secrets removed, and credential-named query parameters
    /// emptied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The query is rebuilt from its parts rather than pattern-matched, so a parameter
    /// whose <em>value</em> happens to contain <c>&amp;access_token=</c> cannot fool the
    /// redaction into cutting in the wrong place.
    /// </para>
    /// <para>
    /// The fragment is dropped entirely. It is never sent to a server, so keeping it in a
    /// record of what was sent would be a small lie - and implicit-flow tokens live in
    /// fragments, so it is the one part of a URL most likely to hold a credential.
    /// </para>
    /// </remarks>
    public string Url(Uri? url)
    {
        if (url is null)
        {
            return string.Empty;
        }

        // GetLeftPart(Path) is scheme, authority and path with no query and no fragment,
        // exactly the part that is not a place credentials hide. Userinfo cannot appear:
        // RequestResolver refuses a URL carrying any.
        var head = Component(url.GetLeftPart(UriPartial.Path));

        if (url.Query.Length <= 1)
        {
            return head;
        }

        var builder = new StringBuilder(head);
        var separator = '?';

        foreach (var pair in url.Query[1..].Split('&'))
        {
            if (pair.Length == 0)
            {
                continue;
            }

            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? null : pair[(equals + 1)..];

            builder.Append(separator).Append(Component(name));
            separator = '&';

            if (value is null)
            {
                continue;
            }

            builder
                .Append('=')
                .Append(CredentialParameters.Contains(Uri.UnescapeDataString(name), StringComparer.OrdinalIgnoreCase)
                    ? Marker
                    : Component(value));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Redacts one piece of a URL, which is percent-encoded and so does not literally
    /// contain the secret it carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Text"/> alone matches the raw characters, and a URL holds the escaped
    /// form: a secret containing an accent, a space or anything else
    /// <see cref="Uri"/> encodes reaches the stored URL as <c>s%C3%A9cret</c> and slips
    /// straight past a search for <c>sécret</c>. ASCII secrets are unaffected, which is
    /// why this was invisible.
    /// </para>
    /// <para>
    /// So: redact literally first - that keeps the ordinary case readable, with the secret
    /// cut out of the middle of a longer value - and then check the unescaped remainder.
    /// If a secret is still in there, the whole component goes, because there is no way to
    /// splice a replacement back into the escaped form and be sure of the boundaries.
    /// Losing one path segment or one parameter value is the right price.
    /// </para>
    /// </remarks>
    private string Component(string escaped)
    {
        var redacted = Text(escaped);

        if (_secrets.Length == 0)
        {
            return redacted;
        }

        string unescaped;

        try
        {
            unescaped = Uri.UnescapeDataString(redacted);
        }
        catch (UriFormatException)
        {
            // A malformed escape sequence. Nothing can be said about what it decodes to,
            // so the component is not kept.
            return Marker;
        }

        return _secrets.Any(secret => unescaped.Contains(secret, StringComparison.Ordinal))
            ? Marker
            : redacted;
    }
}
