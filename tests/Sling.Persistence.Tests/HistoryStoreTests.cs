using Sling.Core.Documents;
using Sling.Core.History;
using Sling.Core.Redaction;
using Sling.Core.Variables;
using Sling.Persistence.History;

namespace Sling.Persistence.Tests;

/// <summary>
/// The history file: what it holds, what it bounds, and what it survives.
/// </summary>
public sealed class HistoryStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_entry_survives_a_round_trip()
    {
        using var folder = new TemporaryFolder();
        var store = new HistoryStore(folder.Path);

        await store.AppendAsync([Entry("GET", 200)], 500, TestContext.Current.CancellationToken);

        var entry = Assert.Single(await store.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal("GET", entry.Method);
        Assert.Equal(200, entry.StatusCode);
        Assert.Equal("https://api.example.com/v1", entry.Url);
        Assert.Equal(Now, entry.SentUtc);
        Assert.Equal(TimeSpan.FromMilliseconds(12), entry.Elapsed);
        Assert.Equal("staging", entry.EnvironmentName);
        Assert.Equal("application/json", entry.RequestHeaders.Single().Value);
    }

    [Fact]
    public async Task Reading_a_file_that_is_not_there_yields_nothing()
    {
        using var folder = new TemporaryFolder();

        Assert.Empty(await new HistoryStore(folder.Path).ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Entries_accumulate_in_the_order_they_were_written()
    {
        using var folder = new TemporaryFolder();
        var store = new HistoryStore(folder.Path);

        await store.AppendAsync([Entry("GET", 200)], 500, TestContext.Current.CancellationToken);
        await store.AppendAsync([Entry("POST", 201)], 500, TestContext.Current.CancellationToken);

        var entries = await store.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["GET", "POST"], entries.Select(e => e.Method));
    }

    [Fact]
    public async Task A_truncated_last_line_is_skipped_rather_than_fatal()
    {
        // The file is appended to by a process that can be killed mid-write, so a half
        // line is an expected state and not a corrupt file.
        using var folder = new TemporaryFolder();
        var store = new HistoryStore(folder.Path);

        await store.AppendAsync([Entry("GET", 200)], 500, TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            store.FilePath,
            "{\"method\":\"POS",
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(await store.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("GET", entry.Method);
    }

    [Fact]
    public async Task The_entry_cap_is_exact()
    {
        // The setting is called "history entries kept", so the cap has to be a count and
        // has to hold after every append - not a byte heuristic that lets the file drift
        // past it between rewrites.
        using var folder = new TemporaryFolder();
        var store = new HistoryStore(folder.Path);

        for (var i = 0; i < 25; i++)
        {
            await store.AppendAsync([Entry("GET", 200 + i)], 10, TestContext.Current.CancellationToken);

            var so_far = await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.True(so_far.Count <= 10, $"held {so_far.Count} entries with a cap of ten");
        }

        var entries = await store.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10, entries.Count);

        // The newest are the ones kept.
        Assert.Equal(224, entries[^1].StatusCode);
    }

    [Fact]
    public async Task The_smallest_possible_entry_is_still_larger_than_the_trim_checks_bound()
    {
        // The size pre-check skips the read when the file cannot hold too many entries,
        // and that is only sound while no entry is smaller than the bound. The entry here
        // is the *minimum* the writer can produce - no environment name, no headers on
        // either side, an empty reason phrase and the shortest URL a Uri will accept,
        // because measuring a typical entry would leave the check safe by accident and
        // would not notice a field being removed from Serialize.
        using var folder = new TemporaryFolder();
        var store = new HistoryStore(folder.Path);

        var request = new ResolvedRequest(null, string.Empty, new Uri("http://a/"), [], null, null);

        var response = new ResponseSnapshot(
            0,
            string.Empty,
            string.Empty,
            [],
            string.Empty,
            0,
            false,
            TimeSpan.Zero,
            new Uri("http://a/"),
            []);

        await store.AppendAsync(
            [HistoryEntry.Record(request, response, Now, null, Redactor.WithoutKnownSecrets)],
            500,
            TestContext.Current.CancellationToken);

        var length = new FileInfo(store.FilePath).Length;

        Assert.True(
            length >= HistoryStore.MinimumBytesPerEntry,
            $"the smallest entry is {length} bytes, under the {HistoryStore.MinimumBytesPerEntry}-byte "
                + "bound the trim check assumes");
    }

    [Fact]
    public async Task An_entry_too_large_to_record_is_reported_rather_than_dropped_in_silence()
    {
        // Reporting success for a run that was not recorded is the one thing a log must
        // never do.
        using var folder = new TemporaryFolder();
        var store = new HistoryStore(folder.Path);

        var request = new ResolvedRequest(
            null,
            "GET",
            new Uri("https://api.example.com/v1"),
            [new HeaderField("X-Huge", new string('x', 200_000), 1)],
            null,
            null);

        var response = new ResponseSnapshot(
            200,
            "OK",
            "1.1",
            [],
            "{}",
            2,
            false,
            TimeSpan.Zero,
            new Uri("https://api.example.com/v1"),
            []);

        var problem = await store.AppendAsync(
            [HistoryEntry.Record(request, response, Now, null, Redactor.WithoutKnownSecrets)],
            500,
            TestContext.Current.CancellationToken);

        Assert.NotNull(problem);
        Assert.Contains("too large", problem, StringComparison.Ordinal);
        Assert.Empty(await store.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Trimming_leaves_no_temporary_file_behind()
    {
        using var folder = new TemporaryFolder();
        var store = new HistoryStore(folder.Path);

        for (var i = 0; i < 25; i++)
        {
            await store.AppendAsync([Entry("GET", 200)], 10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            [HistoryStore.FileName],
            Directory.GetFiles(folder.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Clearing_removes_the_file()
    {
        using var folder = new TemporaryFolder();
        var store = new HistoryStore(folder.Path);

        await store.AppendAsync([Entry("GET", 200)], 500, TestContext.Current.CancellationToken);

        Assert.Null(store.Clear());
        Assert.Empty(await store.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Clearing_a_history_that_does_not_exist_is_not_an_error()
    {
        using var folder = new TemporaryFolder();

        Assert.Null(new HistoryStore(folder.Path).Clear());
    }

    [Fact]
    public async Task What_reaches_the_file_is_already_redacted()
    {
        // The property that matters: a credential never reaches disk. It holds because
        // HistoryEntry cannot be built without a Redactor, not because this method
        // remembers to call one.
        const string Secret = "zzq-distinctive-secret-value";

        using var folder = new TemporaryFolder();
        var store = new HistoryStore(folder.Path);

        var request = new ResolvedRequest(
            null,
            "GET",
            new Uri($"https://api.example.com/v1?tenant={Secret}"),
            [new HeaderField("Authorization", $"Bearer {Secret}", 1)],
            null,
            null);

        var response = new ResponseSnapshot(
            200,
            "OK",
            "1.1",
            [],
            "{}",
            2,
            false,
            TimeSpan.Zero,
            new Uri($"https://api.example.com/v1?tenant={Secret}"),
            []);

        await store.AppendAsync(
            [HistoryEntry.Record(request, response, Now, null, new Redactor([Secret]))],
            500,
            TestContext.Current.CancellationToken);

        var written = await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(Secret, written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_header_value_holding_a_newline_cannot_break_the_line_format()
    {
        // JSON Lines depends on one entry being one line. A response header is a place
        // Sling does not control the bytes.
        using var folder = new TemporaryFolder();
        var store = new HistoryStore(folder.Path);

        var response = new ResponseSnapshot(
            200,
            "OK",
            "1.1",
            [new ResponseHeader("X-Note", "one" + (char)10 + "two")],
            "{}",
            2,
            false,
            TimeSpan.Zero,
            new Uri("https://api.example.com/v1"),
            []);

        var request = new ResolvedRequest(null, "GET", new Uri("https://api.example.com/v1"), [], null, null);

        await store.AppendAsync(
            [HistoryEntry.Record(request, response, Now, null, Redactor.WithoutKnownSecrets)],
            500,
            TestContext.Current.CancellationToken);

        var lines = await File.ReadAllLinesAsync(store.FilePath, TestContext.Current.CancellationToken);

        Assert.Single(lines);
        Assert.Single(await store.ReadAsync(TestContext.Current.CancellationToken));
    }

    private static HistoryEntry Entry(string method, int status)
    {
        var request = new ResolvedRequest(
            null,
            method,
            new Uri("https://api.example.com/v1"),
            [new HeaderField("Accept", "application/json", 1)],
            null,
            null);

        var response = new ResponseSnapshot(
            status,
            "OK",
            "1.1",
            [],
            "{}",
            2,
            false,
            TimeSpan.FromMilliseconds(12),
            new Uri("https://api.example.com/v1"),
            []);

        return HistoryEntry.Record(request, response, Now, "staging", Redactor.WithoutKnownSecrets);
    }
}
