# Pasting a curl command

Paste a curl command into the request pane and Sling writes the request for you. Nothing
to click, no import dialog: the paste handler recognises the command and converts it in
place.

**Anything that is not recognisably curl is left completely alone.** A paste handler that
rewrites text is a hostile thing to build if it ever guesses — you pasted something you
had in your hand, and getting it back changed is worse than not having the feature. The
converter answers "not curl" rather than trying.

The system clipboard is not modified either. Paste the same command into another
application afterwards and you still get the curl command.

## Why this is worth more than its size

It is the escape hatch for every request not yet migrated. Postman copies as curl,
browsers copy as curl from the network tab, and API documentation is written in curl — so
this is the shortest path from anywhere into Sling, and the thing that makes dogfooding
possible before the Postman importer exists (`Sling.md` §4b).

## Nothing is dropped silently

Anything the converter cannot express becomes a comment at the top of the request, naming
what was lost. That is the same rule the Postman importer will follow, and the reason is
that a silent drop turns an import into a request that *looks* right and behaves
differently — which is worse than an import that visibly did not finish.

```
# --insecure was NOT applied. Sling verifies TLS certificates and has no global way to
# turn that off; if this endpoint genuinely needs it, the request will fail and say so.

GET https://internal.example.com/health
```

## What is converted

| curl | Becomes |
|---|---|
| the URL, or `--url` | the request target; a missing scheme becomes `https://` and is noted |
| `-X` / `--request` | the method |
| `-H` / `--header` | a header line |
| `-d`, `--data`, `--data-raw`, `--data-binary`, `--data-ascii` | the body; repeated flags join with `&`, as curl does |
| `--data-urlencode` | the body, with the value percent-encoded and the name left alone |
| `-G` / `--get` | the data folded into the query string instead of a body |
| `-I` / `--head` | `HEAD` |
| `-u` / `--user` | `Authorization: Basic …`, **with a warning** — see below |
| `-A`, `-e`, `-b` with a cookie string | `User-Agent`, `Referer`, `Cookie` headers |

The method follows curl's own rule: an explicit `-X` always wins, a body without one
implies `POST`, everything else is `GET`. A copied command that silently became a `GET`
would be a request that looks identical and does nothing.

`Content-Type: application/x-www-form-urlencoded` is written out when there is a body and
no explicit type, because that is what curl sends and does not mention. The document
should say what will actually go on the wire — that is the whole reason the request is a
document.

## What is refused, and why

**`-k` / `--insecure` is not applied.** Sling verifies TLS certificates and has no global
way to turn that off (`Sling.md` §5.3). Quietly accepting the flag would be the worst
outcome available: you would believe verification was disabled when it was not.

**`-u` becomes a real credential in a file that is meant to be committed.** It is
converted rather than dropped — a request that silently loses its credentials fails with a
401 you then debug — but the note above it says so in as many words. Moving it into the
gitignored `http-client.private.env.json` and referencing it as `{{name}}` is manual; see
[environments.md](environments.md).

**`-F` / `--form` fields are named, not converted.** Sling can now *send* a multipart body
— written out with a `< ./file` per part, which is how the `.http` format expresses one —
but the importer still names these fields rather than generating the boundary, the parts
and the imports for them. Converting `-F` is its own piece of work, and a half-right
multipart body is worse than a note saying what was dropped.

**File references are dropped.** `-d @payload.json`, `--data-urlencode name@file` and
`-b cookies.txt` all read from disk, and the importer does no I/O — it is a pure
text-to-text function, which is what makes it testable against a corpus. `-d @payload.json`
now has an exact equivalent you can write by hand — `< ./payload.json` — so the note it
leaves says so.

**An unknown flag is named and does not consume the next token.** Guessing that an unknown
flag takes a value eats the URL, which is the one thing the import cannot do without.
Guessing that it does not leaves a stray value, and Sling prefers a URL candidate that
actually carries a scheme — a recoverable, visible failure instead of a silent one.

Flags that change nothing about the request Sling would send — `-s`, `-L`, `-i`,
`--compressed`, `-o` and friends — are accepted without comment. A note for each would
bury the ones that matter.

## Quoting

Three continuation conventions are understood, because commands get copied from three
places: a trailing `\` (bash), a trailing `^` (Windows `cmd`, which is what Chrome emits
for "Copy as cURL (cmd)") and a trailing backtick (PowerShell). Each counts only at the
very end of a line — a `^` inside a URL is an ordinary character.

An unquoted backslash escapes the next character only when that character is **not** a
letter or digit. The two shell conventions collide here: bash always escapes, while
Windows uses backslash as a path separator, so `C:\tools\curl.exe` has to survive.
Escaping a letter is meaningless in bash — `\t` unquoted is just `t`, never a tab — so
nobody writes it on purpose, while every real use escapes punctuation or a space.

An unterminated quote yields a partial import rather than a refusal. Half a pasted command
is something to fix, not something to reject.

## The security rule

A pasted command is untrusted input: it arrives from a chat message or a web page as often
as from your own shell history.

Nothing is executed. There is no shell here — a quoting parser and a table of flags, with
no file access and no process launch.

And **no value taken from the command may carry a CR or LF into the generated document**.
The `.http` format is newline-delimited, so a header value containing a line break would
become an additional header line: an injection into the artifact you are about to trust
and send. Control characters are stripped from every value. A body keeps its line breaks,
because there a newline is content rather than structure — but a body line beginning
`###` would separate requests in the file, and since nothing can escape that, it is named
instead of being quietly corrupted.
