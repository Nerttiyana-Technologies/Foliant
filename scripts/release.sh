#!/usr/bin/env bash
# Cut a Foliant release. The package version is the git tag (MinVer) — there is NO version file to
# bump and no merge-before-tag dance. This just tags main HEAD and pushes; release.yml then builds
# the version from the tag, tests, packs, and publishes to nuget.org.
#
#   ./scripts/release.sh 1.2.0
#
# Guards: refuses unless you're on an up-to-date main and the tag doesn't already exist.
set -euo pipefail
VER="${1:?usage: release.sh X.Y.Z}"
[[ "$VER" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] || { echo "FAIL: '$VER' is not X.Y.Z[-pre]"; exit 1; }
TAG="v$VER"

br="$(git rev-parse --abbrev-ref HEAD)"
[ "$br" = main ] || { echo "FAIL: on '$br', not main — releases tag main."; exit 1; }

git fetch origin --tags --quiet
git rev-parse -q --verify "refs/tags/$TAG" >/dev/null && { echo "FAIL: tag $TAG already exists (NuGet versions are immutable — bump instead)."; exit 1; }
[ "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" ] || {
  echo "FAIL: local main != origin/main. Push/pull so the tag lands on the published HEAD."; exit 1; }

grep -qiE "(\[$VER\]|## $VER|# $VER)" CHANGELOG.md 2>/dev/null \
  || echo "WARN: no CHANGELOG.md entry found for $VER — add one before releasing (continuing anyway)."

echo "Tagging $TAG at $(git rev-parse --short HEAD) on main…"
git tag "$TAG"
git push origin "$TAG"
echo
echo "Pushed $TAG. release.yml will build (MinVer → $VER), test, pack, and publish to NuGet."
echo "Watch: GitHub Actions → 'Release to nuget.org'  +  nuget.org (brief 'Validating' then listed)."
