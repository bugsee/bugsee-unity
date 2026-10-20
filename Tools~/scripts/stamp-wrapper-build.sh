#!/usr/bin/env bash
# Stamp the wrapper build id into the two constants the SDK is told about (Runtime/BugseePackageVersion.cs `Build`
# and Plugins/Android/UnityWrapperIdentity.java `BUILD`; iOS gets it from the C# one). Run it as a release step,
# commit the result, then tag that commit:
#
#   Tools~/scripts/stamp-wrapper-build.sh            # id = short commit hash of HEAD (the commit this release is cut from)
#   Tools~/scripts/stamp-wrapper-build.sh <build-id> # an explicit id
#
# The committed value on main stays `dev` (the wrapper reports no build for it). A release commit carries the
# hash of the commit it was cut from; Tools~/scripts/check-release-build.sh (the release-check workflow, on every
# v* tag) refuses a tag whose constants are `dev`, disagree, or name a commit that is not in the tag's history.
set -euo pipefail

ROOT_DIR="${BUGSEE_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
BUILD="${1:-$(git -C "${ROOT_DIR}" rev-parse --short=12 HEAD)}"

if [[ ! "${BUILD}" =~ ^[0-9A-Za-z][0-9A-Za-z._-]*$ || "${BUILD}" == dev ]]; then
  echo "Invalid build id '${BUILD}': use letters, digits, '.', '_' or '-', and not the placeholder 'dev'." >&2
  exit 1
fi

stamp() { # file, regex prefix
  local file="${ROOT_DIR}/$1" prefix="$2"
  grep -Eq "${prefix}\"[^\"]*\"" "${file}" || { echo "No build constant in $1" >&2; exit 1; }
  # Rewrite via a temp file: `sed -i` differs between macOS and GNU.
  sed -E "s/(${prefix})\"[^\"]*\"/\1\"${BUILD}\"/" "${file}" > "${file}.tmp"
  mv "${file}.tmp" "${file}"
  echo "Stamped $1 -> ${BUILD}"
}

stamp "Runtime/BugseePackageVersion.cs" 'public const string Build = '
stamp "Plugins/Android/UnityWrapperIdentity.java" 'static final String BUILD = '
