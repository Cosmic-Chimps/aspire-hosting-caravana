#!/usr/bin/env bash
# Prepares a release of the NuGet package from CHANGELOG.md.
#
#   tools/release.sh notes VERSION
#   tools/release.sh prepare VERSION
#   tools/release.sh unreleased
#
# prepare turns CHANGELOG.md's "## Unreleased" section into "## VERSION - <today, UTC>" and puts
# a new empty "## Unreleased" above it. The release workflow runs it for a stable version with no
# section yet, whether it was started from the button or by a pushed tag, and commits the rename
# to main (.github/workflows/release.yml). It refuses an empty Unreleased section, a version that
# already has one, and a prerelease version (one with a -suffix): a preview never renames the
# changelog.
#
# notes prints VERSION's section of CHANGELOG.md (the lines under "## VERSION - date", without
# that heading), which the release workflow publishes as the GitHub release's notes. It fails when
# the section is missing or empty, so no release goes out with nothing to say.
#
# unreleased prints the body of the "## Unreleased" section, trimmed the same way. A prerelease
# uses it as its notes ("what is coming in the next release"). It prints nothing, and succeeds,
# when the section is empty: the caller decides what to say then.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

usage() {
  echo "usage: tools/release.sh notes VERSION | prepare VERSION | unreleased" >&2
  exit 2
}

notes() {
  local version="$1" text
  # From the version's heading (exactly "## X.Y.Z" then " - date" or the end of the line) to
  # the next "## ", blank lines at either end trimmed.
  text="$(awk -v v="$version" '
    /^## / { on = ($2 == v && (NF == 2 || $3 == "-")); next }
    on' "$root/CHANGELOG.md" | sed -e '/./,$!d' | sed -e ':a' -e '/^\n*$/{$d;N;ba' -e '}')"
  if [ -z "$text" ]; then
    echo "release: CHANGELOG.md has no section \"## $version - <date>\", or it is empty" >&2
    # The usual cause: the changes are still under "Unreleased". Say how to fix it.
    if grep -q '^## Unreleased' "$root/CHANGELOG.md"; then
      echo "release: rename \"## Unreleased\" to \"## $version - $(date -u +%Y-%m-%d)\" (add a new empty \"## Unreleased\" above it), commit, and tag that commit" >&2
    fi
    exit 1
  fi
  printf '%s\n' "$text"
}

unreleased() {
  local text
  # From "## Unreleased" to the next "## ", blank lines at either end trimmed (as in notes).
  text="$(awk '
    /^## / { on = ($2 == "Unreleased" && NF == 2); next }
    on' "$root/CHANGELOG.md" | sed -e '/./,$!d' | sed -e ':a' -e '/^\n*$/{$d;N;ba' -e '}')"
  if [ -n "$text" ]; then
    printf '%s\n' "$text"
  fi
}

prepare() {
  local version="$1" changelog="$root/CHANGELOG.md" date tmp
  [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] ||
    { echo "release: version $version isn't X.Y.Z or X.Y.Z-suffix" >&2; exit 2; }
  if [[ "$version" == *-* ]]; then
    echo "release: $version is a prerelease; prereleases never rename the changelog" >&2
    exit 2
  fi
  if awk -v v="$version" '/^## / && $2 == v { found = 1 } END { exit !found }' "$changelog"; then
    echo "release: CHANGELOG.md already has a section for $version" >&2
    exit 1
  fi
  if ! awk '/^## Unreleased/ { on = 1; next } /^## / { on = 0 } on && /[^[:space:]]/ { found = 1 } END { exit !found }' "$changelog"; then
    echo "release: CHANGELOG.md's \"## Unreleased\" section is missing or empty: nothing to release" >&2
    exit 1
  fi
  date="$(date -u +%Y-%m-%d)"
  tmp="$(mktemp)"
  awk -v v="$version" -v d="$date" '
    !done && /^## Unreleased[[:space:]]*$/ { print; print ""; print "## " v " - " d; done = 1; next }
    { print }' "$changelog" >"$tmp"
  mv "$tmp" "$changelog"
  echo "CHANGELOG.md: \"## Unreleased\" is now \"## $version - $date\"; commit it, then tag v$version"
}

case "${1:-}" in
prepare)
  [ $# -eq 2 ] || usage
  prepare "$2"
  ;;
notes)
  [ $# -eq 2 ] || usage
  notes "$2"
  ;;
unreleased)
  [ $# -eq 1 ] || usage
  unreleased
  ;;
*) usage ;;
esac
