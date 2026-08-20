using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace Sling.Core.Json;

/// <summary>
/// Reads a single value out of a JSON document using the subset of JSONPath that
/// <c>.http</c> chaining actually uses: <c>$</c>, <c>.member</c>, <c>['member']</c> and
/// <c>[index]</c>.
/// </summary>
/// <remarks>
/// <para>
/// A subset, deliberately. Filters, wildcards, slices and recursive descent all return
/// <em>sets</em>, and a request field needs exactly one value — supporting them would
/// mean inventing a rule for which element of the set gets substituted. Anything outside
/// the subset is rejected with a message saying so, which is a better outcome than
/// silently picking the first match.
/// </para>
/// <para>
/// Hand-written rather than taken from a package: <c>Sling.Core</c> carries no
/// dependencies (<c>Sling.md</c> §3), and <see cref="JsonDocument"/> already does the
/// parsing. It is also the reflection-free half of System.Text.Json, so nothing here
/// costs the project its AOT compatibility.
/// </para>
/// </remarks>
internal static class JsonPathReader
{
    /// <summary>
    /// Extracts <paramref name="path"/> from <paramref name="json"/>.
    /// </summary>
    /// <returns>
    /// True with <paramref name="value"/> set, or false with <paramref name="error"/>
    /// explaining which step failed. A string lands as its text; anything else lands as
    /// its JSON, so an object or array can be substituted into a body.
    /// </returns>
    public static bool TryRead(
        string json,
        string path,
        [NotNullWhen(true)] out string? value,
        [NotNullWhen(false)] out string? error)
    {
        value = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            error = $"the response body is not JSON ({ex.Message})";
            return false;
        }
        catch (ArgumentException ex)
        {
            // Parse transcodes to UTF-8 before parsing, so a lone surrogate in the body
            // surfaces here rather than as a JsonException.
            error = $"the response body is not valid text ({ex.Message})";
            return false;
        }

        using (document)
        {
            if (!TryWalk(document.RootElement, path, out var element, out error))
            {
                return false;
            }

            value = element.ValueKind switch
            {
                // A string's raw text is quoted and escaped; a header or URL wants the
                // string itself.
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Null => string.Empty,
                _ => element.GetRawText(),
            };

            error = null;
            return true;
        }
    }

    private static bool TryWalk(
        JsonElement root,
        string path,
        out JsonElement result,
        [NotNullWhen(false)] out string? error)
    {
        result = root;

        var remainder = path.Trim();
        if (remainder.StartsWith('$'))
        {
            remainder = remainder[1..];
        }

        // A leading member may be written bare: '{{login.response.body.token}}' is how
        // people write it when the '$.' is not in front of them in an example.
        if (remainder.Length > 0 && remainder[0] is not ('.' or '['))
        {
            remainder = "." + remainder;
        }

        while (remainder.Length > 0)
        {
            if (!TryTakeStep(ref remainder, out var step, out error))
            {
                return false;
            }

            if (!TryDescend(result, step, out result, out error))
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>Consumes one <c>.member</c>, <c>['member']</c> or <c>[index]</c>.</summary>
    private static bool TryTakeStep(ref string remainder, out Step step, [NotNullWhen(false)] out string? error)
    {
        step = default;

        if (remainder.StartsWith('.'))
        {
            // From index 1: remainder starts with the '.' that introduces this step, so
            // searching from 0 would find that same dot and yield an empty name.
            var end = remainder.IndexOfAny(['.', '['], 1);
            var name = end < 0 ? remainder[1..] : remainder[1..end];

            if (name.Length == 0)
            {
                error = "the path has an empty member name";
                return false;
            }

            if (name.Contains('*', StringComparison.Ordinal))
            {
                error = "wildcards are not supported — a request field needs exactly one value";
                return false;
            }

            remainder = end < 0 ? string.Empty : remainder[end..];
            step = Step.Member(name);
            error = null;
            return true;
        }

        if (remainder.StartsWith('['))
        {
            var close = remainder.IndexOf(']');
            if (close < 0)
            {
                error = "the path has an unclosed '['";
                return false;
            }

            var inner = remainder[1..close].Trim();
            remainder = remainder[(close + 1)..];

            if (inner.Length >= 2 && ((inner[0] == '\'' && inner[^1] == '\'') || (inner[0] == '"' && inner[^1] == '"')))
            {
                step = Step.Member(inner[1..^1]);
                error = null;
                return true;
            }

            if (int.TryParse(inner, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
            {
                step = Step.Index(index);
                error = null;
                return true;
            }

            error = $"'[{inner}]' is not a member name or an array index";
            return false;
        }

        error = $"'{remainder}' is not a supported path step — write '.member', \"['member']\" or '[0]'";
        return false;
    }

    private static bool TryDescend(
        JsonElement current,
        Step step,
        out JsonElement result,
        [NotNullWhen(false)] out string? error)
    {
        result = default;

        if (step.Name is not null)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                error = $"'{step.Name}' was asked for, but that part of the body is {Describe(current.ValueKind)}, not an object";
                return false;
            }

            if (!current.TryGetProperty(step.Name, out result))
            {
                error = $"the body has no '{step.Name}'";
                return false;
            }

            error = null;
            return true;
        }

        if (current.ValueKind != JsonValueKind.Array)
        {
            error = $"an index was asked for, but that part of the body is {Describe(current.ValueKind)}, not an array";
            return false;
        }

        var length = current.GetArrayLength();
        var index = step.Position < 0 ? length + step.Position : step.Position;

        if (index < 0 || index >= length)
        {
            error = $"index {step.Position.ToString(CultureInfo.InvariantCulture)} is outside an array of "
                + $"{length.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        result = current[index];
        error = null;
        return true;
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "undefined",
    };

    /// <summary>One step of a path: a member name, or an array position.</summary>
    private readonly record struct Step(string? Name, int Position)
    {
        public static Step Member(string name) => new(name, 0);

        public static Step Index(int position) => new(null, position);
    }
}
