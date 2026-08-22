using Sling.Core.Cookies;

namespace Sling.Core.Tests;

/// <summary>
/// The cookie rules from RFC 6265 §5.1 to §5.4.
/// </summary>
/// <remarks>
/// These are the tests that make <c>Sling.md</c> §5.6 — "the cookie jar respects domain,
/// path and <c>Secure</c>" — a statement rather than an intention. Each of them names a
/// specific way a credential reaches the wrong host.
/// </remarks>
public sealed class CookieTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly Uri Origin = new("https://api.example.com/v1/orders");

    [Fact]
    public void A_cookie_with_no_domain_attribute_is_host_only()
    {
        Assert.True(CookieParser.TryParse("sid=abc", Origin, Now, out var cookie, out _));

        Assert.True(cookie.HostOnly);
        Assert.Equal("api.example.com", cookie.Domain);

        // Default-path: the request path up to but not including its last slash.
        Assert.Equal("/v1", cookie.Path);
    }

    [Fact]
    public void A_domain_attribute_widens_the_cookie_to_subdomains()
    {
        Assert.True(CookieParser.TryParse("sid=abc; Domain=.example.com", Origin, Now, out var cookie, out _));

        Assert.False(cookie.HostOnly);

        // The leading dot carries no meaning of its own in RFC 6265 and is stripped.
        Assert.Equal("example.com", cookie.Domain);
    }

    [Fact]
    public void A_domain_the_setting_host_is_not_under_is_refused()
    {
        Assert.False(CookieParser.TryParse("sid=abc; Domain=example.org", Origin, Now, out _, out var reason));
        Assert.Contains("does not cover", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_top_level_domain_is_refused()
    {
        // Without a public suffix list this is the one supercookie that can still be
        // recognised with certainty: no single label is ever a registrable domain.
        Assert.False(CookieParser.TryParse("sid=abc; Domain=com", Origin, Now, out _, out var reason));
        Assert.Contains("top-level domain", reason, StringComparison.Ordinal);
    }

    [Theory]
    // The suffix must start at a label boundary. Without the dot test, whoever registers
    // 'notexample.com' receives every cookie scoped to 'example.com'.
    [InlineData("notexample.com", "example.com", false)]
    [InlineData("api.example.com", "example.com", true)]
    [InlineData("example.com", "example.com", true)]
    [InlineData("API.Example.COM", "example.com", true)]
    [InlineData("example.com.evil.test", "example.com", false)]
    public void Domain_matching_stops_at_a_label_boundary(string host, string domain, bool expected) =>
        Assert.Equal(expected, CookieParser.DomainMatches(host, domain));

    [Theory]
    // The difference between RFC 6265 §5.1.4 and a plain prefix match, which is why Sling
    // does not use the framework's cookie container: '/foobar' is a different resource.
    [InlineData("/foo", "/foo", true)]
    [InlineData("/foo/bar", "/foo", true)]
    [InlineData("/foobar", "/foo", false)]
    [InlineData("/foo/", "/foo/", true)]
    [InlineData("/foo", "/foo/", false)]
    [InlineData("/", "/", true)]
    [InlineData("/anything", "/", true)]
    public void Path_matching_stops_at_a_segment_boundary(string requestPath, string cookiePath, bool expected) =>
        Assert.Equal(expected, CookieParser.PathMatches(requestPath, cookiePath));

    [Theory]
    [InlineData("/v1/orders", "/v1")]
    [InlineData("/orders", "/")]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData("relative", "/")]
    public void The_default_path_drops_the_last_segment(string requestPath, string expected) =>
        Assert.Equal(expected, CookieParser.DefaultPath(requestPath));

    [Fact]
    public void A_secure_cookie_set_over_plain_http_is_refused()
    {
        // Refusing costs nothing real — a correct client could never send it back to the
        // http origin — and storing it is how cookie forcing works.
        var insecure = new Uri("http://api.example.com/v1/orders");

        Assert.False(CookieParser.TryParse("sid=abc; Secure", insecure, Now, out _, out var reason));
        Assert.Contains("Secure", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secure_cookie_is_not_sent_over_plain_http()
    {
        var jar = new CookieJar();
        jar.Store(Origin, ["sid=abc; Secure; Path=/"], Now);

        Assert.Null(jar.Header(new Uri("http://api.example.com/v1/orders"), Now));
        Assert.Equal("sid=abc", jar.Header(Origin, Now));
    }

    [Theory]
    [InlineData("__Host-sid=abc; Secure; Path=/", true)]
    [InlineData("__Host-sid=abc; Secure; Path=/v1", false)]
    [InlineData("__Host-sid=abc; Secure; Path=/; Domain=example.com", false)]
    [InlineData("__Host-sid=abc; Path=/", false)]
    [InlineData("__Secure-sid=abc; Secure", true)]
    [InlineData("__Secure-sid=abc", false)]
    public void The_name_prefixes_are_enforced(string header, bool accepted) =>
        Assert.Equal(accepted, CookieParser.TryParse(header, Origin, Now, out _, out _));

    [Fact]
    public void A_header_with_no_equals_sign_is_not_a_cookie()
    {
        Assert.False(CookieParser.TryParse("flag", Origin, Now, out _, out var reason));
        Assert.Contains("'='", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Max_age_beats_expires()
    {
        const string Header = "sid=abc; Max-Age=60; Expires=Thu, 01 Jan 1970 00:00:00 GMT";

        Assert.True(CookieParser.TryParse(Header, Origin, Now, out var cookie, out _));
        Assert.Equal(Now.AddSeconds(60), cookie.Expires);
    }

    [Fact]
    public void A_max_age_that_would_overflow_the_calendar_does_not_throw()
    {
        var header = $"sid=abc; Max-Age={long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        Assert.True(CookieParser.TryParse(header, Origin, Now, out var cookie, out _));
        Assert.Equal(DateTimeOffset.MaxValue, cookie.Expires);
    }

    [Theory]
    // RFC 6265 §5.1.1 is an extraction algorithm, not a format list. Every spelling below
    // yields the same instant, and the day name is never consulted.
    [InlineData("Sat, 22 Aug 2026 13:00:00 GMT")]
    [InlineData("Sat, 22-Aug-2026 13:00:00 GMT")]
    [InlineData("22 Aug 2026 13:00:00 GMT")]
    [InlineData("Sat, 22 Aug 2026 13:00:00 UTC")]
    [InlineData("Sat, 22 Aug 2026 13:00:00")]
    // The day name is Saturday. A server that says Monday is not to be believed about the
    // day, and is to be believed about the date.
    [InlineData("Mon, 22 Aug 2026 13:00:00 GMT")]
    public void An_expires_value_is_read_by_its_date_and_not_by_its_day_name(string expires)
    {
        // Validating the day name is wrong in both directions at once: a future expiry
        // becomes a session cookie that never expires, and a server's own logout — Expires
        // in 1970 — is ignored, so the session keeps travelling.
        Assert.True(CookieParser.TryParse($"sid=abc; Expires={expires}", Origin, Now, out var cookie, out _));

        // UTC, not machine-local: reading it as local would make a cookie's lifetime depend
        // on where the user is sitting, which is the DateTimeOffset.TryParse default.
        Assert.Equal(new DateTimeOffset(2026, 8, 22, 13, 0, 0, TimeSpan.Zero), cookie.Expires);
    }

    [Fact]
    public void A_deletion_whose_day_name_is_wrong_still_deletes()
    {
        // 1 January 1970 was a Thursday. Servers write Friday, Monday and everything else.
        var jar = new CookieJar();
        jar.Store(Origin, ["sid=abc; Path=/"], Now);
        jar.Store(Origin, ["sid=; Path=/; Expires=Fri, 01-Jan-1970 00:00:00 GMT"], Now);

        Assert.Equal(0, jar.Count);
    }

    [Theory]
    // RFC 6265bis §4.1.3 specifies a case-insensitive match. An ordinal one lets a name
    // through carrying a promise its attributes do not keep.
    [InlineData("__host-sid=x; Secure; Path=/v1")]
    [InlineData("__HOST-sid=x; Secure; Path=/v1")]
    [InlineData("__secure-sid=x")]
    [InlineData("__SECURE-sid=x")]
    public void The_name_prefixes_are_matched_without_regard_to_case(string header) =>
        Assert.False(CookieParser.TryParse(header, Origin, Now, out _, out _));

    [Fact]
    public void Loopback_counts_as_a_secure_context()
    {
        // The same rule the OAuth2 token endpoint uses. Without it a local development
        // server issuing Secure session cookies cannot hold a session, while the identity
        // provider beside it authenticates happily — one diff disagreeing with itself.
        var local = new Uri("http://localhost:3000/v1/orders");

        Assert.True(CookieParser.TryParse("sid=abc; Secure; Path=/", local, Now, out _, out _));

        var jar = new CookieJar();
        jar.Store(local, ["sid=abc; Secure; Path=/"], Now);

        Assert.Equal("sid=abc", jar.Header(local, Now));
    }

    [Fact]
    public void An_expired_cookie_is_not_sent_and_is_swept()
    {
        var jar = new CookieJar();
        jar.Store(Origin, ["sid=abc; Path=/; Max-Age=60"], Now);

        Assert.Equal("sid=abc", jar.Header(Origin, Now));
        Assert.Null(jar.Header(Origin, Now.AddSeconds(120)));
        Assert.Equal(0, jar.Count);
    }

    [Fact]
    public void Setting_a_cookie_with_a_past_expiry_deletes_it()
    {
        var jar = new CookieJar();
        jar.Store(Origin, ["sid=abc; Path=/"], Now);
        jar.Store(Origin, ["sid=abc; Path=/; Max-Age=0"], Now);

        Assert.Null(jar.Header(Origin, Now));
        Assert.Equal(0, jar.Count);
    }

    [Fact]
    public void Replacing_a_cookie_keeps_its_original_creation_time()
    {
        // RFC 6265 §5.3 step 11. Creation time breaks ties in send order, so a session
        // that refreshes its own cookie would otherwise reshuffle the header each time.
        var jar = new CookieJar();
        jar.Store(Origin, ["sid=one; Path=/"], Now);
        jar.Store(Origin, ["sid=two; Path=/"], Now.AddMinutes(5));

        var stored = Assert.Single(jar.Snapshot(Now.AddMinutes(5)));

        Assert.Equal("two", stored.Value);
        Assert.Equal(Now, stored.CreatedUtc);
    }

    [Fact]
    public void Cookies_are_sent_longest_path_first()
    {
        var jar = new CookieJar();
        jar.Store(Origin, ["wide=1; Path=/"], Now);
        jar.Store(Origin, ["narrow=1; Path=/v1/orders"], Now);

        Assert.Equal("narrow=1; wide=1", jar.Header(Origin, Now));
    }

    [Fact]
    public void A_cookie_for_another_host_is_never_sent()
    {
        var jar = new CookieJar();
        jar.Store(Origin, ["sid=abc; Path=/"], Now);

        Assert.Null(jar.Header(new Uri("https://other.example.org/v1/orders"), Now));
    }

    [Fact]
    public void A_host_only_cookie_is_not_sent_to_a_subdomain()
    {
        var jar = new CookieJar();
        jar.Store(new Uri("https://example.com/"), ["sid=abc; Path=/"], Now);

        Assert.Null(jar.Header(new Uri("https://api.example.com/"), Now));
        Assert.Equal("sid=abc", jar.Header(new Uri("https://example.com/"), Now));
    }

    [Fact]
    public void A_refused_cookie_is_reported_rather_than_dropped_in_silence()
    {
        var jar = new CookieJar();
        var notes = jar.Store(Origin, ["sid=abc; Domain=example.org"], Now);

        // A session that silently does not work is the hardest kind to debug.
        var note = Assert.Single(notes);
        Assert.Contains("was not stored", note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_per_domain_ceiling_evicts_the_least_recently_used()
    {
        var jar = new CookieJar();

        for (var i = 0; i < 60; i++)
        {
            var name = "c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            jar.Store(Origin, [$"{name}=v; Path=/"], Now.AddSeconds(i));
        }

        // RFC 6265 §6.1's suggested 50 per domain. The bound is what stops one chatty host
        // evicting every other host's session.
        Assert.Equal(50, jar.Count);

        var kept = jar.Snapshot(Now.AddMinutes(5)).Select(c => c.Name).ToList();
        Assert.DoesNotContain("c0", kept);
        Assert.Contains("c59", kept);
    }

    [Fact]
    public void Clearing_the_jar_empties_it()
    {
        var jar = new CookieJar();
        jar.Store(Origin, ["sid=abc; Path=/"], Now);
        jar.Clear();

        Assert.Equal(0, jar.Count);
        Assert.Null(jar.Header(Origin, Now));
    }
}
