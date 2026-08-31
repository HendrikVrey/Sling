using Sling.Persistence.Environments;
using Sling.Persistence.Workspaces;

namespace Sling.App.Environments;

/// <summary>
/// One variable as the environment card draws it.
/// </summary>
/// <remarks>
/// <para>
/// A projection of <see cref="EnvironmentEntry"/> and nothing more: the card is rebuilt
/// from the files every time it opens, so there is no state here to fall out of step with
/// them. Same rule as the collections rail, one file over - nothing is stored to draw this.
/// </para>
/// <para>
/// <b>A secret's value is not in <see cref="Display"/> unless it was asked for.</b> The
/// people who open this card are the people who put a live token in it, and a panel is a
/// place a screenshot comes from. The value is still on the row, because editing it is the
/// whole point of the card - it is just not on screen by default.
/// </para>
/// </remarks>
internal sealed class EnvironmentRow
{
    /// <summary>What a hidden secret looks like. A fixed width, so it says nothing about the length of the real one.</summary>
    private const string Mask = "••••••••••••";

    internal EnvironmentRow(EnvironmentEntry entry, bool reveal)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Environment = entry.Environment;
        Name = entry.Name;
        Value = entry.Value;
        Secret = entry.Secret;

        Display = Secret && !reveal ? Mask : entry.Value;

        FileLabel = Secret ? "secret" : "committed";

        Hint = Secret
            ? $"'{Name}' is in {Workspace.PrivateEnvironmentFileName}, which is gitignored. Click to change it."
            : $"'{Name}' is in {Workspace.SharedEnvironmentFileName}, which is meant to be committed. Click to change it.";
    }

    /// <summary>The environment this value is declared under.</summary>
    public string Environment { get; }

    public string Name { get; }

    /// <summary>The value as written. A credential when <see cref="Secret"/> is true.</summary>
    public string Value { get; }

    public bool Secret { get; }

    /// <summary>What the row shows: the value, or a mask standing in for a hidden secret.</summary>
    public string Display { get; }

    public string FileLabel { get; }

    public string Hint { get; }
}
