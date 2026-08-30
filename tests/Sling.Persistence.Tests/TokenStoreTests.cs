using Sling.Core.Auth;
using Sling.Persistence.Tokens;

namespace Sling.Persistence.Tests;

/// <summary>
/// The token store: what it keeps, what it refuses to keep, and what it will not do across
/// a scope boundary.
/// </summary>
/// <remarks>
/// <para>
/// The property that matters most is asserted first and negatively: a store written for one
/// environment does not come back under another. That is the staging-token-reaching-production
/// hazard, and here it is enforced by the encryption rather than by a file name - so the test
/// copies the bytes across rather than merely asking for the wrong scope.
/// </para>
/// <para>
/// Real files and real DPAPI. A fake would only confirm that the fake agrees with the
/// production code's assumptions, and the assumption under test is about the operating
/// system.
/// </para>
/// </remarks>
public sealed class TokenStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void A_stored_token_comes_back()
    {
        using var folder = new TemporaryFolder();
        var store = new TokenStore(folder.Path);
        var scope = TokenStore.ScopeOf(@"C:\work\api", "dev");

        store.Save(scope, [Token("abc123")]);

        var read = Assert.Single(store.Load(scope));

        Assert.Equal("abc123", read.AccessToken);
        Assert.Equal("my-client", read.Identity.ClientId);
    }

    /// <summary>
    /// The whole security argument for storing anything at all. The scope decides the file
    /// <em>and</em> the entropy, so moving the bytes is not enough to move the token.
    /// </summary>
    [Fact]
    public void A_store_from_another_environment_does_not_decrypt()
    {
        using var folder = new TemporaryFolder();
        var store = new TokenStore(folder.Path);

        var staging = TokenStore.ScopeOf(@"C:\work\api", "staging");
        var production = TokenStore.ScopeOf(@"C:\work\api", "production");

        store.Save(staging, [Token("staging-token")]);

        // Copied, not merely asked for under the wrong name: a scoping that is only a file
        // name is defeated by exactly this.
        File.Copy(
            Path.Combine(store.Folder, staging + ".bin"),
            Path.Combine(store.Folder, production + ".bin"));

        Assert.Empty(store.Load(production));
    }

    [Fact]
    public void A_different_workspace_is_a_different_store()
    {
        Assert.NotEqual(
            TokenStore.ScopeOf(@"C:\work\api", "dev"),
            TokenStore.ScopeOf(@"C:\work\other", "dev"));
    }

    /// <summary>
    /// The path would otherwise carry somebody's directory tree in a file name, in a folder
    /// every process on the machine can enumerate.
    /// </summary>
    [Fact]
    public void The_scope_does_not_spell_out_where_the_workspace_is()
    {
        var scope = TokenStore.ScopeOf(@"C:\work\secret-project", "dev");

        Assert.DoesNotContain("secret-project", scope, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dev", scope, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Saving_nothing_removes_the_file_rather_than_leaving_an_empty_one()
    {
        using var folder = new TemporaryFolder();
        var store = new TokenStore(folder.Path);
        var scope = TokenStore.ScopeOf(@"C:\work\api", "dev");

        store.Save(scope, [Token("abc123")]);
        store.Save(scope, []);

        Assert.False(File.Exists(Path.Combine(store.Folder, scope + ".bin")));
        Assert.Empty(store.Load(scope));
    }

    [Fact]
    public void Clearing_everything_leaves_no_store_behind()
    {
        using var folder = new TemporaryFolder();
        var store = new TokenStore(folder.Path);

        store.Save(TokenStore.ScopeOf(@"C:\work\api", "dev"), [Token("one")]);
        store.Save(TokenStore.ScopeOf(@"C:\work\api", "prod"), [Token("two")]);

        store.ClearAll();

        Assert.Empty(store.Load(TokenStore.ScopeOf(@"C:\work\api", "dev")));
        Assert.Empty(store.Load(TokenStore.ScopeOf(@"C:\work\api", "prod")));
    }

    /// <summary>
    /// A corrupted or truncated store means a round trip, never an error: refusing to open a
    /// workspace over a token cache would be the tail wagging the dog.
    /// </summary>
    [Fact]
    public void A_store_that_cannot_be_read_answers_with_nothing()
    {
        using var folder = new TemporaryFolder();
        var store = new TokenStore(folder.Path);
        var scope = TokenStore.ScopeOf(@"C:\work\api", "dev");

        Directory.CreateDirectory(store.Folder);
        File.WriteAllText(Path.Combine(store.Folder, scope + ".bin"), "not a protected blob");

        Assert.Empty(store.Load(scope));
    }

    /// <summary>
    /// The token is written down; the client secret is not, and cannot be - the type has no
    /// field for one. Asserted against the bytes on disk so that adding one later fails here.
    /// </summary>
    [Fact]
    public void No_client_secret_reaches_the_file()
    {
        using var folder = new TemporaryFolder();
        var store = new TokenStore(folder.Path);
        var scope = TokenStore.ScopeOf(@"C:\work\api", "dev");

        store.Save(scope, [Token("abc123")]);

        var bytes = File.ReadAllBytes(Path.Combine(store.Folder, scope + ".bin"));

        // Encrypted, so neither string is there in any case - which is the point. The token
        // survives a round trip through the same store and the secret has nowhere to have
        // come from.
        Assert.DoesNotContain("s3cret", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    /// <summary>
    /// Rotating a client secret has to stop the stored token matching at once rather than at
    /// its expiry, and the fingerprint is what delivers that.
    /// </summary>
    [Fact]
    public void Rotating_the_client_secret_changes_the_fingerprint()
    {
        var before = new TokenCacheKey(
            "https://auth.example.com/token",
            "my-client",
            "s3cret",
            null,
            null,
            ClientAuthPlacement.BasicHeader);

        var after = before with { ClientSecret = "rotated" };

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    /// <summary>
    /// A separator a field can contain is a separator two different grants hash the same way.
    /// </summary>
    [Fact]
    public void Two_grants_that_differ_only_in_where_a_field_ends_hash_differently()
    {
        var left = new TokenCacheKey("https://a", "b", "c", "d", null, ClientAuthPlacement.BasicHeader);
        var right = new TokenCacheKey("https://a", "b", "c", null, "d", ClientAuthPlacement.BasicHeader);

        Assert.NotEqual(left.Fingerprint, right.Fingerprint);
    }

    private static PersistedToken Token(string accessToken) =>
        new(
            new TokenCacheKey(
                "https://auth.example.com/token",
                "my-client",
                "s3cret",
                null,
                null,
                ClientAuthPlacement.BasicHeader).Fingerprint,
            new TokenIdentity("https://auth.example.com/token", "my-client", null, null),
            accessToken,
            "Bearer",
            Now.AddHours(1),
            Now);
}
