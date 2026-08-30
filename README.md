<p align="center">
  <img src="assets/sling-256.png" alt="Sling logo" width="112">
</p>

<h1 align="center">Sling</h1>

<p align="center">
  <b>An editor-first HTTP client for Windows. The request is a document, not a form.</b>
</p>

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

That is the whole interface. **Send** — or `Ctrl+Enter` — sends the request under the
caret, and the command bar says which one that is before you press it; the response opens
beside it in a real editor buffer, highlighted, foldable, searchable, with its status
colour-coded beside the pane.

## Why

Postman's core defect is that a request is a **form**. Six tabs, a modal for variables,
and the artifact you version is a multi-thousand-line JSON blob that cannot be reviewed
in a pull request. The account nag and the workspace concept are downstream of that one
choice.

Sling stores requests as [`.http`](https://learn.microsoft.com/aspnet/core/test/http-files)
files — the same format Visual Studio 2022, Rider and the VS Code REST Client already
read. A collection becomes a folder of text files. Grouping is `###` separators;
hierarchy is folders; sharing is `git push`; review is a normal diff.

There **is** a collection tree — folders, files and the requests inside them, with the
verbs colour-coded, and buttons to add to it. What there is not is a collection *format*
behind it: the tree is drawn from the folder every time, so renaming a collection is
renaming a directory and moving one is `git mv`.
[docs/collections.md](docs/collections.md) has the rest.

No account, no cloud, no sync, no save dialog.

## Status

**The command bar.** Send, Run all, a File menu, Save, History and Settings are buttons
above the panes, with the collections rail on a toggle beside them. Every chord Sling has
is now something you can see, and every button names its own chord — the keyboard is still
the fast path, it is just no longer the only way to find out a command exists. Beside the
buttons is the request Send would send; beside RESPONSE is the status, coloured by class.

**Collections.** The rail is a tree: collections (folders), the request files in them, and
the requests inside each file with their verbs colour-coded. Clicking a request opens its
file and puts the caret on it, so `Ctrl+Enter` sends it; moving around a file highlights
the request that would go. **+ Collection**, **+ File** and **+ Request** sit above the
tree and land inside whatever is selected.

Nothing is stored to make this work — no manifest, no index, no ordering — so a collection
is still just a directory and Sling can be deleted without taking your requests with it.
There is deliberately no rename and no delete in the rail;
[docs/collections.md](docs/collections.md) says why.

**M4 — the Postman importer.** `Ctrl+I`, pick your collection export and its environment
exports in the same dialog, pick a folder. Sling writes the `.http` files, writes both
environment files, and opens the folder.

A collection becomes a folder of files: requests at the root go into one named after the
collection, a folder `Orders` becomes `orders.http`, `Orders / Refunds` becomes
`orders/refunds.http`. Bodies come across in every mode Postman has, including form-data —
which becomes a real multipart body, because that is how the `.http` format expresses one.
Auth comes across too, inherited the way Postman inherits it, and an OAuth 2.0
client-credentials block becomes a real `# @auth oauth2` grant.

**No credential is ever written into a `.http` file.** An export routinely carries a live
token in plain text, and an imported document is meant to be committed — so every literal
credential moves into the gitignored `http-client.private.env.json` and the request gets a
`{{name}}`. Read both environment files before you commit: Postman only marks a value
secret when its owner ticked the box, so anything whose *name* reads like a credential is
treated as one too.

Scripts are not run — that is a non-goal, not a gap — but they are copied into the document
as comments so you can see what they did. Everything else the importer cannot do exactly
becomes a comment naming what was lost. Nothing is dropped silently.
[docs/postman-import.md](docs/postman-import.md) has the whole account, including what a
collection can and cannot make Sling do with it.

**M3 slice 2 — cookies, OAuth2, history, run-all, settings.** Sling keeps a cookie jar
per environment, by RFC 6265's rules for domain, path and `Secure`, so a cookie set by
staging cannot reach production. It lives in memory and is discarded when you switch
environment, open another file, or close the window.

An OAuth2 **client-credentials** grant is four lines above a request:

```http
# @auth oauth2
# @token-url {{auth_base}}/oauth2/token
# @client-id {{client_id}}
# @client-secret {{client_secret}}
GET {{base}}/orders
```

Sling fetches the token, attaches it, caches it until it expires, and shows the token
exchange in the response pane like any other call it makes on your behalf. Tokens are
never written to disk. The authorization-code flow is not supported and is not planned.

`Ctrl+Shift+Enter` sends every request in the file in one run — shared chain responses, so
a dependency already satisfied is not sent twice, and a failure does not stop the rest.

`Ctrl+H` shows the local history in the response buffer: what was sent, when, and what
came back. Credentials are removed before anything is written, and **no request or
response body is stored at all** — a login response body *is* the token, and a redactor
that has to recognise credentials inside arbitrary payloads is a guess that fails
silently. `Ctrl+,` opens settings: timeout, response cap, redirects, and switches for
cookies and history. [docs/history.md](docs/history.md) has all of it, including what
redaction does and does not catch.

**M3 slice 1 — files on disk, environments, and file bodies.** `Ctrl+Shift+O` opens a
folder of `.http` files; `Ctrl+O` opens one; `Ctrl+S` saves, with a dirty marker in the
title. Saving is explicit rather than continuous — a `.http` file is a git artifact, and
rewriting one as you type moves the diff under whoever is reading it.

Environments come from `http-client.env.json` beside the requests, with the secrets in a
gitignored `http-client.private.env.json` — the convention Rider and Visual Studio 2022
already use, so an existing set of environments works unchanged. Opening a folder that
holds a secrets file adds the `.gitignore` entry if it is missing.
[docs/environments.md](docs/environments.md) has the format and the precedence rules.

A body can come from a file: `< ./payload.json` copies the bytes, `<@ ./template.json`
substitutes `{{variables}}` into them first. Because the `.http` format expresses a
multipart body by writing it out with an import per part, that *is* multipart support. An
import may only read files inside the open folder — a request file gets shared, and
`< C:\Users\me\.ssh\id_rsa` is an ordinary thing for one to say.

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

Still to come: a release build (M5).

The exact dialect Sling reads, and every place it differs from the VS Code REST Client,
is written down in [docs/http-dialect.md](docs/http-dialect.md).

## Keys

Every one of these is also a button on the command bar above the panes, and each button
names its own chord in its tooltip — the toolbar is there to make the keyboard findable,
not to replace it. The **File** menu lists the document commands with their gestures
beside them.

| | |
|---|---|
| `Ctrl+Enter` | Send the request under the caret |
| `Ctrl+Shift+Enter` | Send every request in the file |
| `Esc` | Cancel the run, or close settings |
| `Ctrl+O` / `Ctrl+Shift+O` | Open a file / a folder |
| `Ctrl+I` | Import a Postman export |
| `Ctrl+S` / `Ctrl+Shift+S` | Save / save as |
| `Ctrl+N` | New document |
| `Ctrl+Shift+N` | Add a request to the open file |
| `Ctrl+B` | Show or hide the collections rail |
| `Ctrl+F` | Find, in either pane |
| `Ctrl+H` | Show the local history |
| `Ctrl+,` | Settings |

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
- **Cookies are scoped per environment** — a staging cookie never reaches production,
  because the two do not share a jar.
- **Response bodies render as text**, never into a browser control.
- **History stores no bodies, and credentials are redacted before anything is written.**
- **Access tokens and cookies never touch the disk.**
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
