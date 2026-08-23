using Sling.Import.Postman;
using Sling.Persistence.Workspaces;

namespace Sling.Persistence.Tests;

/// <summary>
/// Reading Postman exports off disk and writing what the importer made of them.
/// </summary>
/// <remarks>
/// Two properties are the whole of this file, and both are about what does <em>not</em>
/// happen: a name inside somebody else's collection cannot decide where a file lands, and
/// nothing that is already on disk is replaced.
/// </remarks>
public sealed class ImportStoreTests
{
    private const string Collection = """
        {
          "info": {
            "name": "Acme API",
            "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
          },
          "variable": [ { "key": "base_url", "value": "https://api.example.com" } ],
          "item": [
            { "name": "Ping", "request": "{{base_url}}/ping" },
            {
              "name": "Orders",
              "item": [ { "name": "List", "request": "{{base_url}}/orders" } ]
            }
          ]
        }
        """;

    [Fact]
    public async Task An_import_lands_as_files_and_directories()
    {
        using var folder = new TemporaryFolder();

        var result = PostmanImport.Convert("acme.postman_collection.json", Collection);
        var write = await ImportStore.WriteAsync(folder.Path, result.Files, TestContext.Current.CancellationToken);

        Assert.Empty(write.Refused);

        Assert.Contains("acme-api.http", write.Written, StringComparer.Ordinal);
        Assert.Contains("orders.http", write.Written, StringComparer.Ordinal);
        Assert.Contains("http-client.env.json", write.Written, StringComparer.Ordinal);

        Assert.True(folder.Exists("acme-api.http"));
        Assert.Contains("{{base_url}}/ping", folder.Read("acme-api.http"), StringComparison.Ordinal);
    }

    /// <summary>
    /// An import lands in a folder picked from a dialog, and picking the wrong one is a
    /// single mis-click — one that could otherwise replace a request file somebody wrote by
    /// hand, or an <c>http-client.private.env.json</c> holding their real tokens.
    /// </summary>
    [Fact]
    public async Task Nothing_already_on_disk_is_ever_replaced()
    {
        using var folder = new TemporaryFolder();

        folder.Write("acme-api.http", "### mine\nGET https://mine.example.com/\n");

        var result = PostmanImport.Convert("acme.postman_collection.json", Collection);
        var write = await ImportStore.WriteAsync(folder.Path, result.Files, TestContext.Current.CancellationToken);

        Assert.Equal("### mine\nGET https://mine.example.com/\n", folder.Read("acme-api.http"));

        Assert.Contains(
            write.Refused,
            r => r.StartsWith("acme-api.http", StringComparison.Ordinal));

        // The rest of the import still lands: one collision is not a reason to lose every
        // other file.
        Assert.Contains("orders.http", write.Written, StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_write_into_a_folder_that_does_not_exist_yet_creates_it()
    {
        using var folder = new TemporaryFolder();

        var destination = Path.Combine(folder.Path, "new", "place");
        var result = PostmanImport.Convert("acme.postman_collection.json", Collection);
        var write = await ImportStore.WriteAsync(destination, result.Files, TestContext.Current.CancellationToken);

        Assert.NotEmpty(write.Written);
        Assert.True(File.Exists(Path.Combine(destination, "acme-api.http")));
    }

    /// <summary>
    /// Asserted against the function that implements the rule rather than through a
    /// conversion, because the importer's slug rules already make these paths impossible to
    /// produce — which would leave this passing for a reason that has nothing to do with
    /// what it claims.
    /// </summary>
    [Theory]
    [InlineData("../escape.http")]
    [InlineData("..\\escape.http")]
    [InlineData("a/../../escape.http")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\Temp\\x.http")]
    [InlineData("\\\\server\\share\\x.http")]
    [InlineData("stream.http:hidden")]
    [InlineData("..")]
    public void A_path_that_leaves_the_destination_is_refused(string relative)
    {
        using var folder = new TemporaryFolder();

        Assert.Null(ImportStore.Resolve(folder.Path, relative));
    }

    /// <summary>
    /// The sibling-prefix case, which a string comparison gets wrong: <c>work\api-secrets</c>
    /// starts with <c>work\api</c> and is not inside it.
    /// </summary>
    [Theory]
    [InlineData("orders.http")]
    [InlineData("orders/refunds.http")]
    [InlineData("a/b/c/d/e/f.http")]
    public void A_path_that_stays_inside_is_accepted(string relative)
    {
        using var folder = new TemporaryFolder();

        var full = ImportStore.Resolve(folder.Path, relative);

        Assert.NotNull(full);
        Assert.StartsWith(folder.Path, full, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_export_that_cannot_be_read_is_named_rather_than_skipped()
    {
        using var folder = new TemporaryFolder();

        var refusals = new List<string>();
        var missing = Path.Combine(folder.Path, "not-there.json");

        var sources = await ImportStore.ReadAsync([missing], refusals, TestContext.Current.CancellationToken);

        Assert.Empty(sources);
        Assert.Contains(refusals, r => r.Contains("not-there.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_export_is_read_with_the_name_it_had()
    {
        using var folder = new TemporaryFolder();

        folder.Write("Acme.postman_collection.json", Collection);

        var refusals = new List<string>();
        var sources = await ImportStore.ReadAsync(
            [Path.Combine(folder.Path, "Acme.postman_collection.json")],
            refusals,
            TestContext.Current.CancellationToken);

        Assert.Empty(refusals);
        Assert.Equal("Acme.postman_collection.json", Assert.Single(sources).Name);
    }
}
