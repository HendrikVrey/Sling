# Auth, credentials and tokens

Everything Sling knows about a credential is written in your own files: the `.http` document
says how the request authenticates, and the environment files hold the values. Nothing here
changes that. **Delete Sling and the auth still works**, because it is in the file.

What follows is what Sling adds on top of that text.

## The auth panel

`Ctrl+Alt+A`, or **Auth** beside the environment picker, or right-click in the request pane.

It answers the question that used to take reading three files: *what credential is this
request actually sending, and where does it come from*. The first two lines say which - an
`Authorization` header on line 12, a `# @auth oauth2` block on line 9, or nothing - and
whether the variable it names resolves in the environment you have selected.

The rest of the panel edits it. Pick a kind, fill in the fields, press Apply, and Sling
rewrites the header or the directives **in your document**. Close the panel and what you have
is a `.http` file a colleague can review in a pull request.

| Kind | What it writes |
|---|---|
| No auth | Removes the header or the `@auth` block. Nothing else in the request is touched. |
| Bearer token | `Authorization: Bearer {{token}}` |
| Basic | `Authorization: Basic {{basic_auth}}`, with base64 of `user:password` in the secrets file |
| API key in a header | `X-API-Key: {{api_key}}`, or whichever header you name |
| OAuth2 client credentials | A `# @auth oauth2` block with its directives |

A header whose scheme Sling does not write is shown rather than edited. A tool that silently
reinterprets something you wrote by hand is worse than one that admits it does not know.

### A credential you type never lands in the document

Paste a token into the panel and it goes to `http-client.private.env.json` under the selected
environment, and the request gets `{{token}}`. This is the rule the Postman importer already
holds - an imported document is meant to be committed and a live token is not - applied to
writing one by hand.

The panel says so before it does it, and names the variable it is about to create.

## Environments and secrets

`Ctrl+E`, or **Edit** beside the environment picker. See
[environments.md](environments.md) - that is where a credential is created, and the auth
panel writes through it.

When a request fails because a `{{variable}}` does not resolve, the status bar offers
**Define <name>** and opens that card with the name filled in. If the name was used in an
`Authorization` header or an auth directive it arrives with the secret toggle already on,
because a name missing from one of those is a credential far more often than not.

## The token chip

Beside the environment picker, once a token has been fetched: `token · 12 min`, `3 tokens ·
4 min`, `token spent`. Click it for the list.

The list shows each token by **grant** - client id, scope, audience, token endpoint - with
when it was fetched and how long it has left. **It never shows a token value.** The grant and
the clock are what answer "why did that 401": a stale token, a wrong scope, and a token
fetched against the other environment all produce the same status code and are told apart
only here.

**Forget them** drops every cached token, for when you know a secret has been rotated at the
far end before the server says so.

### Remembering them across restarts

On by default, and switchable off in Settings. A token is:

- **encrypted with Windows data protection** under your account, with the scope mixed into
  the entropy - so a store written for staging does not decrypt for production even if the
  file is copied across;
- **scoped per folder and environment**, exactly as the in-memory cache is;
- **stored without any client secret**. Tokens are identified by a hash over every field of
  the grant, so rotating a secret stops the stored token matching at once and the secret is
  never written down, not even inside the encrypted blob.

Switching the setting off deletes what was already stored. A setting that stops adding to a
pile of credentials without removing the pile is not what anybody switching it off is asking
for.

**It is an accelerator and nothing more.** Delete the store and you lose one round trip to
the token endpoint; the grant is still in your `.http` file and the credential is still in
your environment file.

## When a 401 arrives

If the request used a token Sling fetched, and that token came from the cache, Sling discards
it, fetches another and sends once more. Both attempts are in the response picker, the second
labelled `retry after refresh`. See
[http-dialect.md](http-dialect.md#tokens) for the three boundaries on that.

Every call Sling makes on your behalf is labelled the same way: `sent for you` for a chained
dependency, `token request` for a token exchange.

## JWTs

Right-click a token in a response body and Sling offers to **decode** it, when it is one -
three base64url segments with a JOSE header, not merely a string with dots in it.

Before a send, if the `Authorization` header carries a JWT whose `exp` has already passed,
the status bar says so: *this bearer token expired 41 minutes ago*. It is a note, not a
refusal - you may well be sending it precisely to see the 401.

**Nothing here says a token is valid, and nothing ever will.** No signature is verified.
Doing so means fetching JWKS, holding keys, and then telling you a token is trustworthy,
which depends on issuer and audience policy Sling does not know. Saying "valid" wrongly is
worse than saying nothing, and the question people actually have is when it expires and what
is in it.

## Completion

`Ctrl+Space` in the request pane offers the directive names, the standard verbs, the header
names, every variable the selected environment defines, and a `{{name.response.body.$.}}`
stub for each named request in the file.

The auth block is why it exists. Six directive names, and the rule that any of them without
`# @auth oauth2` above it is an error rather than a comment, is more than anybody should have
to remember.

## Chaining by pointing

Right-click a value in a response body and take **Copy as chain reference**. Sling builds
`{{name.response.body.$.path}}` from the position in the parsed body and the `# @name` of the
request that produced it - and offers to add that name if the request has none, because a
reference against an unnamed request is the commonest way chaining fails.

## What is deliberately absent

- **Signature verification.** See above.
- **A response-handler scripting runtime.** `> {% script %}` means an eval sandbox, which is
  a different risk class from everything else in the product. Named requests plus JSONPath
  cover the workflow, and the right-click above makes writing one a click.
- **A global "ignore certificate errors".** `Sling.md` §5.3 allows a TLS bypass only per
  request and with loud indication, and the surest way to hold that line is for the setting
  that could weaken it not to exist.
