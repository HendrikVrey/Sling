using System.Text;
using System.Text.RegularExpressions;
using Sling.Core.Parsing;

namespace Sling.Import.Postman;

/// <summary>
/// Turns a Postman URL into a request target.
/// </summary>
internal static partial class TargetBuilder
{
    /// <summary>
    /// A <c>:name</c> path variable, which only ever follows a slash.
    /// </summary>
    /// <remarks>
    /// The lookbehind is what keeps this off <c>https:</c> and off a <c>:8080</c> port.
    /// Matching a bare colon-word anywhere would rewrite the scheme of every URL in the
    /// collection, which is a failure that looks like a network problem.
    /// </remarks>
    [GeneratedRegex(@"(?<=/):([A-Za-z_][A-Za-z0-9_\-]*)")]
    private static partial Regex PathVariablePattern { get; }

    /// <summary>
    /// Builds the target, noting anything that had to be assumed or could not be carried.
    /// </summary>
    /// <returns>The target, or null when the request had no URL at all.</returns>
    public static string? Build(PostmanUrl url, HttpWriter writer)
    {
        var text = (url.Raw ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            text = Compose(url);
        }

        if (text.Length == 0)
        {
            return null;
        }

        text = SubstitutePathVariables(text, url.PathVariables, writer);
        text = EnsureScheme(text, writer);

        // Filtered by HttpWriter either way; noted here because a target that silently loses
        // a character is a request to a different URL, and "it 404s" is a long way from the
        // export that caused it.
        if (!text.All(HttpSyntax.IsLegalRequestTargetChar))
        {
            writer.Note(
                "The URL held a character a request line cannot carry ("
                    + HttpSyntax.DescribeFirstIllegal(text, HttpSyntax.IsLegalRequestTargetChar)
                    + "), and it was removed. Check the line below before sending it.");
        }

        return text;
    }

    /// <summary>
    /// Assembles a URL from the structured fields, for an export that carries no
    /// <c>raw</c>.
    /// </summary>
    private static string Compose(PostmanUrl url)
    {
        var text = new StringBuilder();

        if (!string.IsNullOrEmpty(url.Protocol))
        {
            text.Append(url.Protocol).Append("://");
        }

        text.Append(string.Join('.', url.Host));

        if (!string.IsNullOrEmpty(url.Port))
        {
            text.Append(':').Append(url.Port);
        }

        foreach (var segment in url.Path)
        {
            text.Append('/').Append(segment);
        }

        // Written as they arrived rather than percent-encoded: a Postman query value is very
        // often {{a_variable}}, and encoding it would turn the braces into %7B and leave a
        // request that resolves nothing and says nothing about why.
        var query = url.Query.Where(q => q.Key.Length > 0).ToList();

        for (var i = 0; i < query.Count; i++)
        {
            text.Append(i == 0 ? '?' : '&').Append(query[i].Key);

            if (query[i].Value is { } value)
            {
                text.Append('=').Append(value);
            }
        }

        if (!string.IsNullOrEmpty(url.Hash))
        {
            text.Append('#').Append(url.Hash);
        }

        return text.ToString();
    }

    /// <summary>
    /// Replaces <c>/:id</c> with the value Postman had for it.
    /// </summary>
    /// <remarks>
    /// Postman resolves these at send time and the <c>.http</c> format has no equivalent, so
    /// leaving them alone would send the literal text <c>:id</c> to the server. One left
    /// without a value is left as written and noted — that is what Postman itself does with
    /// an unset path variable, and inventing a placeholder would hide the fact that the
    /// export never had one.
    /// </remarks>
    private static string SubstitutePathVariables(
        string target,
        IReadOnlyList<PostmanPair> variables,
        HttpWriter writer)
    {
        if (!target.Contains(':', StringComparison.Ordinal))
        {
            return target;
        }

        var missing = new List<string>();

        var result = PathVariablePattern.Replace(target, match =>
        {
            var name = match.Groups[1].Value;
            var value = variables.FirstOrDefault(v =>
                string.Equals(v.Key, name, StringComparison.Ordinal))?.Value;

            if (string.IsNullOrEmpty(value))
            {
                missing.Add(name);
                return match.Value;
            }

            return value;
        });

        if (missing.Count > 0)
        {
            writer.Note(
                $"The path variable{(missing.Count == 1 ? string.Empty : "s")} "
                    + string.Join(", ", missing.Select(m => "':" + m + "'"))
                    + " had no value in the collection, so "
                    + (missing.Count == 1 ? "it was" : "they were")
                    + " left as written. Replace "
                    + (missing.Count == 1 ? "it" : "them")
                    + " or point at an environment variable.");
        }

        return result;
    }

    /// <summary>
    /// Adds the scheme Postman would have assumed, when the URL has none.
    /// </summary>
    /// <remarks>
    /// <c>https</c> rather than <c>http</c>: defaulting a credential-carrying tool to
    /// cleartext would be indefensible, and saying so is what makes it correctable. A target
    /// that <em>opens</em> with a variable is left alone — <c>{{base}}/orders</c> is the
    /// format's central idiom, the scheme lives inside <c>base</c>, and prefixing it would
    /// produce <c>https://{{base}}/orders</c>, which is wrong in a way that only shows up at
    /// send time.
    /// </remarks>
    private static string EnsureScheme(string target, HttpWriter writer)
    {
        if (target.StartsWith("{{", StringComparison.Ordinal)
            || target.Contains("://", StringComparison.Ordinal))
        {
            return target;
        }

        writer.Note("The URL had no scheme, so https:// was assumed.");

        return "https://" + target;
    }
}
