# Importing from Postman

`Ctrl+I`. Pick your collection export — and any environment exports beside it, in the same
dialog — then pick a folder to import into. Sling writes the `.http` files, writes the two
environment files, opens the folder as a workspace and puts the first document on screen.

Two dialogs and nothing else. There is no wizard and no mapping screen, because what an
import turns into is readable text in files you are about to have open, and reading it
there is a better review than any preview could be.

## Export from Postman first

In Postman: **⋯ next to the collection → Export → Collection v2.1 → Export**. Then, for each
environment you want: **Environments → ⋯ → Export**.

Take the environments. A collection on its own is full of `{{base_url}}` and `{{token}}`
references whose values live in the environment file, so importing one without the other
produces documents that resolve nothing.

## What you get

A collection becomes **a folder of `.http` files** (`Sling.md` §1). There is no collection
tree, no index and no metadata file — grouping is `###` separators inside a file, hierarchy
is directories, and sharing is `git push`.

| In Postman | On disk |
|---|---|
| requests at the collection's root | `<collection-name>.http` |
| a folder `Orders` | `orders.http` |
| a folder `Orders / Refunds` | `orders/refunds.http` |
| collection variables | `http-client.env.json`, under `$shared` |
| each environment export | an environment of the same name |
| anything that looks like a credential | `http-client.private.env.json`, which is gitignored |

Names are lower-cased and reduced to letters, digits, `-` and `_`. Two folders whose names
differ only by case still get separate files, because Windows would otherwise let the second
silently replace the first.

**Nothing already in the destination folder is overwritten.** A file that is already there
is left alone and reported; the rest of the import still lands.

## Credentials never go into a `.http` file

This is the one rule worth knowing before you import anything.

An exported collection routinely carries a live bearer token, a basic password or an OAuth2
client secret in plain text. A `.http` file is meant to be committed, so **every literal
credential is moved into `http-client.private.env.json`** and the request gets a `{{name}}`
instead:

```http
### Get me
GET {{base_url}}/me
Authorization: Bearer {{bearer_token}}
```

Sling adds `http-client.private.env.json` to your `.gitignore` itself, and the import
summary says which file holds the credentials.

**Read both environment files before you commit.** Postman only marks a value secret when
its owner ticked the box, and most people do not — so anything whose *name* reads like a
credential (`token`, `secret`, `password`, `api_key`, …) is treated as one too. That is a
guess, deliberately biased towards the gitignored file, and it will occasionally put
something harmless there.

## Auth

| Postman | Becomes |
|---|---|
| Bearer | `Authorization: Bearer {{bearer_token}}` |
| Basic | `Authorization: Basic {{basic_auth}}`, base64-encoded into the secrets file |
| API key | a header, or a query parameter if that is where the collection had it |
| OAuth 2.0, **client credentials** | a real `# @auth oauth2` block — see [http-dialect.md](http-dialect.md) |
| OAuth 2.0, any other grant | a note; the static access token is carried if there is one |
| No auth | nothing |
| Digest, NTLM, AWS, Hawk, OAuth 1 | a note saying the request is unauthenticated |

Auth is inherited the way Postman inherits it: a request's own block wins, then its folder's,
then the collection's. An explicit **No Auth** is a real answer and stops the search.

**Basic auth built from variables cannot be converted.** A Basic header is base64 of
`user:password`, and that cannot be assembled from `{{username}}` and `{{password}}` at send
time. The request gets `Authorization: Basic {{basic_auth}}` and a note telling you to put
the encoded value in the secrets file; until you do, it refuses to send rather than
authenticating as nobody.

## Bodies

| Postman mode | Becomes |
|---|---|
| **raw** | the body, with the `Content-Type` the language setting implies |
| **x-www-form-urlencoded** | `a=1&b=2`, percent-encoded on both sides of the `=` |
| **form-data** | a real multipart body, written out in full with a `< ./file` per file part |
| **binary** | `< ./file` |
| **GraphQL** | `{"query": …, "variables": …}` as JSON |

A `Content-Type` the collection stated is never overridden.

**Files are not copied.** A collection records the path the file had on the machine it was
exported from, and a body import may only read files inside the workspace — so a file part
becomes `< ./avatar.png` with a note asking you to put that file beside the `.http` file.
Only the bare file name survives; `../../../etc/passwd` imports as `passwd`.

## What is not converted, and why

**Pre-request and test scripts.** Sling does not run scripts — that is a deliberate non-goal
(`Sling.md` §1), not a gap. The script is copied into the document as comments so you can see
what it did, capped at forty lines. **Nothing in a collection is ever executed.**

Postman's most common use of a pre-request script is fetching a token. If that is what yours
does, look at `# @auth oauth2` for client credentials, and at request chaining
(`{{login.response.body.$.access_token}}`) for anything else — both are in
[http-dialect.md](http-dialect.md).

**Saved example responses.** Sling shows real responses only. The count is noted.

**Path variables without a value.** `/orders/:id` with no value in the collection is left as
written, which is what Postman does with an unset one.

Everything the importer cannot do exactly is written into the file it belongs to as a
comment. Nothing is dropped silently — a silent drop turns an import into a request that
*looks* right and behaves differently, which is worse than an import that visibly did not
finish.

## A collection is untrusted input

It arrives as a download, a forward, or a vendor's published link, so the importer treats it
as hostile (`Sling.md` §5.8):

- **Nothing is executed**, including script blocks.
- **No name inside the collection can decide where a file lands.** Folder and request names
  are reduced to letters, digits, `-` and `_`, which makes `..`, `/`, `\` and `:` impossible
  by construction rather than by a list of things to refuse — and the destination is checked
  again before anything is written.
- **No value can write structure into a document.** Control characters are stripped from
  every value; a header value carrying a newline cannot become a second header.
- **A description cannot name a request.** A line beginning `@` would come back as
  `# @name …`, which the parser reads as a directive — so such a line is quoted with `>`.
  Left alone, a crafted description could name a request and have another one send that
  API's token somewhere else.
- **A body containing a line that starts with `###`** would split the document, and nothing
  can escape it — so the body is **not written at all**. It is reproduced as comments, the
  way a script is, and the note tells you to put it in a file and import it with
  `< ./file`. A comment saying "this will not read back correctly" above a body that then
  gets written anyway is not a mitigation: the injected text becomes real requests.

## Limits

An import stops at 5000 requests or 500 files and says so. An export larger than 64 MB is
refused. Folders nested more than six deep share a directory, each keeping a file of its own.
