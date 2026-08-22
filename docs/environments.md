# Environments and secrets

An environment is a set of `{{variable}}` values you can switch between — dev, staging,
prod — so the same request file works against all of them.

Sling reads two files from the root of the open folder:

| File | Committed? | Holds |
|---|---|---|
| `http-client.env.json` | **Yes.** This is the point of it. | Base URLs, versions, account ids — anything a colleague needs and nobody would mind seeing in a pull request. |
| `http-client.private.env.json` | **Never.** Sling adds the `.gitignore` entry itself. | Tokens, keys, passwords. |

Both names are the convention **Rider and Visual Studio 2022 already use**, for the same
reason the `.http` format itself was chosen: someone arriving from either tool keeps the
environments they already have, and someone leaving takes them along.

## The format

```json
{
  "$shared": {
    "version": "v2"
  },
  "dev": {
    "base": "https://dev.api.example.com"
  },
  "prod": {
    "base": "https://api.example.com"
  }
}
```

`$shared` is reserved: its values underlie every environment, so anything that does not
differ per deployment is written once instead of copied into each one and then edited in
three places out of four.

The secrets file has the same shape and the same environment names:

```json
{
  "dev":  { "token": "dev-token-here" },
  "prod": { "token": "prod-token-here" }
}
```

Values are text. A number or `true`/`false` is accepted and used as the text that was
written — a port as `8080` rather than `"8080"` is the commonest thing in one of these
files. Anything else (an object, an array) is reported and skipped rather than guessed at.

Comments and trailing commas are allowed, because this file is written by hand and a note
saying which token belongs to which deployment is exactly what its author wants to leave
in it.

## How a value is chosen

Four layers, each overriding the one before:

1. `$shared` in the committed file
2. `$shared` in the secrets file
3. the selected environment in the committed file
4. the selected environment in the secrets file

Secrets last on both halves, so a placeholder in the committed file is a working default
that the real value replaces — not something that overrides it.

Then, resolving `{{name}}` in a request:

1. a chain reference (`{{login.response.body.$.token}}`)
2. **the selected environment**
3. the document's own `@name = value` lines

The environment beating the file is a deliberate divergence from the VS Code REST Client —
see [http-dialect.md](http-dialect.md) for why. With no environment selected, nothing
changes and file variables behave exactly as they did.

## Switching

The picker sits beside the file name above the request pane, and appears only once an
environment file defines a named environment. (A file holding only `$shared` gets no
picker — its values are still in force, and the status bar says so when the folder loads.)

Switching **forgets every response Sling has stored this session**, which matters: a token
fetched against staging is a valid-looking bearer token, and a chained request that reused
it would send it to production. The same thing happens when the environment you had
selected disappears from the file, and when a value it binds changes — a name that quietly
starts pointing at a different deployment is the same transition arrived at sideways.
Opening a different document forgets them too, since request names are per-file.

Sling re-reads both files whenever its window comes forward, so editing them in another
editor takes effect without restarting anything.

## What keeps the secrets out of git

`Sling.md` §5.1 treats this as structural rather than advisory, because a committed bearer
token is *the* known failure mode of `.http` files in the wild:

- Opening a folder that contains `http-client.private.env.json` **adds the `.gitignore`
  entry if it is missing**, and says so in the status bar. This runs whether Sling created
  the file or found it — the dangerous case is precisely the one it did not create, sitting
  in a repository whose `.gitignore` has never heard of it, one `git add -A` away from
  being public.
- The entry is only ever *appended*. Nothing in your `.gitignore` is reordered, rewritten
  or removed, and a workspace with no secrets file is left alone entirely.
- A secret is never resolvable from the committed file. If a name exists in both, the
  secrets file wins.
- **A request body cannot import an environment file.** `< ./http-client.private.env.json`
  followed by a `POST` would be the shortest route to your credentials there is, so both
  environment files are refused as body imports even though they sit inside the workspace.
- The check runs whenever Sling re-reads the files, not only when the folder is opened —
  nothing in Sling creates the secrets file, so the only way you get one is by writing it
  yourself, after the folder is already open.
