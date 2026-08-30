using System.Text;

namespace Sling.Import.Curl;

/// <summary>
/// Splits a pasted curl command into arguments the way a shell would.
/// </summary>
/// <remarks>
/// <para>
/// A curl command arrives from a browser's "Copy as cURL", from documentation, or from a
/// colleague's message, and each of those quotes differently. This has to cope with all
/// of them without being a shell - it never expands a variable, never resolves a glob and
/// never executes anything. <b>It is a quoting parser, and the security property is that
/// there is nothing else in it to abuse.</b>
/// </para>
/// <para>
/// Three continuation conventions are honoured, because the three places these get copied
/// from use three different ones: a trailing backslash (bash), a trailing caret (Windows
/// <c>cmd</c>, which is what Chrome emits for "Copy as cURL (cmd)"), and a trailing
/// backtick (PowerShell). Each is only a continuation at the very end of a line - a caret
/// or a backtick in the middle of a URL is an ordinary character, and treating it
/// otherwise would corrupt perfectly good input.
/// </para>
/// </remarks>
internal static class CurlTokenizer
{
    /// <summary>
    /// Tokenizes <paramref name="commandLine"/>.
    /// </summary>
    /// <remarks>
    /// An unterminated quote is not an error. The rest of the input becomes the final
    /// token, which is what a person who pasted half a command wants: a partial import
    /// they can fix, rather than a refusal.
    /// </remarks>
    internal static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var hasToken = false;

        var quote = '\0';
        var i = 0;

        while (i < commandLine.Length)
        {
            var c = commandLine[i];

            if (quote == '\'')
            {
                // Single quotes are literal in every shell that uses them: there is no
                // escape, and the only thing that ends the string is the closing quote.
                if (c == '\'')
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                i++;
                continue;
            }

            if (quote == '"')
            {
                if (c == '\\' && i + 1 < commandLine.Length)
                {
                    // Inside double quotes a backslash only escapes a small set; before
                    // anything else it stays a literal backslash. Applying it universally
                    // would eat the separators out of a Windows path in a -d payload.
                    var next = commandLine[i + 1];

                    if (next is '"' or '\\' or '$' or '`')
                    {
                        current.Append(next);
                        i += 2;
                        continue;
                    }

                    if (next is '\n' or '\r')
                    {
                        i = SkipNewLine(commandLine, i + 1);
                        continue;
                    }

                    current.Append(c);
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                i++;
                continue;
            }

            // ANSI-C quoting: $'…', where backslash escapes mean what they do in C.
            //
            // Not an exotic corner. Chrome and Firefox DevTools switch "Copy as cURL" to
            // this form whenever a value contains a control character, an apostrophe or a
            // '!' - which is every multi-line JSON body and every value with an apostrophe
            // in it. Without this the '$' was an ordinary character and the following
            // quotes were read as a plain literal string, so an escaped apostrophe ended
            // the string early: the method came out wrong, the body vanished, and the
            // notes were nonsense. For the most-used browser, that is the common path.
            if (c == '$' && i + 1 < commandLine.Length && commandLine[i + 1] == '\'')
            {
                i = ReadAnsiC(commandLine, i + 2, current);
                hasToken = true;
                continue;
            }

            switch (c)
            {
                case '\'':
                case '"':
                    quote = c;
                    // A quote starts a token even when what it encloses is empty, which is
                    // how `-d ''` reaches the importer as an empty body rather than as no
                    // argument at all.
                    hasToken = true;
                    i++;
                    continue;

                case '\\' when i + 1 < commandLine.Length && commandLine[i + 1] is '\n' or '\r':
                    i = SkipNewLine(commandLine, i + 1);
                    continue;

                // An unquoted backslash escapes the next character - but only when that
                // character is one anybody would bother escaping.
                //
                // The two conventions collide here. In bash an unquoted backslash always
                // escapes, so `\&` and `\ ` and `\"` are real. On Windows a backslash is a
                // path separator, so `C:\tools\curl.exe` is a perfectly ordinary token.
                // Consuming backslashes unconditionally turned that into `C:toolscurl.exe`
                // and the command stopped being recognised as curl at all.
                //
                // The rule that serves both: escaping a letter or a digit is meaningless
                // in bash - `\t` unquoted is just `t`, never a tab - so nobody writes it
                // on purpose, while every real use escapes punctuation or a space. Before
                // an alphanumeric the backslash stays literal; before anything else it
                // escapes.
                case '\\' when i + 1 < commandLine.Length && !char.IsLetterOrDigit(commandLine[i + 1]):
                    current.Append(commandLine[i + 1]);
                    hasToken = true;
                    i += 2;
                    continue;

                case '^' when IsAtLineEnd(commandLine, i + 1):
                case '`' when IsAtLineEnd(commandLine, i + 1):
                    i = SkipNewLine(commandLine, i + 1);
                    continue;

                case '\n':
                case '\r':
                case ' ':
                case '\t':
                    Flush(tokens, current, ref hasToken);
                    i++;
                    continue;

                default:
                    current.Append(c);
                    hasToken = true;
                    i++;
                    continue;
            }
        }

        Flush(tokens, current, ref hasToken);

        return tokens;
    }

    /// <summary>
    /// Reads the body of a <c>$'…'</c> string, starting just past the opening quote.
    /// </summary>
    /// <returns>The index just past the closing quote, or the end of the input.</returns>
    /// <remarks>
    /// The escapes bash defines for this form. <c>\x</c> takes one or two hex digits and
    /// <c>\u</c> takes up to four - both are variable-length in bash, and reading a fixed
    /// count would swallow the character after a short one. An unrecognised escape keeps
    /// the character and drops the backslash, which is what bash does.
    /// </remarks>
    private static int ReadAnsiC(string text, int index, StringBuilder into)
    {
        while (index < text.Length)
        {
            var c = text[index];

            if (c == '\'')
            {
                return index + 1;
            }

            if (c != '\\' || index + 1 >= text.Length)
            {
                into.Append(c);
                index++;
                continue;
            }

            var escape = text[index + 1];
            index += 2;

            switch (escape)
            {
                case 'n': into.Append('\n'); break;
                case 't': into.Append('\t'); break;
                case 'r': into.Append('\r'); break;
                case 'a': into.Append('\a'); break;
                case 'b': into.Append('\b'); break;
                case 'f': into.Append('\f'); break;
                case 'v': into.Append('\v'); break;
                case 'e' or 'E': into.Append('\u001B'); break;
                case '0': into.Append('\0'); break;
                case 'x': index = Hex(text, index, maxDigits: 2, into); break;
                case 'u': index = Hex(text, index, maxDigits: 4, into); break;
                case 'U': index = Hex(text, index, maxDigits: 8, into); break;
                default: into.Append(escape); break;
            }
        }

        // Unterminated, like every other quote here: the rest becomes part of the token
        // rather than the whole import being refused.
        return index;
    }

    /// <summary>Reads up to <paramref name="maxDigits"/> hex digits as one code point.</summary>
    private static int Hex(string text, int index, int maxDigits, StringBuilder into)
    {
        var value = 0;
        var digits = 0;

        while (digits < maxDigits && index < text.Length && Uri.IsHexDigit(text[index]))
        {
            value = (value * 16) + Convert.ToInt32(text[index].ToString(), 16);
            index++;
            digits++;
        }

        if (digits == 0)
        {
            // '\x' with nothing after it is not an escape at all; bash leaves it alone.
            into.Append('x');
            return index;
        }

        // A lone surrogate or an out-of-range code point cannot be appended as a rune, and
        // this is untrusted input, so the check is not optional.
        if (value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF))
        {
            into.Append('�');
            return index;
        }

        into.Append(char.ConvertFromUtf32(value));

        return index;
    }

    private static void Flush(List<string> tokens, StringBuilder current, ref bool hasToken)
    {
        if (!hasToken)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
        hasToken = false;
    }

    /// <summary>
    /// Whether the character at <paramref name="index"/> begins a line break, ignoring
    /// nothing - a caret followed by a space then a newline is <em>not</em> a continuation
    /// in <c>cmd</c>, and pretending otherwise would join two arguments into one.
    /// </summary>
    private static bool IsAtLineEnd(string text, int index) =>
        index < text.Length && text[index] is '\n' or '\r';

    /// <summary>Steps over one line break, treating CRLF as a single one.</summary>
    private static int SkipNewLine(string text, int index)
    {
        if (index < text.Length && text[index] == '\r')
        {
            index++;
        }

        if (index < text.Length && text[index] == '\n')
        {
            index++;
        }

        return index;
    }
}
