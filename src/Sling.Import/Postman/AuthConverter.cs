using System.Text;

namespace Sling.Import.Postman;

/// <summary>
/// What a Postman auth block turns into: headers, query parameters, and the
/// <c># @auth oauth2</c> directives that sit above the request line.
/// </summary>
internal sealed record AuthPlan(
    IReadOnlyList<PostmanPair> Headers,
    IReadOnlyList<PostmanPair> QueryParameters,
    IReadOnlyList<string> Directives)
{
    public static AuthPlan None { get; } = new([], [], []);
}

/// <summary>
/// Converts Postman's auth blocks into the equivalent <c>.http</c> construct.
/// </summary>
/// <remarks>
/// <para>
/// <b>No credential found here is ever written into the document.</b> Every literal value
/// goes through <see cref="ImportContext.Reference"/> and lands in the gitignored secrets
/// file, leaving a <c>{{name}}</c> behind - see <c>Sling.md</c> §5.1. A collection carrying
/// a live token is the normal case rather than the exceptional one, and an importer that
/// wrote it into a file destined for a commit would be the single most damaging thing in
/// this product.
/// </para>
/// <para>
/// Auth is inherited in Postman: a request with no block of its own uses its folder's, and
/// a folder with none uses the collection's. The nearest block wins, and
/// <c>{ "type": "noauth" }</c> is a real answer that stops the search - which is why an
/// absent block and an explicit "no auth" have to stay distinguishable all the way from the
/// JSON reader.
/// </para>
/// </remarks>
internal static class AuthConverter
{
    public static AuthPlan Convert(PostmanAuth? auth, HttpWriter writer, ImportContext context)
    {
        if (auth is null || auth.Type.Equals("noauth", StringComparison.OrdinalIgnoreCase))
        {
            return AuthPlan.None;
        }

        return auth.Type.ToLowerInvariant() switch
        {
            "bearer" => Bearer(auth, writer, context),
            "basic" => Basic(auth, writer, context),
            "apikey" => ApiKey(auth, writer, context),
            "oauth2" => OAuth2(auth, writer, context),
            _ => Unsupported(auth, writer),
        };
    }

    private static AuthPlan Bearer(PostmanAuth auth, HttpWriter writer, ImportContext context)
    {
        if (context.Reference("bearer_token", auth.Get("token"), secret: true) is not { } token)
        {
            writer.Note("Postman used bearer auth here but the collection carried no token.");
            return AuthPlan.None;
        }

        return new AuthPlan([new PostmanPair("Authorization", "Bearer " + token)], [], []);
    }

    /// <summary>
    /// Basic auth, which is the one type that cannot always be converted.
    /// </summary>
    /// <remarks>
    /// A Basic header is base64 of <c>user:password</c>, so it can only be built when both
    /// halves are known - and Postman collections very often hold <c>{{username}}</c> and
    /// <c>{{password}}</c>, whose values live in an environment this importer may not have
    /// been given. There is no <c>.http</c> construct that encodes at send time, so the
    /// honest output is a reference that <em>fails loudly</em> plus a note saying exactly
    /// what to put where. Declaring the variable with an empty value would be friendlier and
    /// wrong: the request would then send <c>Authorization: Basic</c> and come back 401,
    /// which is a debugging session rather than an error message.
    /// </remarks>
    private static AuthPlan Basic(PostmanAuth auth, HttpWriter writer, ImportContext context)
    {
        var user = auth.Get("username") ?? string.Empty;
        var password = auth.Get("password") ?? string.Empty;

        if (user.Length == 0 && password.Length == 0)
        {
            writer.Note("Postman used basic auth here but the collection carried no credentials.");
            return AuthPlan.None;
        }

        if (user.Contains("{{", StringComparison.Ordinal) || password.Contains("{{", StringComparison.Ordinal))
        {
            writer.Note(
                "Postman used basic auth with variables for the credentials, and a Basic header "
                    + "is base64 of 'user:password' - which cannot be assembled from variables. "
                    + "Put the encoded value in http-client.private.env.json as 'basic_auth'. "
                    + "Until you do, this request will refuse to send rather than authenticate "
                    + "as nobody.");

            return new AuthPlan([new PostmanPair("Authorization", "Basic {{basic_auth}}")], [], []);
        }

        var encoded = System.Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
        var reference = context.Reference("basic_auth", encoded, secret: true);

        return new AuthPlan([new PostmanPair("Authorization", "Basic " + reference)], [], []);
    }

    private static AuthPlan ApiKey(PostmanAuth auth, HttpWriter writer, ImportContext context)
    {
        var name = TextSafety.StripControl(auth.Get("key") ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            writer.Note("Postman used an API key here but the collection did not say what to call it.");
            return AuthPlan.None;
        }

        var value = context.Reference("api_key", auth.Get("value"), secret: true) ?? string.Empty;
        var pair = new PostmanPair(name, value);

        // Postman's field is 'in', and 'query' means the key rides in the query string. That
        // is a credential in a URL, which ends up in server logs - worth saying once, since
        // the collection's author may not have chosen it deliberately.
        if (string.Equals(auth.Get("in"), "query", StringComparison.OrdinalIgnoreCase))
        {
            writer.Note(
                "The API key goes in the query string, as the collection had it. A credential "
                    + "in a URL is logged by most servers and proxies; a header is safer if the "
                    + "API accepts one.");

            return new AuthPlan([], [pair], []);
        }

        return new AuthPlan([pair], [], []);
    }

    /// <summary>
    /// OAuth2, of which exactly one grant survives the trip.
    /// </summary>
    /// <remarks>
    /// Client credentials becomes a real <c># @auth oauth2</c> block, which is the whole
    /// reason that syntax exists (<c>Sling.md</c> §4e). Every other grant needs a browser, a
    /// redirect listener and a consent screen - a different product, and one the README
    /// already says Sling is not (§1 non-goals). A collection holding a static
    /// <c>accessToken</c> is carried across as a bearer header, because that part of it does
    /// work; what is lost is the ability to refresh it, and the note says so.
    /// </remarks>
    private static AuthPlan OAuth2(PostmanAuth auth, HttpWriter writer, ImportContext context)
    {
        var grant = auth.Get("grant_type") ?? string.Empty;
        var tokenUrl = auth.Get("accessTokenUrl");

        if (grant.Equals("client_credentials", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(tokenUrl))
        {
            return ClientCredentials(auth, writer, context, tokenUrl);
        }

        if (context.Reference("access_token", auth.Get("accessToken"), secret: true) is { } token)
        {
            var prefix = TextSafety.StripControl(auth.Get("headerPrefix") ?? "Bearer").Trim();

            writer.Note(
                $"Postman used the OAuth2 '{HttpWriter.Describe(grant)}' grant, which Sling does "
                    + "not run - it needs a browser and a redirect listener. The access token "
                    + "the collection had is used below, and will stop working when it expires.");

            return new AuthPlan(
                [new PostmanPair("Authorization", (prefix.Length == 0 ? "Bearer" : prefix) + " " + token)],
                [],
                []);
        }

        writer.Note(
            $"Postman used the OAuth2 '{HttpWriter.Describe(grant)}' grant. Sling supports the "
                + "client-credentials grant only - see docs/http-dialect.md for '# @auth oauth2'. "
                + "Send a token you already have as an Authorization header instead.");

        return AuthPlan.None;
    }

    private static AuthPlan ClientCredentials(
        PostmanAuth auth,
        HttpWriter writer,
        ImportContext context,
        string tokenUrl)
    {
        // The id is not a credential and belongs in the file that gets committed; the secret
        // is, and does not. Putting both in the same place would be simpler and would defeat
        // the point of there being two files.
        var clientId = context.Reference("oauth2_client_id", auth.Get("clientId"), secret: false);
        var clientSecret = context.Reference("oauth2_client_secret", auth.Get("clientSecret"), secret: true);

        if (clientId is null || clientSecret is null)
        {
            writer.Note(
                "The OAuth2 block below is missing a client "
                    + (clientId is null ? "id" : "secret")
                    + " - the collection did not carry one. Fill it in before sending, or the "
                    + "request will refuse rather than authenticate as nobody.");
        }

        var directives = new List<string>
        {
            "@auth oauth2",
            "@token-url " + TextSafety.StripControl(tokenUrl).Trim(),
        };

        Directive(directives, "@client-id", clientId);
        Directive(directives, "@client-secret", clientSecret);
        Directive(directives, "@scope", auth.Get("scope"));
        Directive(directives, "@audience", auth.Get("audience"));

        // Postman spells these 'header' and 'body'; Sling's directive spells the first
        // 'basic', because that is what RFC 6749 §2.3.1 calls it.
        if (string.Equals(auth.Get("client_authentication"), "body", StringComparison.OrdinalIgnoreCase))
        {
            directives.Add("@client-auth body");
        }

        if (string.Equals(auth.Get("addTokenTo"), "queryParams", StringComparison.OrdinalIgnoreCase))
        {
            writer.Note(
                "Postman put the OAuth2 token in the query string. Sling always sends it as an "
                    + "Authorization header, which is what the request below does.");
        }

        return new AuthPlan([], [], directives);
    }

    private static void Directive(List<string> directives, string name, string? value)
    {
        var clean = TextSafety.StripControl(value ?? string.Empty).Trim();

        if (clean.Length > 0)
        {
            directives.Add(name + " " + clean);
        }
    }

    private static AuthPlan Unsupported(PostmanAuth auth, HttpWriter writer)
    {
        writer.Note(
            $"Postman used '{HttpWriter.Describe(auth.Type)}' auth here, which Sling has no "
                + "equivalent for. The request below is unauthenticated - add whatever header "
                + "the API expects.");

        return AuthPlan.None;
    }
}
