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

**M0 — early scaffolding. Not usable yet.** The window opens and the panes are wired;
nothing is sent. Sending arrives in M1.

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

Requires the .NET 10 SDK.

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
| `Sling.App` | WPF + WPF-UI + AvalonEdit shell. No business logic. |

Those boundaries are enforced by `ArchitectureTests`, not just documented.

## Licence

**Source-available, not open source.** Sling is free to download, read, compile and
run — for anything, including commercially and at work. You may not modify it,
republish it, or sell it. See [LICENSE](LICENSE) for the terms that actually apply,
and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the components it is built on,
which carry their own licences.

The request files you write, and everything you send and receive with Sling, are
yours. The licence claims nothing over them.
