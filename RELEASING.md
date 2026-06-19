# Releasing

The package version is **derived from the git tag by [MinVer]** — there is **no version file to bump**
and **no merge-before-tag dance**. The tag *is* the version, so the whole class of "main says 1.0.2 but
I tagged 1.1.0" mistakes is gone. `main` allows direct pushes (force-push and branch deletion stay
blocked), so a solo release needs no PR.

## Release in one command

```bash
# 1. commit your changes to main and push  (direct push is allowed)
git add -p && git commit -m "…" && git push

# 2. add a CHANGELOG.md entry for the new version

# 3. cut the release
./scripts/release.sh 1.2.0
```

`release.sh` tags `v1.2.0` on `main` HEAD and pushes it. The tag triggers `release.yml`, which builds
the version straight from the tag (MinVer → 1.2.0), tests, packs, and publishes to nuget.org via OIDC
Trusted Publishing. Then watch **Actions → "Release to nuget.org"** and **nuget.org** (a brief
"Validating" before the version lists).

## What the script guards against
- Not on `main`, or local `main` out of sync with `origin/main` (so the tag lands on the published HEAD).
- A tag that already exists (NuGet versions are immutable — bump to the next patch instead).
- Missing CHANGELOG entry (warning).

## Mechanics & recovery
- **Version source:** the nearest `vX.Y.Z` tag (prefix `v`, set via `MinVerTagPrefix` in
  `src/Directory.Build.props`). Untagged builds get a pre-release version; only tags publish.
- **CI needs full history:** both workflows check out with `fetch-depth: 0` so MinVer can see tags.
- **Wrong tag?** Delete and re-tag the right commit, then push:
  ```bash
  git tag -d vX.Y.Z && git push origin :refs/tags/vX.Y.Z
  git tag vX.Y.Z <commit> && git push origin vX.Y.Z
  ```
  The workflow re-runs on the new tag push. (If it doesn't, Actions → release workflow → Run workflow.)

## One-time GitHub setting
Settings → Rules → the `main` ruleset: keep **Block force pushes** and **Restrict deletions** on, but
turn **off** "Require a pull request before merging" so the maintainer can push to `main` directly.

[MinVer]: https://github.com/adamralph/minver
