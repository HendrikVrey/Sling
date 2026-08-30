# The `.http` dialect Sling reads

Sling's reference dialect is the **VS Code REST Client's**, chosen because it is the one
Visual Studio 2022 and Rider were written against. The three existing implementations
disagree in corners, so every place Sling differs is written down here rather than left
to be discovered from a bug report.

This file describes what the parser in `Sling.Core` actually does. If it and the code
disagree, the code is right and this file is a bug.

## Supported

| Construct | Notes |
|---|---|
| `###` separators | Text after the hashes is the request's title. Documentation only. |
| Request line | `METHOD target [HTTP/1.1]`, or a bare URL (implies `GET`). |
| Headers | `Name: value`, up to the first blank line. |
| Body | Everything from the blank line after the headers to the next `###`. |
| `@name = value` | File-scoped variables. May reference other variables. |
| `{{name}}` | Substitution into the target, header names, header values and the body. |
| `# @name login` | Names a request so later requests can chain against it. |
| `{{login.response.body.$.path}}` | JSONPath into an earlier response's body. |
| `{{login.response.headers.Name}}` | A header from an earlier response. Case-insensitive. |
| Query continuation | An indented line starting with `?` or `&` joins the request line. |
| Comments | `#` or `//` at the start of a line, outside a body. |
| `< ./file` | Body import: the file's bytes, verbatim. Relative to the request file. |
| `<@ ./file` | Body import read as text, with `{{variables}}` substituted inside it. |
| `<@utf16 ./file` | The same, naming the encoding to read the file as. |
| Multipart bodies | Written out in full with a `< ./file` per part - there is no separate syntax. |
| `# @auth oauth2` | An OAuth2 client-credentials grant. Sling's own; see below. |

## Divergences, and why

**Chaining sends the dependency for you.** The reference implementation requires you to
have sent `login` by hand before `{{login.response…}}` resolves; Sling sends it
automatically, once per session, and shows both exchanges in the response pane. Chaining
that only works if you remember to click twice is chaining people stop using. Sling never
hides a request it made on your behalf - every one appears in the response.

**Response values are checked before substitution, and encoded on their way into a URL.**
Two rules, both applying only to values read from a response - never to the text you
typed or to the file variables you wrote.

- **In a header**, a value carrying CR, LF, NUL or another control character is refused
  outright, with an error naming the reference and the character (never the value - it is
  usually a credential). A header field has no escape mechanism to escape it into.
- **In a URL**, the value is percent-encoded. A character check alone is not enough there:
  `@`, `:` and `\` are all legal URL characters, and `@` in particular ends the userinfo
  component - so `https://api.example.com{{next}}/x` with a `next` of `@evil.example.com`
  would send the whole request, `Authorization` header included, to `evil.example.com`,
  on the first request, where no redirect policy can help. Encoding makes the value a
  path or query component rather than syntax.

The practical consequence: **a response value cannot supply a URL, a path with slashes,
or a query string.** `{{page.response.body.$.next_url}}` will not work - it arrives
encoded. Substituting an id, a cursor or a token works exactly as expected. The response
pane always shows the URL that was actually sent, so an encoded value is visible rather
than mysterious.

A URL may not carry userinfo at all - the `user:pass@` part before the host - whoever
wrote it. Send credentials in an `Authorization` header.

The reference implementation does none of this. See `Sling.md` §5.7.

**An environment value beats a file variable of the same name.** The reference dialect
gives the file precedence; Sling gives it to the selected environment. The other way
round, a document containing `@base = https://api.example.com` cannot be pointed at
staging without editing the very line the environment exists to replace - which makes
environments decorative. File variables still resolve normally for every name the
environment does not define, and with no environment selected nothing changes.

Resolution order is: a chain reference, then the environment, then the document's
`@name = value` lines.

**A body's line endings are preserved exactly as written.** A CRLF document sends a CRLF
body. This changed with body imports: multipart separates its parts with CRLF by
specification (RFC 2046), and a multipart body typed on Windows and silently rewritten to
LF is rejected by strict servers for a reason nothing in the document could explain.

The same rule cuts the other way, so Sling **warns** when a `multipart/*` request's body
has LF endings - a repository carrying `*.http text eol=lf` in its `.gitattributes`, or a
file written on Linux, produces exactly the body this change set out to prevent. It is a
warning and not a rewrite: normalising every terminator would also rewrite the *content*
of a text part, and a part whose author wanted LF is entitled to it.

**Saving always writes BOM-free UTF-8.** A document opened from a UTF-16 file, or one with
a byte order mark, is re-encoded when you press `Ctrl+S` - which is a large and otherwise
unexplained diff. Sling opens request files up to 16 MB.

**A body import must be relative, and may only read files inside the workspace.** An
absolute path is refused outright; a relative one is resolved against the request file and
must land under the open folder - after following links, and links on the folders along the
way, not only as written. **The environment files are refused as well**, even though they
sit inside the workspace: `< ./http-client.private.env.json` followed by a `POST` is the
shortest path there is to your credentials. Reference their values as `{{name}}` instead. A `.http`
file is something people share, paste from a colleague, or generate by importing somebody
else's Postman collection, so `< C:\Users\me\.ssh\id_rsa` followed by a `POST` elsewhere is
an ordinary document rather than an exotic attack. The way to send a file from outside the
workspace is to move it in, which is a decision made in a file manager with time to think.
There is deliberately no "allow this file?" prompt, because that prompt is answered yes.

**`< ./file` copies bytes; `<@ ./file` substitutes into text.** Only the second reads the
file as text and expands `{{variables}}` inside it, and only the first can carry a PNG,
decoding arbitrary bytes as text and re-encoding them replaces the invalid sequences and
hands the server a corrupt file. An encoding may only be named on the `<@` form, because
it says how to *read* the file and the raw form never reads it. What is sent is always
UTF-8: the encoding names the file's, not the wire's. A byte order mark is consumed by
`<@` - it is not text content, and a JSON body beginning with one is refused by most
servers - and kept by `<`, where verbatim means verbatim.

The encodings available are `utf-8`, `utf-16`, `utf-32` and `latin1`. Legacy Windows code
pages such as `windows-1252` are **not** registered, so naming one is an error rather than
a silent fallback.

A `{{variable}}` inside an imported file may itself be a chain reference. The dependency
is sent and the file is then read again - so an imported body is read once per resolution
pass rather than cached, which is also the honest behaviour, since the file may have
changed in between.

**A line is a body import only when whitespace follows the marker.** `< ./file` and
`<@ ./file` are imports; `<?xml version="1.0"?>`, `<html>` and `<root>` are body text. Two
of the commonest body formats begin with `<`, and without the whitespace rule they would
be read as imports of files that do not exist.

**JSONPath is a subset.** `$`, `.member`, `['member']` and `[index]` (including negative
indexes). Filters, wildcards, slices and recursive descent are refused rather than
silently resolved, because they return *sets* and a request field needs exactly one value
 -  supporting them would mean inventing a rule for which element gets substituted.

**Only `http` and `https` are sent.** A target or a redirect naming any other scheme is
refused. Sling is a tool for talking to HTTP APIs; `file://` would turn a request into a
local read.

**A body line beginning with `###` is a separator.** No dialect can represent one; put
that body in a file and import it with `< ./file` instead.

**`@name = value` is recognised only before a request line.** After one, it is body text.
The reference implementation is looser about placement.

**A lowercase method is upper-cased**, and an unrecognised all-letters method is sent as
written with a warning. Extension verbs exist and refusing them would be wrong; silently
accepting a typo would be worse.

**Unknown `# @directive` lines are warned about, not ignored.** A directive that does
nothing silently is worse than one that says it does nothing. The warning appears in the
response pane alongside the response.

**A name must be unique, and a request has one name.** Two requests claiming the same
`# @name`, or two `@name` lines on one request, are errors. Left unreported, the first
case is the worst thing this format can do: chain resolution would look up one request
and substitute a value from another.

**`# @name` may sit above the request line or among its headers**, and names the request
it belongs to either way.

**`{{name.response.body.}}` - a trailing dot - is refused.** It used to walk no path steps
and return the whole body, which quietly sent an entire login response as a header value.
Write `{{name.response.body}}` if the whole body really is what you want.

**`# @auth oauth2` is Sling's own, because the reference dialect has no syntax for it.**
It declares an OAuth2 **client-credentials** grant on the request above which it sits:

```http
# @auth oauth2
# @token-url {{auth_base}}/oauth2/token
# @client-id {{client_id}}
# @client-secret {{client_secret}}
# @scope orders.read orders.write
GET {{base}}/orders
```

Sling fetches a token, attaches `Authorization: Bearer …`, and sends the request. The
token exchange appears in the response pane as an exchange of its own, like any request
Sling makes on your behalf.

- `@token-url`, `@client-id` and `@client-secret` are required; the rest are optional.
- `@scope` is sent as-is - space-separated for several scopes.
- `@audience` is not in RFC 6749. It is how Auth0 and several others name which API the
  token is for; omit it if your server does not want one.
- `@client-auth` is `basic` (the default, RFC 6749 §2.3.1) or `body`. Use `body` for a
  server that will not accept HTTP Basic.
- One directive per parameter rather than a positional line, because the positional form
  puts a client id and a client secret next to each other with nothing but order telling
  them apart - and getting that wrong sends the secret as the id.
- Any of these directives written **without** `# @auth oauth2` above it is an error, not
  an ignored comment. A document that quietly does not authenticate fails at the API,
  several layers from the line that caused it.

**The token URL must be `https`**, or `http` to a loopback address. A client secret and
the token it buys are the two most valuable strings in the process, and plain HTTP puts
both on the wire in clear; the loopback exception is the same rule browsers use for a
secure context, so a mock authorization server still works.

**Tokens are cached until they expire and are never written to disk.** The cache is keyed
by the token URL, client id, client secret, scope and audience together - so asking for a
different scope fetches a different token, and rotating a secret takes effect at once
rather than at the old token's expiry. A response with no `expires_in` is not cached at
all: RFC 6749 only recommends the field, and inventing a lifetime for a server that did
not state one produces a run of 401s partway through a session from a cache you cannot
see. Switching environment or opening another document drops every cached token, for the
same reason it drops stored responses.

**A token request is never redirected.** `@token-url` is checked for being HTTPS once,
before the request goes out, and that check covers exactly one hop - so a 3xx from the
token endpoint is reported rather than followed. Following it would hand the client secret
to whoever the `Location` names (307 and 308 carry a body across an origin change
untouched, and under `@client-auth body` the secret *is* the body), and would let that host
mint the bearer token attached to your real request. Put the final URL in `@token-url`.
Ordinary requests follow redirects as before.

**A 401 on a cached token refreshes it and sends once more, and shows that it did.** A
token can stop being honoured before its stated expiry - a rotated secret, a revoked
client, a session ended at the far end - and the clock cannot see that. So Sling discards
the cached token, fetches another, and sends the request again.

Three boundaries, and each one is what keeps this from hiding a signal:

- **Only where Sling owns the token.** A bearer token you wrote in the document is yours,
  and a 401 on it is news rather than something to paper over.
- **Only on a token that came from the cache.** A token minted seconds ago and refused is
  one the server is refusing for a reason a refresh will not fix.
- **Once.** A retry that is also refused is the answer.

**And it is never silent.** Both attempts are in the exchange picker, the second labelled
`retry after refresh`, so what you see is 401, refreshed, 200 rather than a success you
cannot account for. Every call Sling makes for you is labelled the same way: a chained
dependency reads `sent for you`, a token exchange reads `token request`.

**Cookies are stored and replayed per environment.** A `Set-Cookie` on a response goes
into a jar scoped to the selected environment, and matching cookies are attached to later
requests by RFC 6265's rules - domain, path and `Secure` - so a cookie set by staging
cannot reach production. A `Cookie` header written in the document wins outright and the
jar is not consulted for that request. Cookies live in memory only: closing Sling,
switching environment or opening another document discards them. `docs/history.md` has
the rest.

## Not supported yet

| Construct | Where it lands |
|---|---|
| `> {% script %}` response handlers | Deferred indefinitely - `Sling.md` §2. A scripting runtime is a security surface Sling does not want. |
| `{{$guid}}`, `{{$timestamp}}`, other dynamic variables | Deferred with the above. |
| `# @no-redirect`, `# @no-cookie-jar`, `# @prompt` | Parsed and warned about; not honoured. |
| A relative request target | Not planned. Write the scheme and host, or put a base URL in an environment and use `{{base}}`. |
| OAuth2 authorization-code flow | Not planned. It needs a browser, a redirect listener and a consent screen, which is a different product. Send a token you already have as a header. |
| `{{name.request.…}}` references | Not planned. |
