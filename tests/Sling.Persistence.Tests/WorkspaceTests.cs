using System.Text;
using Sling.Persistence.Workspaces;

namespace Sling.Persistence.Tests;

/// <summary>
/// Listing a folder's request files, saving one, and keeping the secrets file out of git.
/// </summary>
public sealed class WorkspaceTests
{
    [Fact]
    public void Request_files_are_listed_relative_to_the_root_in_a_stable_order()
    {
        using var folder = new TemporaryFolder();
        folder.Write("zebra.http", string.Empty);
        folder.Write("api/users.http", string.Empty);
        folder.Write("api/auth.rest", string.Empty);
        folder.Write("readme.md", string.Empty);

        var files = Workspace.Open(folder.Path).RequestFiles(out var truncated);

        Assert.False(truncated);
        Assert.Equal(
            [Path.Combine("api", "auth.rest"), Path.Combine("api", "users.http"), "zebra.http"],
            files);
    }

    [Fact]
    public void Build_output_and_version_control_folders_are_not_walked()
    {
        // A .git directory can hold more objects than the rest of the tree combined, and
        // none of them is a request somebody wrote.
        using var folder = new TemporaryFolder();
        folder.Write("real.http", string.Empty);
        folder.Write("bin/Debug/copied.http", string.Empty);
        folder.Write("obj/generated.http", string.Empty);
        folder.Write("node_modules/pkg/fixture.http", string.Empty);
        folder.Write(".git/hooks/sample.http", string.Empty);

        var files = Workspace.Open(folder.Path).RequestFiles(out _);

        Assert.Equal(["real.http"], files);
    }

    [Fact]
    public void Opening_a_folder_that_is_not_there_says_so()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sling-tests", Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(() => Workspace.Open(missing));
    }

    [Fact]
    public async Task A_document_round_trips_through_save_and_read()
    {
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "requests.http");

        await RequestFileStore.SaveAsync(path, "GET https://api.example.com/things\r\n", TestContext.Current.CancellationToken);
        var text = await RequestFileStore.ReadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("GET https://api.example.com/things\r\n", text);
    }

    [Fact]
    public async Task A_saved_document_has_no_byte_order_mark()
    {
        // A .http file that starts with a BOM is a file whose first request line does not
        // parse in half the tools that read the format.
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "requests.http");

        await RequestFileStore.SaveAsync(path, "GET https://api.example.com/", TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal((byte)'G', bytes[0]);
    }

    [Fact]
    public async Task Saving_leaves_no_temporary_file_behind()
    {
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "requests.http");

        await RequestFileStore.SaveAsync(path, "GET https://api.example.com/", TestContext.Current.CancellationToken);

        Assert.Equal(["requests.http"], Directory.GetFiles(folder.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Saving_over_an_existing_document_replaces_it_completely()
    {
        // The failure a non-atomic write produces is a shorter file with the tail of the
        // old one still on the end, which is worse than either version.
        using var folder = new TemporaryFolder();
        var path = folder.Write("requests.http", new string('x', 4096));

        await RequestFileStore.SaveAsync(path, "GET https://api.example.com/", TestContext.Current.CancellationToken);

        Assert.Equal("GET https://api.example.com/", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_document_saved_with_a_byte_order_mark_elsewhere_is_read_without_it()
    {
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "bom.http");
        await File.WriteAllTextAsync(path, "GET https://api.example.com/", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), TestContext.Current.CancellationToken);

        var text = await RequestFileStore.ReadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("GET https://api.example.com/", text);
    }
}
