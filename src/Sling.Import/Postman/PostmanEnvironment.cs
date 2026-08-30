using System.Text;
using System.Text.Json;

namespace Sling.Import.Postman;

/// <summary>
/// A Postman environment export - a different file from a collection, and the one that
/// actually holds the values.
/// </summary>
/// <remarks>
/// <para>
/// Imported alongside a collection because without it the result does not work: an exported
/// collection is full of <c>{{base_url}}</c> and <c>{{token}}</c> references whose values
/// live in the environment file, so a collection imported on its own produces documents
/// that resolve nothing. <c>Sling.md</c> §4a asks the importer for "<c>.http</c> files plus
/// an environment file", and this is where an environment's contents come from.
/// </para>
/// <para>
/// Postman spells "switched off" as <c>enabled: false</c> here and as <c>disabled: true</c>
/// inside a collection - both are read, because a value the owner had switched off is very
/// often a stale token, and resurrecting one is a confusing way for an import to be wrong.
/// </para>
/// </remarks>
internal sealed record PostmanEnvironment(string Name, IReadOnlyList<PostmanPair> Values)
{
    /// <summary>
    /// Words that make a variable name a credential, whatever Postman labelled it.
    /// </summary>
    /// <remarks>
    /// <b>The label cannot be trusted on its own.</b> Postman only marks a value secret when
    /// its owner ticked the box, and most do not - so honouring the flag alone would put a
    /// live bearer token in <c>http-client.env.json</c>, which is the file destined for a
    /// commit. That is the exact failure <c>Sling.md</c> §5.1 exists to prevent, so a name
    /// that reads like a credential is treated as one.
    /// </remarks>
    private static readonly string[] CredentialWords =
    [
        "secret", "token", "password", "passwd", "passphrase", "apikey", "api_key", "api-key",
        "auth", "credential", "bearer", "private", "signature", "signing", "session", "cookie",
    ];

    /// <summary>
    /// Names that are credentials only when they stand as a whole word.
    /// </summary>
    /// <remarks>
    /// <b>The short names are the common ones, and substring matching missed all of them.</b>
    /// <c>apikey</c> matched and a bare <c>key</c> did not; <c>password</c> matched and
    /// <c>pass</c> and <c>pwd</c> did not; <c>jwt</c>, <c>pat</c> and <c>hmac</c> were absent
    /// - so live credentials under the shortest, most-used names went into the file that
    /// gets committed, in a heuristic documented as biased the other way. They cannot go on
    /// the substring list: <c>key</c> would claim <c>keyword</c> and <c>pat</c> would claim
    /// <c>path</c>, and a base URL in the gitignored file breaks a colleague's checkout.
    /// </remarks>
    private static readonly string[] CredentialNames =
        ["key", "keys", "pass", "pwd", "pin", "jwt", "pat", "sig", "hmac", "salt", "cert"];

    /// <summary>
    /// Words that make a name a location rather than a credential.
    /// </summary>
    /// <remarks>
    /// <c>auth_url</c> and <c>authorization_endpoint</c> match <see cref="CredentialWords"/>
    /// and are addresses, not credentials - and putting an address in the gitignored file is
    /// not a safe failure either: a colleague cloning the repository gets a workspace whose
    /// requests do not resolve, with nothing saying why.
    /// </remarks>
    /// <remarks>
    /// <c>base</c> is deliberately not on this list. It would have excused
    /// <c>database_password</c>, which is a credential in a file about to be committed - and
    /// a bare <c>base</c> never reaches this check anyway, because it matches no credential
    /// word in the first place.
    /// </remarks>
    private static readonly string[] LocationWords = ["url", "uri", "endpoint", "host", "domain"];

    /// <summary>Whether the file at hand is an environment export rather than something else.</summary>
    public static bool Looks(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.Property("values") is { ValueKind: JsonValueKind.Array };

    public static PostmanEnvironment Read(JsonElement root, string fallbackName)
    {
        var values = new List<PostmanPair>();

        foreach (var entry in root.Array("values"))
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (entry.Property("enabled") is { ValueKind: JsonValueKind.False } || entry.IsDisabled())
            {
                continue;
            }

            var key = entry.Text("key");

            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            var secret = string.Equals(entry.Text("type"), "secret", StringComparison.OrdinalIgnoreCase)
                || LooksLikeACredential(key);

            values.Add(new PostmanPair(key, entry.Text("value"), secret));
        }

        return new PostmanEnvironment(root.Text("name") ?? fallbackName, values);
    }

    internal static bool LooksLikeACredential(string key)
    {
        var looks = CredentialWords.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase))
            || Words(key).Any(word => CredentialNames.Contains(word, StringComparer.Ordinal));

        return looks && !LocationWords.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Splits a variable name into its words, at punctuation and at camel-case boundaries.
    /// </summary>
    /// <remarks>
    /// Both spellings are everywhere in these files - <c>api_key</c> and <c>apiKey</c> are
    /// the same variable to everyone except a string comparison.
    /// </remarks>
    private static IEnumerable<string> Words(string name)
    {
        var word = new StringBuilder();

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                if (word.Length > 0)
                {
                    yield return word.ToString();
                    word.Clear();
                }

                continue;
            }

            if (char.IsAsciiLetterUpper(c) && word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }

            word.Append(char.ToLowerInvariant(c));
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }
}
