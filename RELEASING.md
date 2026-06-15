# Releasing

## The golden rule

**Never tag a release from a feature branch. Merge to `main` first, then tag from `main`, and tag last.**

Tagging triggers the publish workflow, so a tag pushed from an unmerged branch ships the package
while `main` still points at the old source. We have hit this repeatedly (1.0.0, 1.0.1): the NuGet
package was live, but `main`'s `Directory.Build.props` still read the previous version and the fix
was missing from `main`. The release is not "done" until `main` contains it.

## Correct sequence (every release, no exceptions)

1. **Branch** off an up-to-date `main`: `git checkout main && git pull && git checkout -b release/x.y.z`
2. **Implement + bump** the version in `Directory.Build.props` (single source of truth) and add the
   `CHANGELOG.md` entry. Commit the version bump *before* tagging — never after.
3. **Prove it green**: `dotnet build Foliant.sln && dotnet test`.
4. **PR → merge to `main`.** `main` is branch-protected; the PR is the only way in.
5. **Switch to main and verify it actually has the change** (see checklist below).
6. **Tag from `main`, last of all**: `git tag vX.Y.Z && git push origin vX.Y.Z`. The release workflow
   packs `Foliant.sln` and pushes to NuGet on the tag.

## Pre-tag verification checklist (run on `main` after the merge)

```
git checkout main && git pull
# version on main matches what you're about to tag:
git show HEAD:Directory.Build.props | grep FoliantVersion
# the specific fix is present on main (example):
git grep -c "TableGridFits" -- src/Foliant.Pipeline/MarkdownComposer.cs
```

## Post-tag verification (confirm the tag came from main, not a branch)

```
git fetch origin
# the tagged commit must be an ancestor of origin/main → "on main", else the release is detached:
git merge-base --is-ancestor "$(git rev-list -n1 vX.Y.Z)" origin/main && echo "on main" || echo "DETACHED — fix before relying on it"
```

If a tag was already pushed from a branch (the mistake), recover by merging that branch into `main`
so `main` catches up; the published package is unaffected, but `main` must end up containing the
tagged source.

## Notes

- `Directory.Build.props` `<FoliantVersion>` is the one place the version lives; `src/` projects and
  the `Foliant.Forms.*` packs all inherit it.
- The branch name is cosmetic; the shipped version is whatever `Directory.Build.props` says on the
  tagged commit. Don't trust the branch name — verify the file.
