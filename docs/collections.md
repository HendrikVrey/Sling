# Collections

Sling shows your workspace as a tree: **collections** (folders), the **request files**
inside them, and the **requests** inside each file. If you are coming from Postman it is
the panel you already know. What is underneath it is not.

```
COLLECTIONS
  billing/                    ← a collection is a folder
    invoices.http             ← a request file
      All requests            ← the whole file
      GET    list invoices    ← a request is a "###" block
      POST   raise an invoice
      DELETE scrap one
    refunds.http
  health.http
```

## There is still no collection format

Nothing in the rail is stored anywhere. There is no manifest, no index, no ordering to
sync and no metadata file. The tree is rebuilt from the folder every time it is drawn, so:

- **Rename a folder in Explorer** and the collection is renamed.
- **`git mv` a file** and it moves, with history.
- **Delete Sling** and you are left with a folder of `.http` files that Rider, Visual
  Studio 2022 and the VS Code REST Client all still open.
- **Review a change to a collection** in a normal pull request, as a text diff.

That is the whole trade Sling makes, and the rail does not take any of it back. What it
adds is the ability to *see* the structure that was always on disk, and to add to it
without leaving the window.

## Opening one

The rail is always there. Until you open a folder it says so, and gives you the buttons:
**Open a folder** (`Ctrl+Shift+O`) and **Import from Postman** (`Ctrl+I`). Point the first
at a directory of `.http` files - very often a checkout of the API's own repository, with
the request files beside the code they exercise.

There is nothing to import and nothing to convert. The folder is the workspace.

The rail can be put away with the toolbar's panel button, or `Ctrl+B`. That is a "get out
of my way" control rather than a preference, so it is not remembered between runs - a
narrow window is the case it exists for.

## Getting around

| Click | What happens |
|---|---|
| A collection | Opens or closes it. |
| A request file | Opens it, showing all of it. |
| **All requests** | Shows the whole file again. |
| A request | Opens its file and shows **only that request**. |

Clicking a request also puts the caret on it, which is the half that matters most:
`Ctrl+Enter` sends the request under the caret, so picking an endpoint and sending it is a
click and a chord. The rail follows the caret the other way too - as you move around a
file, the highlighted row is the request that `Ctrl+Enter` would send.

Requests are read when you open a file's branch, not when you open the folder. A checkout
with three hundred request files in it costs nothing to list. A single file contributes at
most 500 request rows; past that the rail says how many it did not list, because a tree
nobody can scroll is not worth the pause it would cost to draw.

## One request at a time

Click a request and the pane shows that request and the `@variables` above it, and nothing
else. Click **All requests**, or the file's own row, and the whole file comes back.

```
REQUESTS.HTTP                                    2 of 3

  1  @base = https://api.example.com
  2  @token = {{login.response.body.$.access_token}}
  3       ··· 1 request hidden ···
  7  ### Create a user
  8  POST {{base}}/users
  9  Content-Type: application/json
 10
 11  {"name": "Ada"}
 12       ··· 1 request hidden ···
```

Three things about it are worth knowing, because they are what stop it being a mode you
can get stuck in.

**The file is untouched.** This hides lines; it does not change, split or extract
anything. `Ctrl+S` writes the whole file, **Run all** runs the whole file, a chained
`{{login.response...}}` still finds the request it depends on, and the line numbers keep
counting so you can see where you are. Close Sling while narrowed and nothing about the
file records that you ever were.

**The `@variables` at the top stay on screen.** They are what every `{{reference}}` below
resolves against, so a request shown without them would be a request you cannot read.

**Anything that leaves the request brings the file back.** Move the caret out of it, drag
a selection past it, press `Ctrl+A`, or let `Ctrl+F` land on a match further down: the
whole file reappears first. That is deliberate - text you cannot see is text you could
otherwise type over without knowing.

The `2 of 3` beside the file name says which request you are on. Click it to show
everything again.

**With the caret up in the `@variables`, `Ctrl+Enter` and the Auth panel both act on the
request you are looking at**, not on the first one in the file. The label beside the Send
button and the highlight in the rail say which that is, and they always agree.

A file that opens straight into a `###`, with no variables above it, keeps its first line
on screen with the notice beside it. There has to be a line for the notice to sit on.

A file too large for the rail to parse as you type is too large for this as well, and says
so rather than narrowing to a request that might have moved.

## Creating

| Command | How | What it makes |
|---|---|---|
| New collection | **+ Collection**, the File menu, or right-click the rail | A folder, with a `requests.http` already in it. |
| New request file | **+ File**, the File menu, or right-click the rail | An empty `.http` document. |
| New request | **+ Request**, **Ctrl+Shift+N**, the File menu, or right-click the rail | A `###` block appended to the file that is open. |

All three go **into whatever is selected** - select a collection and the new one nests
inside it; select nothing and it lands at the top of the workspace. Right-clicking a row
selects it first, so a command from the context menu acts on the row you right-clicked.

A new collection gets a request file straight away because a collection is a directory,
and a directory with no request files in it is not something the rail can show you.

**A new request goes into the buffer, not onto the disk.** Saving is explicit everywhere
in Sling - `Ctrl+S`, with a `•` in the title while there is something to save - because a
`.http` file is a git artifact and rewriting one behind your back moves the diff under
whoever is reading it.

### Names

What you type is reduced to something a file system can hold: letters, digits, spaces,
`-` and `_` survive, and anything else becomes a `-`. So `Orders - refunds (v2)` becomes
`Orders-refunds-v2`, and there is no way to type a `..`, a `/` or a `:` that survives into
a path. Sling tells you the name it actually used.

A name it cannot use at all is refused with the prompt still open and your text still in
it - nothing is created until it has a name that works.

Nothing is ever overwritten. If a collection or a file of that name is already there, you
are told and nothing is touched.

## What the rail does not do

**No rename and no delete.** Both are real operations on a git working tree with
consequences the rail cannot show you: renaming a folder breaks every `< ./file` body
import that pointed into it, and there is no recycle bin behind a delete. They are one
keystroke away in any file manager, where you have undo, history and time to think - and
`git mv` is better than either.

**No drag to reorder.** Order in the rail is folders first, then files, alphabetically.
Ordering a collection by hand would need somewhere to store the order, which is the
manifest this page is about not having.

**A folder with no request files in it is not shown.** The walk reports files; an empty
directory is not a collection yet.

## See also

- [`environments.md`](environments.md) - the environment picker beside the file name.
- [`postman-import.md`](postman-import.md) - `Ctrl+I`, which writes a folder shaped exactly
  like the one above.
- [`http-dialect.md`](http-dialect.md) - what `###` and `# @name` mean.
