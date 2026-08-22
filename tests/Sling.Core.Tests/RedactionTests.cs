using Sling.Core.Documents;
using Sling.Core.History;
using Sling.Core.Redaction;
using Sling.Core.Variables;

namespace Sling.Core.Tests;

/// <summary>
/// <c>Sling.md</c> §5.4: credentials do not reach anything that outlives the request.
/// </summary>
/// <remarks>
/// The two lines are tested separately because they exist to cover different things. The
/// provenance line is exact and catches a secret anywhere it appears; the header-name line
/// is a deny-list and catches a credential typed straight into the document.
/// </remarks>
public sealed class RedactionTests
{
    private const string Secret = "zzq-distinctive-secret-value";

    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_known_secret_is_removed_wherever_it_appears()
    {
        var redactor = new Redactor([Secret]);

        Assert.DoesNotContain(Secret, redactor.Text($"prefix {Secret} suffix"), StringComparison.Ordinal);
        Assert.Contains(Redactor.Marker, redactor.Text(Secret), StringComparison.Ordinal);
    }

    [Fact]
    public void A_credential_header_is_removed_whatever_its_value()
    {
        // The deny-list half: a token typed into the document is in no secrets file, so
        // provenance cannot see it.
        var redactor = Redactor.WithoutKnownSecrets;

        Assert.Equal(Redactor.Marker, redactor.HeaderValue("Authorization", "Bearer typed-in-by-hand"));
        Assert.Equal(Redactor.Marker, redactor.HeaderValue("cookie", "sid=abc"));
        Assert.Equal(Redactor.Marker, redactor.HeaderValue("Set-Cookie", "sid=abc"));
        Assert.Equal("application/json", redactor.HeaderValue("Accept", "application/json"));
    }

    [Fact]
    public void A_secret_inside_an_ordinary_header_is_still_removed()
    {
        // Which is the point of having both lines: X-Tenant is not a credential header,
        // and the value came out of the secrets file.
        var redactor = new Redactor([Secret]);

        Assert.DoesNotContain(Secret, redactor.HeaderValue("X-Tenant", Secret), StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_secret_is_left_alone()
    {
        // A secrets file legitimately holds a tenant id of '7' or a flag of 'true'.
        // Redacting every occurrence of a short string turns an entry into a row of
        // markers with nothing left in it.
        var redactor = new Redactor(["true"]);

        Assert.Equal("verify=true", redactor.Text("verify=true"));
    }

    [Fact]
    public void A_secret_that_contains_another_secret_is_removed_whole()
    {
        var redactor = new Redactor(["inner-secret", "an-inner-secret-and-more"]);

        var result = redactor.Text("value=an-inner-secret-and-more");

        // Longest first. The other order leaves the outer value half-redacted and the
        // remainder still readable.
        Assert.Equal($"value={Redactor.Marker}", result);
    }

    [Fact]
    public void A_credential_query_parameter_is_removed_by_name()
    {
        var redactor = Redactor.WithoutKnownSecrets;
        var url = new Uri("https://api.example.com/v1?page=2&access_token=typed-in-by-hand&sort=name");

        var result = redactor.Url(url);

        Assert.DoesNotContain("typed-in-by-hand", result, StringComparison.Ordinal);

        // Everything that is not a credential survives, because an entry with no detail in
        // it answers no question.
        Assert.Contains("page=2", result, StringComparison.Ordinal);
        Assert.Contains("sort=name", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_in_a_query_value_is_removed_even_under_an_innocent_name()
    {
        var redactor = new Redactor([Secret]);

        Assert.DoesNotContain(
            Secret,
            redactor.Url(new Uri($"https://api.example.com/v1?tenant={Secret}")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_looks_like_a_query_separator_cannot_shift_the_redaction()
    {
        // The query is rebuilt from its parts rather than pattern-matched, so a value
        // holding '&access_token=' cannot make the cut land in the wrong place.
        var redactor = Redactor.WithoutKnownSecrets;
        var url = new Uri("https://api.example.com/v1?note=x%26access_token%3Dy&access_token=real");

        var result = redactor.Url(url);

        Assert.DoesNotContain("real", result, StringComparison.Ordinal);
        Assert.Contains("note=x%26access_token%3Dy", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_that_percent_encodes_is_still_removed_from_a_url()
    {
        // A URL holds the escaped form, so a secret containing anything Uri encodes — an
        // accent, a space — does not literally appear in it and slips past a plain search.
        // ASCII secrets are unaffected, which is why this was invisible.
        const string Accented = "sécret-value-abcdefgh";

        var redactor = new Redactor([Accented]);
        var url = new Uri($"https://api.example.com/v1?tenant={Uri.EscapeDataString(Accented)}");

        var result = redactor.Url(url);

        Assert.DoesNotContain("C3%A9", result, StringComparison.Ordinal);
        Assert.Contains(Redactor.Marker, result, StringComparison.Ordinal);

        // Only the value that carried it goes; the rest of the URL is still there.
        Assert.Contains("https://api.example.com/v1", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_percent_encoded_into_a_path_segment_is_removed()
    {
        const string Accented = "sécret-value-abcdefgh";

        var redactor = new Redactor([Accented]);
        var url = new Uri($"https://api.example.com/v1/{Uri.EscapeDataString(Accented)}/orders");

        Assert.Equal(Redactor.Marker, redactor.Url(url));
    }

    [Fact]
    public void A_url_holding_no_secret_survives_intact()
    {
        // The escaped-form check must not turn every URL into a marker.
        var redactor = new Redactor(["zzq-distinctive-secret-value"]);
        var url = new Uri("https://api.example.com/v1/caf%C3%A9?page=2");

        Assert.Equal("https://api.example.com/v1/caf%C3%A9?page=2", redactor.Url(url));
    }

    [Fact]
    public void A_fragment_is_dropped()
    {
        // It is never sent to a server, so keeping it in a record of what was sent would be
        // a small lie — and implicit-flow tokens live in fragments.
        var result = Redactor.WithoutKnownSecrets.Url(new Uri("https://api.example.com/v1#access_token=abc"));

        Assert.Equal("https://api.example.com/v1", result);
    }

    [Fact]
    public void A_history_entry_cannot_be_built_without_redaction_happening()
    {
        var request = new ResolvedRequest(
            null,
            "GET",
            new Uri($"https://api.example.com/v1?tenant={Secret}"),
            [new HeaderField("Authorization", "Bearer typed-in-by-hand", 1)],
            null,
            null);

        var response = new ResponseSnapshot(
            200,
            "OK",
            "1.1",
            [new ResponseHeader("Set-Cookie", "sid=abc")],
            "{}",
            2,
            false,
            TimeSpan.FromMilliseconds(12),
            new Uri($"https://api.example.com/v1?tenant={Secret}"),
            []);

        var entry = HistoryEntry.Record(request, response, Now, "staging", new Redactor([Secret]));

        Assert.DoesNotContain(Secret, entry.Url, StringComparison.Ordinal);
        Assert.Equal(Redactor.Marker, entry.RequestHeaders.Single().Value);
        Assert.Equal(Redactor.Marker, entry.ResponseHeaders.Single().Value);
        Assert.Equal("staging", entry.EnvironmentName);
    }

    [Fact]
    public void A_history_entry_records_the_url_the_response_came_from()
    {
        // After a redirect the requested URL and the answering one differ, and the one
        // worth recording is where the bytes actually came from.
        var request = new ResolvedRequest(null, "GET", new Uri("https://api.example.com/old"), [], null, null);

        var response = new ResponseSnapshot(
            200,
            "OK",
            "1.1",
            [],
            "{}",
            2,
            false,
            TimeSpan.Zero,
            new Uri("https://api.example.com/new"),
            [new Uri("https://api.example.com/new")]);

        var entry = HistoryEntry.Record(request, response, Now, null, Redactor.WithoutKnownSecrets);

        Assert.Equal("https://api.example.com/new", entry.Url);
    }
}
