# History, cookies and settings

The three things Sling keeps *about* requests rather than in them. None of them lives in
your workspace: a workspace is a git checkout, and Sling's own state is not a fact about
your API. It all sits in `%LOCALAPPDATA%\Sling`.

| What | Where | Survives closing Sling |
|---|---|---|
| Settings | `%LOCALAPPDATA%\Sling\settings.json` | yes |
| History | `%LOCALAPPDATA%\Sling\history.jsonl` | yes |
| Cookies | memory only | **no** |
| OAuth2 access tokens | memory only | **no** |

## History — `Ctrl+H`

Every completed exchange is recorded, including the ones Sling sends on your behalf to
satisfy a chain or fetch a token. `Ctrl+H` renders the log into the response pane, newest
first, where `Ctrl+F` and the rest of the editor already work on it.

**No bodies are stored. History records the exchange, not the payload.** A login response
body *is* the token, and redacting an arbitrary body means recognising credentials in
JSON, XML, form encoding and whatever else a server sends — a guess that fails silently in
the direction that matters. Storing no body at all cannot leak one. The bodies of the
current session are in the response pane, where they belong and where they are not at
rest.

### What gets redacted

Two independent rules, because neither covers the other.

**By provenance.** Every value your `http-client.private.env.json` supplied, plus every
access token fetched this session — cached or not — is removed wherever it appears: in a
header, in a URL, in the middle of a longer string. This is the exact half. It needs no
guessing about which header names are credentials, and it catches an API key that reached
a query parameter, where no name-based rule is looking.

A URL holds the *escaped* form, so a secret containing an accent or a space does not
literally appear in it. Each component is therefore also unescaped and checked, and if the
secret is in there the whole component goes — there is no way to splice a replacement back
into escaped bytes and be sure of the boundaries, and losing one path segment or one
parameter value is the right price.

Values shorter than eight characters are left alone, because a secrets file legitimately
holds a tenant id of `7`, and redacting every occurrence of a two-character string turns
an entry into a row of markers.

**By header name.** `Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie` and the
common API-key headers are replaced whole, whatever their value. This is a deny-list and
is admitted as one; it is here to catch a credential typed straight into the document
rather than referenced from the secrets file. Query parameters named `access_token`,
`client_secret`, `password` and a few others are treated the same way. URL fragments are
dropped entirely — they are never sent to a server anyway, and implicit-flow tokens live
in them.

**What is not caught:** a credential typed literally into the document, in a header nobody
has heard of, and not referenced from the secrets file. That is also a credential sitting
in a file that gets committed, which is the larger problem — put it in
`http-client.private.env.json` and both are solved.

Nothing is shortened or hinted at. No prefix, no last four characters, no length: the
first eight characters of a token are enough to identify which credential it is, and a
history file is a place a screenshot comes from.

### Bounds

The file keeps the most recent *n* entries — 500 by default, settable — and the cap is
exact rather than approximate. Switch recording off in settings, or clear the file from
there.

## Cookies

A `Set-Cookie` on a response goes into a jar, and matching cookies are attached to later
requests. The rules are RFC 6265's:

- **Domain.** A cookie with no `Domain` attribute goes back only to the exact host that
  set it. One with a `Domain` may only widen to a domain that covers the setting host, at
  a label boundary — so `notexample.com` does not receive `example.com`'s cookies, and a
  single-label `Domain=com` is refused outright.
- **Path.** A cookie scoped to `/foo` reaches `/foo` and `/foo/bar` and **not** `/foobar`,
  which is a different resource. (This is why Sling does not use the framework's cookie
  container, whose path handling is a prefix match.)
- **`Secure`.** Never sent except to a secure context, and a `Secure` cookie *set* from
  one that is not is refused — which costs nothing real, since a correct client could
  never send it back to that origin, and refusing it is what stops cookie forcing.
  Loopback counts as secure, so a local development server issuing `Secure` session
  cookies still works; it is the same rule the OAuth2 token endpoint uses.
- **`__Host-` and `__Secure-` prefixes** are enforced, case-insensitively as RFC 6265bis
  specifies. A name that promises something the attributes do not deliver is refused.
- **`Expires` is read by its date, and its day name is ignored** — RFC 6265 §5.1.1 never
  looks at the day name, and validating it would turn a server whose day name is wrong
  into a cookie that never expires, and a server's own logout into one that is ignored.

**One jar per environment.** A cookie set by staging cannot be sent to production, because
the two do not share storage. Switching environment, opening a different document, opening
a different folder, or turning cookies off in settings discards the jar entirely — and
each of those drops the cached access tokens with it.

**Memory only.** Cookies are never written to disk. A session cookie in an API client
exists to carry a login across the requests of one working session, and keeping it after
the process exits would buy a small convenience for a credential sitting at rest in a
file.

A `Cookie` header written in the document wins outright: the jar is not consulted for that
request, because appending stored cookies to a header you wrote would send the session you
were trying to override. `Show cookies` in the settings panel lists what the jar holds —
scopes and expiries, never values.

**Known limitation, stated rather than papered over:** there is no public suffix list.
RFC 6265 says a `Domain` that is a public suffix must be refused, which is what stops
`evil.co.uk` setting a cookie for `Domain=co.uk`. Sling refuses a single-label domain and
scopes the jar per environment, so the blast radius is one environment's requests — but it
is not a browser-grade boundary and should not be relied on as one.

## Settings — `Ctrl+,`

| Setting | Default | What it bounds |
|---|---|---|
| Request timeout | 100 s | A whole exchange, including redirects. |
| Largest response held | 16 MB | Above it the body is kept as a prefix and flagged. |
| Redirects followed | 10 | `0` follows none, which is a fine way to inspect what a redirect says. |
| Cookies | on | Store and replay them at all. |
| History | on | Record exchanges to disk. |
| History entries kept | 500 | |

Changes apply immediately and are saved as you make them; there is no OK and nothing to
revert. The file is plain JSON and can be edited by hand — comments and a trailing comma
are tolerated, a value out of range is clamped rather than refused, and a file that will
not parse falls back to the defaults with a message saying so.

**There is no setting for TLS validation, and there will not be one.** A global "ignore
certificate errors" is switched on once in frustration and left on for a year. A per
request opt-in, indicated loudly while active, is what `Sling.md` §5.3 allows; until that
exists, certificate validation is simply always on.

## Run everything — `Ctrl+Shift+Enter`

Sends every request in the file, in order, in one run: stored responses are shared, so a
chain dependency already satisfied is not sent twice, and every exchange lands in one
picker. A request whose own lines hold an error is skipped and reported; a request that
fails does not stop the rest, because half a file sent and half not is the worst outcome
to be left with. `Esc` stops the run.
