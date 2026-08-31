using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sling.Persistence.Environments;

/// <summary>
/// Adds or updates one key in an environment file, by editing its text rather than
/// rewriting it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A serialiser round trip is not an option here, and that is the whole reason this
/// class exists.</b> <see cref="EnvironmentFile"/> reads these files with comments allowed
/// precisely because they are written by hand, and a note saying which token belongs to
/// which deployment is exactly the kind of thing their author leaves in one.
/// <see cref="JsonDocument"/> does not carry comments, so parsing a file and writing it
/// back deletes every one of them - silently, in somebody's repository, as a side effect
/// of setting an unrelated key.
/// </para>
/// <para>
/// So the edit is a splice. The file is located with <see cref="Utf8JsonReader"/>, which
/// reports where each token sits, and the smallest possible range is replaced: an existing
/// key's value token, or an insertion point in front of a closing brace. Everything the
/// user wrote - ordering, indentation, comments, the blank line they left between two
/// groups - survives because it is never re-emitted.
/// </para>
/// <para>
/// <b>Nothing here removes anything.</b> There is no delete and no move between the two
/// files, for the same reason <see cref="Workspaces.WorkspaceEditor"/> has no delete: this
/// is a git artifact somebody else may be editing, and an operation that takes lines out of
/// one belongs in a text editor, where there is undo.
/// </para>
/// </remarks>
internal static class EnvironmentFileWriter
{
    /// <summary>One level of indentation, for the parts this writes rather than preserves.</summary>
    private const string IndentStep = "  ";

    /// <summary>
    /// Returns <paramref name="json"/> with <c>environment.name</c> set to
    /// <paramref name="value"/>.
    /// </summary>
    /// <remarks>
    /// An empty file is replaced by a new document holding just this value. That is not the
    /// "never rewrite" rule being bent: there is nothing to preserve, and refusing would
    /// leave the user with a feature that only works once the file they were told they would
    /// never have to write already exists.
    /// </remarks>
    /// <exception cref="InvalidDataException">
    /// The file holds JSON this cannot edit safely - it is malformed, its root is not an
    /// object, or the environment's entry is not one. Refusing is the only safe answer:
    /// splicing into text whose structure was not understood is how a hand-written file gets
    /// corrupted.
    /// </exception>
    internal static string SetValue(string json, string environment, string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(environment);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        if (string.IsNullOrWhiteSpace(json))
        {
            return NewDocument(environment, name, value);
        }

        var located = Locate(json, environment, name);

        // An existing key: only its value token is replaced, so the name, the spacing around
        // the colon and any comment on the line all survive untouched.
        if (located.ValueStart is { } start && located.ValueEnd is { } end)
        {
            return json[..start] + Quote(value) + json[end..];
        }

        return located is { EnvironmentOpen: { } open, EnvironmentClose: { } close }
            ? InsertIntoEnvironment(json, located, open, close, name, value)
            : InsertEnvironment(json, located, environment, name, value);
    }

    /// <summary>A whole file, for a workspace that has none yet.</summary>
    private static string NewDocument(string environment, string name, string value)
    {
        var text = new StringBuilder();

        text.Append('{').Append('\n');
        text.Append(IndentStep).Append(Quote(environment)).Append(": {").Append('\n');
        text.Append(IndentStep).Append(IndentStep).Append(Quote(name)).Append(": ").Append(Quote(value)).Append('\n');
        text.Append(IndentStep).Append('}').Append('\n');
        text.Append('}').Append('\n');

        return text.ToString();
    }

    /// <summary>Adds a key to an environment that is already in the file.</summary>
    private static string InsertIntoEnvironment(
        string json,
        Located located,
        int open,
        int close,
        string name,
        string value)
    {
        var entry = Quote(name) + ": " + Quote(value);

        // An empty environment has no formatting between its braces worth preserving, so
        // the pair is replaced by a laid-out one. Inserting into it instead would turn '{}'
        // into '{"token": "..."}', which is valid and reads as damage.
        if (located.PropertyCount == 0)
        {
            var indent = LineIndent(json, open);

            return json[..open]
                + "{\n" + indent + IndentStep + entry + "\n" + indent + "}"
                + json[(close + 1)..];
        }

        // Two splices, and the order matters: the later one goes in first, so the earlier
        // one's offset is still the offset it was measured at.
        var edited = InsertionPoint(json, close).Splice(json, entry);

        return located is { NeedsComma: true, LastValueEnd: { } comma }
            ? edited[..comma] + "," + edited[comma..]
            : edited;
    }

    /// <summary>Adds a whole environment to a file that does not have one.</summary>
    private static string InsertEnvironment(
        string json,
        Located located,
        string environment,
        string name,
        string value)
    {
        if (located.RootClose is not { } rootClose)
        {
            throw new InvalidDataException("the file does not hold a JSON object of environments");
        }

        var placement = InsertionPoint(json, rootClose);
        var pair = Quote(name) + ": " + Quote(value);

        var entry = placement.OwnLine
            ? Quote(environment) + ": {\n"
                + placement.Indent + IndentStep + pair + "\n"
                + placement.Indent + "}"
            : Quote(environment) + ": { " + pair + " }";

        var edited = placement.Splice(json, entry);

        return located is { RootNeedsComma: true, RootLastValueEnd: { } comma }
            ? edited[..comma] + "," + edited[comma..]
            : edited;
    }

    /// <summary>
    /// Where a new entry goes in front of the closing brace at <paramref name="close"/>.
    /// </summary>
    /// <remarks>
    /// Two shapes, because both occur in files people write. When the brace sits on a line
    /// of its own the entry becomes another line above it, indented to match. When the whole
    /// object is written inline it stays inline: reflowing somebody's formatting is what
    /// this class exists not to do.
    /// </remarks>
    private static Placement InsertionPoint(string json, int close)
    {
        var lineStart = close;

        while (lineStart > 0 && json[lineStart - 1] is ' ' or '\t')
        {
            lineStart--;
        }

        return lineStart > 0 && json[lineStart - 1] == '\n'
            ? new Placement(lineStart, OwnLine: true, json[lineStart..close] + IndentStep)
            : new Placement(close, OwnLine: false, string.Empty);
    }

    /// <summary>
    /// Finds the spans this edit needs, or reports that the file cannot be edited safely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Utf8JsonReader"/> rather than a hand-written scanner. It is the only thing
    /// in the framework that reports where a token <em>is</em>, and getting string escapes,
    /// comments and nesting right by hand - in the code that decides where to cut somebody's
    /// file - is not a trade worth making.
    /// </para>
    /// <para>
    /// Offsets are converted to character indices as they are recorded. The reader counts
    /// UTF-8 bytes, so a file holding an accent, an ideograph or an emoji anywhere above the
    /// edit would otherwise be spliced in the wrong place, or through the middle of a
    /// character.
    /// </para>
    /// </remarks>
    private static Located Locate(string json, string environment, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(json);

        var reader = new Utf8JsonReader(
            bytes,
            new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

        var located = new Located();

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new InvalidDataException("the file does not hold a JSON object of environments");
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var property = reader.GetString();
                var isTarget = string.Equals(property, environment, StringComparison.Ordinal);

                reader.Read();

                if (isTarget && reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new InvalidDataException(
                        $"'{environment}' is in the file already and is not an object of name/value pairs");
                }

                if (isTarget)
                {
                    located.EnvironmentOpen = Chars(bytes, reader.TokenStartIndex);
                    ReadEnvironment(ref reader, bytes, name, located);
                }
                else
                {
                    // A no-op on a primitive, and the matching End token on a container -
                    // which is what keeps every offset after it correct.
                    reader.Skip();
                }

                located.RootPropertyCount++;
                located.RootLastValueEnd = Chars(bytes, reader.BytesConsumed);
            }

            if (reader.TokenType != JsonTokenType.EndObject)
            {
                throw new InvalidDataException("the file ends before its outermost object closes");
            }

            located.RootClose = Chars(bytes, reader.TokenStartIndex);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"it is not valid JSON ({ex.Message})", ex);
        }

        // Here rather than in the walk above: whether a comma is still needed is the one
        // question that takes the file's text as well as its offsets.
        located.RootNeedsComma = located.RootPropertyCount > 0
            && !HasSeparator(json, located.RootLastValueEnd, located.RootClose);

        located.NeedsComma = located.PropertyCount > 0
            && !HasSeparator(json, located.LastValueEnd, located.EnvironmentClose);

        return located;
    }

    /// <summary>Walks one environment object, recording where its entries end.</summary>
    private static void ReadEnvironment(ref Utf8JsonReader reader, byte[] bytes, string name, Located located)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString();
            reader.Read();

            var valueStart = reader.TokenStartIndex;

            // An environment cannot hold a nested object or array, but the file is written
            // by hand and may hold one anyway. Skipping it is what keeps the offsets of
            // everything after it correct.
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            located.PropertyCount++;
            located.LastValueEnd = Chars(bytes, reader.BytesConsumed);

            if (string.Equals(key, name, StringComparison.Ordinal))
            {
                located.ValueStart = Chars(bytes, valueStart);
                located.ValueEnd = located.LastValueEnd;
            }
        }

        located.EnvironmentClose = Chars(bytes, reader.TokenStartIndex);
    }

    /// <summary>
    /// Whether a comma already separates the last entry from the closing brace.
    /// </summary>
    /// <remarks>
    /// The reader accepts a trailing comma and stops in front of it, so its own offsets
    /// cannot answer this - and writing a second one produces a file nothing will read.
    /// Comments are stepped over because a comma inside one is text rather than syntax.
    /// </remarks>
    private static bool HasSeparator(string json, int? from, int? to)
    {
        if (from is not { } start || to is not { } end || start >= end)
        {
            return false;
        }

        for (var i = start; i < end; i++)
        {
            if (json[i] == ',')
            {
                return true;
            }

            if (json[i] != '/' || i + 1 >= end)
            {
                continue;
            }

            if (json[i + 1] == '/')
            {
                while (i < end && json[i] != '\n')
                {
                    i++;
                }
            }
            else if (json[i + 1] == '*')
            {
                i += 2;

                while (i + 1 < end && !(json[i] == '*' && json[i + 1] == '/'))
                {
                    i++;
                }

                i++;
            }
        }

        return false;
    }

    /// <summary>The whitespace at the start of the line <paramref name="index"/> sits on.</summary>
    private static string LineIndent(string json, int index)
    {
        var lineStart = json.LastIndexOf('\n', Math.Clamp(index, 0, json.Length - 1)) + 1;
        var end = lineStart;

        while (end < json.Length && json[end] is ' ' or '\t')
        {
            end++;
        }

        return json[lineStart..end];
    }

    /// <summary>Converts a UTF-8 byte offset into the character offset of the same position.</summary>
    private static int Chars(byte[] bytes, long byteOffset) =>
        Encoding.UTF8.GetCharCount(bytes, 0, (int)byteOffset);

    /// <summary>
    /// A JSON string literal, quotes included.
    /// </summary>
    /// <remarks>
    /// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> because this file is read
    /// by people. The default encoder escapes every non-ASCII character and the HTML-unsafe
    /// ASCII ones, which turns an ordinary URL or a name carrying an accent into a row of
    /// <c>\uXXXX</c>. The relaxed encoder still escapes everything JSON requires - quotes,
    /// backslashes and control characters - which is the part that matters here, and the
    /// output is not HTML.
    /// </remarks>
    private static string Quote(string value) =>
        "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";

    /// <summary>Where a new entry goes, and how it is laid out when it gets there.</summary>
    /// <param name="OwnLine">
    /// True when the closing brace sits on a line of its own, so the entry becomes another
    /// line above it.
    /// </param>
    /// <param name="Indent">The indentation an own-line entry is written at.</param>
    private readonly record struct Placement(int Offset, bool OwnLine, string Indent)
    {
        /// <summary>Puts <paramref name="entry"/> into <paramref name="json"/> at this point.</summary>
        internal string Splice(string json, string entry)
        {
            var text = OwnLine ? Indent + entry + "\n" : entry + " ";

            return json[..Offset] + text + json[Offset..];
        }
    }

    /// <summary>Everything the walk found, in character offsets.</summary>
    private sealed class Located
    {
        public int? RootClose { get; set; }

        public int RootPropertyCount { get; set; }

        public int? RootLastValueEnd { get; set; }

        /// <summary>Whether the root's last entry still needs a comma after it.</summary>
        public bool RootNeedsComma { get; set; }

        public int? EnvironmentOpen { get; set; }

        public int? EnvironmentClose { get; set; }

        public int PropertyCount { get; set; }

        public int? LastValueEnd { get; set; }

        /// <summary>Whether the environment's last entry still needs a comma after it.</summary>
        public bool NeedsComma { get; set; }

        public int? ValueStart { get; set; }

        public int? ValueEnd { get; set; }
    }
}
