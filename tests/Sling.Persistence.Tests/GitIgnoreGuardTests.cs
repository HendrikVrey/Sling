using Sling.Persistence.Environments;
using Sling.Persistence.Workspaces;

namespace Sling.Persistence.Tests;

/// <summary>
/// Keeping the secrets file out of the repository.
/// </summary>
/// <remarks>
/// <c>Sling.md</c> §5.1 makes this structural rather than advisory: a committed bearer
/// token is <em>the</em> known failure mode of <c>.http</c> files in the wild. These
/// tests also pin the other half of the rule - that this only ever appends, because it is
/// editing somebody else's repository.
/// </remarks>
public sealed class GitIgnoreGuardTests
{
    [Fact]
    public void The_entries_are_added_when_there_is_no_gitignore_at_all()
    {
        using var folder = new TemporaryFolder();

        var added = GitIgnoreGuard.EnsureIgnored(folder.Path, ["secrets.json"]);

        Assert.Equal(["secrets.json"], added);
        Assert.Contains("secrets.json", folder.Read(".gitignore"), StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_rules_are_kept_exactly_as_they_were()
    {
        using var folder = new TemporaryFolder();
        folder.Write(".gitignore", "bin/\nobj/\n");

        GitIgnoreGuard.EnsureIgnored(folder.Path, ["secrets.json"]);

        var text = folder.Read(".gitignore");
        Assert.StartsWith("bin/\nobj/\n", text, StringComparison.Ordinal);
        Assert.Contains("secrets.json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_already_present_is_not_added_again()
    {
        using var folder = new TemporaryFolder();
        folder.Write(".gitignore", "bin/\nsecrets.json\n");

        var added = GitIgnoreGuard.EnsureIgnored(folder.Path, ["secrets.json"]);

        Assert.Empty(added);
        Assert.Equal("bin/\nsecrets.json\n", folder.Read(".gitignore"));
    }

    [Fact]
    public void Only_the_missing_entries_are_added()
    {
        using var folder = new TemporaryFolder();
        folder.Write(".gitignore", "secrets.json\n");

        var added = GitIgnoreGuard.EnsureIgnored(folder.Path, ["secrets.json", "cookies.json"]);

        Assert.Equal(["cookies.json"], added);
    }

    [Fact]
    public void A_file_that_does_not_end_in_a_newline_does_not_get_its_last_rule_welded_to_the_next()
    {
        // Appending straight on to 'bin/' would produce 'bin/# Sling - …', and git would
        // stop honouring the rule that was already there.
        using var folder = new TemporaryFolder();
        folder.Write(".gitignore", "bin/");

        GitIgnoreGuard.EnsureIgnored(folder.Path, ["secrets.json"]);

        Assert.Contains("bin/\n", folder.Read(".gitignore"), StringComparison.Ordinal);
    }

    [Fact]
    public void Running_it_twice_changes_nothing_the_second_time()
    {
        using var folder = new TemporaryFolder();

        GitIgnoreGuard.EnsureIgnored(folder.Path, ["secrets.json"]);
        var after = folder.Read(".gitignore");

        Assert.Empty(GitIgnoreGuard.EnsureIgnored(folder.Path, ["secrets.json"]));
        Assert.Equal(after, folder.Read(".gitignore"));
    }

    [Fact]
    public void A_workspace_holding_a_secrets_file_gets_it_ignored_on_open()
    {
        // The dangerous case is the one Sling did not create: a secrets file that arrived
        // by hand, in a repository whose .gitignore has never heard of it.
        using var folder = new TemporaryFolder();
        folder.Write(Workspace.PrivateEnvironmentFileName, """{ "dev": { "token": "s3cret" } }""");

        var added = EnvironmentStore.ProtectSecrets(Workspace.Open(folder.Path));

        Assert.NotEmpty(added);
        Assert.Contains(Workspace.PrivateEnvironmentFileName, folder.Read(".gitignore"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_workspace_with_no_secrets_file_is_left_alone()
    {
        // No secrets, no business writing into somebody's .gitignore.
        using var folder = new TemporaryFolder();

        Assert.Empty(EnvironmentStore.ProtectSecrets(Workspace.Open(folder.Path)));
        Assert.False(folder.Exists(".gitignore"));
    }
}
