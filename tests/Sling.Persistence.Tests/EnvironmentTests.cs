using Sling.Persistence.Environments;
using Sling.Persistence.Workspaces;

namespace Sling.Persistence.Tests;

/// <summary>
/// The two environment files, how they layer, and what happens when one is wrong.
/// </summary>
/// <remarks>
/// The layering is the security-relevant part: <c>Sling.md</c> §5.1 requires that a
/// secret is never resolvable from the file that gets committed, and the split between
/// <c>http-client.env.json</c> and <c>http-client.private.env.json</c> is what delivers
/// it. A merge that took the committed value last would silently defeat that.
/// </remarks>
public sealed class EnvironmentTests
{
    [Fact]
    public void An_environment_supplies_its_own_values()
    {
        using var folder = new TemporaryFolder();
        folder.Write(Workspace.SharedEnvironmentFileName, """{ "dev": { "base": "https://dev.example.com" } }""");

        var values = EnvironmentStore.Load(Workspace.Open(folder.Path)).Select("dev");

        Assert.True(values.TryGet("base", out var value));
        Assert.Equal("https://dev.example.com", value);
    }

    [Fact]
    public void The_shared_environment_underlies_every_other_one()
    {
        using var folder = new TemporaryFolder();
        folder.Write(
            Workspace.SharedEnvironmentFileName,
            """
            {
              "$shared": { "version": "v2", "base": "https://example.com" },
              "prod": { "base": "https://api.example.com" }
            }
            """);

        var values = EnvironmentStore.Load(Workspace.Open(folder.Path)).Select("prod");

        Assert.True(values.TryGet("version", out var version));
        Assert.Equal("v2", version);

        // The named environment overrides the shared one, not the other way round.
        Assert.True(values.TryGet("base", out var @base));
        Assert.Equal("https://api.example.com", @base);
    }

    [Fact]
    public void The_private_file_overrides_the_committed_one_and_is_marked_secret()
    {
        using var folder = new TemporaryFolder();
        folder.Write(Workspace.SharedEnvironmentFileName, """{ "dev": { "token": "put-yours-in-the-private-file" } }""");
        folder.Write(Workspace.PrivateEnvironmentFileName, """{ "dev": { "token": "real-one" } }""");

        var values = EnvironmentStore.Load(Workspace.Open(folder.Path)).Select("dev");

        // The committed placeholder is a working default that the real value replaces,
        // which only holds if the private file is applied last.
        Assert.True(values.TryGet("token", out var token));
        Assert.Equal("real-one", token);
        Assert.True(values.IsSecret("token"));
    }

    [Fact]
    public void A_value_from_the_committed_file_alone_is_not_marked_secret()
    {
        using var folder = new TemporaryFolder();
        folder.Write(Workspace.SharedEnvironmentFileName, """{ "dev": { "base": "https://dev.example.com" } }""");

        var values = EnvironmentStore.Load(Workspace.Open(folder.Path)).Select("dev");

        Assert.False(values.IsSecret("base"));
    }

    [Fact]
    public void Environment_names_come_from_both_files_and_exclude_the_shared_one()
    {
        using var folder = new TemporaryFolder();
        folder.Write(Workspace.SharedEnvironmentFileName, """{ "$shared": {}, "dev": {}, "prod": {} }""");
        folder.Write(Workspace.PrivateEnvironmentFileName, """{ "prod": {}, "local": {} }""");

        var names = EnvironmentStore.Load(Workspace.Open(folder.Path)).Names;

        Assert.Equal(["dev", "local", "prod"], names);
    }

    [Fact]
    public void Selecting_nothing_resolves_nothing_but_still_applies_the_shared_values()
    {
        // The shared environment is not one of the choices, so it cannot be "not selected".
        using var folder = new TemporaryFolder();
        folder.Write(Workspace.SharedEnvironmentFileName, """{ "$shared": { "agent": "Sling" }, "dev": { "base": "x" } }""");

        var values = EnvironmentStore.Load(Workspace.Open(folder.Path)).Select(null);

        Assert.True(values.TryGet("agent", out _));
        Assert.False(values.TryGet("base", out _));
    }

    [Theory]
    [InlineData("""{ "dev": { "port": 8080 } }""", "8080")]
    [InlineData("""{ "dev": { "port": true } }""", "true")]
    public void A_number_or_boolean_is_taken_as_the_text_that_was_written(string json, string expected)
    {
        // A port written unquoted is the commonest thing in one of these files, and it
        // substitutes into a URL as text either way.
        using var folder = new TemporaryFolder();
        folder.Write(Workspace.SharedEnvironmentFileName, json);

        var set = EnvironmentStore.Load(Workspace.Open(folder.Path));

        Assert.True(set.Select("dev").TryGet("port", out var value));
        Assert.Equal(expected, value);
        Assert.Empty(set.Problems);
    }

    [Fact]
    public void A_malformed_file_is_reported_rather_than_thrown_and_names_its_line()
    {
        using var folder = new TemporaryFolder();
        folder.Write(Workspace.SharedEnvironmentFileName, "{\n  \"dev\": { \"base\": \n}");

        var set = EnvironmentStore.Load(Workspace.Open(folder.Path));

        var problem = Assert.Single(set.Problems);
        Assert.Contains(Workspace.SharedEnvironmentFileName, problem, StringComparison.Ordinal);
        Assert.Contains("line", problem, StringComparison.Ordinal);
        Assert.Empty(set.Names);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed_because_the_file_is_hand_written()
    {
        using var folder = new TemporaryFolder();
        folder.Write(
            Workspace.SharedEnvironmentFileName,
            """
            {
              // the one the team shares
              "dev": { "base": "https://dev.example.com", },
            }
            """);

        var set = EnvironmentStore.Load(Workspace.Open(folder.Path));

        Assert.Empty(set.Problems);
        Assert.True(set.Select("dev").TryGet("base", out _));
    }

    [Fact]
    public void A_value_that_is_not_a_scalar_is_reported_and_the_rest_of_the_file_still_loads()
    {
        using var folder = new TemporaryFolder();
        folder.Write(
            Workspace.SharedEnvironmentFileName,
            """{ "dev": { "nested": { "a": 1 }, "base": "https://dev.example.com" } }""");

        var set = EnvironmentStore.Load(Workspace.Open(folder.Path));

        Assert.Contains("dev.nested", Assert.Single(set.Problems), StringComparison.Ordinal);
        Assert.True(set.Select("dev").TryGet("base", out _));
    }

    [Fact]
    public void A_workspace_with_no_environment_files_is_empty_and_not_a_problem()
    {
        // Most workspaces have none, and a tool that complains about a file it does not
        // need is a tool people stop reading messages from.
        using var folder = new TemporaryFolder();

        var set = EnvironmentStore.Load(Workspace.Open(folder.Path));

        Assert.Empty(set.Names);
        Assert.Empty(set.Problems);
    }
}
