#!/usr/bin/env bash
# Check the wrapper build id.
#
#   check-release-build.sh <tag>   a release: the id is real and describes the tagged code (below)
#   check-release-build.sh --dev   main: the id is still the `dev` placeholder (a stamped release commit was not merged back)
#
# Release checks: C# and Java constants agree; not `dev`; a valid shape; when hash-shaped, that commit is an ANCESTOR of
# HEAD and HEAD differs from it only in the files a release edits (the two constants, CHANGELOG.md, package.json,
# Tools~/versions.env), so the id really names the code that was tagged; and the tag is v<package.json version>
# (a prerelease tag such as v0.1.0-rc1 needs package.json "0.1.0-rc1").
# Run it before `git push --tags`; the release-check workflow repeats it after the push and can only report.
set -euo pipefail

ROOT_DIR="${BUGSEE_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
ARG="${1:-}"

read_const() { # file, regex prefix
  sed -nE "s/.*${2}\"([^\"]*)\".*/\1/p" "${ROOT_DIR}/$1" | head -n 1
}
CS="$(read_const Runtime/BugseePackageVersion.cs 'public const string Build = ')"
JAVA="$(read_const Plugins/Android/UnityWrapperIdentity.java 'static final String BUILD = ')"
fail() { echo "release check failed: $*" >&2; exit 1; }

[[ -n "${CS}" && -n "${JAVA}" ]] || fail "could not read the build constants (C# '${CS}', Java '${JAVA}')"
[[ "${CS}" == "${JAVA}" ]] || fail "C# says '${CS}', Java says '${JAVA}'; stamp both with Tools~/scripts/stamp-wrapper-build.sh"

if [[ "${ARG}" == "--dev" ]]; then
  [[ "${CS}" == dev ]] || fail "the build id is '${CS}' where main keeps 'dev'; a stamped release commit must not be merged into main"
  echo "OK: wrapper build is the dev placeholder"
  exit 0
fi

[[ "${CS}" != dev ]] || fail "the build id is the 'dev' placeholder; run Tools~/scripts/stamp-wrapper-build.sh and commit before tagging"
[[ "${CS}" =~ ^[0-9A-Za-z][0-9A-Za-z._-]*$ ]] || fail "'${CS}' is not a valid build id"

if [[ "${CS}" =~ ^[0-9a-f]{7,40}$ ]]; then
  git -C "${ROOT_DIR}" cat-file -e "${CS}^{commit}" 2>/dev/null \
    || fail "build id ${CS} is not a commit in this repository (shallow checkout, or stamped elsewhere)"
  git -C "${ROOT_DIR}" merge-base --is-ancestor "${CS}" HEAD \
    || fail "build id ${CS} is not an ancestor of the tagged commit (it names a commit on another branch)"
  changed="$(git -C "${ROOT_DIR}" diff --name-only "${CS}" HEAD)"
  while IFS= read -r file; do
    case "${file}" in
      ""|Runtime/BugseePackageVersion.cs|Plugins/Android/UnityWrapperIdentity.java|CHANGELOG.md|package.json|Tools~/versions.env) ;;
      *) fail "build id ${CS} does not describe this commit: ${file} changed after it (stamp again from the release commit's parent)" ;;
    esac
  done <<<"${changed}"
fi

if [[ -n "${ARG}" ]]; then
  VERSION="$(sed -nE 's/^[[:space:]]*"version":[[:space:]]*"([^"]+)".*/\1/p' "${ROOT_DIR}/package.json" | head -n 1)"
  [[ "${ARG}" == "v${VERSION}" ]] || fail "tag ${ARG} does not match package.json version ${VERSION}"
fi
echo "OK: wrapper build ${CS}${ARG:+ for ${ARG}}"
