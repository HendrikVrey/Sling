using System.Text;
using Sling.Core.Navigation;
using Sling.Persistence.Workspaces;

namespace Sling.Persistence.Tests;

/// <summary>
/// Reading a workspace into the list Quick Open searches.
/// </summary>
public sealed class QuickOpenIndexTests
{
    private const string TwoRequests = """
        ### list them
        GET https://api.example.com/orders

        ### create an order
        POST https://api.example.com/orders
        """;

    [Fact]
    public async Task Every_request_file_is_listed_with_its_collection_and_its_requests()
    {
        using var folder = new TemporaryFolder();
        folder.Write(Path.Combine("orders", "orders.http"), TwoRequests);

        var listing = await QuickOpenIndex.BuildAsync(
            Workspace.Open(folder.Path),
            openDocumentPath: null,
            openDocumentText: null,
            TestContext.Current.CancellationToken);

        Assert.False(listing.Truncated);

        var document = Assert.Single(listing.Entries, e => e.Kind == QuickOpenKind.Document);

        Assert.Equal("orders.http", document.File);
        Assert.Equal("orders", document.Collection);

        var requests = listing.Entries.Where(e => e.Kind == QuickOpenKind.Request).ToList();

        Assert.Equal(2, requests.Count);
        Assert.Equal(["list them", "create an order"], requests.Select(r => r.Name));
        Assert.Equal(["GET", "POST"], requests.Select(r => r.Method));
        Assert.All(requests, r => Assert.Equal("orders", r.Collection));
        Assert.All(requests, r => Assert.True(r.Line > 0));
    }

    [Fact]
    public async Task The_open_documents_requests_come_from_the_buffer_rather_than_from_disk()
    {
        // The one that earns its keep: a request typed five seconds ago and not saved is
        // exactly the one somebody is about to go looking for, and it is not in the file.
        using var folder = new TemporaryFolder();
        var path = folder.Write("requests.http", "### saved\nGET https://api.example.com/a\n");

        var listing = await QuickOpenIndex.BuildAsync(
            Workspace.Open(folder.Path),
            path,
            "### typed but not saved\nGET https://api.example.com/b\n",
            TestContext.Current.CancellationToken);

        var names = listing.Entries.Where(e => e.Kind == QuickOpenKind.Request).Select(r => r.Name);

        Assert.Equal(["typed but not saved"], names);
    }

    [Fact]
    public async Task A_file_too_large_to_read_is_still_listed_and_the_listing_says_it_is_partial()
    {
        // A file past the ceiling is still findable by name; only its requests are missing.
        // Saying the listing is partial is the half that matters, because the row somebody
        // wants may be one of the missing ones.
        using var folder = new TemporaryFolder();

        var padding = new StringBuilder("### huge\nGET https://api.example.com/a\n");
        padding.Append('#', (int)QuickOpenIndex.MaxFileBytes + 1);

        folder.Write("huge.http", padding.ToString());

        var listing = await QuickOpenIndex.BuildAsync(
            Workspace.Open(folder.Path),
            openDocumentPath: null,
            openDocumentText: null,
            TestContext.Current.CancellationToken);

        Assert.True(listing.Truncated);

        var document = Assert.Single(listing.Entries);

        Assert.Equal(QuickOpenKind.Document, document.Kind);
        Assert.Equal("huge.http", document.File);
    }

    [Fact]
    public async Task A_file_at_the_root_has_no_collection()
    {
        using var folder = new TemporaryFolder();
        folder.Write("requests.http", TwoRequests);

        var listing = await QuickOpenIndex.BuildAsync(
            Workspace.Open(folder.Path),
            openDocumentPath: null,
            openDocumentText: null,
            TestContext.Current.CancellationToken);

        Assert.All(listing.Entries, e => Assert.Equal(string.Empty, e.Collection));
    }

    [Fact]
    public async Task An_empty_folder_lists_nothing_rather_than_failing()
    {
        using var folder = new TemporaryFolder();

        var listing = await QuickOpenIndex.BuildAsync(
            Workspace.Open(folder.Path),
            openDocumentPath: null,
            openDocumentText: null,
            TestContext.Current.CancellationToken);

        Assert.Empty(listing.Entries);
        Assert.False(listing.Truncated);
    }

    [Fact]
    public async Task The_number_of_rows_is_capped_as_well_as_the_bytes_read()
    {
        // The byte budget bounds the reading and not the answer. A file whose every other
        // line is a '###' turns a few hundred kilobytes into tens of thousands of rows, and
        // the search runs over all of them on every keystroke, on the dispatcher.
        using var folder = new TemporaryFolder();

        var dense = new StringBuilder();

        for (var i = 0; i < QuickOpenIndex.MaxEntries + 100; i++)
        {
            dense.Append("### r").Append(i).Append('\n').Append("GET https://api.example.com/").Append(i).Append('\n');
        }

        folder.Write("dense.http", dense.ToString());

        var listing = await QuickOpenIndex.BuildAsync(
            Workspace.Open(folder.Path),
            openDocumentPath: null,
            openDocumentText: null,
            TestContext.Current.CancellationToken);

        Assert.True(listing.Entries.Count <= QuickOpenIndex.MaxEntries);
        Assert.True(listing.Truncated, "a list that stopped must say it stopped");
    }

    [Fact]
    public async Task A_cancelled_walk_stops()
    {
        // The palette cancels this when it closes, so a folder opened and dismissed does not
        // leave a read running with nothing waiting for it.
        using var folder = new TemporaryFolder();

        for (var i = 0; i < 50; i++)
        {
            folder.Write($"f{i}.http", TwoRequests);
        }

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QuickOpenIndex.BuildAsync(
            Workspace.Open(folder.Path),
            openDocumentPath: null,
            openDocumentText: null,
            cancelled.Token));
    }

    [Fact]
    public async Task A_request_carries_the_path_of_the_file_it_is_in()
    {
        // So opening a row never has to walk back up a tree to work out which file it means.
        using var folder = new TemporaryFolder();
        var path = folder.Write(Path.Combine("orders", "orders.http"), TwoRequests);

        var listing = await QuickOpenIndex.BuildAsync(
            Workspace.Open(folder.Path),
            openDocumentPath: null,
            openDocumentText: null,
            TestContext.Current.CancellationToken);

        Assert.All(listing.Entries, e => Assert.Equal(Path.GetFullPath(path), e.Path));
    }
}
