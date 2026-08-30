# local-feed

A folder-shaped NuGet source, and the offline fallback for exactly one package:
**`Etch.Core`**.

## Why this exists

Sling runs [Etch](https://github.com/HendrikVrey/Etch)'s transform engine over
response bodies, and consumes it as a package rather than as a submodule or a copied
folder. That package is **private** - it is not on nuget.org and it never will be,
because Etch's licence forbids making any part of it available to a third party and
a public package is precisely that (see `docs/etch-core-package.md`).

The canonical feed is GitHub Packages, which needs authentication. This folder is
what makes a clone build **without** it: drop the `.nupkg` here and restore finds it.

## Filling it

From a checkout of the Etch repository:

```bash
dotnet pack src/Etch.Core/Etch.Core.csproj -c Release -o <path-to-Sling>/local-feed
```

The version must match the one pinned in `Directory.Packages.props`. If NuGet says
the package cannot be found, that mismatch is the first thing to check - a folder
source is matched by version, not by "whatever is newest".

## What is tracked

Only this file and `.gitignore`. The packages are another repository's build output
and are never committed. The folder itself *is* tracked, deliberately: NuGet treats a
local source whose directory is missing as a restore **error**, so an empty folder
here is the difference between a clone that restores and one that does not.
