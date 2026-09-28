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
    public void A_file_in_the_folder_or_under_it_is_contained()
    {
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        Assert.True(workspace.Contains(Path.Combine(folder.Path, "requests.http")));
        Assert.True(workspace.Contains(Path.Combine(folder.Path, "api", "deep", "users.http")));
        Assert.True(workspace.Contains(folder.Path));
    }

    [Fact]
    public void A_file_outside_the_folder_is_not_contained()
    {
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        var elsewhere = Path.Combine(Path.GetDirectoryName(folder.Path)!, "other", "requests.http");
        var above = Path.Combine(folder.Path, "..", "requests.http");

        Assert.False(workspace.Contains(elsewhere));
        Assert.False(workspace.Contains(above));
    }

    [Fact]
    public void A_sibling_whose_name_starts_with_the_folder_name_is_not_contained()
    {
        // The classic way this check is got wrong: a string prefix test says
        // 'C:\work\api-secrets' is inside 'C:\work\api'. Worth asserting here and not only
        // against WorkspacePaths, because Contains is what decides whether an open document
        // keeps resolving against the folder the window has open.
        using var parent = new TemporaryFolder();

        Directory.CreateDirectory(Path.Combine(parent.Path, "api"));
        var sibling = parent.Write(Path.Combine("api-secrets", "requests.http"), string.Empty);

        Assert.False(Workspace.Open(Path.Combine(parent.Path, "api")).Contains(sibling));
    }

    [Fact]
    public void Peeking_at_a_file_that_is_not_there_answers_nothing()
    {
        using var folder = new TemporaryFolder();

        Assert.Null(RequestFileStore.Peek(Path.Combine(folder.Path, "gone.http")));
    }

    [Fact]
    public void Peeking_reports_the_length_and_the_write_time()
    {
        using var folder = new TemporaryFolder();
        var path = folder.Write("requests.http", "GET https://api.example.com/a\n");

        var peek = RequestFileStore.Peek(path);

        Assert.NotNull(peek);
        Assert.Equal(new FileInfo(path).Length, peek.Value.Length);
        Assert.Equal(new FileInfo(path).LastWriteTimeUtc, peek.Value.LastWriteUtc);
    }

    [Fact]
    public async Task A_files_fingerprint_survives_being_written_and_read_back()
    {
        // The property the changed-on-disk check rests on: what Sling wrote and what Sling
        // reads back are the same document, so its own save must never look like somebody
        // else's edit.
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "requests.http");
        var text = "### one\r\nGET https://api.example.com/a\r\n";

        await RequestFileStore.SaveAsync(path, text, TestContext.Current.CancellationToken);

        Assert.Equal(RequestFileStore.HashOf(text), RequestFileStore.HashOnDisk(path));
    }

    [Fact]
    public void A_byte_order_mark_is_not_a_change_to_the_document()
    {
        // The fingerprint is over the decoded text, which is the comparison actually wanted:
        // two files holding the same requests differ in their bytes if one carries a BOM,
        // and that is not something anybody changed about a request.
        using var folder = new TemporaryFolder();
        var text = "GET https://api.example.com/a\n";

        var plain = Path.Combine(folder.Path, "plain.http");
        var marked = Path.Combine(folder.Path, "marked.http");

        File.WriteAllText(plain, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(marked, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(RequestFileStore.HashOnDisk(plain), RequestFileStore.HashOnDisk(marked));
    }

    [Fact]
    public void A_file_that_cannot_be_read_fingerprints_as_nothing()
    {
        // Nothing equals the empty string, so a caller adopting this as a baseline fails
        // towards asking again rather than towards a dismissal that hides the next change.
        using var folder = new TemporaryFolder();

        Assert.Equal(string.Empty, RequestFileStore.HashOnDisk(Path.Combine(folder.Path, "gone.http")));
    }

    [Fact]
    public void A_file_past_the_document_ceiling_is_not_fingerprinted()
    {
        // Without the ceiling this is a whole-file read with no bound: pointed at a database
        // dump beside the request files it allocates the file twice over, and past about two
        // gigabytes of text it throws out of a method whose contract is that it answers.
        // Written sparse so the test costs no disk.
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "huge.http");

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(RequestFileStore.MaxDocumentBytes + 1);
        }

        Assert.Equal(string.Empty, RequestFileStore.HashOnDisk(path));
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
