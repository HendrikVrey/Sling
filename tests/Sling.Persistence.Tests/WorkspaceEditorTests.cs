using System.Text;
using Sling.Core.Documents;
using Sling.Core.Parsing;
using Sling.Persistence.Workspaces;

namespace Sling.Persistence.Tests;

/// <summary>
/// Creating a collection and a request document from the rail.
/// </summary>
/// <remarks>
/// Against a real folder, for the reason on <see cref="TemporaryFolder"/>: everything
/// interesting here - never overwriting, containment, what a link does - is behaviour of
/// the file system rather than of this code.
/// </remarks>
public sealed class WorkspaceEditorTests
{
    [Fact]
    public async Task A_new_collection_is_a_folder_with_a_request_file_in_it()
    {
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        var relative = await WorkspaceEditor
            .CreateCollectionAsync(workspace, null, "Billing", TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine("Billing", "requests.http"), relative);
        Assert.True(folder.Exists(relative));
    }

    [Fact]
    public async Task The_seeded_document_parses_into_exactly_one_request()
    {
        // The seed is generated text fed straight back through the parser. It is the same
        // hazard the Postman importer's descriptions were, so the property worth asserting
        // is the round trip, not the presence of a line.
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        var relative = await WorkspaceEditor
            .CreateCollectionAsync(workspace, null, "Billing", TestContext.Current.CancellationToken);

        var parsed = RequestDocumentParser.Parse(folder.Read(relative));

        Assert.Single(parsed.Requests);
        Assert.Equal("GET", parsed.Requests[0].Method);
        Assert.Empty(parsed.Diagnostics);
    }

    [Fact]
    public async Task A_collection_can_be_created_inside_another_one()
    {
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        await WorkspaceEditor.CreateCollectionAsync(
            workspace, null, "v1", TestContext.Current.CancellationToken);

        var relative = await WorkspaceEditor.CreateCollectionAsync(
            workspace, "v1", "Billing", TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine("v1", "Billing", "requests.http"), relative);
        Assert.True(folder.Exists(relative));
    }

    [Fact]
    public async Task A_collection_that_is_already_there_is_refused()
    {
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        await WorkspaceEditor.CreateCollectionAsync(
            workspace, null, "Billing", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => WorkspaceEditor.CreateCollectionAsync(
            workspace, null, "Billing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_existing_document_is_never_overwritten()
    {
        using var folder = new TemporaryFolder();
        folder.Write("orders.http", "GET https://example.test/mine\n");

        var workspace = Workspace.Open(folder.Path);

        await Assert.ThrowsAsync<IOException>(() => WorkspaceEditor.CreateDocumentAsync(
            workspace, null, "orders", TestContext.Current.CancellationToken));

        // The refusal is worth nothing if the file was clobbered on the way to it.
        Assert.Contains("mine", folder.Read("orders.http"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_gets_the_http_extension_without_being_asked()
    {
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        var relative = await WorkspaceEditor.CreateDocumentAsync(
            workspace, null, "orders.http", TestContext.Current.CancellationToken);

        Assert.Equal("orders.http", relative);
    }

    [Fact]
    public async Task A_document_is_written_without_a_byte_order_mark()
    {
        // A .http file that starts with one is a file whose first request line does not
        // parse in half the tools that read the format.
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        var relative = await WorkspaceEditor.CreateDocumentAsync(
            workspace, null, "orders", TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(folder.Path, relative), TestContext.Current.CancellationToken);

        // The bytes, not a guess at the first one of them. An earlier version asserted
        // bytes[0] != 0xEF, which is a third of a UTF-8 BOM, would have passed on a UTF-16
        // one, and indexed a file it never checked was non-empty.
        var text = folder.Read(relative);

        Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), bytes);
        Assert.StartsWith("# orders", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData(@"..\..\escape")]
    [InlineData("a/b")]
    public async Task A_name_cannot_place_a_collection_outside_the_workspace(string typed)
    {
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        var relative = await WorkspaceEditor
            .CreateCollectionAsync(workspace, null, typed, TestContext.Current.CancellationToken);

        // Not "it threw" - the name is slugged, so what has to be true is that whatever it
        // became is still under the root.
        var created = Path.GetFullPath(Path.Combine(folder.Path, relative));

        Assert.StartsWith(
            Path.GetFullPath(folder.Path) + Path.DirectorySeparatorChar,
            created,
            StringComparison.OrdinalIgnoreCase);

        Assert.Single(Directory.GetDirectories(folder.Path));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"..\..")]
    [InlineData("does-not-exist")]
    public async Task A_parent_outside_the_workspace_is_refused(string parent)
    {
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        await Assert.ThrowsAsync<IOException>(() => WorkspaceEditor.CreateCollectionAsync(
            workspace, parent, "Billing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_absolute_parent_is_refused()
    {
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        await Assert.ThrowsAsync<IOException>(() => WorkspaceEditor.CreateCollectionAsync(
            workspace, Path.GetTempPath(), "Billing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_name_with_nothing_keepable_is_refused()
    {
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => WorkspaceEditor.CreateCollectionAsync(
            workspace, null, "...", TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetDirectories(folder.Path));
    }

    [Fact]
    public void An_appended_request_parses_as_exactly_one_more_request()
    {
        // The block is generated text read back by the parser, so the assertion has to be
        // the parse rather than the presence of the '###' line - asserting the text is what
        // the M4 review called certifying the comment instead of the property.
        const string Existing = "### first\nGET https://example.test/one\n";

        var parsed = RequestDocumentParser.Parse(
            Existing + WorkspaceEditor.RequestBlockText(Existing, "second", "\n"));

        Assert.Equal(2, parsed.Requests.Count);
        Assert.Equal("second", parsed.Requests[1].Title);
        Assert.DoesNotContain(parsed.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    [InlineData("\n\n\n")]
    [InlineData("\t \t")]
    [InlineData("# just a comment")]
    [InlineData("# just a comment\n")]
    [InlineData("@base = https://example.test")]
    [InlineData("### first\nGET https://example.test/one")]
    [InlineData("### first\nGET https://example.test/one\n")]
    [InlineData("### first\nGET https://example.test/one\n\n\n")]
    public void The_appended_request_always_arrives_as_a_request_with_its_name(string existing)
    {
        // The whole point of the buffer going in rather than a flag: '###' is only a
        // separator at the start of a line, and IsSeparator does not trim. A document of
        // "   " used to produce "   ### orders", which parses as a COMMENT - one request in
        // the file instead of two, and the name silently gone.
        var before = RequestDocumentParser.Parse(existing).Requests.Count;

        var parsed = RequestDocumentParser.Parse(
            existing + WorkspaceEditor.RequestBlockText(existing, "orders", "\n"));

        Assert.Equal(before + 1, parsed.Requests.Count);
        Assert.Equal("orders", parsed.Requests[^1].Title);
        Assert.Equal("GET", parsed.Requests[^1].Method);
        Assert.DoesNotContain(parsed.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void An_appended_request_uses_the_document_s_own_line_endings()
    {
        // A file from a checkout with CRLF endings must not gain one LF line in the middle,
        // invisible in the editor, and a whole-file diff for whoever reviews it next.
        var block = WorkspaceEditor.RequestBlockText("### first\r\nGET https://a.test\r\n", "second", "\r\n");

        Assert.DoesNotContain('\n', block.Replace("\r\n", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void An_appended_request_cannot_smuggle_a_directive_into_the_document()
    {
        // A title is free text in the user's head and a line the parser reads in fact.
        // '# @name' is a directive, so a name of "@name login" must not become one - and it
        // cannot, because '@' is not on the whitelist.
        var block = WorkspaceEditor.RequestBlockText(string.Empty, "@name login", "\n");

        var parsed = RequestDocumentParser.Parse(block);

        Assert.Null(Assert.Single(parsed.Requests).Name);
    }

    [Fact]
    public void An_appended_request_cannot_smuggle_a_second_request_in()
    {
        var block = WorkspaceEditor.RequestBlockText(
            string.Empty,
            "x\n### stolen\nPOST https://evil.test/",
            "\n");

        Assert.Single(RequestDocumentParser.Parse(block).Requests);
    }

    [Fact]
    public void The_first_request_in_an_empty_document_gets_no_leading_blank_line()
    {
        Assert.StartsWith("###", WorkspaceEditor.RequestBlockText(string.Empty, "first", "\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The write boundary, through a junction - the read side's counterpart.
    /// </summary>
    /// <remarks>
    /// <c>WorkspacePaths</c> now has two callers and its own remark says they guard the same
    /// boundary from opposite directions. Only the read direction had a test, and a workspace
    /// whose two boundaries disagree is one where a file is created somewhere it could never
    /// be read from. The walk genuinely can list a file through a junction, so the rail can
    /// hold a node that is lexically inside and physically outside.
    /// </remarks>
    [Fact]
    public async Task A_junction_cannot_be_used_to_write_outside_the_workspace()
    {
        using var inside = new TemporaryFolder();
        using var outside = new TemporaryFolder();

        if (!TryLinkDirectory(Path.Combine(inside.Path, "fixtures"), outside.Path))
        {
            // Creating a link needs a privilege this account may not have. Skipped rather
            // than passed silently - see the assertion below.
            Assert.SkipWhen(true, "this account cannot create a directory link");
            return;
        }

        var workspace = Workspace.Open(inside.Path);

        await Assert.ThrowsAsync<IOException>(() => WorkspaceEditor.CreateCollectionAsync(
            workspace, "fixtures", "Billing", TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<IOException>(() => WorkspaceEditor.CreateDocumentAsync(
            workspace, "fixtures", "orders", TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFileSystemEntries(outside.Path));
    }

    /// <summary>
    /// The join the rail actually depends on: create, walk, build, find.
    /// </summary>
    /// <remarks>
    /// Both halves are tested on their own. This is the seam - a collection that is created
    /// and then not listed is a collection the user cannot see, and nothing else would catch
    /// a disagreement about separators or casing between the two.
    /// </remarks>
    [Fact]
    public async Task A_created_collection_appears_in_the_tree_the_rail_is_built_from()
    {
        using var folder = new TemporaryFolder();
        var workspace = Workspace.Open(folder.Path);

        await WorkspaceEditor.CreateCollectionAsync(
            workspace, null, "Billing", TestContext.Current.CancellationToken);

        var nested = await WorkspaceEditor.CreateCollectionAsync(
            workspace, "Billing", "Refunds", TestContext.Current.CancellationToken);

        var tree = CollectionTree.Build(workspace.RequestFiles(out var truncated));

        Assert.False(truncated);

        var billing = Assert.Single(tree);
        Assert.Equal("Billing", billing.Name);
        Assert.Equal(CollectionEntryKind.Folder, billing.Kind);

        var refunds = billing.Children.Single(c => c.Kind == CollectionEntryKind.Folder);
        var document = Assert.Single(refunds.Children);

        // The path the rail joins back onto the root has to name the file that was written.
        Assert.Equal(
            Path.GetFullPath(Path.Combine(workspace.Root, nested)),
            Path.GetFullPath(Path.Combine(workspace.Root, document.RelativePath)));
    }

    /// <summary>Creates a directory link, or reports that this account may not.</summary>
    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
