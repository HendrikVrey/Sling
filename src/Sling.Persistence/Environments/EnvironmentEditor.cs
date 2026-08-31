using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Sling.Persistence.Workspaces;

namespace Sling.Persistence.Environments;

/// <summary>
/// Writing environment values from inside Sling, including creating the secrets file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Until this existed, a credential could not be created from inside Sling at all.</b>
/// <c>docs/environments.md</c> said as much in plain words: nothing in Sling wrote
/// <c>http-client.private.env.json</c>. So the only route to a bearer token was to leave
/// the application, know the file name and the JSON shape, hand-write it, and make its
/// environment names match another file that had also been hand-written. Every other
/// friction in Sling's auth story was smaller than that one.
/// </para>
/// <para>
/// The files stay the truth. This writes text into them and then has no further role -
/// there is no store, no index and nothing Sling remembers about a variable. Delete Sling
/// and the environments are still two JSON files that Rider and Visual Studio open
/// unchanged.
/// </para>
/// <para>
/// <b>The secret flag decides which file the value lands in, and that is the whole of the
/// security design.</b> A value written as a secret goes to the gitignored file, and
/// writing one creates the file and the <c>.gitignore</c> entry together, in that order,
/// so there is no window in which a secrets file exists unignored.
/// </para>
/// </remarks>
public static partial class EnvironmentEditor
{
    /// <summary>
    /// What a variable may be called.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately narrower than what <see cref="EnvironmentFile"/> will read. An existing
    /// file may hold anything; this is the rule for names Sling is asked to <em>write</em>,
    /// and it exists so that a name typed into a card is always referenceable as
    /// <c>{{name}}</c> afterwards. A name carrying a brace, a space or a hash resolves to
    /// nothing and gives no clue why.
    /// </para>
    /// <para>
    /// No dot, which is the one restriction that needs saying out loud: a reference is
    /// tested against the <c>{{name.response.body.$.field}}</c> chain grammar before the
    /// environment is consulted, so a dotted name is one the chain syntax can shadow at some
    /// later date. Refusing it costs nothing today and forecloses that.
    /// </para>
    /// </remarks>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex NamePattern { get; }

    /// <summary>
    /// Checks a variable name, and says why not when it is refused.
    /// </summary>
    /// <remarks>
    /// Separate from the write so a card can say "that will not work" while it is being
    /// typed, rather than after the file has been opened.
    /// </remarks>
    public static bool IsWritableName(string? name, [NotNullWhen(false)] out string? reason)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            reason = "A variable needs a name.";
            return false;
        }

        if (name.Length > 128)
        {
            reason = "That name is too long for a variable.";
            return false;
        }

        if (!NamePattern.IsMatch(name))
        {
            reason = "A variable name starts with a letter or an underscore, and holds only "
                + "letters, digits, underscores and hyphens.";

            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Sets one variable in one environment, creating the file if it is not there.
    /// </summary>
    /// <param name="environment">
    /// The environment the value belongs to, or <see cref="EnvironmentSet.SharedName"/> for
    /// the values that underlie every environment.
    /// </param>
    /// <param name="secret">
    /// True to write it to the gitignored file. This is the only thing that decides where a
    /// value is stored, and it is what stops a credential reaching a committed file.
    /// </param>
    /// <returns>What was written, and any <c>.gitignore</c> entries that had to be added.</returns>
    /// <exception cref="ArgumentException">The name is not one Sling will write.</exception>
    /// <exception cref="InvalidDataException">The existing file cannot be edited safely.</exception>
    /// <exception cref="IOException">The file could not be read or written.</exception>
    public static async Task<EnvironmentWrite> SetAsync(
        Workspace workspace,
        string environment,
        string name,
        string value,
        bool secret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);

        if (!IsWritableName(name, out var reason))
        {
            throw new ArgumentException(reason, nameof(name));
        }

        var path = secret ? workspace.PrivateEnvironmentFile : workspace.SharedEnvironmentFile;
        var existing = File.Exists(path)
            ? await RequestFileStore.ReadAsync(path, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        string edited;

        try
        {
            edited = EnvironmentFileWriter.SetValue(existing, environment, name, value);
        }
        catch (InvalidDataException ex)
        {
            // Re-thrown with the file named. The writer works on text and does not know
            // which of the two files it was handed, and "it is not valid JSON" without a
            // file name is a message that sends the user to the wrong one.
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' cannot be edited by Sling because {ex.Message}. "
                    + "Fix it in a text editor and try again.",
                ex);
        }

        // The ignore entry goes in before the secret does. The other order leaves a window -
        // short, but a 'git add -A' wide - in which a file full of credentials exists in a
        // repository that has never heard of it.
        IReadOnlyList<string> ignored = secret ? Protect(workspace) : [];

        // RequestFileStore rather than a second copy of the same write. Its name says
        // 'request file' and its behaviour is what an environment file needs too: UTF-8
        // without a byte order mark, written to a sibling temporary and moved over the
        // target, so a failure leaves the previous version rather than half of this one.
        await RequestFileStore.SaveAsync(path, edited, cancellationToken).ConfigureAwait(false);

        return new EnvironmentWrite(Path.GetFileName(path), environment, name, secret, ignored);
    }

    /// <summary>
    /// Adds the ignore entries for a secrets file that is about to exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="EnvironmentStore.ProtectSecrets"/> cannot be used here: it returns early
    /// unless the file already exists, which is right for the case it covers - a workspace
    /// being opened - and exactly wrong for this one, where the file is being created a
    /// moment from now.
    /// </para>
    /// <para>
    /// A failure is deliberately not caught. <see cref="EnvironmentStore.ProtectSecrets"/>
    /// swallows one because it runs on every window activation, where failing to harden is no
    /// reason to refuse to open a folder. This runs at the moment a credential is about to be
    /// written, where the same failure means the secret would land in a repository that does
    /// not ignore it - so the write does not happen and the user is told why.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> Protect(Workspace workspace) =>
        GitIgnoreGuard.EnsureIgnored(workspace.Root, EnvironmentStore.IgnoreEntries);
}

/// <summary>What a write to an environment file did.</summary>
/// <param name="FileName">The file it landed in, so the user can be told which one.</param>
/// <param name="Environment">The environment the value belongs to.</param>
/// <param name="Name">The variable's name. Never accompanied by its value.</param>
/// <param name="Secret">True when it went to the gitignored file.</param>
/// <param name="IgnoreEntriesAdded">
/// The <c>.gitignore</c> entries that had to be added, so the user can be told their
/// repository was modified. Saying so matters as much as doing it.
/// </param>
public sealed record EnvironmentWrite(
    string FileName,
    string Environment,
    string Name,
    bool Secret,
    IReadOnlyList<string> IgnoreEntriesAdded);
