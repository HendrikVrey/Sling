using System.Globalization;
using System.Text.Json;

namespace Sling.Persistence.Environments;

/// <summary>
/// One parsed environment file: <c>{ "dev": { "base": "https://…" }, … }</c>.
/// </summary>
/// <remarks>
/// <para>
/// Read with <see cref="JsonDocument"/> and walked by hand rather than deserialised into
/// a model. Two reasons, and both are about the file being hand-edited: the shape is
/// dictionaries all the way down, so a model buys nothing; and walking it means a
/// wrongly-shaped entry becomes a sentence naming the environment and the key, instead of
/// an exception naming a type nobody has heard of. It also sidesteps source generation
/// entirely, which this project needs because it is built AOT-compatible.
/// </para>
/// <para>
/// Nothing here ever throws for a bad file. An environment file is edited in a text
/// editor and is malformed regularly; the answer is to say what is wrong and carry on
/// with whatever parsed.
/// </para>
/// </remarks>
internal sealed class EnvironmentFile
{
    /// <summary>
    /// The largest environment file that will be read. An environment is a few dozen
    /// name/value pairs; anything at this size is not one.
    /// </summary>
    private const long MaxBytes = 4L * 1024 * 1024;

    private readonly Dictionary<string, Dictionary<string, string>> _environments;

    private EnvironmentFile(
        Dictionary<string, Dictionary<string, string>> environments,
        IReadOnlyList<string> problems)
    {
        _environments = environments;
        Problems = problems;
    }

    public static EnvironmentFile Empty { get; } = new([], []);

    public IEnumerable<string> EnvironmentNames => _environments.Keys;

    public IReadOnlyList<string> Problems { get; }

    public IReadOnlyDictionary<string, string> Values(string environment) =>
        _environments.GetValueOrDefault(environment) ?? [];

    /// <summary>
    /// Loads <paramref name="path"/>. A file that is not there is not a problem - most
    /// workspaces have no environments, and one that has no secrets should not be nagged
    /// about a file it does not need.
    /// </summary>
    public static EnvironmentFile Load(string path)
    {
        if (!File.Exists(path))
        {
            return Empty;
        }

        string text;

        try
        {
            // Bounded for the same reason a document is: this is a JSON file somebody
            // hand-edits, and reading an arbitrarily large one whole, on the dispatcher, is
            // a freeze rather than an error message.
            if (new FileInfo(path).Length > MaxBytes)
            {
                return new EnvironmentFile(
                    [],
                    [$"'{Path.GetFileName(path)}' is larger than {MaxBytes / 1024} KB, which is far "
                        + "past any real environment file."]);
            }

            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            return new EnvironmentFile([], [$"'{Path.GetFileName(path)}' could not be read: {ex.Message}"]);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new EnvironmentFile([], [$"'{Path.GetFileName(path)}' could not be read: {ex.Message}"]);
        }

        return Parse(text, Path.GetFileName(path));
    }

    internal static EnvironmentFile Parse(string text, string fileName)
    {
        var environments = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var problems = new List<string>();

        JsonDocument document;

        try
        {
            // Comments and trailing commas are allowed because this file is written by
            // hand, and a note saying which token belongs to which deployment is exactly
            // the kind of thing its author wants to leave in it.
            document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
        }
        catch (JsonException ex)
        {
            // LineNumber is 0-based; the byte position is deliberately not reported,
            // because it counts UTF-8 bytes and would be wrong as a column on any line
            // holding an accent, an ideograph or an emoji.
            var line = ex.LineNumber is { } number
                ? $" (line {(number + 1).ToString(CultureInfo.InvariantCulture)})"
                : string.Empty;

            return new EnvironmentFile([], [$"'{fileName}' is not valid JSON{line}: {ex.Message}"]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new EnvironmentFile(
                    [],
                    [$"'{fileName}' must hold an object of environments, like {{ \"dev\": {{ … }} }}."]);
            }

            foreach (var environment in document.RootElement.EnumerateObject())
            {
                if (environment.Value.ValueKind != JsonValueKind.Object)
                {
                    problems.Add(
                        $"'{fileName}': environment '{environment.Name}' must be an object of "
                            + "name/value pairs.");
                    continue;
                }

                environments[environment.Name] = ReadValues(environment, fileName, problems);
            }
        }

        return new EnvironmentFile(environments, problems);
    }

    private static Dictionary<string, string> ReadValues(
        JsonProperty environment,
        string fileName,
        List<string> problems)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in environment.Value.EnumerateObject())
        {
            switch (entry.Value.ValueKind)
            {
                case JsonValueKind.String:
                    values[entry.Name] = entry.Value.GetString() ?? string.Empty;
                    break;

                // A port written as 8080 rather than "8080" is the commonest thing in one
                // of these files, and refusing it would be pedantry: it substitutes into a
                // URL as text either way, and GetRawText is exactly the text that was
                // written.
                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    values[entry.Name] = entry.Value.GetRawText();
                    break;

                default:
                    problems.Add(
                        $"'{fileName}': '{environment.Name}.{entry.Name}' is not a value a request "
                            + "can be given. Environments hold text, numbers and true/false.");
                    break;
            }
        }

        return values;
    }
}
