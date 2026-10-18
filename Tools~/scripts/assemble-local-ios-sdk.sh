#!/usr/bin/env bash
# Assemble a LOCAL copy of the bugsee/spm package from a bugsee-cocoa checkout, so the Unity package can be built
# and tested against an iOS SDK whose tag does not exist yet. Nothing is published, tagged or pushed.
#
# Replicates only the assembly half of bugsee-cocoa's beta release (.github/workflows/deploy-beta.yml):
#   scripts/build_new.sh (xcframework build) + the package layout scripts/deploy_spm_beta.sh pushes to bugsee/spm
#   (Package.swift, README.md, Tools/, Sources/BugseeSwiftUI). Not run: build.sh's legacy/static targets, the
#   feedback add-on, the options manifest, every deploy*.sh. No signing identity is used.
#
# Usage:
#   Tools~/scripts/assemble-local-ios-sdk.sh <bugsee-cocoa checkout> <output dir> [version]
#
#   version defaults to the checkout's version.txt. It is stamped into the SDK (BGSSdkVersion.h, tools.bundle/version.txt,
#   both git-ignored build files that the script puts back, or removes, when it exits) without editing the tracked
#   version.txt, so a checkout that has not bumped it yet can still be built as the next beta. The checkout's build/
#   directory is deleted and rebuilt. The output directory must be empty or hold the BUILD-INFO.txt of an earlier run, and
#   may not be (or be inside, or contain) the checkout or its build/; that non-empty/BUILD-INFO.txt check is also the
#   backstop where a path spelled with another case or a symlink slips past the string comparison.
#
# Needs the checkout's plcrashreporter submodule (git submodule update --init) and the iOS, tvOS, visionOS and
# Mac Catalyst SDKs, as the release build does.
#
# Result: <output dir>/Bugsee.xcframework, Package.swift (binaryTarget path), Sources/, Tools/, README.md,
# bugsee-spm-xcframework.zip (the artifact bugsee/spm would reference by URL) and BUILD-INFO.txt.
# Use it with BUGSEE_IOS_SDK_LOCAL_PACKAGE=<output dir> (Unity export) or Tests~/iOS/run-tests.sh <output dir>.

set -euo pipefail

if [[ $# -lt 2 ]]; then
  sed -n '2,/^set -euo/p' "$0" | sed -e '/^set -euo/d' -e 's/^# \{0,1\}//' >&2
  exit 2
fi

COCOA="$(cd "$1" && pwd -P)"
# The output is created only after every refusal below, so a refused path is not left behind. A directory that does not
# exist yet is resolved through its parent.
if [[ -d "$2" ]]; then
  OUT="$(cd "$2" && pwd -P)"
else
  OUT="$(cd "$(dirname "$2")" && pwd -P)/$(basename "$2")"
fi
VERSION="${3:-$(head -n 1 "${COCOA}/version.txt" | tr -d '[:space:]')}"

# The build wipes ${COCOA}/build, and this script wipes the output: neither may take the other, the checkout or anything above it.
case "${OUT}/" in
  "${COCOA}/"*)
    if [[ "${OUT}" == "${COCOA}" || "${OUT}" == "${COCOA}/build" || "${OUT}" == "${COCOA}/build/"* ]]; then
      echo "Refusing to use ${OUT}: it is the bugsee-cocoa checkout or inside its build directory, which the build deletes." >&2
      exit 1
    fi
    ;;
esac
case "${COCOA}/" in
  "${OUT}/"*)
    echo "Refusing to use ${OUT}: it contains the bugsee-cocoa checkout." >&2
    exit 1
    ;;
esac

# The output is wiped on every run, so only an empty directory or an earlier output of this script may be used.
if [[ -d "${OUT}" && -n "$(ls -A "${OUT}")" && ! -f "${OUT}/BUILD-INFO.txt" ]]; then
  echo "Refusing to use ${OUT}: it is not empty and holds no BUILD-INFO.txt from an earlier run." >&2
  exit 1
fi

if ! printf '%s' "${VERSION}" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+-beta[0-9]+$'; then
  echo "Expected an N.N.N-betaN version, got '${VERSION}'" >&2
  exit 1
fi
if [[ ! -e "${COCOA}/BugseeDev/BugseeLib/plcrashreporter/.git" ]]; then
  echo "plcrashreporter submodule missing: git -C ${COCOA} submodule update --init BugseeDev/BugseeLib/plcrashreporter" >&2
  exit 1
fi

SHA="$(git -C "${COCOA}" rev-parse HEAD)"
SHORT="$(git -C "${COCOA}" rev-parse --short HEAD)"

# One run per checkout: a second run would save the first one's stamped files as the originals. A stale lock (a killed
# run) is removed by hand, the message says where.
LOCK="${TMPDIR:-/tmp}/bugsee-assemble-$(printf '%s' "${COCOA}" | shasum | cut -c1-12).lock"

# The two files the stamp writes, put back as they were (or removed when they did not exist) however the script ends.
STAGE="$(mktemp -d)"
STAMPED_HEADER="${COCOA}/BugseeDev/BugseeLib/BugseeLib/BGSSdkVersion.h"
STAMPED_VERSION="${COCOA}/BugseeDev/BugseeLib/BugseeResources/tools.bundle/version.txt"
restore_stamped() {
  local file saved index=0
  for file in "${STAMPED_HEADER}" "${STAMPED_VERSION}"; do
    saved="${STAGE}/saved.${index}"
    index=$((index + 1))
    if [[ -f "${saved}" ]]; then cp "${saved}" "${file}"; elif [[ -f "${saved}.absent" ]]; then rm -f "${file}"; fi
  done
}
index=0
for file in "${STAMPED_HEADER}" "${STAMPED_VERSION}"; do
  if [[ -f "${file}" ]]; then cp "${file}" "${STAGE}/saved.${index}"; else : > "${STAGE}/saved.${index}.absent"; fi
  index=$((index + 1))
done
LOCKED=0
# Restored only by the run that holds the lock: a run refused by the lock must not write its saved copies over the files
# of the run that is building.
trap '[[ "${LOCKED}" == 1 ]] && restore_stamped; rm -rf "${STAGE}"; [[ "${LOCKED}" == 1 ]] && rmdir "${LOCK}"; true' EXIT
if ! mkdir "${LOCK}" 2>/dev/null; then
  echo "Another assemble run seems to be using ${COCOA} (lock ${LOCK}); remove the lock if it is stale." >&2
  exit 1
fi
LOCKED=1
mkdir -p "${OUT}"

# What build.sh stamps before build_new.sh runs (build_new.sh has its own injectCode disabled).
cat > "${COCOA}/BugseeDev/BugseeLib/BugseeLib/BGSSdkVersion.h" <<INJ
#define BUGSEE_SDK_VERSION  @"${VERSION}"
#define BUGSEE_SDK_BUILD @"${SHORT}-local"
INJ
echo "${VERSION}" > "${COCOA}/BugseeDev/BugseeLib/BugseeResources/tools.bundle/version.txt"

rm -rf "${COCOA}/build"
(cd "${COCOA}" && RELEASE=true WORKSPACE="${COCOA}" bash scripts/build_new.sh Release)

ZIP="${COCOA}/build/bugsee-spm-xcframework.zip"
[[ -f "${ZIP}" ]] || { echo "build_new.sh did not produce ${ZIP}" >&2; exit 1; }

rm -rf "${OUT:?}"/*
cp "${ZIP}" "${OUT}/bugsee-spm-xcframework.zip"
# Same tree the published zip carries; unzipped with ditto so symlinks inside the frameworks survive.
ditto -x -k "${ZIP}" "${STAGE}"
ditto "${STAGE}/Bugsee.xcframework" "${OUT}/Bugsee.xcframework"

cp "${COCOA}/README.md" "${OUT}/README.md"
mkdir -p "${OUT}/Tools"
cp -a "${COCOA}/BugseeDev/BugseeLib/BugseeResources/tools.bundle/BugseeAgent" "${OUT}/Tools/BugseeAgent"
cp -a "${COCOA}/BugseeDev/BugseeLib/BugseeResources/tools.bundle/BugseeClean" "${OUT}/Tools/BugseeClean"
mkdir -p "${OUT}/Sources"
cp -a "${COCOA}/BugseeDev/BugseeLib/BugseeSwiftUI/Sources" "${OUT}/Sources/BugseeSwiftUI"

# Package.swift.template with the URL binaryTarget turned into the local path one.
# Run from the temp dir: swiftpm leaves a .build directory in the working directory.
CHECKSUM="$(cd "${STAGE}" && swift package compute-checksum "${ZIP}" | tr -d '\r')"
python3 - "${COCOA}/Package.swift.template" "${OUT}/Package.swift" <<'PY'
import re, sys
src = open(sys.argv[1], encoding="utf-8").read()
new, n = re.subn(r'url: "[^"]*",\s*checksum: "[^"]*"\)', 'path: "Bugsee.xcframework")', src)
if n != 1:
    sys.exit("Package.swift.template has no url/checksum binaryTarget to localise")
open(sys.argv[2], "w", encoding="utf-8").write(new)
PY

{
  echo "bugsee-cocoa commit: ${SHA}"
  echo "stamped version:     ${VERSION}"
  echo "version.txt:         $(head -n 1 "${COCOA}/version.txt")"
  echo "zip sha256 (swift package compute-checksum): ${CHECKSUM}"
  echo "xcframework slices:  $(ls "${OUT}/Bugsee.xcframework" | grep -v Info.plist | tr '\n' ' ')"
  echo "xcode:               $(xcodebuild -version | tr '\n' ' ')"
} > "${OUT}/BUILD-INFO.txt"
cat "${OUT}/BUILD-INFO.txt"
