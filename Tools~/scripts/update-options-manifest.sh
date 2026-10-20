#!/usr/bin/env bash
# Vendor the native SDKs' published option manifests for the versions pinned in Tools~/versions.env.
#
# Each SDK publishes options-manifest.json, generated from its live option registry in a Release build, at the
# immutable URL https://download.bugsee.com/sdk/options/<android|ios>/<version>.json (spec: bugsee/specs
# sdk/options). The files are copied verbatim to Tests~/WrapperPolicy/Fixtures/options-manifest/ and
# OptionsManifestTests checks the wrapper's option surface against them (offline, no network in the test).
#
#   Tools~/scripts/update-options-manifest.sh
#   ANDROID_MANIFEST_FILE=/path/options-manifest.json IOS_MANIFEST_FILE=... Tools~/scripts/update-options-manifest.sh
#
# The *_MANIFEST_FILE variables take a manifest from a local SDK build instead of the download. Its "sdkVersion"
# must still equal the pin: a manifest of another version describes another option surface.
# BUGSEE_OPTIONS_MANIFEST_BASE overrides the base URL (default https://download.bugsee.com/sdk/options).
# Run by update-native-sdks.sh after a pin bump. Needs curl (unless both files are given) and python3.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck disable=SC1090
source "${ROOT_DIR}/Tools~/versions.env"

BASE_URL="${BUGSEE_OPTIONS_MANIFEST_BASE:-https://download.bugsee.com/sdk/options}"
OUT_DIR="${ROOT_DIR}/Tests~/WrapperPolicy/Fixtures/options-manifest"
STAGE="$(mktemp -d)"
trap 'rm -rf "${STAGE}"' EXIT

fetch() { # <sdk> <version> <local file or empty>
  local sdk="$1" version="$2" local_file="$3" url="${BASE_URL}/$1/$2.json"
  if [[ -n "${local_file}" ]]; then
    cp "${local_file}" "${STAGE}/${sdk}.json"
    url="local build, file $(basename "${local_file}")"
  else
    curl -fsSL --max-time 60 -o "${STAGE}/${sdk}.json" "${url}" ||
      { echo "No ${sdk} manifest published for ${version} (${url}). Is the pin a released version?" >&2; exit 1; }
  fi
  python3 -I - "${STAGE}/${sdk}.json" "${sdk}" "${version}" <<'PY'
import json, sys
path, sdk, version = sys.argv[1:4]
with open(path, encoding="utf-8") as f:
    m = json.load(f)
got = (m.get("manifestVersion"), m.get("sdk"), m.get("sdkVersion"))
if got != (1, sdk, version):
    sys.exit(f"manifestVersion/sdk/sdkVersion are {got}, expected (1, {sdk!r}, {version!r})")
PY
  echo "${sdk}|${version}|${url}|$(shasum -a 256 "${STAGE}/${sdk}.json" | awk '{print $1}')" >> "${STAGE}/sources.txt"
}

{
  echo "# Vendored by Tools~/scripts/update-options-manifest.sh: <sdk>|<version>|<source>|<sha256 of the file>."
  echo "# Verbatim copies of the SDK-published manifests; checked by Tests~/WrapperPolicy/OptionsManifestTests.cs."
} > "${STAGE}/sources.txt"
fetch android "${ANDROID_SDK_VERSION}" "${ANDROID_MANIFEST_FILE:-}"
fetch ios "${IOS_SDK_VERSION}" "${IOS_MANIFEST_FILE:-}"

mkdir -p "${OUT_DIR}"
cp "${STAGE}/android.json" "${STAGE}/ios.json" "${STAGE}/sources.txt" "${OUT_DIR}/"
echo "Vendored option manifests: android ${ANDROID_SDK_VERSION}, ios ${IOS_SDK_VERSION} -> ${OUT_DIR}"
echo "Run the WrapperPolicy tests: a new native option must be exposed or listed in OptionsManifestTests."
