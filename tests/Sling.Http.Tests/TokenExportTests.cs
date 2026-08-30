using Sling.Core.Auth;
using Sling.Core.Parsing;
using Sling.Core.Variables;

namespace Sling.Http.Tests;

/// <summary>
/// Taking tokens out of the cache and putting them back, which is what lets one outlive the
/// process.
/// </summary>
/// <remarks>
/// The route back in is the interesting half. A store is a file somebody can edit, so a
/// restored token is untrusted input exactly like a token response - and it goes through the
/// same constructor, which refuses anything that could not go in a header.
/// </remarks>
public sealed class TokenExportTests
{
    private static readonly ResolutionContext Context = new();

    private const string Granted = """
        # @auth oauth2
        # @token-url https://auth.example.com/token
        # @client-id my-client
        # @client-secret s3cret
        GET https://api.example.com/orders
        """;

    [Fact]
    public async Task A_restored_token_is_used_instead_of_fetching_another()
    {
        var exported = await FetchAsync();

        var handler = new StubHandler((_, _) => StubHandler.Ok("{}"));
        var document = RequestDocumentParser.Parse(Granted);

        using var runner = new RequestRunner(new RequestSender(handler));

        Assert.Equal(1, runner.RestoreTokens(exported));

        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);

        // One request, and it is the API call. The token endpoint was never asked, which is
        // the whole point of the store.
        Assert.Single(handler.Requests);
        Assert.Equal("https://api.example.com/orders", handler.Requests[0].Url.ToString());
        Assert.Equal("Bearer abc123", handler.Requests[0].Header("Authorization"));
    }

    [Fact]
    public async Task What_comes_out_carries_the_grant_but_no_secret()
    {
        var exported = Assert.Single(await FetchAsync());

        Assert.Equal("https://auth.example.com/token", exported.Identity.TokenUrl);
        Assert.Equal("my-client", exported.Identity.ClientId);

        // The fingerprint is what identifies it, and a hash is not the secret. Asserted so
        // that a later change putting the key's fields back on the type fails here.
        Assert.DoesNotContain("s3cret", exported.Fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", exported.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A record's generated <c>ToString</c> prints every property, and one of these is a
    /// bearer credential. Interpolating the object into a message is the quietest way a
    /// token reaches a screen.
    /// </summary>
    [Fact]
    public async Task Printing_one_does_not_print_the_token()
    {
        var exported = Assert.Single(await FetchAsync());

        Assert.DoesNotContain("abc123", exported.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_that_has_already_expired_is_not_restored()
    {
        var exported = Assert.Single(await FetchAsync());
        var stale = exported with { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };

        using var runner = new RequestRunner(new RequestSender(new StubHandler((_, _) => StubHandler.Ok("{}"))));

        Assert.Equal(0, runner.RestoreTokens([stale]));
        Assert.Empty(runner.HeldTokens());
    }

    /// <summary>
    /// A store is a file, and a file can be edited. A token carrying a newline appended to
    /// <c>Authorization: Bearer </c> would add headers of its own to every request the grant
    /// covers, so the way back in is the same checked constructor everything else uses.
    /// </summary>
    [Fact]
    public async Task A_token_that_could_not_go_in_a_header_is_refused()
    {
        var exported = Assert.Single(await FetchAsync());
        var tampered = exported with { AccessToken = "abc123\r\nX-Admin: 1" };

        using var runner = new RequestRunner(new RequestSender(new StubHandler((_, _) => StubHandler.Ok("{}"))));

        Assert.Equal(0, runner.RestoreTokens([tampered]));
    }

    [Fact]
    public async Task A_restored_token_is_still_known_to_redaction()
    {
        var exported = await FetchAsync();

        using var runner = new RequestRunner(new RequestSender(new StubHandler((_, _) => StubHandler.Ok("{}"))));
        runner.RestoreTokens(exported);

        // Fetched last session or this one, it is exactly as sensitive either way.
        Assert.Contains("abc123", runner.AcquiredTokens());
    }

    /// <summary>Runs one grant against a stub and hands back what the cache would store.</summary>
    private static async Task<IReadOnlyList<PersistedToken>> FetchAsync()
    {
        var handler = new StubHandler((request, _) =>
            request.RequestUri!.Host == "auth.example.com"
                ? StubHandler.Ok("""{"access_token":"abc123","token_type":"Bearer","expires_in":3600}""")
                : StubHandler.Ok("{}"));

        var document = RequestDocumentParser.Parse(Granted);
        using var runner = new RequestRunner(new RequestSender(handler));

        await runner.RunAsync(document, document.Requests[0], Context, TestContext.Current.CancellationToken);

        return runner.ExportTokens();
    }
}
