using Sling.Core.Documents;
using Sling.Core.Parsing;

namespace Sling.Core.Auth;

/// <summary>The values a <c># @auth oauth2</c> block is written from, still <c>{{braced}}</c>.</summary>
/// <remarks>
/// Separate from <see cref="OAuth2Grant"/>, which carries the line it was parsed from. This
/// is a grant that has not been written yet and therefore has no line, and a type with a
/// meaningless zero in it is a type that invites somebody to report an error against line 0.
/// </remarks>
public sealed record GrantFields(
    string TokenUrl,
    string ClientId,
    string ClientSecret,
    string? Scope,
    string? Audience,
    ClientAuthPlacement Placement);

/// <summary>What a request's auth is being changed to.</summary>
/// <param name="Scheme">The kind of credential to write. <see cref="AuthScheme.None"/> removes it.</param>
/// <param name="HeaderName">
/// The header to write it in. Ignored for a grant, and defaulted to
/// <see cref="RequestAuth.AuthorizationHeader"/> for the schemes that use one.
/// </param>
/// <param name="Credential">
/// The credential as it should appear in the document: the part after <c>Bearer</c> or
/// <c>Basic</c>, or the whole value of an API-key header. Expected to be a
/// <c>{{reference}}</c> - putting a literal credential in a <c>.http</c> file is the one
/// thing the importer refuses to do, and hand-authoring is not a reason to relax it.
/// </param>
/// <param name="Grant">The grant to write, when <paramref name="Scheme"/> is client credentials.</param>
public sealed record AuthSetting(
    AuthScheme Scheme,
    string? HeaderName = null,
    string? Credential = null,
    GrantFields? Grant = null);

/// <summary>
/// Rewrites the auth of one request in a document, as text edits.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes the auth panel a view rather than a store.</b> The panel shows
/// fields, the fields produce an <see cref="AuthSetting"/>, and this turns it into edits to
/// the user's own <c>.http</c> file. Close the panel and what is left is a document a
/// colleague can review - which is the whole difference from the tool whose auth tab writes
/// into a database.
/// </para>
/// <para>
/// Only the lines that carry auth are touched. Everything else about the request - its
/// comments, its other headers, its body, the order it was written in - is left exactly as
/// it was, because it is never re-emitted.
/// </para>
/// </remarks>
public static class AuthDocumentEditor
{
    /// <summary>The directives that belong to a <c># @auth</c> block, including the opener.</summary>
    private static readonly string[] BlockDirectives =
        ["auth", "token-url", "client-id", "client-secret", "scope", "audience", "client-auth"];

    /// <summary>
    /// The edits that make <paramref name="block"/>'s auth into <paramref name="setting"/>.
    /// </summary>
    /// <param name="text">The document exactly as <paramref name="block"/> was parsed from.</param>
    /// <returns>
    /// Edits against <paramref name="text"/>, to be applied last first. Empty when there is
    /// nothing to do.
    /// </returns>
    public static IReadOnlyList<TextEdit> Rewrite(string text, RequestBlock block, AuthSetting setting)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(setting);

        var lines = new Lines(text);
        var edits = new List<TextEdit>();

        var existing = RequestAuth.Describe(block);
        var directives = BlockLines(lines, block);

        if (setting.Scheme == AuthScheme.ClientCredentials)
        {
            if (setting.Grant is not { } grant)
            {
                throw new ArgumentException(
                    "A client-credentials setting needs the grant it is written from.",
                    nameof(setting));
            }

            edits.Add(lines.ReplaceRun(directives, GrantText(grant, lines.NewLine), block.StartLine));
            RemoveHeader(lines, existing, edits);

            return edits;
        }

        if (directives.Count > 0)
        {
            edits.Add(lines.Delete(directives));
        }

        if (setting.Scheme == AuthScheme.None)
        {
            RemoveHeader(lines, existing, edits);
            return edits;
        }

        var name = HeaderNameFor(setting);
        var value = HeaderValueFor(setting);

        // In place when there is already an auth header, even when the header's name is
        // changing: an API key becoming a bearer token stays on the line the user put it on,
        // rather than being deleted from there and re-added at the bottom of the request.
        edits.Add(existing.Origin == AuthOrigin.Header
            ? lines.Replace(existing.Line, name + ": " + value)
            : lines.Insert(HeaderInsertionLine(block), name + ": " + value));

        return edits;
    }

    /// <summary>Removes the header a credential was written in, if there was one.</summary>
    private static void RemoveHeader(Lines lines, RequestAuthView existing, List<TextEdit> edits)
    {
        if (existing.Origin == AuthOrigin.Header)
        {
            edits.Add(lines.Delete([existing.Line]));
        }
    }

    private static string HeaderNameFor(AuthSetting setting) =>
        setting.Scheme == AuthScheme.ApiKeyHeader
            ? Blank(setting.HeaderName) ?? RequestAuth.ApiKeyHeaders[0]
            : RequestAuth.AuthorizationHeader;

    private static string HeaderValueFor(AuthSetting setting)
    {
        var credential = Blank(setting.Credential) ?? string.Empty;

        return setting.Scheme switch
        {
            AuthScheme.Bearer => "Bearer " + credential,
            AuthScheme.Basic => "Basic " + credential,
            _ => credential,
        };
    }

    /// <summary>
    /// The line a new header goes on: after the request's last header, or after the request
    /// line when it has none.
    /// </summary>
    /// <remarks>
    /// After rather than before, so a header Sling adds reads as the newest one rather than
    /// appearing above headers that were written first. The request line itself is the
    /// fallback because a header before it is not a header at all - it is a second request
    /// line the parser will refuse.
    /// </remarks>
    private static int HeaderInsertionLine(RequestBlock block) =>
        (block.Headers.Count > 0 ? block.Headers.Max(h => h.Line) : block.StartLine) + 1;

    /// <summary>The lines of the request's <c># @auth</c> block, if it has one.</summary>
    /// <remarks>
    /// Found by reading the text rather than from the parse, because the parse keeps only
    /// the <c># @auth</c> line: the directives under it become fields on a grant and their
    /// lines are not carried. Bounded to the comment lines above the request line, which is
    /// the only place they can legally be.
    /// </remarks>
    private static List<int> BlockLines(Lines lines, RequestBlock block)
    {
        var found = new List<int>();

        for (var line = block.FirstLine; line < block.StartLine; line++)
        {
            if (lines.TryDirective(line, out var directive)
                && BlockDirectives.Contains(directive, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(line);
            }
        }

        return found;
    }

    /// <summary>Writes a grant as the directives that declare it.</summary>
    /// <remarks>
    /// An empty optional directive is left out rather than written blank. <c># @scope</c>
    /// with nothing after it is a scope of the empty string as far as the parser is
    /// concerned, and an authorization server told to issue a token for no scopes answers
    /// differently from one not told about scopes at all.
    /// </remarks>
    private static string GrantText(GrantFields grant, string newLine)
    {
        var lines = new List<string>
        {
            "# @auth oauth2",
            "# @token-url " + grant.TokenUrl,
            "# @client-id " + grant.ClientId,
            "# @client-secret " + grant.ClientSecret,
        };

        if (Blank(grant.Scope) is { } scope)
        {
            lines.Add("# @scope " + scope);
        }

        if (Blank(grant.Audience) is { } audience)
        {
            lines.Add("# @audience " + audience);
        }

        // Only when it is not the default. A directive restating what would happen anyway is
        // a line the next reader has to look up before deciding it says nothing.
        if (grant.Placement == ClientAuthPlacement.FormBody)
        {
            lines.Add("# @client-auth body");
        }

        return string.Join(newLine, lines);
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// A document's lines, by offset, so an edit can name one.
    /// </summary>
    /// <remarks>
    /// Split the same three ways the parser splits - CRLF, LF and a lone CR - because the
    /// line numbers used here come from the parse and have to mean the same lines. A
    /// terminator is kept with the line it ends so deleting a line takes its newline with it
    /// rather than leaving a blank one behind.
    /// </remarks>
    private sealed class Lines
    {
        private readonly string _text;
        private readonly List<int> _starts = [0];

        internal Lines(string text)
        {
            _text = text;

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] is not ('\n' or '\r'))
                {
                    continue;
                }

                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                _starts.Add(i + 1);
            }

            // The document's own terminator, so a file from a CRLF checkout does not gain one
            // LF line in the middle of it - invisible in the editor, and a whole-file diff for
            // whoever reviews it next.
            NewLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        }

        internal string NewLine { get; }

        /// <summary>The offset line <paramref name="line"/> starts at, clamped to the text.</summary>
        private int Start(int line) =>
            line <= 1 ? 0 : line - 1 < _starts.Count ? _starts[line - 1] : _text.Length;

        /// <summary>One line's text, without the terminator that ended it.</summary>
        private string Text(int line) =>
            _text[Start(line)..Start(line + 1)].TrimEnd('\n').TrimEnd('\r');

        /// <summary>The metadata directive on <paramref name="line"/>, if it carries one.</summary>
        internal bool TryDirective(int line, out string directive)
        {
            directive = string.Empty;

            if (line < 1 || line >= _starts.Count + 1)
            {
                return false;
            }

            if (RequestDocumentParser.MetadataDirective(Text(line)) is not { } name)
            {
                return false;
            }

            directive = name;
            return true;
        }

        internal TextEdit Delete(IReadOnlyList<int> lines)
        {
            var first = lines[0];
            var last = lines[^1];

            return new TextEdit(Start(first), Start(last + 1) - Start(first), string.Empty);
        }

        internal TextEdit Replace(int line, string replacement) =>
            new(Start(line), Start(line + 1) - Start(line), replacement + NewLine);

        /// <summary>
        /// Inserts a line before <paramref name="line"/>.
        /// </summary>
        /// <remarks>
        /// The leading terminator is not decoration. A document whose last line has no
        /// newline after it - which is most of them, since an editor does not add one - puts
        /// the insertion point at the very end of the text, and without this the new header
        /// would be welded on to the end of the request line: <c>GET https://xAuthorization:
        /// ...</c>, a document that no longer parses.
        /// </remarks>
        internal TextEdit Insert(int line, string text)
        {
            var offset = Start(line);
            var needsBreak = offset > 0 && _text[offset - 1] is not ('\n' or '\r');

            return new TextEdit(offset, 0, (needsBreak ? NewLine : string.Empty) + text + NewLine);
        }

        /// <summary>
        /// Replaces a run of lines, or inserts at <paramref name="fallback"/> when the run is
        /// empty.
        /// </summary>
        internal TextEdit ReplaceRun(IReadOnlyList<int> lines, string replacement, int fallback)
        {
            if (lines.Count == 0)
            {
                return Insert(fallback, replacement);
            }

            var first = lines[0];
            var last = lines[^1];

            return new TextEdit(
                Start(first),
                Start(last + 1) - Start(first),
                replacement + NewLine);
        }
    }
}
