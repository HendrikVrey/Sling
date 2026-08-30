using System.Globalization;

namespace Sling.Import.Postman;

/// <summary>
/// What the whole import shares: the variables the two environment files will hold.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its real job is that no credential found in a collection is ever written into a
/// <c>.http</c> file.</b> An imported document is meant to be committed - that is the whole
/// premise of the format - and a Postman export routinely carries a live bearer token, a
/// basic password or a client secret in plain text. <c>Sling.md</c> §5.1 says a secret must
/// never be resolvable from a committed file, so every literal credential goes through
/// <see cref="Reference"/>, lands in the gitignored <c>http-client.private.env.json</c>, and
/// the document gets a <c>{{name}}</c> instead.
/// </para>
/// <para>
/// This is strictly better than what the curl importer can do, and the difference is not
/// cleverness: a pasted curl command produces one request and nowhere to put a secret, while
/// an import produces a whole workspace and the secrets file is part of it.
/// </para>
/// </remarks>
internal sealed class ImportContext
{
    private readonly Dictionary<string, string> _shared = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _secret = new(StringComparer.Ordinal);

    /// <summary>
    /// Reference text already handed out for a value, so one credential becomes one entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Load-bearing rather than tidy. Auth is usually declared once on the collection and
    /// inherited by every request in it, so without this a forty-request collection would
    /// produce forty copies of the same token under forty names - and rotating it would mean
    /// editing all of them.
    /// </para>
    /// <para>
    /// <b>Keyed by the value <em>and</em> whether it is a credential, which is not a detail.</b>
    /// Keyed by value alone, a client id and a client secret that happen to be equal - both
    /// <c>REPLACE-ME</c>, which is exactly what a published or vendor collection carries,
    /// shared one reference, and because the id is resolved first and is not a credential,
    /// the secret inherited its name and landed in <c>http-client.env.json</c>. That is the
    /// file that gets committed, and it is the one thing this class exists to prevent.
    /// </para>
    /// </remarks>
    private readonly Dictionary<(string Value, bool Secret), string> _references = [];

    /// <summary>Variables for the committed environment file, under <c>$shared</c>.</summary>
    public IReadOnlyDictionary<string, string> Shared => _shared;

    /// <summary>Variables for the gitignored environment file, under <c>$shared</c>.</summary>
    public IReadOnlyDictionary<string, string> Secret => _secret;

    /// <summary>
    /// Records a variable the collection declared.
    /// </summary>
    /// <remarks>
    /// Kept under the name the collection used, because the requests already reference it by
    /// that name - renaming it here would silently break every <c>{{…}}</c> in the export.
    /// A collision between the two files is not one: a name may legitimately have a
    /// placeholder in the committed file and a real value in the secrets file, which is how
    /// the two are meant to work together.
    /// </remarks>
    public void Declare(string name, string value, bool secret)
    {
        var clean = TextSafety.StripControl(name).Trim();

        if (clean.Length == 0)
        {
            return;
        }

        (secret ? _secret : _shared)[clean] = TextSafety.StripControl(value, keepLineBreaks: true);
    }

    /// <summary>
    /// Turns a value found in an auth block into text the document can carry.
    /// </summary>
    /// <param name="preferredName">The variable name to use, if it is free.</param>
    /// <param name="value">The value as the collection wrote it.</param>
    /// <param name="secret">
    /// True when the value is a credential, which decides which environment file it lands
    /// in. A client id is not a credential; a client secret is.
    /// </param>
    /// <returns>
    /// A <c>{{reference}}</c>, or null when there was nothing to reference. A value that is
    /// <em>already</em> a reference comes back untouched - a collection that says
    /// <c>{{access_token}}</c> is already doing the right thing, and wrapping it in a second
    /// variable would only add a layer.
    /// </returns>
    public string? Reference(string preferredName, string? value, bool secret)
    {
        var clean = TextSafety.StripControl(value ?? string.Empty).Trim();

        if (clean.Length == 0)
        {
            return null;
        }

        // Exactly a reference, not merely containing one. 'LIVE{{x}}TOKEN' contains a
        // reference and is still a credential, and passing it through wrote the literal
        // token into the document. Storing it in the environment file instead is safe and
        // still works, because a variable's own value is expanded in turn.
        if (IsExactlyAReference(clean))
        {
            return clean;
        }

        if (_references.TryGetValue((clean, secret), out var existing))
        {
            return existing;
        }

        var name = FreeName(preferredName);

        (secret ? _secret : _shared)[name] = clean;

        var reference = "{{" + name + "}}";
        _references[(clean, secret)] = reference;

        return reference;
    }

    /// <summary>
    /// Whether a value is a single <c>{{reference}}</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// A collection that already says <c>{{access_token}}</c> is doing the right thing and
    /// wrapping it in a second variable would only add a layer - but that is true only when
    /// the value <em>is</em> the reference. A value that merely contains one is a literal
    /// with a hole in it, and treating it as safe wrote the surrounding characters, which
    /// are usually the credential, into the document.
    /// </remarks>
    private static bool IsExactlyAReference(string value) =>
        value.StartsWith("{{", StringComparison.Ordinal)
        && value.EndsWith("}}", StringComparison.Ordinal)
        && value.Length > 4
        && value.IndexOf("{{", 2, StringComparison.Ordinal) < 0
        && value.IndexOf("}}", StringComparison.Ordinal) == value.Length - 2;

    /// <summary>
    /// A variable name nothing else in this import has taken.
    /// </summary>
    /// <remarks>
    /// Checked against both files. A generated name landing on top of a collection variable
    /// would change what every request referencing that name resolves to, which is a silent
    /// wrong answer rather than a failure.
    /// </remarks>
    private string FreeName(string preferred)
    {
        var stem = Slug(preferred);

        if (!_shared.ContainsKey(stem) && !_secret.ContainsKey(stem))
        {
            return stem;
        }

        for (var n = 2; ; n++)
        {
            var candidate = stem + "_" + n.ToString(CultureInfo.InvariantCulture);

            if (!_shared.ContainsKey(candidate) && !_secret.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Reduces a name to what the parser accepts as a variable name.
    /// </summary>
    /// <remarks>
    /// The parser's own pattern is <c>[A-Za-z_][A-Za-z0-9_.\-]*</c>, so a generated name
    /// that strays outside it produces a <c>{{reference}}</c> nothing resolves - and the
    /// request then sends the literal braces to the server, which is the quietest possible
    /// way for an import to be wrong.
    /// </remarks>
    private static string Slug(string name)
    {
        var slug = new string([.. name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')]);

        return slug.Length > 0 && (char.IsAsciiLetter(slug[0]) || slug[0] == '_') ? slug : "imported";
    }
}
