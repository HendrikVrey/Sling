using System.Text.Json;

namespace Sling.Import.Postman;

/// <summary>One <c>{ "key": …, "value": … }</c> pair, as they appear all over the schema.</summary>
/// <param name="Secret">
/// True when Postman marked the value as a secret. Only environment exports carry the
/// flag, and it is what decides which of the two environment files the value lands in
/// (<c>Sling.md</c> §5.1).
/// </param>
internal sealed record PostmanPair(string Key, string? Value, bool Secret = false);

/// <summary>An OAuth2, bearer, basic or API-key auth block, flattened to its parameters.</summary>
/// <remarks>
/// Flattened because the schema puts the parameters under a property named after the type
/// itself — <c>{ "type": "bearer", "bearer": [ … ] }</c> — and every consumer then has to
/// repeat that indirection. Both shapes real exports use are read here: the documented
/// array of pairs, and the object form the app has also emitted.
/// </remarks>
internal sealed record PostmanAuth(string Type, IReadOnlyDictionary<string, string> Parameters)
{
    public string? Get(string key) => Parameters.GetValueOrDefault(key);
}

/// <summary>A pre-request or test script, kept as text and never run.</summary>
internal sealed record PostmanScript(string Kind, string Source);

/// <summary>A URL, in both the shapes the schema allows.</summary>
/// <param name="Raw">
/// The URL as the user typed it, which is the field Postman's own UI shows. Preferred when
/// it is there, because the structured fields below are a parse of it and a hand-edited
/// export can disagree with itself — and when it does, the string the author was looking at
/// is the one they meant.
/// </param>
internal sealed record PostmanUrl(
    string? Raw,
    string? Protocol,
    IReadOnlyList<string> Host,
    string? Port,
    IReadOnlyList<string> Path,
    IReadOnlyList<PostmanPair> Query,
    string? Hash,
    IReadOnlyList<PostmanPair> PathVariables)
{
    public static PostmanUrl Empty { get; } = new(null, null, [], null, [], [], null, []);
}

/// <summary>One part of a <c>formdata</c> body.</summary>
/// <param name="HadMoreSources">
/// True when the field pointed at several files. Only the first is imported: the
/// <c>.http</c> format writes one <c>&lt; ./file</c> per part, so the rest would each need
/// a part of their own, and inventing those would send a body the collection never
/// described. Recorded so the converter can say so rather than dropping them in silence.
/// </param>
internal sealed record PostmanFormPart(
    string Key,
    string? Value,
    string? Source,
    string? ContentType,
    bool IsFile,
    bool HadMoreSources);

/// <summary>A request body, in whichever of Postman's five modes it was written.</summary>
internal sealed record PostmanBody(
    string Mode,
    string? Raw,
    string? RawLanguage,
    IReadOnlyList<PostmanPair> UrlEncoded,
    IReadOnlyList<PostmanFormPart> FormData,
    string? FileSource,
    string? GraphQlQuery,
    string? GraphQlVariables);

/// <summary>A request, as the collection describes it.</summary>
/// <param name="Auth">
/// The auth block, which for a <em>request</em> lives here rather than on the item that
/// holds it — the schema puts it on the request object and only a folder carries one at
/// item level. Reading it from the item alone made a request's explicit
/// <c>{ "type": "noauth" }</c> do nothing, so a collection-wide bearer token was attached
/// to the one request that had asked not to have it.
/// </param>
internal sealed record PostmanRequest(
    string? Method,
    PostmanUrl Url,
    IReadOnlyList<PostmanPair> Headers,
    PostmanBody? Body,
    PostmanAuth? Auth,
    string? Description);

/// <summary>
/// One node of the collection tree: a folder when <see cref="Children"/> is non-null, a
/// request when <see cref="Request"/> is.
/// </summary>
/// <remarks>
/// One type for both because that is how the schema models it — an item with an
/// <c>item</c> array is a folder and an item with a <c>request</c> is a request — and
/// splitting them here would mean deciding what a node carrying both is, which real
/// exports occasionally do after a drag in the app.
/// </remarks>
internal sealed record PostmanItem(
    string? Name,
    string? Description,
    IReadOnlyList<PostmanItem>? Children,
    PostmanRequest? Request,
    PostmanAuth? Auth,
    IReadOnlyList<PostmanScript> Scripts,
    int SavedResponses);

/// <summary>A parsed Postman Collection v2.1 export.</summary>
internal sealed record PostmanCollection(
    string? Name,
    string? Description,
    IReadOnlyList<PostmanItem> Items,
    IReadOnlyList<PostmanPair> Variables,
    PostmanAuth? Auth,
    IReadOnlyList<PostmanScript> Scripts)
{
    /// <summary>
    /// Reads a collection out of an already-parsed document's root.
    /// </summary>
    /// <remarks>
    /// The caller has established that this is a collection (see
    /// <see cref="PostmanImport"/>); this only has to survive it being a badly shaped one.
    /// </remarks>
    public static PostmanCollection Read(JsonElement root)
    {
        // Undefined when 'info' is missing, and every reader below already answers "not
        // there" for a value of that kind — so a collection with no info block reads as one
        // with no name rather than needing a guard at each use.
        var info = root.Property("info") ?? default;

        return new PostmanCollection(
            info.Text("name"),
            info.Description(),
            ReadItems(root),
            ReadPairs(root, "variable"),
            ReadAuth(root),
            ReadScripts(root));
    }

    private static IReadOnlyList<PostmanItem> ReadItems(JsonElement parent) =>
        [.. parent.Array("item")
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(ReadItem)];

    private static PostmanItem ReadItem(JsonElement element)
    {
        var request = element.Property("request");

        return new PostmanItem(
            element.Text("name"),
            element.Description(),
            element.Property("item") is { ValueKind: JsonValueKind.Array } ? ReadItems(element) : null,
            request is { ValueKind: JsonValueKind.Object or JsonValueKind.String }
                ? ReadRequest(request.Value)
                : null,
            ReadAuth(element),
            ReadScripts(element),
            element.Array("response").Count());
    }

    /// <summary>
    /// Reads a request, which the schema also allows to be a bare URL string.
    /// </summary>
    private static PostmanRequest ReadRequest(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return new PostmanRequest(
                "GET",
                PostmanUrl.Empty with { Raw = element.Str() },
                [],
                Body: null,
                Auth: null,
                Description: null);
        }

        return new PostmanRequest(
            element.Text("method"),
            ReadUrl(element.Property("url")),
            ReadHeaders(element),
            ReadBody(element.Property("body")),
            ReadAuth(element),
            element.Description());
    }

    /// <summary>
    /// Reads the headers, which the schema allows as an array of pairs or as one raw block
    /// of <c>Name: value</c> lines.
    /// </summary>
    private static IReadOnlyList<PostmanPair> ReadHeaders(JsonElement request)
    {
        if (request.Property("header") is { ValueKind: JsonValueKind.String } raw)
        {
            return
            [
                .. (raw.Str() ?? string.Empty)
                    .Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .Select(line => line.IndexOf(':', StringComparison.Ordinal) is var colon && colon > 0
                        ? new PostmanPair(line[..colon].Trim(), line[(colon + 1)..].Trim())
                        : new PostmanPair(line, null)),
            ];
        }

        return ReadPairs(request, "header");
    }

    private static PostmanUrl ReadUrl(JsonElement? url)
    {
        if (url is not { } element)
        {
            return PostmanUrl.Empty;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return PostmanUrl.Empty with { Raw = element.Str() };
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return PostmanUrl.Empty;
        }

        return new PostmanUrl(
            element.Text("raw"),
            element.Text("protocol"),
            ReadStrings(element, "host"),
            element.Text("port"),
            ReadStrings(element, "path"),
            ReadPairs(element, "query"),
            element.Text("hash"),
            ReadPairs(element, "variable"));
    }

    /// <summary>
    /// Reads a string array whose elements Postman sometimes writes as objects.
    /// </summary>
    /// <remarks>
    /// A path segment holding a variable arrives as <c>{ "value": ":id" }</c> in some
    /// exports and as <c>":id"</c> in others. Reading only the string form drops the
    /// segment, which silently shortens the path — a request to the wrong resource rather
    /// than a request that fails.
    /// </remarks>
    private static IReadOnlyList<string> ReadStrings(JsonElement parent, string name) =>
        [.. parent.Array(name)
            .Select(e => e.ValueKind switch
            {
                JsonValueKind.String => e.Str(),
                JsonValueKind.Object => e.Text("value"),
                JsonValueKind.Number => e.GetRawText(),
                _ => null,
            })
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)];

    private static PostmanBody? ReadBody(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        var graphql = element.Property("graphql");

        return new PostmanBody(
            element.Text("mode") ?? string.Empty,
            element.Text("raw"),
            element.Property("options")?.Property("raw")?.Text("language"),
            ReadPairs(element, "urlencoded"),
            ReadFormData(element),
            element.Property("file")?.Text("src"),
            graphql?.Text("query"),
            graphql?.Text("variables"));
    }

    private static List<PostmanFormPart> ReadFormData(JsonElement body)
    {
        var parts = new List<PostmanFormPart>();

        foreach (var part in body.Array("formdata"))
        {
            if (part.ValueKind != JsonValueKind.Object || part.IsDisabled())
            {
                continue;
            }

            var sources = Sources(part);

            parts.Add(new PostmanFormPart(
                part.Text("key") ?? string.Empty,
                part.Text("value"),
                sources.FirstOrDefault(),
                part.Text("contentType"),
                string.Equals(part.Text("type"), "file", StringComparison.OrdinalIgnoreCase),
                sources.Count > 1));
        }

        return parts;
    }

    /// <summary>
    /// The files a form part points at — one, or several when the user attached several to
    /// the same field.
    /// </summary>
    private static List<string> Sources(JsonElement part)
    {
        if (part.Property("src") is not { } source)
        {
            return [];
        }

        if (source.ValueKind != JsonValueKind.Array)
        {
            return source.AsText() is { } single ? [single] : [];
        }

        return [.. source.AsArray().Select(e => e.AsText()).Where(s => s is not null).Select(s => s!)];
    }

    private static IReadOnlyList<PostmanPair> ReadPairs(JsonElement parent, string name) =>
        [.. parent.Array(name)
            .Where(e => e.ValueKind == JsonValueKind.Object && !e.IsDisabled() && !IsSwitchedOff(e))
            .Select(e => new PostmanPair(
                e.Text("key") ?? string.Empty,
                e.Text("value"),
                string.Equals(e.Text("type"), "secret", StringComparison.OrdinalIgnoreCase)))
            .Where(p => p.Key.Length > 0)];

    /// <summary>
    /// The other spelling of "the user unticked this".
    /// </summary>
    /// <remarks>
    /// Collection variables and headers carry <c>disabled: true</c>; an environment
    /// export's values carry <c>enabled: false</c> instead. Two names for one idea, both in
    /// current exports — and reading only one of them imports variables the owner had
    /// switched off, which for an environment is how a stale token gets resurrected.
    /// </remarks>
    private static bool IsSwitchedOff(JsonElement element) =>
        element.Property("enabled") is { ValueKind: JsonValueKind.False };

    private static PostmanAuth? ReadAuth(JsonElement parent)
    {
        // Absent and null both mean "inherit from the parent". Postman writes an explicit
        // { "type": "noauth" } when the user chose No Auth, so the two really are different
        // and treating null as noauth would strip a collection-level credential from every
        // request in an export that spells inheritance that way.
        if (parent.Property("auth") is not { ValueKind: JsonValueKind.Object } auth)
        {
            return null;
        }

        var type = auth.Text("type");

        if (string.IsNullOrEmpty(type))
        {
            return null;
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (auth.Property(type) is { } block)
        {
            // The documented shape: an array of { key, value } pairs.
            foreach (var entry in block.AsArray())
            {
                if (entry.Text("key") is { } key && entry.Text("value") is { } value)
                {
                    parameters[key] = value;
                }
            }

            // The shape the app has also emitted: one object of named values. Both are in
            // current exports, and reading only the documented one loses the credential
            // entirely on the other half of them.
            foreach (var property in block.AsObject())
            {
                if (property.Value.AsText() is { } value)
                {
                    parameters[property.Name] = value;
                }
            }
        }

        return new PostmanAuth(type, parameters);
    }

    private static IReadOnlyList<PostmanScript> ReadScripts(JsonElement parent) =>
        [.. parent.Array("event")
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(e => (Kind: e.Text("listen"), Source: e.ScriptSource()))
            .Where(e => e.Source is not null)
            .Select(e => new PostmanScript(
                string.Equals(e.Kind, "test", StringComparison.OrdinalIgnoreCase) ? "test" : "pre-request",
                e.Source!))];
}
