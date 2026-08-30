using System.Text.Json;

namespace Sling.Import.Postman;

/// <summary>
/// Defensive readers over a Postman export's JSON.
/// </summary>
/// <remarks>
/// <para>
/// Hand-walked with <see cref="JsonDocument"/> rather than deserialised into a model, the
/// same choice <c>EnvironmentFile</c> made and for the same reasons: the schema is large,
/// mostly optional, and real exports disagree with it in corners - several fields are
/// documented as one shape and emitted as two. A model would turn each of those into an
/// exception naming a type nobody has heard of, where a walk turns it into a note naming
/// the request. It also keeps <c>Sling.Import</c> AOT-compatible with no source generation.
/// </para>
/// <para>
/// <b>Nothing here throws for a wrong shape.</b> A collection is a file from somewhere
/// else, so every reader answers "not there" for anything that is not what it wanted, and
/// the converter decides whether that is worth a note. The one exception is the top-level
/// parse, whose failure is the whole document's failure.
/// </para>
/// </remarks>
internal static class PostmanJson
{
    /// <summary>An object's property, or nothing.</summary>
    public static JsonElement? Property(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var found)
            ? found
            : null;

    /// <summary>
    /// A property as text.
    /// </summary>
    /// <remarks>
    /// Numbers and booleans come back as the text that was written, because a port given as
    /// <c>8080</c> rather than <c>"8080"</c> is the commonest deviation in these files and
    /// it substitutes into a URL as text either way. An empty string reads as absent: a
    /// Postman field that is present and blank means the same thing as one that is missing
    /// everywhere in this format, and distinguishing them would only produce
    /// <c>": "</c> headers.
    /// </remarks>
    public static string? Text(this JsonElement element, string name) =>
        element.Property(name)?.AsText();

    /// <summary>A value as text, or nothing when it is not one.</summary>
    public static string? AsText(this JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.Str(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };

        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// <see cref="JsonElement.GetString"/>, except that it cannot throw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every string in this project's Postman reading goes through here, and it is not
    /// defensive tidiness.</b> <c>"\ud800"</c> - a lone surrogate - is <em>syntactically
    /// valid JSON</em>, so <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/>
    /// accepts it and <see cref="JsonElement.GetString"/> throws
    /// <see cref="InvalidOperationException"/> later, at read time, long past the only place
    /// a parse failure is caught. One escape sequence anywhere in a published collection
    /// therefore killed the whole import with a framework message about UTF-16.
    /// </para>
    /// <para>
    /// The value reads as absent, which every caller already handles - a URL that vanishes
    /// becomes "this request has no URL", a name that vanishes becomes a fallback. Absent is
    /// the honest answer: an unpaired surrogate is not text, and there is nothing to
    /// preserve.
    /// </para>
    /// </remarks>
    public static string? Str(this JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>An array's elements, or none, for an element that is the array itself.</summary>
    public static IEnumerable<JsonElement> AsArray(this JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return element.EnumerateArray();
    }

    /// <summary>An object's properties, or none.</summary>
    public static IEnumerable<JsonProperty> AsObject(this JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return element.EnumerateObject();
    }

    /// <summary>A property that is an array, as its elements, or none.</summary>
    public static IEnumerable<JsonElement> Array(this JsonElement element, string name)
    {
        if (element.Property(name) is not { } property)
        {
            return [];
        }

        return property.AsArray();
    }

    /// <summary>
    /// Whether an item is switched off in Postman's UI.
    /// </summary>
    /// <remarks>
    /// A disabled header, query parameter or form field is one the user unticked, and
    /// Postman does not send it. Importing it would produce a request that differs from the
    /// one they were running, which is the failure mode this whole importer exists to
    /// avoid. Anything other than a literal <c>true</c> counts as enabled, including the
    /// string <c>"true"</c> - guessing at a string here would silently drop a field on an
    /// export that spells the flag differently.
    /// </remarks>
    public static bool IsDisabled(this JsonElement element) =>
        element.Property("disabled") is { ValueKind: JsonValueKind.True };

    /// <summary>
    /// A description, which Postman writes as either a string or
    /// <c>{ "content": "…", "type": "text/markdown" }</c>.
    /// </summary>
    /// <remarks>
    /// Both shapes are in real exports - the object form arrives from the API, the string
    /// form from the app - and reading only one of them silently loses every description in
    /// half the collections there are.
    /// </remarks>
    public static string? Description(this JsonElement element)
    {
        if (element.Property("description") is not { } description)
        {
            return null;
        }

        return description.ValueKind switch
        {
            JsonValueKind.String => Blank(description.Str()),
            JsonValueKind.Object => description.Text("content"),
            _ => null,
        };
    }

    /// <summary>
    /// The lines of a script block, which Postman writes as <c>exec</c> - an array of lines,
    /// or occasionally one string.
    /// </summary>
    public static string? ScriptSource(this JsonElement scriptEvent)
    {
        if (scriptEvent.Property("script") is not { } script)
        {
            return null;
        }

        if (script.Property("exec") is not { } exec)
        {
            return null;
        }

        return exec.ValueKind switch
        {
            JsonValueKind.String => Blank(exec.Str()),
            JsonValueKind.Array => Blank(string.Join(
                '\n',
                exec.EnumerateArray()
                    .Where(l => l.ValueKind == JsonValueKind.String)
                    .Select(l => l.Str()))),
            _ => null,
        };
    }

    private static string? Blank(string? text) => string.IsNullOrEmpty(text) ? null : text;
}
