#!/usr/bin/env bash
# Fail unless the wrapper build id is a real, consistent one. Run by the release-check workflow on every v* tag
# (Tools~/scripts/check-release-build.sh <tag>), and by hand before tagging.
#
# Checks: C# and Java constants agree; they are not the `dev` placeholder; the id has a sane shape; when it
# looks like a commit hash, that commit is in the checked-out history (CI must fetch it all); and the tag, if
# given, is v<package.json version>.
set -euo pipefail

ROOT_DIR="${BUGSEE_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
TAG="${1:-}"

read_const() { # file, regex prefix
  sed -nE "s/.*${2}\"([^\"]*)\".*/\1/p" "${ROOT_DIR}/$1" | head -n 1
}
CS="$(read_const Runtime/BugseePackageVersion.cs 'public const string Build = ')"
JAVA="$(read_const Plugins/Android/UnityWrapperIdentity.java 'static final String BUILD = ')"
fail() { echo "release check failed: $*" >&2; exit 1; }

[[ -n "${CS}" && -n "${JAVA}" ]] || fail "could not read the build constants (C# '${CS}', Java '${JAVA}')"
[[ "${CS}" == "${JAVA}" ]] || fail "C# says '${CS}', Java says '${JAVA}'; stamp both with Tools~/scripts/stamp-wrapper-build.sh"
[[ "${CS}" != dev ]] || fail "the build id is the 'dev' placeholder; run Tools~/scripts/stamp-wrapper-build.sh and commit before tagging"
[[ "${CS}" =~ ^[0-9A-Za-z][0-9A-Za-z._-]*$ ]] || fail "'${CS}' is not a valid build id"

if [[ "${CS}" =~ ^[0-9a-f]{7,40}$ ]]; then
  git -C "${ROOT_DIR}" cat-file -e "${CS}^{commit}" 2>/dev/null \
    || fail "build id ${CS} is not a commit in this history (shallow checkout, or stamped from another repository)"
fi

if [[ -n "${TAG}" ]]; then
  VERSION="$(sed -nE 's/^[[:space:]]*"version":[[:space:]]*"([^"]+)".*/\1/p' "${ROOT_DIR}/package.json" | head -n 1)"
  [[ "${TAG}" == "v${VERSION}" ]] || fail "tag ${TAG} does not match package.json version ${VERSION}"
fi
echo "OK: wrapper build ${CS}${TAG:+ for ${TAG}}"
