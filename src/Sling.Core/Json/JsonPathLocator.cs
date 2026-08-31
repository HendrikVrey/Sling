using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sling.Core.Json;

/// <summary>
/// The JSONPath of whatever sits at a given position in a JSON document.
/// </summary>
/// <remarks>
/// <para>
/// The other half of chaining. <see cref="JsonPathReader"/> turns a path into a value at
/// send time; this turns a position into a path, so a path can be got by pointing at the
/// value in a response instead of typed by hand from a body two panes away.
/// </para>
/// <para>
/// It emits only the subset <see cref="JsonPathReader"/> reads - <c>$</c>, <c>.member</c>,
/// <c>['member']</c> and <c>[index]</c> - because a path this produces and that cannot read
/// is worse than no path at all: it would look right in the document and fail at send time.
/// </para>
/// </remarks>
public static class JsonPathLocator
{
    /// <summary>
    /// The path of the innermost value covering <paramref name="offset"/>.
    /// </summary>
    /// <param name="json">The document, exactly as the pane holds it.</param>
    /// <param name="offset">A character offset into <paramref name="json"/>.</param>
    /// <param name="path">
    /// The path, when there is one. A click in the whitespace between values, or on a body
    /// that is not JSON, answers false rather than guessing at the nearest one.
    /// </param>
    /// <remarks>
    /// A click on a property <em>name</em> answers with that property's path, which is what
    /// anybody pointing at <c>"access_token"</c> means. A click on a container answers with
    /// the container's path, which is a legal thing to chain against - a whole object
    /// substitutes into a body as its JSON.
    /// </remarks>
    public static bool TryLocate(string json, int offset, [NotNullWhen(true)] out string? path)
    {
        ArgumentNullException.ThrowIfNull(json);

        path = null;

        if (offset < 0 || offset > json.Length)
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(json);

        // The reader counts UTF-8 bytes and the caller counts characters. A body holding an
        // accent, an ideograph or an emoji anywhere above the click would otherwise resolve
        // to a position several bytes short of the one that was clicked.
        var target = Encoding.UTF8.GetByteCount(json.AsSpan(0, offset));

        try
        {
            return TryWalk(bytes, target, out path);
        }
        catch (JsonException)
        {
            // A body that is not JSON, or is truncated. Not an error worth reporting: the
            // caller offers the action only when this succeeds.
            return false;
        }
    }

    /// <summary>
    /// Walks the document, keeping the path to wherever the reader currently is.
    /// </summary>
    /// <remarks>
    /// The innermost match wins, which falls out of taking the <em>last</em> answer rather
    /// than the first: an offset inside a string inside an object is covered by both, and
    /// the string is read after the object was opened.
    /// </remarks>
    private static bool TryWalk(byte[] bytes, long target, out string? path)
    {
        var reader = new Utf8JsonReader(
            bytes,
            new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

        var steps = new List<string>();
        var indexes = new List<int>();

        path = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    // Replaces the step rather than pushing one: a property name and the
                    // value after it are the same position in the path.
                    Set(steps, Member(reader.GetString()));

                    if (Covers(reader, target))
                    {
                        path = Render(steps);
                    }

                    break;

                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    Position(steps, indexes);

                    if (Covers(reader, target))
                    {
                        path = Render(steps);
                    }

                    // An array's elements are numbered from the array's own frame, so the
                    // counter is pushed with it and popped with its End.
                    steps.Add(string.Empty);
                    indexes.Add(reader.TokenType == JsonTokenType.StartArray ? 0 : -1);

                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    Pop(steps);
                    Pop(indexes);
                    Advance(indexes);

                    break;

                default:
                    Position(steps, indexes);

                    if (Covers(reader, target))
                    {
                        path = Render(steps);
                    }

                    Advance(indexes);
                    break;
            }
        }

        return path is not null;
    }

    /// <summary>Whether the token the reader is on covers <paramref name="target"/>.</summary>
    /// <remarks>
    /// The end is inclusive so that a caret placed immediately after a value - which is where
    /// a double-click leaves it - still counts as being on it.
    /// </remarks>
    private static bool Covers(in Utf8JsonReader reader, long target) =>
        target >= reader.TokenStartIndex && target <= reader.BytesConsumed;

    /// <summary>
    /// Writes the current array index into the path, when the enclosing frame is an array.
    /// </summary>
    /// <remarks>
    /// Inside an object the step was already written by the property name, and there is
    /// nothing to do. Inside an array there is no name, so the position is the step.
    /// </remarks>
    private static void Position(List<string> steps, List<int> indexes)
    {
        if (indexes.Count > 0 && indexes[^1] >= 0)
        {
            Set(steps, "[" + indexes[^1].ToString(CultureInfo.InvariantCulture) + "]");
        }
    }

    /// <summary>Moves an array's counter on, once an element has been read.</summary>
    private static void Advance(List<int> indexes)
    {
        if (indexes.Count > 0 && indexes[^1] >= 0)
        {
            indexes[^1]++;
        }
    }

    private static void Set(List<string> steps, string step)
    {
        if (steps.Count == 0)
        {
            steps.Add(step);
            return;
        }

        steps[^1] = step;
    }

    private static void Pop<T>(List<T> items)
    {
        if (items.Count > 0)
        {
            items.RemoveAt(items.Count - 1);
        }
    }

    /// <summary>
    /// One step, in whichever of the two spellings <see cref="JsonPathReader"/> can read.
    /// </summary>
    /// <remarks>
    /// A name holding a quote gets neither: the bracket form has no escape in this subset, so
    /// a path for it cannot be written. Answering with nothing is right - the alternative is a
    /// path that looks correct and reads the wrong field, or none at all, at send time.
    /// </remarks>
    private static string Member(string? name)
    {
        if (name is null || name.Contains('\'', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            ? "." + name
            : "['" + name + "']";
    }

    /// <summary>
    /// Joins the steps into a path, or nothing when one of them could not be written.
    /// </summary>
    /// <remarks>
    /// An empty step means <see cref="Member"/> refused a name it has no spelling for.
    /// Dropping it and joining the rest would produce a path that looks right and reads a
    /// different field, which is the one outcome worse than declining.
    /// </remarks>
    private static string? Render(List<string> steps) =>
        steps.Any(s => s.Length == 0) ? null : "$" + string.Concat(steps);
}
