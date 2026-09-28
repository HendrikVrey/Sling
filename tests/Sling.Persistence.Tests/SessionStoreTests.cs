using Sling.Persistence.Settings;

namespace Sling.Persistence.Tests;

/// <summary>
/// Remembering where somebody was, and refusing to be broken by the file that holds it.
/// </summary>
public sealed class SessionStoreTests
{
    [Fact]
    public void A_machine_with_no_session_file_gets_an_empty_one()
    {
        using var folder = new TemporaryFolder();

        var session = new SessionStore(folder.Path).Load();

        Assert.Null(session.WorkspaceRoot);
        Assert.Null(session.DocumentPath);
        Assert.Empty(session.RecentFolders);
    }

    [Fact]
    public void A_session_round_trips()
    {
        using var folder = new TemporaryFolder();
        var store = new SessionStore(folder.Path);

        var written = new SlingSession
        {
            WorkspaceRoot = @"C:\work\api",
            DocumentPath = @"C:\work\api\orders.http",
            CaretLine = 42,
            CaretColumn = 7,
            Split = 0.63,
            RecentFolders = [@"C:\work\api", @"C:\work\other"],
        };

        Assert.True(store.Save(written));

        var read = store.Load();

        Assert.Equal(written.WorkspaceRoot, read.WorkspaceRoot);
        Assert.Equal(written.DocumentPath, read.DocumentPath);
        Assert.Equal(42, read.CaretLine);
        Assert.Equal(7, read.CaretColumn);
        Assert.Equal(0.63, read.Split, 6);
        Assert.Equal([@"C:\work\api", @"C:\work\other"], read.RecentFolders);
    }

    [Fact]
    public void An_untitled_buffer_and_no_folder_round_trip_as_nothing()
    {
        using var folder = new TemporaryFolder();
        var store = new SessionStore(folder.Path);

        Assert.True(store.Save(SlingSession.None));

        var read = store.Load();

        Assert.Null(read.WorkspaceRoot);
        Assert.Null(read.DocumentPath);
    }

    [Fact]
    public void A_file_that_is_not_json_is_read_as_no_session_rather_than_throwing()
    {
        // A session is remembered rather than chosen, so a machine that cannot read one
        // should open with an empty window and say nothing - exactly as it did before the
        // file existed.
        using var folder = new TemporaryFolder();
        folder.Write(SessionStore.FileName, "{ this is not json");

        Assert.Null(new SessionStore(folder.Path).Load().WorkspaceRoot);
    }

    [Fact]
    public void A_json_array_where_an_object_belongs_is_read_as_no_session()
    {
        using var folder = new TemporaryFolder();
        folder.Write(SessionStore.FileName, "[1, 2, 3]");

        Assert.Empty(new SessionStore(folder.Path).Load().RecentFolders);
    }

    [Fact]
    public void A_recent_list_holding_something_that_is_not_a_string_keeps_the_rest()
    {
        // One bad entry is not a reason to forget the other seven, and this is a file people
        // are free to edit.
        using var folder = new TemporaryFolder();
        folder.Write(
            SessionStore.FileName,
            """{ "recentFolders": ["C:\\one", 7, null, "C:\\two"] }""");

        Assert.Equal([@"C:\one", @"C:\two"], new SessionStore(folder.Path).Load().RecentFolders);
    }

    [Fact]
    public void A_split_outside_its_range_is_brought_back_inside_it()
    {
        // A stored extreme would restore a pane the user cannot see and may not realise is
        // there.
        using var folder = new TemporaryFolder();
        folder.Write(SessionStore.FileName, """{ "split": 0.99 }""");

        var session = new SessionStore(folder.Path).Load();

        Assert.Equal(SlingSession.MaximumSplit, session.Split, 6);
    }

    [Fact]
    public void A_split_that_is_not_a_number_falls_back_rather_than_producing_a_pane_of_no_width()
    {
        // Every comparison against NaN is false, so a clamp written as Min and Max lets one
        // through. The same trap has been paid for once already, on a crop rectangle.
        var session = new SlingSession { Split = double.NaN }.Clamped();

        Assert.True(double.IsFinite(session.Split));
        Assert.InRange(session.Split, SlingSession.MinimumSplit, SlingSession.MaximumSplit);
    }

    [Fact]
    public void The_recent_list_is_capped_and_keeps_the_newest()
    {
        var session = SlingSession.None;

        for (var i = 0; i < SlingSession.MaxRecentFolders + 4; i++)
        {
            session = session.WithRecent($@"C:\work\{i}");
        }

        Assert.Equal(SlingSession.MaxRecentFolders, session.RecentFolders.Count);
        Assert.Equal(@"C:\work\11", session.RecentFolders[0]);
    }

    [Fact]
    public void Opening_a_folder_again_moves_it_to_the_top_rather_than_listing_it_twice()
    {
        var session = SlingSession.None
            .WithRecent(@"C:\work\one")
            .WithRecent(@"C:\work\two")
            .WithRecent(@"C:\WORK\ONE");

        // Case-insensitively, because Windows paths are - and because the same folder
        // reached through the dialog twice would otherwise fill the menu with itself.
        Assert.Equal([@"C:\WORK\ONE", @"C:\work\two"], session.RecentFolders);
    }

    [Fact]
    public void A_saved_session_replaces_the_previous_one_and_leaves_no_temporary_behind()
    {
        using var folder = new TemporaryFolder();
        var store = new SessionStore(folder.Path);

        store.Save(new SlingSession { WorkspaceRoot = @"C:\one" });
        store.Save(new SlingSession { WorkspaceRoot = @"C:\two" });

        Assert.Equal(@"C:\two", store.Load().WorkspaceRoot);
        Assert.Equal([SessionStore.FileName], Directory.GetFiles(folder.Path).Select(Path.GetFileName));
    }
}
