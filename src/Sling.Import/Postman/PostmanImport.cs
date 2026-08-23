using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sling.Import.Postman;

/// <summary>
/// Turns a Postman export into a folder of <c>.http</c> files and the two environment
/// files.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the feature that makes "switching" a real word</b> (<c>Sling.md</c> §4a).
/// Without it, moving off Postman means retyping every request, and nobody does that — so
/// it is worth more than any three features on the milestone list even though it lands
/// late, because an import into a tool that is not yet good is a wasted import.
/// </para>
/// <para>
/// <b>Pure: text in, files out.</b> Nothing here reads or writes a file, which is what makes
/// it testable against a corpus of exports. Writing the result is
/// <c>Sling.Persistence</c>'s job, and it checks containment again before it does.
/// </para>
/// <para>
/// <b>Two security rules run through the whole of it.</b> Nothing found in a collection is
/// ever executed, including its script blocks (§5.8) — they are copied out as comments and
/// nothing more. And no literal credential is ever written into a <c>.http</c> file (§5.1);
/// it goes to the gitignored environment file and the document gets a <c>{{name}}</c>.
/// </para>
/// </remarks>
public static class PostmanImport
{
    /// <summary>
    /// A ceiling on how many requests one import may produce.
    /// </summary>
    /// <remarks>
    /// A collection is written by a person and never approaches this. A file that does is
    /// either generated or hostile, and either way the useful answer is to import what fits
    /// and say that it stopped — not to spend an unbounded amount of time on the dispatcher
    /// producing a workspace nobody can read.
    /// </remarks>
    private const int MaxRequests = 5000;

    /// <summary>A ceiling on how many files one import may produce, for the same reason.</summary>
    private const int MaxFiles = 500;

    /// <summary>
    /// Converts everything handed in: one collection, and any environment exports beside it.
    /// </summary>
    /// <remarks>
    /// The documents are classified by shape rather than by file name, so the user can select
    /// a collection and its environments together in one dialog and does not have to tell
    /// Sling which is which. Anything that is neither is named and skipped — silently
    /// ignoring a file somebody deliberately selected is how an import comes out missing
    /// half its environments with no explanation.
    /// </remarks>
    public static PostmanImportResult Convert(IReadOnlyList<PostmanSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var notes = new List<string>();
        var collections = new List<(PostmanCollection Collection, string From)>();
        var environments = new List<PostmanEnvironment>();

        foreach (var source in sources)
        {
            Classify(source, collections, environments, notes);
        }

        if (collections.Count == 0 && environments.Count == 0)
        {
            return PostmanImportResult.Unrecognized(notes);
        }

        if (collections.Count > 1)
        {
            notes.Add(
                $"{collections.Count.ToString(CultureInfo.InvariantCulture)} collections were "
                    + "selected. Each one becomes its own folder.");
        }

        var names = new FileNames();
        var context = new ImportContext();
        var files = new List<ImportedFile>();

        foreach (var (collection, from) in collections)
        {
            WriteCollection(collection, from, names, context, files, notes);
        }

        WriteEnvironments(environments, context, names, files, notes);

        if (files.Count == 0)
        {
            notes.Add("Nothing in the selected files turned into a request.");
        }

        return new PostmanImportResult(files, notes, Recognized: true);
    }

    /// <summary>Converts a single document, for callers with only one.</summary>
    public static PostmanImportResult Convert(string name, string json) =>
        Convert([new PostmanSource(name, json)]);

    private static void Classify(
        PostmanSource source,
        List<(PostmanCollection, string)> collections,
        List<PostmanEnvironment> environments,
        List<string> notes)
    {
        JsonDocument document;

        try
        {
            // Comments and trailing commas are tolerated for the same reason the environment
            // files tolerate them: an export gets hand-edited, and refusing the whole file
            // over a comma somebody left behind helps nobody.
            document = JsonDocument.Parse(
                source.Json,
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
        }
        catch (JsonException ex)
        {
            // LineNumber is 0-based, and the byte position is deliberately not reported: it
            // counts UTF-8 bytes and would be wrong as a column on any line holding an
            // accent, an ideograph or an emoji.
            var line = ex.LineNumber is { } number
                ? $" (line {(number + 1).ToString(CultureInfo.InvariantCulture)})"
                : string.Empty;

            // "or nests deeper than" is not padding: JsonDocument's default depth limit is 64,
            // and a valid collection that exceeds it arrives here as a JsonException like any
            // syntax error. Saying only "not valid JSON" would send someone looking for a
            // missing brace that is not there.
            notes.Add(
                $"'{Describe(source.Name)}' is not valid JSON{line}, or nests deeper than 64 "
                    + "levels, and was skipped.");

            return;
        }
        catch (ArgumentException)
        {
            // Parse transcodes to UTF-8 before parsing, so a lone surrogate raises this
            // rather than a JsonException.
            notes.Add($"'{Describe(source.Name)}' is not text this can read, and was skipped.");
            return;
        }

        using (document)
        {
            var root = document.RootElement;

            if (LooksLikeACollection(root))
            {
                collections.Add((PostmanCollection.Read(root), source.Name));
                return;
            }

            if (PostmanEnvironment.Looks(root))
            {
                environments.Add(PostmanEnvironment.Read(root, FallbackEnvironmentName(source.Name)));
                return;
            }

            notes.Add(
                $"'{Describe(source.Name)}' is neither a Postman collection nor an environment "
                    + "export, and was skipped.");
        }
    }

    /// <summary>
    /// Whether a document is a collection.
    /// </summary>
    /// <remarks>
    /// The schema URL is the honest test and the <c>item</c> array is the fallback, because
    /// a collection assembled by a script or trimmed by hand often has no <c>info</c> block
    /// at all. Version 2.0 exports satisfy both and are read as 2.1: the differences are in
    /// fields this importer does not use, and refusing them would turn a working import into
    /// an error message about a number.
    /// </remarks>
    private static bool LooksLikeACollection(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var schema = root.Property("info")?.Text("schema") ?? string.Empty;

        return schema.Contains("collection", StringComparison.OrdinalIgnoreCase)
            || root.Property("item") is { ValueKind: JsonValueKind.Array };
    }

    private static void WriteCollection(
        PostmanCollection collection,
        string from,
        FileNames names,
        ImportContext context,
        List<ImportedFile> files,
        List<string> notes)
    {
        foreach (var variable in collection.Variables)
        {
            context.Declare(
                variable.Key,
                variable.Value ?? string.Empty,
                variable.Secret || PostmanEnvironment.LooksLikeACredential(variable.Key));
        }

        var state = new WalkState(names, context, files, notes);

        Walk(
            collection.Items,
            directory: [],
            fileName: collection.Name ?? "requests",
            isRoot: true,
            inherited: collection.Auth,
            provenance: Provenance(collection, from),
            description: collection.Description,
            scripts: collection.Scripts,
            state);
    }

    /// <summary>
    /// The header naming where the file came from.
    /// </summary>
    /// <remarks>
    /// Kept apart from the collection's <em>description</em>, which is the author's writing
    /// and is content. This is the importer's own, and a document holding nothing else is a
    /// document not worth writing.
    /// </remarks>
    private static string Provenance(PostmanCollection collection, string from)
    {
        var heading = new StringBuilder();

        heading.Append("Imported from ").Append(from).Append('\n');

        if (!string.IsNullOrEmpty(collection.Name))
        {
            heading.Append(collection.Name).Append('\n');
        }

        return heading.ToString();
    }

    /// <summary>What every level of the walk needs, so the recursion carries four arguments rather than eight.</summary>
    private sealed record WalkState(
        FileNames Names,
        ImportContext Context,
        List<ImportedFile> Files,
        List<string> Notes)
    {
        public int Requests { get; set; }

        public bool ReportedDepth { get; set; }

        public bool ReportedRequestLimit { get; set; }

        public bool ReportedFileLimit { get; set; }
    }

    /// <summary>
    /// Writes one level of the collection tree, then recurses into its folders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape of the output is <c>Sling.md</c> §1's "a collection becomes a folder of
    /// <c>.http</c> files" made concrete: <b>one file per Postman folder</b>, named after it,
    /// sitting in a directory named after its ancestors. A top-level folder <c>Orders</c>
    /// becomes <c>orders.http</c>; <c>Orders/Refunds</c> becomes <c>orders/refunds.http</c>.
    /// Requests inside a file are separated by <c>###</c>, which is the format's own grouping.
    /// </para>
    /// <para>
    /// The collection's own root requests go in a file named after the collection, and its
    /// top-level folders sit beside that file rather than under it — nesting everything one
    /// level deeper to preserve a name that is already the folder's would only make every
    /// path longer.
    /// </para>
    /// </remarks>
    private static void Walk(
        IReadOnlyList<PostmanItem> items,
        IReadOnlyList<string> directory,
        string fileName,
        bool isRoot,
        PostmanAuth? inherited,
        string? provenance,
        string? description,
        IReadOnlyList<PostmanScript> scripts,
        WalkState state)
    {
        var writer = new HttpWriter();

        writer.Comment(provenance);

        // Everything above this line is the importer's own; everything below it came out of
        // the collection. The split is what decides whether this file is worth writing when
        // it holds no requests.
        writer.MarkBoilerplate();

        writer.Comment(description);

        // A folder-level script runs before every request in it, so it belongs to the file
        // rather than to any one request. Reported once here instead of once per request,
        // which on a twenty-request folder is the difference between a note and a wall.
        foreach (var script in scripts)
        {
            writer.Script((isRoot ? "collection-level " : "folder-level ") + script.Kind, script.Source);
        }

        foreach (var item in items)
        {
            if (item.Request is not { } request)
            {
                continue;
            }

            if (state.Requests >= MaxRequests)
            {
                if (!state.ReportedRequestLimit)
                {
                    state.ReportedRequestLimit = true;
                    state.Notes.Add(
                        $"The import stopped at {MaxRequests.ToString(CultureInfo.InvariantCulture)} "
                            + "requests. Whatever is past that point in the collection is not here.");
                }

                break;
            }

            state.Requests++;

            RequestConverter.Write(item, request, inherited, writer, state.Context);
        }

        if (writer.HasContent)
        {
            if (state.Files.Count >= MaxFiles)
            {
                if (!state.ReportedFileLimit)
                {
                    state.ReportedFileLimit = true;
                    state.Notes.Add(
                        $"The import stopped at {MaxFiles.ToString(CultureInfo.InvariantCulture)} "
                            + "files. Some of the collection's folders are not here.");
                }
            }
            else
            {
                state.Files.Add(state.Names.Create(directory, fileName, ".http", writer.ToString()));

                if (writer.NoteCount > 0)
                {
                    state.Notes.Add(
                        $"{state.Files[^1].RelativePath}: "
                            + $"{writer.NoteCount.ToString(CultureInfo.InvariantCulture)} thing"
                            + (writer.NoteCount == 1 ? " is" : "s are")
                            + " noted in the file.");
                }
            }
        }

        List<string> childDirectory = isRoot ? [.. directory] : [.. directory, fileName];

        if (childDirectory.Count > FileNames.MaxDepth && !state.ReportedDepth)
        {
            state.ReportedDepth = true;
            state.Notes.Add(
                "The collection nests folders deeper than the import writes directories, so the "
                    + "deepest ones share a directory. Their files still have separate names.");
        }

        foreach (var item in items)
        {
            if (item.Children is { } children)
            {
                Walk(
                    children,
                    childDirectory,
                    item.Name ?? "folder",
                    isRoot: false,
                    item.Auth ?? inherited,
                    provenance: null,
                    description: item.Description,
                    item.Scripts,
                    state);
            }
        }
    }

    /// <summary>
    /// Writes the two environment files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The split between them is <c>Sling.md</c> §5.1 made structural, and it is the reason
    /// this importer can do something the curl one cannot: everything that looks like a
    /// credential goes to <c>http-client.private.env.json</c>, which Sling adds to
    /// <c>.gitignore</c> itself, and everything else goes to the file that gets committed.
    /// </para>
    /// <para>
    /// A collection's own variables and the generated references land under <c>$shared</c>,
    /// because they do not differ per deployment; each Postman environment becomes an
    /// environment of the same name. That is exactly the layering
    /// <c>EnvironmentSet.Select</c> already implements, so nothing new had to be invented for
    /// it — which is the payoff for having chosen Rider's and Visual Studio's file names.
    /// </para>
    /// </remarks>
    private static void WriteEnvironments(
        IReadOnlyList<PostmanEnvironment> environments,
        ImportContext context,
        FileNames names,
        List<ImportedFile> files,
        List<string> notes)
    {
        var committed = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var secret = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        if (context.Shared.Count > 0)
        {
            committed["$shared"] = new Dictionary<string, string>(context.Shared, StringComparer.Ordinal);
        }

        if (context.Secret.Count > 0)
        {
            secret["$shared"] = new Dictionary<string, string>(context.Secret, StringComparer.Ordinal);
        }

        var guessed = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var environment in environments)
        {
            var name = environment.Name.Trim();

            if (name.Length == 0 || string.Equals(name, "$shared", StringComparison.Ordinal))
            {
                notes.Add("An environment had no usable name and was skipped.");
                continue;
            }

            // Two exports carrying the same name is one dialog action away — two workspaces,
            // or a re-export beside the original. Assigning would have dropped the first
            // entirely and said nothing, so they merge and the collision is reported.
            if (!seen.Add(name))
            {
                notes.Add(
                    $"Two environments are called '{Describe(name)}'. They were merged; where "
                        + "both set the same variable, the later file won.");
            }

            guessed += Merge(secret, name, environment.Values.Where(v => v.Secret));
            Merge(committed, name, environment.Values.Where(v => !v.Secret));
        }

        if (committed.Count > 0)
        {
            files.Add(names.CreateFixed("http-client.env.json", Json(committed)));
        }

        if (secret.Count > 0)
        {
            files.Add(names.CreateFixed("http-client.private.env.json", Json(secret)));

            notes.Add(
                $"{(guessed + context.Secret.Count).ToString(CultureInfo.InvariantCulture)} value"
                    + (guessed + context.Secret.Count == 1 ? "" : "s")
                    + " went into http-client.private.env.json, which is gitignored. Postman only "
                    + "marks a value secret when its owner ticked the box, so the split was partly "
                    + "guessed from the names — read both files before you commit.");
        }
    }

    /// <summary>
    /// Folds one environment's values into the file being built, and reports how many landed.
    /// </summary>
    private static int Merge(
        Dictionary<string, Dictionary<string, string>> into,
        string environment,
        IEnumerable<PostmanPair> values)
    {
        var added = 0;

        foreach (var value in values)
        {
            var key = TextSafety.StripControl(value.Key).Trim();

            if (key.Length == 0)
            {
                continue;
            }

            if (!into.TryGetValue(environment, out var map))
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                into[environment] = map;
            }

            map[key] = TextSafety.StripControl(value.Value ?? string.Empty, keepLineBreaks: true);
            added++;
        }

        return added;
    }

    /// <summary>
    /// Writes an environment file.
    /// </summary>
    /// <remarks>
    /// Built with <see cref="Utf8JsonWriter"/> rather than by concatenation. Every key and
    /// value here came out of somebody else's file, and hand-escaping arbitrary text into
    /// JSON is how a generated file stops being JSON — with the added twist that the failure
    /// would land in the file holding the credentials.
    /// </remarks>
    private static string Json(Dictionary<string, Dictionary<string, string>> environments)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();

            foreach (var (environment, values) in environments.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                json.WriteStartObject(environment);

                foreach (var (key, value) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
                {
                    json.WriteString(key, value);
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    /// <summary>
    /// A name for an environment whose export did not carry one.
    /// </summary>
    /// <remarks>
    /// Taken from the file name, with Postman's own suffix removed — an export is called
    /// <c>Staging.postman_environment.json</c>, and an environment called
    /// <c>Staging.postman_environment</c> would be a poor thing to put in a picker.
    /// </remarks>
    private static string FallbackEnvironmentName(string fileName)
    {
        var name = fileName;

        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        foreach (var suffix in new[] { ".postman_environment.json", ".postman_environment", ".json" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        name = TextSafety.StripControl(name).Trim();

        return name.Length == 0 ? "imported" : name;
    }

    private static string Describe(string value) => TextSafety.Cap(TextSafety.StripControl(value), 80);
}
