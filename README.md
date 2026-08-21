# Sling

An editor-first HTTP client for Windows. The request is a document, not a form.

```http
@base = https://api.example.com

# @name login
POST {{base}}/auth
Content-Type: application/json

{ "user": "ada", "pass": "{{secret}}" }

### the token flows into the next request
GET {{base}}/me
Authorization: Bearer {{login.response.body.$.access_token}}
```

That is the whole interface. `Ctrl+Enter` sends the request under the caret; the
response opens beside it in a real editor buffer — highlighted, foldable, searchable.

## Why

Postman's core defect is that a request is a **form**. Six tabs, a modal for variables,
and the artifact you version is a multi-thousand-line JSON blob that cannot be reviewed
in a pull request. The account nag and the workspace concept are downstream of that one
choice.

Sling stores requests as [`.http`](https://learn.microsoft.com/aspnet/core/test/http-files)
files — the same format Visual Studio 2022, Rider and the VS Code REST Client already
read. A collection becomes a folder of text files. Grouping is `###` separators;
hierarchy is folders; sharing is `git push`; review is a normal diff.

No account, no cloud, no sync, no collection tree, no save dialog.

## Status

**M2 — the response is an editor buffer.** `Ctrl+Enter` sends the request under the
caret, `Esc` cancels, and a request that references an earlier one by name sends that one
first, automatically, and shows both.

What arrives is not a viewport. The request line and the status sit above the pane and the
headers behind a collapsed expander, so the buffer holds the **body and nothing else** —
which is what lets it be highlighted, folded, searched with `Ctrl+F`, and transformed in
place. Right-click and the menu offers the transforms that apply to what is actually
there: format the JSON, decode the base64, decode the JWT. They chain, because each one
rewrites the buffer and the pane then asks again what it is holding.

The transform engine is [Etch](https://github.com/HendrikVrey/Etch)'s, consumed as a
package — see [docs/etch-core-package.md](docs/etch-core-package.md), which also explains
why a fresh clone needs one extra step before it will build.

**Paste a curl command into the request pane and you get a request.** Anything it cannot
express becomes a comment saying what was dropped;
[docs/curl-import.md](docs/curl-import.md) has the rules, including the two flags it
deliberately refuses.

Not there yet: environments and a secrets file, cookies, OAuth2, file and multipart
bodies, and saved history. Those are M3. Requests are still typed into the window rather
than opened from disk.

The exact dialect Sling reads, and every place it differs from the VS Code REST Client,
is written down in [docs/http-dialect.md](docs/http-dialect.md).

## Who this is for

Developers who are already fighting Postman — the ones who keep a scratch collection
called "test" and have thought about going back to curl.

It is **not** aimed at people who live in Postman's OAuth 2.0 button, collection runner
and Tests tab. Serving that workflow means rebuilding Postman, which removes the reason
to build Sling. The honest pitch is *everything you actually use Postman for, as text
files you can review in a pull request* — not *a drop-in replacement*.

### Planned for v1

Postman collection import · paste-a-curl-command · environments (dev/staging/prod) ·
cookie jar · OAuth2 client-credentials · request chaining · file and multipart bodies

### Deliberately not in v1

OAuth2 authorization-code flow · test assertions · mock servers · team sync · gRPC ·
WebSocket · proxy capture

## Security

Sling handles credentials, so a few rules are structural rather than hardening added
later:

- **Secrets live in a separate, gitignored file.** A committed bearer token is the known
  failure mode of `.http` files in the wild.
- **`Authorization`, `Cookie` and `Proxy-Authorization` are dropped on a cross-origin
  redirect.**
- **TLS validation is on by default**; any bypass is per-request and shown while active.
- **Cookies are scoped per environment** — a staging cookie never reaches production.
- **Response bodies render as text**, never into a browser control.
- No telemetry, no update ping, no crash upload.

## Building

Requires the .NET 10 SDK — **and one extra step**, because Sling depends on `Etch.Core`,
which is on a private feed rather than nuget.org. From a checkout of
[Etch](https://github.com/HendrikVrey/Etch), beside this one:

```bash
dotnet pack src/Etch.Core/Etch.Core.csproj -c Release -o ../Sling/local-feed
```

[docs/etch-core-package.md](docs/etch-core-package.md) explains why the feed is private
and how to authenticate to it instead.

```bash
dotnet build Sling.slnx
```

```bash
dotnet test Sling.slnx
```

## Layout

| Project | Contains |
|---|---|
| `Sling.Core` | `.http` parser, models, variable and chain resolution, redaction. Pure — no I/O, no network, no dependencies. |
| `Sling.Import` | Postman v2.1 JSON and curl → `.http`. Pure. |
| `Sling.Http` | The only project that touches the network. |
| `Sling.Persistence` | All disk I/O. |
| `Sling.App` | WPF + WPF-UI + AvalonEdit shell. No business logic. Consumes `Etch.Core` for the response transforms. |

Those boundaries are enforced by `ArchitectureTests`, not just documented.

## Licence

**Source-available, not open source.** Sling is free to download, read, compile and
run — for anything, including commercially and at work. You may not modify it,
republish it, or sell it. See [LICENSE](LICENSE) for the terms that actually apply,
and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the components it is built on,
which carry their own licences.

The request files you write, and everything you send and receive with Sling, are
yours. The licence claims nothing over them.
