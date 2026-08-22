using System.Diagnostics.CodeAnalysis;
using Sling.Core.Variables;

namespace Sling.Persistence.Environments;

/// <summary>
/// The environments a workspace defines, and the split between the ones that are
/// committed and the ones that hold secrets.
/// </summary>
/// <remarks>
/// <para>
/// Two files, and the split between them is <c>Sling.md</c> §5.1 made structural:
/// <c>http-client.env.json</c> is meant to be committed and reviewed, and
/// <c>http-client.private.env.json</c> is gitignored and holds the tokens. A value can be
/// resolved from either, so a request file references <c>{{token}}</c> the same way
/// whichever side it comes from — and no secret is ever resolvable from a file that gets
/// committed, which is the property that matters.
/// </para>
/// <para>
/// <c>$shared</c> is a reserved environment name whose values underlie every other one,
/// the same convention Rider and Visual Studio use. It is where things that do not differ
/// per deployment live, so they are written once rather than copied into each environment
/// and then edited in three places out of four.
/// </para>
/// </remarks>
public sealed class EnvironmentSet
{
    /// <summary>The environment whose values underlie all the others.</summary>
    public const string SharedName = "$shared";

    private readonly EnvironmentFile _committed;
    private readonly EnvironmentFile _secret;

    internal EnvironmentSet(EnvironmentFile committed, EnvironmentFile secret)
    {
        _committed = committed;
        _secret = secret;

        Names = committed.EnvironmentNames
            .Concat(secret.EnvironmentNames)
            .Where(n => !string.Equals(n, SharedName, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Problems = [.. committed.Problems, .. secret.Problems];
    }

    /// <summary>An empty set, for a window with no workspace open.</summary>
    public static EnvironmentSet Empty { get; } = new(EnvironmentFile.Empty, EnvironmentFile.Empty);

    /// <summary>Selectable environment names, sorted, without <see cref="SharedName"/>.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>
    /// Anything wrong with either file, phrased for the user. Never a reason to refuse to
    /// open a workspace — an environment file is edited by hand and is malformed from
    /// time to time, and the useful response is to say so and carry on with what parsed.
    /// </summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>
    /// The values in force when <paramref name="name"/> is selected.
    /// </summary>
    /// <remarks>
    /// Four layers, each overriding the one before: shared committed, shared secret, the
    /// environment's committed values, the environment's secrets. Secrets last on both
    /// halves so a placeholder in the committed file is a working default rather than a
    /// thing that overrides the real value.
    /// </remarks>
    public EnvironmentValues Select(string? name)
    {
        var values = new Dictionary<string, EnvironmentValue>(StringComparer.Ordinal);

        Overlay(values, _committed, SharedName, secret: false);
        Overlay(values, _secret, SharedName, secret: true);

        if (name is not null && !string.Equals(name, SharedName, StringComparison.Ordinal))
        {
            Overlay(values, _committed, name, secret: false);
            Overlay(values, _secret, name, secret: true);
        }

        return new EnvironmentValues(name, values);
    }

    private static void Overlay(
        Dictionary<string, EnvironmentValue> into,
        EnvironmentFile file,
        string environment,
        bool secret)
    {
        foreach (var (key, value) in file.Values(environment))
        {
            into[key] = new EnvironmentValue(value, secret);
        }
    }
}

/// <summary>One resolved variable and where it came from.</summary>
/// <param name="Secret">
/// True when it came from the private file. Nothing reads this yet; redaction in history
/// and logs (<c>Sling.md</c> §5.4) is the M3 slice-2 feature that will, and the knowledge
/// only exists at this layer — recording it here is what stops that feature having to
/// reopen the files to find out.
/// </param>
internal sealed record EnvironmentValue(string Text, bool Secret);

/// <summary>
/// The variables of one selected environment, in the shape <c>Sling.Core</c> resolves
/// against.
/// </summary>
public sealed class EnvironmentValues : IVariableSource
{
    private readonly Dictionary<string, EnvironmentValue> _values;

    internal EnvironmentValues(string? name, Dictionary<string, EnvironmentValue> values)
    {
        Name = name;
        _values = values;
    }

    /// <summary>An empty environment — every <c>{{name}}</c> falls through to the document.</summary>
    public static EnvironmentValues None { get; } = new(null, []);

    /// <summary>The selected environment's name, or null when none is selected.</summary>
    public string? Name { get; }

    /// <summary>How many variables are in force.</summary>
    public int Count => _values.Count;

    public bool TryGet(string name, [NotNullWhen(true)] out string? value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_values.TryGetValue(name, out var found))
        {
            value = found.Text;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>True when <paramref name="name"/> was supplied by the private file.</summary>
    public bool IsSecret(string name) => _values.TryGetValue(name, out var found) && found.Secret;

    /// <summary>
    /// Every value that came from the private file, for redaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The provenance half of <c>Sling.md</c> §5.4, and the half that is exact: a value the
    /// user put in the secrets file is a secret because they said so. No guessing about
    /// which header names look like credentials, and it catches an API key that reached a
    /// query parameter, where no name-based rule is looking.
    /// </para>
    /// <para>
    /// Values, not names. What has to be recognised is the text <em>after</em> substitution
    /// — which is what appears in a URL or a header — and by then the variable's name is
    /// gone.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> SecretValues() =>
        [.. _values.Values.Where(v => v.Secret).Select(v => v.Text)];

    /// <summary>
    /// Whether <paramref name="other"/> binds exactly the same names to exactly the same
    /// values.
    /// </summary>
    /// <remarks>
    /// Exists so a caller can tell whether re-reading the files actually changed anything.
    /// The environment files are edited outside Sling and re-read whenever its window
    /// comes forward, so a name can keep pointing at a <em>different</em> deployment
    /// without the selection ever changing — which has to invalidate anything fetched
    /// under the old one just as switching environments does.
    /// <para>
    /// An exact comparison rather than a hash: a hash that collides fails by silently
    /// deciding nothing changed, which is the one answer that must never be wrong here.
    /// </para>
    /// </remarks>
    public bool HasSameValuesAs(EnvironmentValues other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (_values.Count != other._values.Count)
        {
            return false;
        }

        foreach (var (name, value) in _values)
        {
            if (!other._values.TryGetValue(name, out var theirs)
                || !string.Equals(value.Text, theirs.Text, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
