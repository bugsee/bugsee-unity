#!/usr/bin/env bash
# Stamp the wrapper build id into the two constants the SDK is told about (Runtime/BugseePackageVersion.cs `Build`
# and Plugins/Android/UnityWrapperIdentity.java `BUILD`; iOS gets it from the C# one). Release step, on a release
# branch cut from main (see "Releasing" in DESIGN.md):
#
#   Tools~/scripts/stamp-wrapper-build.sh            # id = short hash of HEAD, the commit this release is cut from
#   Tools~/scripts/stamp-wrapper-build.sh <build-id> # an explicit id (not hash-shaped: pass nothing for the hash)
#
# main keeps `dev` (the wrapper reports no build for it). Both files are rewritten, or neither.
set -euo pipefail

ROOT_DIR="${BUGSEE_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
CS_REL="Runtime/BugseePackageVersion.cs"
JAVA_REL="Plugins/Android/UnityWrapperIdentity.java"
CS_PREFIX='public const string Build = '
JAVA_PREFIX='static final String BUILD = '

if [[ $# -ge 1 ]]; then
  BUILD="$1"
  # check-release-build.sh treats a hex id as a commit hash and looks for it in history; an invented one would fail there.
  if [[ "${BUILD}" =~ ^[0-9a-f]{7,40}$ ]]; then
    echo "Build id '${BUILD}' looks like a commit hash; pass no argument to stamp the HEAD hash, or use another spelling." >&2
    exit 1
  fi
else
  if ! git -C "${ROOT_DIR}" diff --quiet HEAD; then
    echo "The working tree has uncommitted changes; the stamped hash would not describe it. Commit or stash first." >&2
    exit 1
  fi
  BUILD="$(git -C "${ROOT_DIR}" rev-parse --short=12 HEAD)"
fi

if [[ ! "${BUILD}" =~ ^[0-9A-Za-z][0-9A-Za-z._-]*$ || "${BUILD}" == dev ]]; then
  echo "Invalid build id '${BUILD}': use letters, digits, '.', '_' or '-', and not the placeholder 'dev'." >&2
  exit 1
fi

for pair in "${CS_REL}|${CS_PREFIX}" "${JAVA_REL}|${JAVA_PREFIX}"; do
  grep -Eq "${pair#*|}\"[^\"]*\"" "${ROOT_DIR}/${pair%%|*}" || { echo "No build constant in ${pair%%|*}" >&2; exit 1; }
done

trap 'rm -f "${ROOT_DIR}/${CS_REL}.tmp" "${ROOT_DIR}/${JAVA_REL}.tmp"' EXIT
# `sed -i` differs between macOS and GNU: write temp files, then move both into place.
sed -E "s/(${CS_PREFIX})\"[^\"]*\"/\1\"${BUILD}\"/" "${ROOT_DIR}/${CS_REL}" > "${ROOT_DIR}/${CS_REL}.tmp"
sed -E "s/(${JAVA_PREFIX})\"[^\"]*\"/\1\"${BUILD}\"/" "${ROOT_DIR}/${JAVA_REL}" > "${ROOT_DIR}/${JAVA_REL}.tmp"
mv "${ROOT_DIR}/${CS_REL}.tmp" "${ROOT_DIR}/${CS_REL}"
mv "${ROOT_DIR}/${JAVA_REL}.tmp" "${ROOT_DIR}/${JAVA_REL}"
echo "Stamped ${CS_REL} and ${JAVA_REL} -> ${BUILD}"
