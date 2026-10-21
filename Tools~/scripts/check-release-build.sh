#!/usr/bin/env bash
# Check the wrapper build id.
#
#   check-release-build.sh <tag>   a release: the id is real and describes the tagged code (below)
#   check-release-build.sh --dev   main: the id is still the `dev` placeholder (a stamped release commit was not merged back)
#
# Release checks: C# and Java constants agree; not `dev`; a valid shape; when hash-shaped, that commit is an ANCESTOR of
# HEAD and HEAD differs from it only in what a release edits (CHANGELOG.md and the build/version lines of the two constant
# files, package.json and Tools~/versions.env), so the id really names the code that was tagged; the package.json, C#, Java and versions.env versions agree, and the tag is v<that version>
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

if [[ -n "${ARG}" ]] && ! git -C "${ROOT_DIR}" diff --quiet HEAD; then
  fail "uncommitted changes: the tag lands on HEAD, so check what is committed (commit first)"
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
    # Besides CHANGELOG.md, a release may change only the quoted build and package version values on those lines of these files; anything else on a line counts;
    # the native SDK pins and Maven coordinates beside them are code.
    case "${file}" in
      "") continue ;;
      CHANGELOG.md) continue ;;
      Runtime/BugseePackageVersion.cs) filter='s/((public const string Build|public const string Version) = ")[^"]*"/\1"/' ;;
      Plugins/Android/UnityWrapperIdentity.java) filter='s/((static final String BUILD|static final String VERSION) = ")[^"]*"/\1"/' ;;
      package.json) filter='s/("version":[[:space:]]*)"[^"]*"/\1""/' ;;
      Tools~/versions.env) filter='s/^(BUGSEE_PACKAGE_VERSION=")[^"]*"/\1"/' ;;
      *) fail "build id ${CS} does not describe this commit: ${file} changed after it (stamp again from the release commit's parent)" ;;
    esac
    if [[ "$(git -C "${ROOT_DIR}" show "${CS}:${file}" | sed -E "${filter}")" != "$(git -C "${ROOT_DIR}" show "HEAD:${file}" | sed -E "${filter}")" ]]; then
      fail "build id ${CS} does not describe this commit: ${file} changed beyond its build/version lines"
    fi
  done <<<"${changed}"
fi

VERSION="$(sed -nE 's/^[[:space:]]*"version":[[:space:]]*"([^"]+)".*/\1/p' "${ROOT_DIR}/package.json" | head -n 1)"
CS_VERSION="$(read_const Runtime/BugseePackageVersion.cs 'public const string Version = ')"
JAVA_VERSION="$(read_const Plugins/Android/UnityWrapperIdentity.java 'static final String VERSION = ')"
# versions.env is the source the other three are generated from.
ENV_VERSION="$(sed -nE 's/^BUGSEE_PACKAGE_VERSION="([^"]*)".*/\1/p' "${ROOT_DIR}/Tools~/versions.env" 2>/dev/null | head -n 1)"
[[ -n "${VERSION}" && "${VERSION}" == "${CS_VERSION}" && "${VERSION}" == "${JAVA_VERSION}" && "${VERSION}" == "${ENV_VERSION}" ]] \
  || fail "the versions disagree: package.json '${VERSION}', C# '${CS_VERSION}', Java '${JAVA_VERSION}', versions.env '${ENV_VERSION}' (the wrapper reports the C#/Java one)"
if [[ -n "${ARG}" ]]; then
  [[ "${ARG}" == "v${VERSION}" ]] || fail "tag ${ARG} does not match package.json version ${VERSION}"
fi
echo "OK: wrapper build ${CS}${ARG:+ for ${ARG}}"
