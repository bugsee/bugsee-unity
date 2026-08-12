#!/usr/bin/env bash
# Validate local prerequisites and build artifacts for Bugsee Unity S1/S2 field proofs.
# Does not require a device — prints a checklist for the remaining device steps.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
BUILD_DIR="${1:-}"
UNITY_EDITOR="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/2021.3.45f1/Unity.app/Contents/MacOS/Unity}"
ERRORS=0
WARNINGS=0

ok()   { printf '  OK  %s\n' "$*"; }
warn() { printf '  WARN %s\n' "$*"; WARNINGS=$((WARNINGS + 1)); }
fail() { printf '  FAIL %s\n' "$*"; ERRORS=$((ERRORS + 1)); }

echo "== Bugsee Unity field validation (S1/S2 prerequisites) =="
echo "Package: ${ROOT_DIR}"

echo
echo "-- Tooling --"
CLI_RESOLVED=""
if [[ -n "${BUGSEE_CLI_PATH:-}" && -x "${BUGSEE_CLI_PATH}" ]]; then
  CLI_RESOLVED="${BUGSEE_CLI_PATH}"
elif command -v bugsee-cli >/dev/null 2>&1; then
  CLI_RESOLVED="$(command -v bugsee-cli)"
elif [[ -x "${HOME}/.bugsee/bin/bugsee-cli" ]]; then
  CLI_RESOLVED="${HOME}/.bugsee/bin/bugsee-cli"
elif [[ -x "${HOME}/.cargo/bin/bugsee-cli" ]]; then
  CLI_RESOLVED="${HOME}/.cargo/bin/bugsee-cli"
elif [[ -x "${ROOT_DIR}/../../bugsee-cli/target/release/bugsee-cli" ]]; then
  CLI_RESOLVED="$(cd "${ROOT_DIR}/../../bugsee-cli/target/release" && pwd)/bugsee-cli"
elif [[ -x "${ROOT_DIR}/../../bugsee-cli/target/debug/bugsee-cli" ]]; then
  CLI_RESOLVED="$(cd "${ROOT_DIR}/../../bugsee-cli/target/debug" && pwd)/bugsee-cli"
elif [[ -x "${ROOT_DIR}/../bugsee-cli/target/release/bugsee-cli" ]]; then
  CLI_RESOLVED="$(cd "${ROOT_DIR}/../bugsee-cli/target/release" && pwd)/bugsee-cli"
elif [[ -x "${ROOT_DIR}/../bugsee-cli/target/debug/bugsee-cli" ]]; then
  CLI_RESOLVED="$(cd "${ROOT_DIR}/../bugsee-cli/target/debug" && pwd)/bugsee-cli"
fi
if [[ -n "${CLI_RESOLVED}" ]]; then
  ok "bugsee-cli → ${CLI_RESOLVED}"
  "${CLI_RESOLVED}" --version 2>/dev/null | head -1 | sed 's/^/       /' || true
  if [[ "${CLI_RESOLVED}" != "bugsee-cli" ]] && ! command -v bugsee-cli >/dev/null 2>&1; then
    warn "not on PATH — set BUGSEE_CLI_PATH=${CLI_RESOLVED} for Unity Editor uploads"
  fi
else
  fail "bugsee-cli not found (install, add to PATH, or set BUGSEE_CLI_PATH)"
fi
if [[ -x "${UNITY_EDITOR}" ]]; then
  ok "Unity editor: ${UNITY_EDITOR}"
else
  warn "Unity 2021.3.45f1 not at default Hub path (set UNITY_EDITOR=...)"
fi

if [[ -n "${BUGSEE_APP_TOKEN:-}" ]]; then
  ok "BUGSEE_APP_TOKEN is set"
else
  warn "BUGSEE_APP_TOKEN unset — Editor/archive uploads will skip"
fi

echo
echo "-- Package surface --"
for f in \
  "Editor/BugseeIl2CppLinemapUpload.cs" \
  "Editor/BugseeIl2CppModuleIdentity.cs" \
  "Editor/BugseeSymbolUpload.cs" \
  "Plugins/iOS/BugseeUnityBridge.mm" \
  "Plugins/iOS/BugseeUnityCallbacks.mm" \
  "Runtime/Internal/ExceptionPipeline.cs" \
  "Runtime/Internal/ManagedExceptionPayload.cs" \
  "Runtime/Platform/IOS/IosNativeCallbacks.cs"
do
  if [[ -f "${ROOT_DIR}/${f}" ]]; then
    ok "$f"
  else
    fail "missing $f"
  fi
done

if [[ -d "${ROOT_DIR}/Native~/ios/Bugsee/Bugsee.xcframework" ]]; then
  ok "Native~/ios/Bugsee/Bugsee.xcframework present"
else
  warn "Bugsee.xcframework missing — run Tools~/scripts/update-native-sdks.sh before iOS builds"
fi

extract_macho_uuid() {
  local bin="$1"
  if command -v otool >/dev/null 2>&1; then
    otool -l "$bin" 2>/dev/null | awk '/cmd LC_UUID/{getline; getline; print $2; exit}'
  fi
}

extract_elf_build_id() {
  local bin="$1"
  if command -v readelf >/dev/null 2>&1; then
    readelf -n "$bin" 2>/dev/null | awk '/Build ID:/{print $3; exit}'
  elif command -v llvm-readelf >/dev/null 2>&1; then
    llvm-readelf -n "$bin" 2>/dev/null | awk '/Build ID:/{print $3; exit}'
  fi
}

if [[ -n "${BUILD_DIR}" ]]; then
  echo
  echo "-- Build artifacts: ${BUILD_DIR} --"
  if [[ ! -d "${BUILD_DIR}" ]]; then
    fail "build dir does not exist"
  else
    MAP="$(find "${BUILD_DIR}" -name 'LineNumberMappings.json' 2>/dev/null | head -1 || true)"
    if [[ -n "${MAP}" ]]; then
      ok "LineNumberMappings.json → ${MAP}"
    else
      fail "LineNumberMappings.json not found (enable IL2CPP stacktrace / emit source mapping)"
    fi

    METHODMAP="$(find "${BUILD_DIR}" -name 'MethodMap.tsv' 2>/dev/null | head -1 || true)"
    if [[ -n "${METHODMAP}" ]]; then
      ok "MethodMap.tsv → ${METHODMAP}"
    else
      warn "MethodMap.tsv not found (managed demangle may be limited)"
    fi

    UF="$(find "${BUILD_DIR}" -type f -name 'UnityFramework' 2>/dev/null | head -1 || true)"
    if [[ -n "${UF}" ]]; then
      UUID="$(extract_macho_uuid "${UF}" || true)"
      if [[ -n "${UUID}" ]]; then
        ok "UnityFramework UUID ${UUID}"
      else
        warn "UnityFramework found but UUID not readable (otool?)"
      fi
    else
      warn "UnityFramework binary not in build dir (iOS archive may still extract from dSYM)"
    fi

    SO="$(find "${BUILD_DIR}" -name 'libil2cpp.so' 2>/dev/null | head -3 || true)"
    if [[ -n "${SO}" ]]; then
      while IFS= read -r so; do
        [[ -z "${so}" ]] && continue
        BID="$(extract_elf_build_id "${so}" || true)"
        if [[ -n "${BID}" ]]; then
          ok "libil2cpp.so build-id ${BID} ($(basename "$(dirname "${so}")"))"
        else
          warn "libil2cpp.so without readable build-id: ${so}"
        fi
      done <<< "${SO}"
    else
      warn "libil2cpp.so not found (expected for Android S2)"
    fi

    SYMZIP="$(find "${BUILD_DIR}" -maxdepth 3 -name 'symbols.zip' 2>/dev/null | head -1 || true)"
    if [[ -n "${SYMZIP}" ]]; then
      ok "symbols.zip → ${SYMZIP}"
    else
      warn "symbols.zip not found (Android FULL native symbols)"
    fi
  fi
else
  echo
  echo "-- Build artifacts --"
  warn "No build dir argument. Re-run: $0 /path/to/unity/build/output"
fi

echo
echo "-- Device / archive checklist (manual) --"
cat <<'EOF'
  S1 iOS:
    [ ] IL2CPP iOS player with LineNumberMappings
    [ ] BUGSEE_APP_TOKEN in Unity + Xcode archive env
    [ ] Archive Run Script uploads dSYM + il2cpp-linemap (UnityFramework UUID)
    [ ] Bugsee.TestCrash / native fatal with DetectAndReportCrash
    [ ] Viewer shows C# file/line on crashing frames

  S2 Android:
    [ ] IL2CPP + FULL native debug symbols / symbols.zip
    [ ] NDK enabled (bugsee-android-ndk + Gradle ndk { enabled = true } on Unity 6+)
    [ ] Post-build elf + il2cpp-linemap uploads (multi-ABI UUIDs)
    [ ] Native fatal on device; viewer C# file/line

  C1 managed:
    [ ] CaptureManagedExceptions on; unhandled appears once
    [ ] MethodMap demangle when module UUID(s) match upload
EOF

echo
if [[ "${ERRORS}" -gt 0 ]]; then
  echo "Result: FAILED (${ERRORS} error(s), ${WARNINGS} warning(s))"
  exit 1
fi
echo "Result: OK (${WARNINGS} warning(s)) — complete device checklist above for S1/S2 proof"
exit 0
