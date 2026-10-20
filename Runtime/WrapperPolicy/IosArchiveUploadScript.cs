namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// The shell script behind the iOS "Bugsee Upload Symbols" Run Script phase (dSYMs + il2cpp-linemap). It never
    /// fails the build: every CLI error is reported as an Xcode <c>warning:</c> line and nothing more.
    /// </summary>
    public static class IosArchiveUploadScript
    {
        /// <summary>The script text, written to BugseeScripts/upload-symbols.sh in the exported Xcode project.</summary>
        public static string Build() => Build(null);

        public static string Build(string resolvedCliPath) => Build(resolvedCliPath, 0);

        /// <summary>
        /// The script text with <paramref name="resolvedCliPath"/> (the CLI the Editor found at export time, if any)
        /// baked in as a candidate: Xcode build phases run with a minimal PATH, so the script cannot rely on finding
        /// the CLI the way the Editor did.
        /// </summary>
        /// <param name="exportStartEpoch">
        /// When the Unity export began, in seconds since the Unix epoch (0: unknown). A line map inside the exported project
        /// that is older than this was left there by an earlier export (an "Append" export keeps the project's files) and is
        /// not uploaded.
        /// </param>
        public static string Build(string resolvedCliPath, long exportStartEpoch)
        {
            string text = Script.Replace("\r\n", "\n");
            text = text.Replace("PINNED_VERSION=''\n", "PINNED_VERSION='" + BugseeCliPolicy.DefaultVersion + "'\n");
            text = text.Replace("MIN_VERSION=''\n", "MIN_VERSION='" + BugseeCliPolicy.IosMinVersion + "'\n");
            if (exportStartEpoch > 0)
            {
                text = text.Replace("EXPORT_START=''\n",
                    "EXPORT_START='" + exportStartEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + "'\n");
            }

            // Only an absolute POSIX path: a Windows or Linux host exporting an iOS project has nothing usable to bake.
            if (string.IsNullOrEmpty(resolvedCliPath) || resolvedCliPath[0] != '/'
                || resolvedCliPath.IndexOfAny(new[] { '\n', '\r', '\0' }) >= 0)
            {
                return text;
            }

            return text.Replace("BAKED_CLI=''\n", "BAKED_CLI='" + resolvedCliPath.Replace("'", "'\\''") + "'\n");
        }

        // Debug builds are the ones tested on devices (Unity's Development Build), and a crash from one is
        // unsymbolicated without its dSYM, so every configuration uploads. Opt out per build environment.
        //
        // Every dSYM in DWARF_DSYM_FOLDER_PATH is uploaded, not only the app's and UnityFramework's: Xcode also copies
        // there the dSYMs shipped inside the xcframeworks the app embeds, Bugsee.framework.dSYM among them. Without
        // that one, a crash with a Bugsee frame on the stack (the SDK's own capture code, Bugsee.testCrash) stays
        // "missing_sym" and those frames show as bare addresses. (Xcode's ProcessXCFramework step copies it there
        // before the target's Run Scripts, for xcodebuild build and archive alike.) Should a layout lack that copy,
        // the dSYM is taken from the xcframework itself: the SPM checkout in DerivedData or a local package inside
        // the exported project, in the slice for PLATFORM_NAME.
        //
        // Unity exports Debug with DEBUG_INFORMATION_FORMAT=dwarf, which writes no dSYM for the app or
        // UnityFramework: say so rather than finish as if the crash symbols were uploaded.
        const string Script = @"#!/bin/sh
set -e
# Xcode fails an archive that prints an error: line, wherever it sits in the line (bugsee-cli prints error: on every
# failure), even when the script exits 0: every error: from the CLI or a path in a message becomes ""error -"" (any case).
defang() {
  # C locale: BSD sed fails on bytes that are not valid UTF-8, which set -e must not turn into an aborted upload loop.
  LC_ALL=C sed 's/[Ee][Rr][Rr][Oo][Rr]:/error -/g' || true
}
# Bugsee archive-time symbol upload (dSYMs + il2cpp-linemap).
# Never fail the Archive: problems are reported as warning: lines (Xcode's issue navigator), never error:.
if [ ""${CONFIGURATION}"" = ""Debug"" ] && [ ""${BUGSEE_SKIP_UPLOAD_ON_DEBUG}"" = ""1"" ]; then
  echo ""Bugsee: skip symbol upload for Debug (BUGSEE_SKIP_UPLOAD_ON_DEBUG=1).""
  exit 0
fi
if [ -z ""${BUGSEE_APP_TOKEN}"" ]; then
  PREFIX=warning:; [ -z ""${ACTION}"" ] || [ ""${ACTION}"" = install ] || PREFIX=note:
  printf '%s\n' ""${PREFIX} Bugsee: BUGSEE_APP_TOKEN unset — dSYMs and the il2cpp line map are NOT uploaded (the build continues). Xcode's GUI does not see your shell's environment: set it in your CI / Xcode Cloud environment, or add a user-defined build setting named BUGSEE_APP_TOKEN to the Unity-iPhone target (a Unity Replace export regenerates the project, so add it again after each export).""
  exit 0
fi

# --- resolve bugsee-cli: Xcode runs build phases with a minimal PATH, so a bare lookup often misses an installed CLI.
# Same order as the Editor's lookup: BUGSEE_CLI_PATH, then (in the script only) the CLI the Editor found at export time,
# PATH, /opt/homebrew/bin, /usr/local/bin, ~/.local/bin, ~/.cargo/bin, ~/.bugsee/bin, then the package's download cache.
# BUGSEE_CLI_PATH and the baked path are used as given. A discovered candidate must run, print a version and be MIN_VERSION or
# newer (a pre-release such as 0.8.0-beta.1 counts, as in the Editor). Never downloads. (BUGSEE_CLI_SYSTEM_DIRS replaces the two system directories and
# BUGSEE_CLI_PROBE_TIMEOUT the 15 s limit of a --version probe; tests.)
BAKED_CLI=''
PINNED_VERSION=''
MIN_VERSION=''
CLI=""""
TRIED=""""
note() {
  TRIED=""${TRIED}
    $1""
  return 0
}
# Prints what ""$1 --version"" writes to stdout; fails when it fails or runs longer than the limit: a probe that reads
# stdin, hangs or ignores SIGTERM must not hold the build (the watchdog sends TERM, then KILL two seconds later).
probe_version() {
  PROBE_DIR=""${TMPDIR:-/tmp}""
  [ -d ""${PROBE_DIR}"" ] && [ -w ""${PROBE_DIR}"" ] || PROBE_DIR=/tmp
  PROBE_OUT=$(mktemp ""${PROBE_DIR}/bugsee-cli-version.XXXXXX"" 2>/dev/null) || return 1
  # This runs in a command substitution, so the cleanup is set here: on exit and when the build is cancelled.
  trap 'rm -f ""${PROBE_OUT}""' EXIT
  trap 'exit 1' HUP INT TERM
  ""$1"" --version </dev/null >""${PROBE_OUT}"" 2>/dev/null &
  PROBE_PID=$!
  ( sleep ""${BUGSEE_CLI_PROBE_TIMEOUT:-15}"" || :; kill ""${PROBE_PID}"" 2>/dev/null || :; sleep 2 || :; kill -9 ""${PROBE_PID}"" 2>/dev/null || : ) >/dev/null 2>&1 &
  PROBE_WATCHDOG=$!
  PROBE_RC=0
  wait ""${PROBE_PID}"" 2>/dev/null || PROBE_RC=$?
  kill ""${PROBE_WATCHDOG}"" 2>/dev/null || true
  wait ""${PROBE_WATCHDOG}"" 2>/dev/null || true
  [ ""${PROBE_RC}"" -eq 0 ] && cat ""${PROBE_OUT}"" 2>/dev/null
  PROBE_RC=$?
  rm -f ""${PROBE_OUT}""
  return ""${PROBE_RC}""
}
# $1 = candidate, $2 = ""given"" to accept it without running it (BUGSEE_CLI_PATH, the baked path), ""quiet"" to say
# nothing when it does not exist (a PATH entry). Each path is tried once.
SEEN=""""
try_cli() {
  [ -n ""$1"" ] || return 0
  [ -z ""${CLI}"" ] || return 0
  case ""${SEEN}"" in
    *""
$1
""*) return 0 ;;
  esac
  SEEN=""${SEEN}
$1
""
  if [ ! -f ""$1"" ] || [ ! -x ""$1"" ]; then
    [ ""$2"" = quiet ] || note ""$1 (not found)""
    return 0
  fi
  if [ ""$2"" = given ]; then
    CLI=""$1""
    return 0
  fi
  CANDIDATE_OUT=$(probe_version ""$1"") || {
    note ""$1 (does not run)""
    return 0
  }
  # The first token that starts with a digit; major.minor.patch of its numeric prefix (0.8.1-beta.1 is 0.8.1, a missing part is 0,
  # leading zeros are numbers not octal) against MIN_VERSION, which the package bakes in from BugseeCliPolicy.IosMinVersion.
  CANDIDATE_TOKEN=$(printf '%s\n' ""${CANDIDATE_OUT}"" | /usr/bin/awk '{ for (i = 1; i <= NF; i++) if ($i ~ /^[0-9]/) { print $i; exit } }')
  CANDIDATE_CMP=$(printf '%s\n' ""${CANDIDATE_TOKEN}"" | /usr/bin/awk -v min=""${MIN_VERSION}"" '{
    if (!match($0, /^[0-9]+(\.[0-9]+)?(\.[0-9]+)?/)) exit
    split(substr($0, RSTART, RLENGTH), a, ""."")
    split(min, b, ""."")
    for (i = 1; i <= 3; i++) {
      x = a[i] + 0; y = b[i] + 0
      if (x != y) { print (x > y) ? ""ok"" : ""old""; exit }
    }
    print ""ok""
  }')
  if [ -z ""${CANDIDATE_CMP}"" ]; then
    note ""$1 (no version in --version output)""
    return 0
  fi
  if [ ""${CANDIDATE_CMP}"" = old ]; then
    note ""$1 (version ${CANDIDATE_TOKEN}, older than ${MIN_VERSION})""
    return 0
  fi
  CLI=""$1""
  return 0
}
try_command() {
  [ -z ""${CLI}"" ] || return 0
  case ""$1"" in
    */*) try_cli ""$1"" given ;;
    *)
      FOUND=$(command -v ""$1"" 2>/dev/null || true)
      case ""${FOUND}"" in
        /*) try_cli ""${FOUND}"" given ;;
        *) note ""$1 (not on PATH=${PATH})"" ;;
      esac ;;
  esac
  return 0
}
[ -z ""${BUGSEE_CLI_PATH}"" ] || try_command ""${BUGSEE_CLI_PATH}""
try_cli ""${BAKED_CLI}"" given
# Every PATH entry, not only the first hit (empty entries skipped, as in the Editor).
OLD_IFS=""${IFS}""
IFS=:
for DIR in ${PATH}; do
  IFS=""${OLD_IFS}""
  [ -z ""${DIR}"" ] || try_cli ""${DIR%/}/bugsee-cli"" quiet
done
IFS=""${OLD_IFS}""
[ -n ""${CLI}"" ] || note ""bugsee-cli (not found in PATH=${PATH})""
for DIR in ${BUGSEE_CLI_SYSTEM_DIRS-/opt/homebrew/bin /usr/local/bin}; do
  try_cli ""${DIR}/bugsee-cli""
done
if [ -n ""${HOME}"" ]; then
  try_cli ""${HOME}/.local/bin/bugsee-cli""
  try_cli ""${HOME}/.cargo/bin/bugsee-cli""
  try_cli ""${HOME}/.bugsee/bin/bugsee-cli""
  # The release the package pins (or BUGSEE_CLI_VERSION) first: a lexical glob would rank 0.10.0 below 0.8.1.
  WANT=""${BUGSEE_CLI_VERSION:-${PINNED_VERSION}}""
  WANT=""${WANT#v}""
  case ""${WANT}"" in
    [0-9]*) case ""${WANT}"" in *[!0-9A-Za-z.+-]*) WANT=""${PINNED_VERSION}"" ;; esac ;;
    *) WANT=""${PINNED_VERSION}"" ;;
  esac
  for CACHE in ""${HOME}/Library/Caches/Bugsee/cli"" ""${XDG_CACHE_HOME:-${HOME}/.cache}/bugsee/cli""; do
    CACHE_HIT=""""
    for CACHED in ""${CACHE}/${WANT}""/*/bugsee-cli ""${CACHE}""/*/*/bugsee-cli; do
      [ -e ""${CACHED}"" ] || continue
      CACHE_HIT=1
      try_cli ""${CACHED}""
    done
    [ -n ""${CLI}"" ] || [ -n ""${CACHE_HIT}"" ] || note ""${CACHE} (no cached CLI)""
  done
fi
if [ -z ""${CLI}"" ]; then
  printf '%s\n' ""warning: Bugsee: bugsee-cli was not found, so symbols are NOT uploaded (the build continues). Looked at:${TRIED}"" | defang
  echo ""Bugsee: install it (curl --proto '=https' --tlsv1.2 -sSfL https://download.bugsee.com/cli/install.sh | sh) or set BUGSEE_CLI_PATH.""
  exit 0
fi
[ -z ""${TRIED}"" ] || printf '%s\n' ""Bugsee: passed over:${TRIED}"" | defang
printf '%s\n' ""Bugsee: using ${CLI}"" | defang
VERSION=""${BUGSEE_VERSION:-${MARKETING_VERSION}}""
BUILD=""${BUGSEE_BUILD:-${CURRENT_PROJECT_VERSION}}""
FAILED=0
# Says why a CLI exit code is a failure (the bugsee-cli exit-code contract); the build is never failed.
cli_failure() {
  case ""$1"" in
    1) WHY=""bugsee-cli hit an unexpected error"" ;;
    2) WHY=""bugsee-cli rejected its arguments (a bugsee-cli older or newer than this package expects?)"" ;;
    10) WHY=""the file to upload was not found"" ;;
    11) WHY=""the file to upload is not a usable symbol file"" ;;
    20) WHY=""bugsee-cli found its configuration invalid"" ;;
    21) WHY=""the server rejected the app token: check BUGSEE_APP_TOKEN"" ;;
    30) WHY=""the Bugsee server returned an error"" ;;
    31) WHY=""the Bugsee server could not be reached (network)"" ;;
    126|127) WHY=""bugsee-cli could not be run"" ;;
    *) WHY=""bugsee-cli failed"" ;;
  esac
}
upload() {
  RC_DIR=""${TMPDIR:-/tmp}""
  [ -d ""${RC_DIR}"" ] && [ -w ""${RC_DIR}"" ] || RC_DIR=/tmp
  RC_FILE=$(mktemp ""${RC_DIR}/bugsee-upload-rc.XXXXXX"" 2>/dev/null) || RC_FILE=""${RC_DIR}/bugsee-upload-rc.$$""
  # The exit code goes through a file, written on success too: a pipe would hand back sed's, and a code that could not
  # be recorded counts as a failure. stdout and stderr both pass through defang.
  { ""$@"" 2>&1 && echo 0 >""${RC_FILE}"" || echo $? >""${RC_FILE}""; } 2>/dev/null | defang
  UPLOAD_RC=$(cat ""${RC_FILE}"" 2>/dev/null) || UPLOAD_RC=1
  rm -f ""${RC_FILE}""
  case ""${UPLOAD_RC}"" in ''|*[!0-9]*) UPLOAD_RC=1 ;; esac
  if [ ""${UPLOAD_RC}"" -ne 0 ]; then
    FAILED=1
    cli_failure ""${UPLOAD_RC}""
    SAFE_NAME=$(printf '%s' ""${LAST_UPLOAD}"" | tr '\n\r' '  ' | defang)
    printf '%s\n'  ""warning: Bugsee: symbol upload failed (exit ${UPLOAD_RC}): ${WHY}. Not uploaded: ${SAFE_NAME} (the build continues).""
  fi
  return 0
}

# --- dSYMs: the app, UnityFramework and the embedded xcframeworks' (Bugsee.framework.dSYM) ---
if [ -n ""${DWARF_DSYM_FOLDER_PATH}"" ] && [ -d ""${DWARF_DSYM_FOLDER_PATH}"" ]; then
  for DSYM in ""${DWARF_DSYM_FOLDER_PATH}""/*.dSYM; do
    [ -d ""${DSYM}"" ] || continue
    echo ""Bugsee: uploading dSYM ${DSYM}"" | defang
    LAST_UPLOAD=""${DSYM}""
    upload ""${CLI}"" debug-files upload ""${DSYM}"" --type dsym --version ""${VERSION}"" --build ""${BUILD}""
  done
fi
# Fallback for a layout where Xcode did not copy the SDK's dSYM next to the app's: take it from the xcframework
# itself (remote SPM checkout in DerivedData, or a local package in the exported project), slice for this platform.
if [ ! -d ""${DWARF_DSYM_FOLDER_PATH}/Bugsee.framework.dSYM"" ]; then
  case ""${PLATFORM_NAME}"" in
    iphonesimulator) SLICE='ios-*simulator' ;;
    *) SLICE='ios-arm64' ;;
  esac
  SDK_DSYM=""""
  for ROOT in ""${BUILD_DIR%Build/*}SourcePackages/artifacts"" ""${PROJECT_DIR}""; do
    [ -n ""${ROOT}"" ] && [ -d ""${ROOT}"" ] || continue
    SDK_DSYM=$(find ""${ROOT}"" -type d -path ""*/Bugsee.xcframework/${SLICE}/dSYMs/Bugsee.framework.dSYM"" 2>/dev/null | head -n1 || true)
    [ -n ""${SDK_DSYM}"" ] && break
  done
  if [ -n ""${SDK_DSYM}"" ]; then
    echo ""Bugsee: uploading SDK dSYM ${SDK_DSYM}"" | defang
    LAST_UPLOAD=""${SDK_DSYM}""
    upload ""${CLI}"" debug-files upload ""${SDK_DSYM}"" --type dsym --version ""${VERSION}"" --build ""${BUILD}""
  fi
fi
if [ ! -d ""${DWARF_DSYM_FOLDER_PATH}/${DWARF_DSYM_FILE_NAME}"" ] && [ ! -d ""${DWARF_DSYM_FOLDER_PATH}/UnityFramework.framework.dSYM"" ]; then
  echo ""warning: Bugsee: no dSYM for ${CONFIGURATION} in ${DWARF_DSYM_FOLDER_PATH}; set DEBUG_INFORMATION_FORMAT=dwarf-with-dsym to symbolicate its crashes."" | defang
fi

# --- il2cpp-linemap (UUID from UnityFramework binary / dSYM) ---
# The map must be this export's. An ""Append"" export keeps the earlier export's files, and Unity leaves an old
# LineNumberMappings.json in place when the new build wrote none: it would be uploaded under the new UnityFramework's UUID.
# The Editor brings the current map's time up to date at export (an unchanged Append export does not copy it again), so a
# map inside the project that is more than two minutes older than the export (EXPORT_START, baked in when the Editor
# exports; the slack covers a file server's clock) is skipped.
# Tried in order: the pending pointer, the maps under Il2CppOutputProject, any other under the project. A path outside the
# project (BUGSEE_IL2CPP_MAPPING at export time) is the user's own file and is used as given.
EXPORT_START=''
STALE_MAPS=""""
# Inside the project by resolved path (a pointer through a symlink such as /tmp -> /private/tmp is inside too).
in_project() {
  REAL_DIR=$(cd -P ""$(dirname ""$1"")"" 2>/dev/null && pwd -P) || return 1
  REAL_PROJECT=$(cd -P ""${PROJECT_DIR}"" 2>/dev/null && pwd -P) || return 1
  case ""${REAL_DIR}/"" in
    ""${REAL_PROJECT}""/*) return 0 ;;
  esac
  return 1
}
map_is_current() {
  [ -f ""$1"" ] || return 1
  in_project ""$1"" || return 0
  [ -n ""${EXPORT_START}"" ] || return 0
  MAP_TIME=$(stat -c %Y ""$1"" 2>/dev/null) || MAP_TIME=$(stat -f %m ""$1"" 2>/dev/null) || MAP_TIME=""""
  case ""${MAP_TIME}"" in
    """"|*[!0-9]*) ;;
    *) [ $((MAP_TIME + 120)) -ge ""${EXPORT_START}"" ] && return 0 ;;
  esac
  case ""${STALE_MAPS} "" in
    *"" $1 ""*) ;;
    *) STALE_MAPS=""${STALE_MAPS} $1"" ;;
  esac
  return 1
}
find_current_map() {
  [ -d ""$1"" ] || return 0
  FOUND_MAPS=$(find ""$1"" -name LineNumberMappings.json 2>/dev/null || true)
  OLD_IFS=""${IFS}""
  IFS='
'
  set -f
  for CANDIDATE in ${FOUND_MAPS}; do
    IFS=""${OLD_IFS}""
    if map_is_current ""${CANDIDATE}""; then
      LINEMAP=""${CANDIDATE}""
      break
    fi
  done
  set +f
  IFS=""${OLD_IFS}""
  return 0
}
LINEMAP=""""
if [ -f ""${PROJECT_DIR}/BugseePendingIl2CppLinemap.txt"" ]; then
  PENDING=$(head -n1 ""${PROJECT_DIR}/BugseePendingIl2CppLinemap.txt"")
  # A map inside the export is named relative to it, so a copy of the export finds its own; only a user's own is absolute.
  case ""${PENDING}"" in
    /*) ;;
    *) PENDING=""${PROJECT_DIR}/${PENDING}"" ;;
  esac
  if map_is_current ""${PENDING}""; then
    LINEMAP=""${PENDING}""
  fi
fi
[ -n ""${LINEMAP}"" ] || find_current_map ""${PROJECT_DIR}/Il2CppOutputProject""
[ -n ""${LINEMAP}"" ] || find_current_map ""${PROJECT_DIR}""
# bugsee-cli packs the MethodMap.tsv and il2cppFileRoot.txt beside the map. One of them that is older than the export is an
# earlier build's (this build did not write it): upload the map from a temporary folder holding only the current files.
STAGE=""""
ORIG_LINEMAP=""${LINEMAP}""
if [ -n ""${LINEMAP}"" ] && [ -n ""${EXPORT_START}"" ] && in_project ""${LINEMAP}""; then
  MAP_DIR=$(dirname ""${LINEMAP}"")
  OLD_SIBLINGS=""""
  for SIBLING in MethodMap.tsv il2cppFileRoot.txt; do
    if [ -f ""${MAP_DIR}/${SIBLING}"" ] && ! map_is_current ""${MAP_DIR}/${SIBLING}""; then
      OLD_SIBLINGS=""${OLD_SIBLINGS} ${SIBLING}""
    fi
  done
  if [ -n ""${OLD_SIBLINGS}"" ]; then
    STAGE=$(mktemp -d ""${TMPDIR:-/tmp}/bugsee-linemap.XXXXXX"" 2>/dev/null) || STAGE=""""
    if [ -n ""${STAGE}"" ] && cp ""${LINEMAP}"" ""${STAGE}/LineNumberMappings.json""; then
      for SIBLING in MethodMap.tsv il2cppFileRoot.txt; do
        case ""${OLD_SIBLINGS} "" in
          *"" ${SIBLING} ""*) ;;
          *) [ ! -f ""${MAP_DIR}/${SIBLING}"" ] || cp ""${MAP_DIR}/${SIBLING}"" ""${STAGE}/${SIBLING}"" || true ;;
        esac
      done
      echo ""Bugsee: not uploading the earlier build's${OLD_SIBLINGS} beside ${LINEMAP}"" | defang
      LINEMAP=""${STAGE}/LineNumberMappings.json""
    else
      # Uploading in place would send the earlier build's files with the map.
      echo ""Bugsee: could not stage the line map without the earlier build's${OLD_SIBLINGS}; not uploading it"" | defang
      LINEMAP=""""
    fi
  fi
fi
UUID=""${BUGSEE_IL2CPP_UUIDS:-}""
if [ -z ""${UUID}"" ]; then
  UF_BIN=""${BUILT_PRODUCTS_DIR}/UnityFramework.framework/UnityFramework""
  if [ ! -f ""${UF_BIN}"" ]; then
    UF_BIN=$(find ""${BUILT_PRODUCTS_DIR}"" -path '*/UnityFramework.framework/UnityFramework' 2>/dev/null | head -n1 || true)
  fi
  if [ -f ""${UF_BIN}"" ]; then
    if command -v dwarfdump >/dev/null 2>&1; then
      UUID=$(dwarfdump -u ""${UF_BIN}"" 2>/dev/null | sed -n 's/.*UUID: \([^ ]*\).*/\1/p' | head -n1 | tr '[:upper:]' '[:lower:]' || true)
    fi
    if [ -z ""${UUID}"" ] && command -v otool >/dev/null 2>&1; then
      UUID=$(otool -l ""${UF_BIN}"" 2>/dev/null | awk '/uuid/ {print tolower($2); exit}' || true)
    fi
  fi
fi
if [ -n ""${LINEMAP}"" ] && [ -f ""${LINEMAP}"" ] && [ -n ""${UUID}"" ]; then
  echo ""Bugsee: uploading il2cpp-linemap ${LINEMAP} uuid=${UUID}"" | defang
  LAST_UPLOAD=""${ORIG_LINEMAP}""
  upload ""${CLI}"" debug-files upload ""${LINEMAP}"" --type il2cpp-linemap --version ""${VERSION}"" --build ""${BUILD}"" --uuid ""${UUID}""
else
  echo ""Bugsee: skip il2cpp-linemap (map='${LINEMAP}' uuid='${UUID}')${STALE_MAPS:+, older than this export:${STALE_MAPS}}"" | defang
fi
[ -z ""${STAGE}"" ] || rm -rf ""${STAGE}""
[ ""${FAILED}"" -eq 0 ] || printf '%s\n' ""warning: Bugsee: some symbols were NOT uploaded; crashes from this build may stay unsymbolicated (see the warnings above).""
exit 0
";
    }
}
