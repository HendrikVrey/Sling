using System.Globalization;

namespace Sling.Core.Auth;

/// <summary>
/// What can safely be said about a cached access token.
/// </summary>
/// <remarks>
/// <para>
/// Tokens are cached by grant and dropped on an environment switch. Correct, and entirely
/// unobservable - so a 401 could not distinguish a stale token from a wrong scope from a
/// token fetched against the other environment. This is the projection that makes the
/// difference visible.
/// </para>
/// <para>
/// <b>There is no token value on this type and there never will be.</b> The grant and the
/// clock are what answer "why did that 401"; the token itself answers nothing and a panel
/// is a place a screenshot comes from. The client secret is absent for the same reason,
/// even though it is part of what identifies the grant.
/// </para>
/// <para>
/// A separate accessor from the one redaction uses, deliberately. That one returns raw
/// token values because a redactor has to recognise them, and hanging a second question off
/// it is how an uncached token reached the history file in clear.
/// </para>
/// </remarks>
/// <param name="TokenUrl">The endpoint the token came from.</param>
/// <param name="ClientId">Which client asked for it. Not a secret; it is half of the identity.</param>
/// <param name="Scope">What was asked for, or null when the grant named none.</param>
/// <param name="Audience">Which API it is for, or null.</param>
/// <param name="FetchedUtc">When it was obtained.</param>
/// <param name="ExpiresUtc">
/// When it stops being usable, or null when the server never said. Null is not "for ever":
/// a token with no stated lifetime is used once and not cached at all.
/// </param>
public sealed record TokenSummary(
    string TokenUrl,
    string ClientId,
    string? Scope,
    string? Audience,
    DateTimeOffset FetchedUtc,
    DateTimeOffset? ExpiresUtc)
{
    /// <summary>How long this token has left at <paramref name="nowUtc"/>.</summary>
    /// <remarks>
    /// Measured against the same margin the cache uses, so what the chip says and what the
    /// next send does cannot disagree. A token inside the margin reads as expired here
    /// because it will be refetched there.
    /// </remarks>
    public TimeSpan? Remaining(DateTimeOffset nowUtc) =>
        ExpiresUtc is { } expires ? expires - OAuth2Token.ExpiryMargin - nowUtc : null;

    /// <summary>True when the next send would fetch a new one.</summary>
    public bool IsSpent(DateTimeOffset nowUtc) => Remaining(nowUtc) is { } left && left <= TimeSpan.Zero;

    /// <summary>One line naming the grant, for a list.</summary>
    /// <remarks>
    /// Client id and scope, because those are the two things that differ between two tokens
    /// somebody is trying to tell apart - one fetched for <c>orders.read</c> and one for
    /// <c>orders.write</c> look identical without the scope.
    /// </remarks>
    public string Describe()
    {
        var scope = Scope is { Length: > 0 } asked ? "  ·  " + asked : string.Empty;
        var audience = Audience is { Length: > 0 } api ? "  ·  " + api : string.Empty;

        return ClientId + scope + audience;
    }

    /// <summary>The clock half: when it arrived, and how long it has left.</summary>
    public string Clock(DateTimeOffset nowUtc)
    {
        var fetched = "fetched " + FetchedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        return Remaining(nowUtc) switch
        {
            null => fetched + "  ·  no stated lifetime, so it is not reused",
            { } left when left <= TimeSpan.Zero => fetched + "  ·  spent",
            { } left => fetched + "  ·  " + Left(left) + " left",
        };
    }

    /// <summary>
    /// What the chip beside the environment picker says.
    /// </summary>
    /// <remarks>
    /// Written here rather than in the window because it is a rule with a right answer, and
    /// a label built inline in a code-behind is a label nothing can check. The wording is
    /// deliberately about the clock and never about trust: a token that has not expired is
    /// not thereby a token the API will accept.
    /// </remarks>
    public static string Chip(IReadOnlyList<TokenSummary> tokens, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (tokens.Count == 0)
        {
            return "no token";
        }

        var live = tokens.Where(t => !t.IsSpent(nowUtc)).ToList();

        if (live.Count == 0)
        {
            return tokens.Count == 1 ? "token spent" : "tokens spent";
        }

        // The one closest to running out, because that is the one about to cost a round trip
        // - and with several grants in a file it is the only number worth one word.
        var soonest = live.Min(t => t.Remaining(nowUtc));

        if (soonest is not { } left)
        {
            return live.Count == 1 ? "1 token" : live.Count.ToString(CultureInfo.InvariantCulture) + " tokens";
        }

        return live.Count == 1
            ? "token  ·  " + Left(left)
            : live.Count.ToString(CultureInfo.InvariantCulture) + " tokens  ·  " + Left(left);
    }

    /// <summary>
    /// A duration at the precision somebody can act on.
    /// </summary>
    /// <remarks>
    /// Rounded down, so "1 min" never means fifty-nine seconds ago rounded up to something
    /// that has already gone. Under a minute it counts seconds, because that is when the
    /// number is worth watching.
    /// </remarks>
    private static string Left(TimeSpan left)
    {
        if (left < TimeSpan.FromMinutes(1))
        {
            return Math.Floor(left.TotalSeconds).ToString("0", CultureInfo.InvariantCulture) + " s";
        }

        return left < TimeSpan.FromHours(1)
            ? Math.Floor(left.TotalMinutes).ToString("0", CultureInfo.InvariantCulture) + " min"
            : Math.Floor(left.TotalHours).ToString("0", CultureInfo.InvariantCulture) + " h";
    }
}
