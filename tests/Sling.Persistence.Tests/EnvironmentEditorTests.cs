using Sling.Persistence.Environments;
using Sling.Persistence.Workspaces;

namespace Sling.Persistence.Tests;

/// <summary>
/// Writing environment values, and the one property that governs how it is done: the
/// user's file comes back with everything they wrote still in it.
/// </summary>
/// <remarks>
/// These files are hand-edited, so they carry comments, an ordering somebody chose, and
/// formatting that is theirs. A serialiser round trip would silently delete the comments
/// and reflow the rest, which is why the write is a splice and why most of what is asserted
/// here is about the parts of the file that were <em>not</em> the target of the edit.
/// </remarks>
public sealed class EnvironmentEditorTests
{
    [Fact]
    public void A_new_value_is_added_to_an_existing_environment()
    {
        var edited = EnvironmentFileWriter.SetValue(
            """
            {
              "dev": {
                "base": "https://dev.example.com"
              }
            }
            """,
            "dev",
            "token",
            "abc123");

        var values = Parse(edited).Select("dev");

        Assert.True(values.TryGet("base", out var @base));
        Assert.Equal("https://dev.example.com", @base);

        Assert.True(values.TryGet("token", out var token));
        Assert.Equal("abc123", token);
    }

    [Fact]
    public void An_existing_value_is_replaced_in_place()
    {
        var edited = EnvironmentFileWriter.SetValue(
            """
            {
              "dev": {
                "base": "https://dev.example.com",
                "token": "old"
              }
            }
            """,
            "dev",
            "token",
            "new");

        Assert.True(Parse(edited).Select("dev").TryGet("token", out var token));
        Assert.Equal("new", token);

        // The rest of the line, not only the value: replacing the whole property would be
        // an easier edit that quietly reformats somebody's file.
        Assert.Contains("\"token\": \"new\"", edited, StringComparison.Ordinal);
    }

    [Fact]
    public void Comments_survive_the_edit()
    {
        var edited = EnvironmentFileWriter.SetValue(
            """
            {
              // the deployment everybody shares
              "dev": {
                "base": "https://dev.example.com", // staging, not production
                "audience": "orders"
              }
            }
            """,
            "dev",
            "token",
            "abc123");

        Assert.Contains("// the deployment everybody shares", edited, StringComparison.Ordinal);
        Assert.Contains("// staging, not production", edited, StringComparison.Ordinal);
        Assert.True(Parse(edited).Select("dev").TryGet("token", out _));
    }

    [Fact]
    public void A_trailing_comma_does_not_become_two()
    {
        var edited = EnvironmentFileWriter.SetValue(
            """
            {
              "dev": {
                "base": "https://dev.example.com",
              }
            }
            """,
            "dev",
            "token",
            "abc123");

        Assert.DoesNotContain(",,", edited, StringComparison.Ordinal);
        Assert.True(Parse(edited).Select("dev").TryGet("token", out _));
    }

    [Fact]
    public void An_environment_that_is_not_there_yet_is_added()
    {
        var edited = EnvironmentFileWriter.SetValue(
            """
            {
              "dev": {
                "base": "https://dev.example.com"
              }
            }
            """,
            "prod",
            "base",
            "https://api.example.com");

        var set = Parse(edited);

        Assert.Equal(["dev", "prod"], set.Names);
        Assert.True(set.Select("prod").TryGet("base", out var @base));
        Assert.Equal("https://api.example.com", @base);
    }

    [Fact]
    public void An_empty_environment_gets_its_first_value()
    {
        var edited = EnvironmentFileWriter.SetValue("""{ "dev": {} }""", "dev", "token", "abc123");

        Assert.True(Parse(edited).Select("dev").TryGet("token", out var token));
        Assert.Equal("abc123", token);
    }

    [Fact]
    public void An_object_written_on_one_line_stays_on_one_line()
    {
        var edited = EnvironmentFileWriter.SetValue(
            """{ "dev": { "base": "https://dev.example.com" } }""",
            "dev",
            "token",
            "abc123");

        Assert.DoesNotContain('\n', edited);
        Assert.True(Parse(edited).Select("dev").TryGet("token", out _));
    }

    [Fact]
    public void An_empty_file_becomes_a_whole_document()
    {
        var edited = EnvironmentFileWriter.SetValue(string.Empty, "dev", "token", "abc123");

        Assert.True(Parse(edited).Select("dev").TryGet("token", out var token));
        Assert.Equal("abc123", token);
    }

    /// <summary>
    /// The reader counts UTF-8 bytes and the splice counts characters, so a file whose
    /// earlier lines are not ASCII is where an unconverted offset shows up - as an edit
    /// landing several characters late, or through the middle of one.
    /// </summary>
    [Fact]
    public void An_edit_below_non_ascii_text_lands_in_the_right_place()
    {
        var edited = EnvironmentFileWriter.SetValue(
            """
            {
              // sandkasten für die königlichen tests 🔑
              "dev": {
                "gruß": "hallo"
              }
            }
            """,
            "dev",
            "token",
            "abc123");

        var values = Parse(edited).Select("dev");

        Assert.True(values.TryGet("gruß", out var greeting));
        Assert.Equal("hallo", greeting);

        Assert.True(values.TryGet("token", out var token));
        Assert.Equal("abc123", token);
    }

    /// <summary>
    /// A value is untrusted text - it is about to be typed into a card by somebody pasting
    /// a token out of a console. A quote or a backslash in it must not be able to end the
    /// string it is in and start something else in the user's file.
    /// </summary>
    [Fact]
    public void A_value_holding_json_syntax_is_escaped()
    {
        var hostile = """a": "b", "injected": "yes""";

        var edited = EnvironmentFileWriter.SetValue("""{ "dev": {} }""", "dev", "token", hostile);
        var values = Parse(edited).Select("dev");

        Assert.True(values.TryGet("token", out var token));
        Assert.Equal(hostile, token);
        Assert.False(values.TryGet("injected", out _));
    }

    [Fact]
    public void A_malformed_file_is_refused_rather_than_spliced()
    {
        var broken = """{ "dev": { "base": "https://dev.example.com" """;

        Assert.Throws<InvalidDataException>(
            () => EnvironmentFileWriter.SetValue(broken, "dev", "token", "abc123"));
    }

    [Fact]
    public void An_environment_that_is_not_an_object_is_refused()
    {
        Assert.Throws<InvalidDataException>(
            () => EnvironmentFileWriter.SetValue("""{ "dev": "not an object" }""", "dev", "token", "abc123"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1token")]
    [InlineData("my token")]
    [InlineData("token.value")]
    [InlineData("{{token}}")]
    public void A_name_that_could_not_be_referenced_is_refused(string name)
    {
        Assert.False(EnvironmentEditor.IsWritableName(name, out var reason));
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("_token")]
    [InlineData("client-secret")]
    [InlineData("base2")]
    public void An_ordinary_name_is_accepted(string name) =>
        Assert.True(EnvironmentEditor.IsWritableName(name, out _));

    [Fact]
    public async Task A_secret_creates_the_private_file_and_the_ignore_entry()
    {
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        var written = await EnvironmentEditor
            .SetAsync(workspace, "dev", "token", "abc123", secret: true, CancellationToken.None);

        Assert.Equal(Workspace.PrivateEnvironmentFileName, written.FileName);
        Assert.True(folder.Exists(Workspace.PrivateEnvironmentFileName));

        // The committed file is not touched, which is the property that keeps the secret
        // out of the repository even if the ignore entry were somehow missed.
        Assert.False(folder.Exists(Workspace.SharedEnvironmentFileName));

        Assert.Contains(Workspace.PrivateEnvironmentFileName, folder.Read(".gitignore"), StringComparison.Ordinal);
        Assert.NotEmpty(written.IgnoreEntriesAdded);
    }

    [Fact]
    public async Task A_value_that_is_not_secret_goes_to_the_committed_file()
    {
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        var written = await EnvironmentEditor
            .SetAsync(workspace, "dev", "base", "https://api.example.com", secret: false, CancellationToken.None);

        Assert.Equal(Workspace.SharedEnvironmentFileName, written.FileName);
        Assert.False(folder.Exists(Workspace.PrivateEnvironmentFileName));

        // No .gitignore entry either: a repository holding no secrets should not have rules
        // written into it on Sling's say-so.
        Assert.False(folder.Exists(".gitignore"));
        Assert.Empty(written.IgnoreEntriesAdded);
    }

    [Fact]
    public async Task Two_writes_to_the_same_environment_both_survive()
    {
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        await EnvironmentEditor.SetAsync(workspace, "dev", "token", "one", secret: true, CancellationToken.None);
        await EnvironmentEditor.SetAsync(workspace, "dev", "audience", "orders", secret: true, CancellationToken.None);

        var values = EnvironmentStore.Load(workspace).Select("dev");

        Assert.True(values.TryGet("token", out var token));
        Assert.Equal("one", token);

        Assert.True(values.TryGet("audience", out var audience));
        Assert.Equal("orders", audience);
    }

    [Fact]
    public void Entries_report_the_environment_and_the_file_a_value_was_written_in()
    {
        using var folder = new TemporaryFolder();
        folder.Write(Workspace.SharedEnvironmentFileName, """{ "dev": { "base": "https://dev.example.com" } }""");
        folder.Write(Workspace.PrivateEnvironmentFileName, """{ "dev": { "token": "abc123" } }""");

        var entries = EnvironmentStore.Load(Workspace.Open(folder.Path)).Entries;

        Assert.Contains(entries, e => e is { Environment: "dev", Name: "base", Secret: false });
        Assert.Contains(entries, e => e is { Environment: "dev", Name: "token", Secret: true });
    }

    /// <summary>
    /// Reads an edited file back the way Sling does, so what is asserted is what a request
    /// would actually resolve rather than what the text looks like.
    /// </summary>
    private static EnvironmentSet Parse(string json)
    {
        var folder = new TemporaryFolder();

        try
        {
            folder.Write(Workspace.SharedEnvironmentFileName, json);

            var set = EnvironmentStore.Load(Workspace.Open(folder.Path));
            Assert.Empty(set.Problems);

            return set;
        }
        finally
        {
            folder.Dispose();
        }
    }
}
