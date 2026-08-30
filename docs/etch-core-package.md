# Depending on `Etch.Core`

Sling's response pane is not a viewport - it is an editor buffer that can format,
fold, search and transform what came back. The engine that does that already exists
in [Etch](https://github.com/HendrikVrey/Etch), and Sling consumes it as the NuGet
package **`Etch.Core`**.

A package rather than a submodule or a copied folder, decided 2026-08-20: a submodule
makes two repositories move as one and puts Etch's whole build in Sling's path, and a
copy is a fork that drifts. A version number is the smallest honest description of
"which engine is in this build", and the `Sling.deps.json` in a release says it out
loud.

## The feed is private

**`Etch.Core` is not on nuget.org, and will not be.** Etch is source-available, not
open source, and its licence §3(b) forbids making any part of it available to a third
party - which is precisely what a public package does, since every consumer's build
output embeds `Etch.Core.dll`. Etch's `docs/packaging.md` has the full argument.

For Sling this has one practical consequence: **a fresh clone cannot restore from the
internet alone.** There are two ways to give it the package, and `NuGet.config`
declares both.

### 1. `local-feed` - no credentials, and what a dev machine uses

A folder-shaped NuGet source in this repository. Fill it from a checkout of Etch:

```bash
dotnet pack src/Etch.Core/Etch.Core.csproj -c Release -o ../Sling/local-feed
```

The version must match the one pinned in `Directory.Packages.props`. A folder source
matches on version, not on "whatever is newest", so a mismatch reads as "package not
found" rather than as anything more helpful.

Only the folder is tracked, never its contents - see `local-feed/README.md` for why
the folder itself has to exist.

### 2. `etch-github` - GitHub Packages, the canonical feed

```
https://nuget.pkg.github.com/HendrikVrey/index.json
```

Authenticate once, into your **user-level** NuGet config. Never into this
repository's `NuGet.config`, which is committed:

```bash
dotnet nuget add source https://nuget.pkg.github.com/HendrikVrey/index.json --name etch-github --username HendrikVrey --password <CLASSIC_PAT> --store-password-in-clear-text
```

**The token must be a classic personal access token with `read:packages`.** GitHub
Packages does not accept fine-grained tokens - worth knowing before minting one and
wondering why it 401s.

## How CI gets it

`.github/workflows/ci.yml` supplies credentials for `etch-github` into the runner's
throwaway checkout, then restores. It prefers an `ETCH_PACKAGES_TOKEN` secret and
falls back to the workflow's own `GITHUB_TOKEN`.

`GITHUB_TOKEN` is scoped to *this* repository while the package is published from the
Etch repository, so it only works once the package's **Manage Actions access** grants
this repository Read. Until that is done, `ETCH_PACKAGES_TOKEN` (a classic PAT with
`read:packages`) is the working path.

## Why the source mapping is not decoration

`NuGet.config` maps the `Etch.*` pattern to those two sources and everything else to
nuget.org.

The id `Etch.Core` is **unclaimed on nuget.org**. Without the mapping, anyone could
register it there, and the next restore on a machine whose cache did not already hold
the real one would resolve theirs - a textbook dependency-confusion substitution, into
a tool that handles bearer tokens. The mapping means nuget.org is never asked about
`Etch.*` at all.

## Where it is referenced, and where it is not

`Sling.App` only.

`Sling.Core` and `Sling.Import` are held to zero package references by
`ArchitectureTests`, and that rule stays intact here. Etch.Core would arguably qualify
on merit - it is pure, has no dependencies, does no I/O and is AOT-clean - but it does
not need the exception: highlighting, folding, the find bar and the transform palette
are all editor concerns, and the editor lives in `Sling.App`. Sling.md §3 said so
before any of it was written.

If a future need does put a transform in `Sling.Core` - response redaction is the
plausible one - that is a deliberate amendment to `ArchitectureTests` with a written
reason, not a quiet edit.

## Upgrading

1. Pack the new `Etch.Core` from Etch, or let Etch's release workflow push it.
2. Bump the version in `Directory.Packages.props`.
3. Refill `local-feed`, or delete the stale `.nupkg` there so nothing resolves to it
   by accident.

There is no floating version range on purpose. An engine change should show up as a
line in a diff.
