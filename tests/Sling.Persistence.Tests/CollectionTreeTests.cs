using Sling.Persistence.Workspaces;

namespace Sling.Persistence.Tests;

/// <summary>
/// The projection the collections rail is drawn from.
/// </summary>
/// <remarks>
/// Pure input to pure output, which is the point: the rail owns no state, so everything
/// about its shape is decidable here rather than in a window nothing can drive.
/// </remarks>
public sealed class CollectionTreeTests
{
    [Fact]
    public void Files_at_the_root_become_documents()
    {
        var tree = CollectionTree.Build(["orders.http", "users.http"]);

        Assert.Equal(2, tree.Count);
        Assert.All(tree, e => Assert.Equal(CollectionEntryKind.Document, e.Kind));
        Assert.Equal(["orders.http", "users.http"], tree.Select(e => e.Name));
    }

    [Fact]
    public void A_directory_becomes_a_collection_holding_its_files()
    {
        var tree = CollectionTree.Build([@"billing\invoices.http", @"billing\refunds.http"]);

        var collection = Assert.Single(tree);

        Assert.Equal(CollectionEntryKind.Folder, collection.Kind);
        Assert.Equal("billing", collection.Name);
        Assert.Equal(["invoices.http", "refunds.http"], collection.Children.Select(c => c.Name));
    }

    [Fact]
    public void Collections_nest_as_deeply_as_the_folders_do()
    {
        var tree = CollectionTree.Build([@"v1\billing\invoices.http"]);

        var v1 = Assert.Single(tree);
        var billing = Assert.Single(v1.Children);
        var file = Assert.Single(billing.Children);

        Assert.Equal("v1/billing/invoices.http", file.RelativePath);
    }

    [Fact]
    public void Both_separators_are_understood()
    {
        // The walk hands back Windows separators; a test, a fixture or a future caller may
        // not. Accepting either costs one character in the split.
        var windows = CollectionTree.Build([@"billing\invoices.http"]);
        var posix = CollectionTree.Build(["billing/invoices.http"]);

        Assert.Equal(
            windows.Single().Children.Single().RelativePath,
            posix.Single().Children.Single().RelativePath);
    }

    [Fact]
    public void Relative_paths_always_use_forward_slashes()
    {
        var tree = CollectionTree.Build([@"a\b\c.http"]);

        Assert.Equal("a", tree.Single().RelativePath);
        Assert.Equal("a/b", tree.Single().Children.Single().RelativePath);
        Assert.Equal("a/b/c.http", tree.Single().Children.Single().Children.Single().RelativePath);
    }

    [Fact]
    public void Folders_come_before_documents()
    {
        // Not alphabetical across the whole level: every file tree people already use puts
        // directories first, and a rail that disagrees is a rail whose rows move when a file
        // is added.
        var tree = CollectionTree.Build(["aaa.http", @"zzz\inner.http"]);

        Assert.Equal(["zzz", "aaa.http"], tree.Select(e => e.Name));
    }

    [Fact]
    public void Two_spellings_of_one_directory_are_one_collection()
    {
        // Windows says these are the same directory, and the walk reports whatever casing
        // the file system gave it. Two nodes would be two collections holding half the
        // files each.
        var tree = CollectionTree.Build([@"Billing\a.http", @"billing\b.http"]);

        var collection = Assert.Single(tree);

        Assert.Equal(["a.http", "b.http"], collection.Children.Select(c => c.Name));
    }

    [Theory]
    [InlineData(@"..\escape.http")]
    [InlineData("../escape.http")]
    [InlineData(@"C:\elsewhere\escape.http")]
    [InlineData(@"a\..\..\escape.http")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_path_that_is_not_a_plain_descent_is_dropped(string path)
    {
        // The walk cannot produce one. This is the second line: every command in the rail
        // acts on a node's path, so a node that escaped the workspace would be a node that
        // creates a collection outside it.
        Assert.Empty(CollectionTree.Build([path]));
    }

    [Fact]
    public void A_dropped_path_does_not_take_the_rest_of_the_tree_with_it()
    {
        var tree = CollectionTree.Build([@"..\escape.http", "orders.http"]);

        Assert.Equal("orders.http", Assert.Single(tree).Name);
    }

    [Fact]
    public void An_empty_workspace_builds_an_empty_tree() =>
        Assert.Empty(CollectionTree.Build([]));
}
