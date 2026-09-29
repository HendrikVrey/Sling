using System.Globalization;

namespace Sling.Core.Updates;

/// <summary>
/// A version as Sling's releases write it: <c>1.1.0</c>, <c>v1.1.0</c> or
/// <c>1.1.1-dev.12+0b1afec</c>.
/// </summary>
/// <remarks>
/// <para>
/// Not <see cref="Version"/>, because <see cref="Version"/> has no pre-release part, and
/// the pre-release part is what the comparison hinges on. Every push to <c>master</c>
/// builds <c>1.1.1-dev.N</c> once <c>1.1.0</c> is out, so somebody running a rolling build
/// must not be offered 1.1.0 (it is older), and must be offered 1.1.1 when it ships (a
/// release outranks its own pre-releases). That is semantic versioning's rule, and the
/// release workflow already refuses any tag that is not a plain semantic version.
/// </para>
/// <para>
/// Build metadata after <c>+</c> is dropped, as the specification says it must be: two
/// builds of the same commit are the same version.
/// </para>
/// </remarks>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private readonly int[] _numbers;
    private readonly string[] _prerelease;

    private ReleaseVersion(int[] numbers, string[] prerelease)
    {
        _numbers = numbers;
        _prerelease = prerelease;
    }

    /// <summary>True for a build of an unreleased version, such as <c>1.1.1-dev.12</c>.</summary>
    public bool IsPrerelease => _prerelease.Length > 0;

    /// <summary>
    /// Reads a version, with or without a leading <c>v</c>.
    /// </summary>
    /// <returns>The version, or null when the text is not one.</returns>
    public static ReleaseVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
        {
            return null;
        }

        var span = text.Trim();

        if (span.StartsWith('v') || span.StartsWith('V'))
        {
            span = span[1..];
        }

        var plus = span.IndexOf('+', StringComparison.Ordinal);

        if (plus >= 0)
        {
            span = span[..plus];
        }

        var dash = span.IndexOf('-', StringComparison.Ordinal);
        var core = dash >= 0 ? span[..dash] : span;
        var prerelease = dash >= 0 ? span[(dash + 1)..] : null;

        var parts = core.Split('.');

        if (parts.Length is < 2 or > 4)
        {
            return null;
        }

        var numbers = new int[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0
                || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        string[] identifiers = [];

        if (prerelease is not null)
        {
            identifiers = prerelease.Split('.');

            if (identifiers.Any(static id => id.Length == 0 || !id.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            {
                return null;
            }
        }

        return new ReleaseVersion(numbers, identifiers);
    }

    /// <summary>The version <paramref name="assembly"/> was built as.</summary>
    /// <remarks>
    /// Read from the informational version, which is the one the release workflow stamps
    /// with the full <c>-dev.N</c> suffix. The assembly version is four plain numbers and
    /// would make every rolling build look like the release it precedes. Taken as an
    /// argument because this project cannot know which assembly is the application.
    /// </remarks>
    public static ReleaseVersion Of(System.Reflection.Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        return TryParse(informational)
            ?? TryParse(assembly.GetName().Version?.ToString(3))
            ?? new ReleaseVersion([0, 0, 0], ["unknown"]);
    }

    /// <inheritdoc />
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        // Missing trailing numbers count as zero, so 1.0 and 1.0.0 are the same version.
        var length = Math.Max(_numbers.Length, other._numbers.Length);

        for (var i = 0; i < length; i++)
        {
            var left = i < _numbers.Length ? _numbers[i] : 0;
            var right = i < other._numbers.Length ? other._numbers[i] : 0;

            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        // A release outranks every pre-release of the same numbers.
        if (IsPrerelease != other.IsPrerelease)
        {
            return IsPrerelease ? -1 : 1;
        }

        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var result = CompareIdentifier(_prerelease[i], other._prerelease[i]);

            if (result != 0)
            {
                return result;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    /// <summary>
    /// Semantic versioning's rule for one pre-release identifier: numbers compare as
    /// numbers and sort below words, words compare as ordinal text.
    /// </summary>
    /// <remarks>Numerically, so <c>dev.10</c> is newer than <c>dev.9</c>.</remarks>
    private static int CompareIdentifier(string left, string right)
    {
        var leftIsNumber = long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
        var rightIsNumber = long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

        return (leftIsNumber, rightIsNumber) switch
        {
            (true, true) => leftNumber.CompareTo(rightNumber),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(left, right),
        };
    }

    /// <inheritdoc />
    public bool Equals(ReleaseVersion? other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        // Trailing zeros are insignificant to Equals, so they must be to the hash too.
        var significant = _numbers.Length;

        while (significant > 0 && _numbers[significant - 1] == 0)
        {
            significant--;
        }

        var hash = new HashCode();

        for (var i = 0; i < significant; i++)
        {
            hash.Add(_numbers[i]);
        }

        foreach (var identifier in _prerelease)
        {
            hash.Add(identifier, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>The version as a person reads it, with no leading <c>v</c>.</summary>
    public override string ToString()
    {
        var core = string.Join('.', _numbers.Select(static n => n.ToString(CultureInfo.InvariantCulture)));

        return IsPrerelease ? core + "-" + string.Join('.', _prerelease) : core;
    }

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator ==(ReleaseVersion? left, ReleaseVersion? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(ReleaseVersion? left, ReleaseVersion? right) => !(left == right);
}
