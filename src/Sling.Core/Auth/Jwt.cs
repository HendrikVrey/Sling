using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sling.Core.Auth;

/// <summary>
/// The two questions Sling asks about a JWT: is this one, and when did it expire.
/// </summary>
/// <remarks>
/// <para>
/// <b>It never says whether a token is valid, and it never will.</b> Nothing here verifies
/// a signature. Doing so means fetching JWKS, holding keys, and then telling somebody a
/// token is trustworthy - which depends on issuer and audience policy Sling does not know.
/// Saying "valid" wrongly is worse than saying nothing, and the question people actually
/// have is when it expires and what is in it.
/// </para>
/// <para>
/// So the vocabulary is deliberately about the clock: expired, expires, no stated expiry.
/// A token that has not expired is not thereby a token the API will accept.
/// </para>
/// <para>
/// Reading it is not trusting it. The payload is untrusted input from a system Sling did
/// not authenticate, so everything here is bounded and refuses rather than throwing.
/// </para>
/// </remarks>
public static class Jwt
{
    /// <summary>
    /// The largest token this will look inside.
    /// </summary>
    /// <remarks>
    /// Real tokens run to a few kilobytes at the outside. The bound exists because this runs
    /// over a response body and over a header on the way to a send, and neither is a place to
    /// base64-decode something arbitrarily large.
    /// </remarks>
    private const int MaxTokenChars = 64 * 1024;

    /// <summary>
    /// Whether <paramref name="value"/> has the shape of a JWT, and a header that reads
    /// like one.
    /// </summary>
    /// <remarks>
    /// Three base64url segments is the shape, and it is not enough on its own - plenty of
    /// opaque tokens have dots in them. The header is decoded and checked for an <c>alg</c>,
    /// which is what RFC 7515 §4.1.1 requires of one, so a random dotted string is not
    /// offered a decode that would fail.
    /// </remarks>
    public static bool LooksLike([NotNullWhen(true)] string? value)
    {
        if (value is not { Length: > 0 and <= MaxTokenChars })
        {
            return false;
        }

        var parts = value.Split('.');

        if (parts.Length != 3 || parts.Any(p => p.Length == 0) || !parts.All(IsBase64Url))
        {
            return false;
        }

        if (!TryReadJson(parts[0], out var header))
        {
            return false;
        }

        using (header)
        {
            return header.RootElement.ValueKind == JsonValueKind.Object
                && header.RootElement.TryGetProperty("alg", out _);
        }
    }

    /// <summary>
    /// The <c>exp</c> claim, when the token has one.
    /// </summary>
    /// <remarks>
    /// RFC 7519 §4.1.4: <c>exp</c> is a NumericDate, which is seconds since the Unix epoch.
    /// A token with no <c>exp</c> answers false rather than "never expires" - the two are
    /// different, and only one of them is a thing to say out loud.
    /// </remarks>
    public static bool TryReadExpiry(string? value, out DateTimeOffset expires)
    {
        expires = default;

        if (!LooksLike(value))
        {
            return false;
        }

        var payload = value.Split('.')[1];

        if (!TryReadJson(payload, out var document))
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("exp", out var exp)
                || exp.ValueKind != JsonValueKind.Number
                || !exp.TryGetInt64(out var seconds))
            {
                return false;
            }

            try
            {
                expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                // A NumericDate outside what a DateTimeOffset can hold. Ordinary enough for
                // a broken issuer to emit, and not worth taking a send down over.
                return false;
            }
        }
    }

    /// <summary>
    /// The sentence to say about a token that has already expired, or null when there is
    /// nothing to say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only about the past. A token with two hours left needs no sentence, and a warning
    /// that fires on every send is a warning nobody reads by the second day.
    /// </para>
    /// <para>
    /// It names no value and quotes no claim: the point is the clock, and a credential is
    /// not a thing to print in order to say something about it.
    /// </para>
    /// </remarks>
    public static string? DescribeIfExpired(string? value, DateTimeOffset nowUtc)
    {
        if (!TryReadExpiry(value, out var expires) || expires > nowUtc)
        {
            return null;
        }

        return $"This bearer token expired {Ago(nowUtc - expires)}. "
            + "Sling read that from the token itself and checked nothing else about it.";
    }

    /// <summary>How long ago, at the precision somebody can act on.</summary>
    private static string Ago(TimeSpan since)
    {
        if (since < TimeSpan.FromMinutes(1))
        {
            return "less than a minute ago";
        }

        if (since < TimeSpan.FromHours(1))
        {
            var minutes = (int)since.TotalMinutes;

            return minutes == 1
                ? "a minute ago"
                : $"{minutes.ToString(CultureInfo.InvariantCulture)} minutes ago";
        }

        if (since < TimeSpan.FromDays(1))
        {
            var hours = (int)since.TotalHours;

            return hours == 1 ? "an hour ago" : $"{hours.ToString(CultureInfo.InvariantCulture)} hours ago";
        }

        var days = (int)since.TotalDays;
        return days == 1 ? "a day ago" : $"{days.ToString(CultureInfo.InvariantCulture)} days ago";
    }

    /// <summary>
    /// Finds the token-shaped run of characters covering <paramref name="offset"/>.
    /// </summary>
    /// <remarks>
    /// A response body is JSON far more often than not, so the run stops at a quote as well
    /// as at whitespace - which is what makes pointing anywhere inside
    /// <c>"eyJhbGci…"</c> select the token and not the quotes around it.
    /// </remarks>
    public static bool TryFindAt(string text, int offset, out int start, out int length)
    {
        ArgumentNullException.ThrowIfNull(text);

        start = 0;
        length = 0;

        if (offset < 0 || offset > text.Length)
        {
            return false;
        }

        var from = Math.Min(offset, text.Length - 1);

        if (from < 0 || !IsTokenChar(text[from]))
        {
            // The caret may be immediately after the token, which is where a double-click
            // leaves it.
            from = offset - 1;

            if (from < 0 || from >= text.Length || !IsTokenChar(text[from]))
            {
                return false;
            }
        }

        var left = from;
        while (left > 0 && IsTokenChar(text[left - 1]))
        {
            left--;
        }

        var right = from;
        while (right + 1 < text.Length && IsTokenChar(text[right + 1]))
        {
            right++;
        }

        start = left;
        length = right - left + 1;

        return LooksLike(text.Substring(start, length));
    }

    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';

    private static bool IsBase64Url(string segment) =>
        segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Decodes one base64url segment and parses it as JSON.</summary>
    private static bool TryReadJson(string segment, [NotNullWhen(true)] out JsonDocument? document)
    {
        document = null;

        // Base64url drops the padding base64 requires, so it goes back on before decoding.
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };

        if (padded.Length % 4 != 0)
        {
            return false;
        }

        byte[] bytes;

        try
        {
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // Not valid UTF-8. A segment that decodes to bytes and not to text.
            return false;
        }
    }
}
