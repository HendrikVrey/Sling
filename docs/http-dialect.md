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

## Divergences, and why

**Chaining sends the dependency for you.** The reference implementation requires you to
have sent `login` by hand before `{{login.response…}}` resolves; Sling sends it
automatically, once per session, and shows both exchanges in the response pane. Chaining
that only works if you remember to click twice is chaining people stop using. Sling never
hides a request it made on your behalf — every one appears in the response.

**Response values are checked before substitution, and encoded on their way into a URL.**
Two rules, both applying only to values read from a response — never to the text you
typed or to the file variables you wrote.

- **In a header**, a value carrying CR, LF, NUL or another control character is refused
  outright, with an error naming the reference and the character (never the value — it is
  usually a credential). A header field has no escape mechanism to escape it into.
- **In a URL**, the value is percent-encoded. A character check alone is not enough there:
  `@`, `:` and `\` are all legal URL characters, and `@` in particular ends the userinfo
  component — so `https://api.example.com{{next}}/x` with a `next` of `@evil.example.com`
  would send the whole request, `Authorization` header included, to `evil.example.com`,
  on the first request, where no redirect policy can help. Encoding makes the value a
  path or query component rather than syntax.

The practical consequence: **a response value cannot supply a URL, a path with slashes,
or a query string.** `{{page.response.body.$.next_url}}` will not work — it arrives
encoded. Substituting an id, a cursor or a token works exactly as expected. The response
pane always shows the URL that was actually sent, so an encoded value is visible rather
than mysterious.

A URL may not carry userinfo at all — the `user:pass@` part before the host — whoever
wrote it. Send credentials in an `Authorization` header.

The reference implementation does none of this. See `Sling.md` §5.7.

**JSONPath is a subset.** `$`, `.member`, `['member']` and `[index]` (including negative
indexes). Filters, wildcards, slices and recursive descent are refused rather than
silently resolved, because they return *sets* and a request field needs exactly one value
— supporting them would mean inventing a rule for which element gets substituted.

**Only `http` and `https` are sent.** A target or a redirect naming any other scheme is
refused. Sling is a tool for talking to HTTP APIs; `file://` would turn a request into a
local read.

**A body's line endings are normalised to LF.** A CRLF document sends an LF body. It
matters for almost nothing and will matter for multipart, which is M3.

**A body line beginning with `###` is a separator.** No dialect can represent one; write
it as an imported body file when `< ./file` lands (M3).

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

**`{{name.response.body.}}` — a trailing dot — is refused.** It used to walk no path steps
and return the whole body, which quietly sent an entire login response as a header value.
Write `{{name.response.body}}` if the whole body really is what you want.

## Not supported yet

| Construct | Where it lands |
|---|---|
| `> {% script %}` response handlers | Deferred indefinitely — `Sling.md` §2. A scripting runtime is a security surface Sling does not want. |
| `{{$guid}}`, `{{$timestamp}}`, other dynamic variables | Deferred with the above. |
| `< ./file` body imports, multipart bodies | M3 |
| `# @no-redirect`, `# @no-cookie-jar`, `# @prompt` | Parsed and warned about; not honoured. |
| Environments and a base URL | M3 — a relative target is currently an error. |
| Cookies | M3, scoped per environment. |
| `{{name.request.…}}` references | Not planned. |
