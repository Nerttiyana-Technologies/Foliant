# Releasing

Releasing is **one button**. You never tag by hand, and a mismatched/mistagged release **fails before it
publishes** — the class of bug where `main` says one version but the tag shipped older code is now
impossible. The package version is still derived from the git tag by [MinVer], but the *workflow* creates
that tag on `main`'s HEAD itself, after verifying it matches the CHANGELOG.

## How to release

1. **Land the release on `main` via PR** (the `main` ruleset requires a PR):
   - Add a `## X.Y.Z — …` entry at the **top** of `CHANGELOG.md`, plus the code/docs for the release.
   - Open a PR, let checks pass, merge to `main`.
2. **Cut the release** — either:
   - **GitHub UI:** Actions → **“Release to nuget.org”** → **Run workflow** → Branch `main`, Version `X.Y.Z`.
   - **CLI:** `./scripts/release.sh X.Y.Z`  (a shortcut for the same `workflow_dispatch`).

That’s it. The workflow verifies the version, builds, tests, tags `vX.Y.Z` on `main`, packs, and publishes
to nuget.org via OIDC Trusted Publishing, then creates the GitHub Release. Watch **Actions** (and `gh run
watch`); nuget.org shows a brief “Validating” before the version lists.

**Rehearse first if you want:** `./scripts/release.sh X.Y.Z --dry-run` (or tick *dry_run* in the UI) builds,
tests, and packs **without** tagging or publishing.

## What the workflow guards (so a bad release can’t ship)
- **Runs from `main` only** — won’t release off a feature branch.
- **Version must equal the top `CHANGELOG.md` entry** — if you type `1.4.0` but the changelog tops out at
  `1.3.1`, it stops *before* building. (This is the exact check that would have caught the 1.3.0 misfire.)
- **Tag must not already exist** — NuGet versions are immutable; bump to the next version instead.
- **Tag is created only after build + test pass** — a failing test never leaves a dangling tag.

## Recovery
- **Typed the wrong version / forgot the CHANGELOG entry:** the run fails at a guard and **nothing is
  published**. Land the correct `## X.Y.Z` entry on `main`, then run the workflow again.
- **Already published a bad version** (immutable on NuGet): bump to the next version, land its CHANGELOG
  entry on `main`, release that, and **unlist** the bad one on nuget.org (package → Manage → Listing).
- **Verify what actually shipped:** download the package and inspect the assembly —
  ```bash
  V=1.4.0
  curl -sL https://api.nuget.org/v3-flatcontainer/foliant.templates/$V/foliant.templates.$V.nupkg -o p.nupkg
  unzip -o p.nupkg -d p
  strings p/lib/*/Foliant.Templates.dll | grep -Eo "$V\+[0-9a-f]+"   # version + the git SHA it was built from
  ```
  The SHA must match `git rev-parse main`.

## Mechanics
- **Version source:** the `vX.Y.Z` tag the workflow creates (prefix `v`, via `MinVerTagPrefix` in
  `src/Directory.Build.props`). Untagged builds get a pre-release version; only a real release tags.
- **Hand-pushing a tag does nothing** — the old `on: push: tags` trigger was removed on purpose, so the
  only way to publish is this workflow. CI needs full history (`fetch-depth: 0`) for MinVer.

## One-time GitHub setup
- nuget.org → **Trusted Publishing** policy for this repo + workflow file `release.yml`.
- Repo secret **`NUGET_USER`** = your nuget.org profile name (not your email).
- `main` ruleset: keep **Require a pull request before merging**, **Block force pushes**, and
  **Restrict deletions** on. The release workflow only needs to push *tags* (not to `main`), which the
  branch ruleset allows via its `contents: write` permission.

[MinVer]: https://github.com/adamralph/minver
