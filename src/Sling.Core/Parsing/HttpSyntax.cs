using System.Globalization;

namespace Sling.Core.Parsing;

/// <summary>
/// The character-level rules of HTTP syntax that both the parser and the variable
/// resolver need.
/// </summary>
/// <remarks>
/// <para>
/// One home on purpose. These predicates are the enforcement point for
/// <c>Sling.md</c> §5.7 — a value that arrives from a response body must not be able to
/// inject a header or a request line — and a rule written in two places will eventually
/// disagree, with the tested copy not necessarily being the one that ships.
/// </para>
/// <para>
/// Public rather than internal because <c>Sling.Import</c> asks the same questions of the
/// header names it finds inside a Postman collection, and answering them with a private
/// copy of the predicate is precisely the two-homes failure above. A generated header name
/// that is not a token would otherwise reach the parser and be refused there, several
/// layers from the collection that supplied it.
/// </para>
/// </remarks>
public static class HttpSyntax
{
    private const char Delete = (char)0x7F;
    private const string TokenPunctuation = "!#$%&'*+-.^_`|~";

    /// <summary>
    /// RFC 9110 <c>token</c>: the only thing a header name may be. Checked rather than
    /// assumed, because a name containing a colon, a space or a control character is how
    /// a header injection is spelled.
    /// </summary>
    public static bool IsToken(string value) => value.Length > 0 && value.All(IsTokenChar);

    public static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || TokenPunctuation.Contains(c, StringComparison.Ordinal);

    /// <summary>
    /// A header value may hold visible ASCII, space, horizontal tab, and (by long
    /// tolerance) characters above 0x7F. It may never hold CR, LF or NUL — those end the
    /// field, so a value carrying one would append headers of its own.
    /// </summary>
    public static bool IsLegalHeaderValueChar(char c) =>
        c is not ('\r' or '\n' or '\0') && (!char.IsControl(c) || c == '\t');

    /// <summary>
    /// A request target has no quoting mechanism at all: whitespace ends it and CR or LF
    /// ends the whole request line. Everything at or below space, and DEL, is rejected.
    /// </summary>
    public static bool IsLegalRequestTargetChar(char c) => c > ' ' && c != Delete;

    /// <summary>
    /// Names the first character <paramref name="isLegal"/> rejects, for use in a
    /// diagnostic.
    /// </summary>
    /// <remarks>
    /// Returns the code point rather than the value it was found in. The value may be a
    /// bearer token, and an error message is not a place to print one.
    /// </remarks>
    public static string DescribeFirstIllegal(string value, Func<char, bool> isLegal)
    {
        foreach (var c in value)
        {
            if (isLegal(c))
            {
                continue;
            }

            var codePoint = "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);
            return char.IsControl(c) || char.IsWhiteSpace(c) ? codePoint : $"'{c}' ({codePoint})";
        }

        return "an unrepresentable character";
    }
}
