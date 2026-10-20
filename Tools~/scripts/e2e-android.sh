#!/usr/bin/env bash
# Run BugseeMiniGame e2e scenarios on one Android device/emulator and report what happened.
#
#   Tools~/scripts/e2e-android.sh <adb-serial> <apk> <scenario> [<scenario>...]
#
# Scenarios: any name BugseeSampleBootstrap knows; the list with what each does is the scenario table in
#   Samples~/BugseeMiniGame/README.md (interactions actions filters presets sig handled unhandled pipeline upload crash native
#   abort fatal java hang delete apm* shapes none ...). The self-checking ones log `[Sample] <name> PASS|FAIL` lines.
#   PRESET=minimal|detect|privacy launches every scenario with that launch-option preset (extra bugsee_preset).
#   `crash` and `java` need the TestCrash-on-main-looper change; `hang` blocks the Android UI thread.
#
# Each launch passes a unique run id (extra bugsee_e2e_run). The app records it before the scenario fires, so
# when Android restarts the activity after a crash with the same extras, that process arms nothing instead of
# crashing again. WAIT is counted from the app's "E2E armed" log line (up to READY_WAIT for it), not from
# `am start`: a cold dev build can take a long time to reach Start().
#
# The checks come from the wrapper workbook (Part 9): assert the device and the build before trusting a
# result, assert the SDK actually launched, and assert the process really died when a scenario is meant to
# kill it. Uploads need a network, so airplane mode is a hard failure (the device setting is never changed).
#
# Deliberately NOT `set -e`: a crashed app makes pidof/adb return non-zero, which is an expected result here,
# and each check records its own outcome in $status instead.
set -uo pipefail

SERIAL="${1:?adb serial}"; APK="${2:?apk path}"; shift 2
[[ $# -ge 1 ]] || { echo "give at least one scenario" >&2; exit 2; }

ADB="${ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
PKG="${PKG:-com.bugsee.minigame}"
ACTIVITY="${ACTIVITY:-com.unity3d.player.UnityPlayerActivity}"
WAIT="${WAIT:-25}"            # seconds to let a scenario run, after the app armed it (it fires 5 s after that)
READY_WAIT="${READY_WAIT:-90}" # seconds to wait for the app to arm the scenario
UPLOAD_WAIT="${UPLOAD_WAIT:-45}"
OUT="${OUT:-$(mktemp -d)}"; mkdir -p "$OUT"
a() { "$ADB" -s "$SERIAL" "$@"; }

# Scenarios that must kill the process; `hang` may or may not (an ANR can kill it), so it is not judged;
# the rest must leave it running.
fatal_scenario() { case "$1" in crash|native|abort|fatal|java) return 0;; *) return 1;; esac; }
liveness_unjudged() { [[ "$1" == hang ]]; }

pid_of() { a shell pidof "$PKG" 2>/dev/null | tr -d '\r' || true; }

echo "== device"
state="$("$ADB" devices | awk -v s="$SERIAL" '$1==s{print $2}')"
[[ "$state" == "device" ]] || { echo "FAIL $SERIAL is not an attached device (state='$state')"; exit 1; }
echo "   $SERIAL api=$(a shell getprop ro.build.version.sdk | tr -d '\r') release=$(a shell getprop ro.build.version.release | tr -d '\r') model=$(a shell getprop ro.product.model | tr -d '\r')"
if [[ "$(a shell settings get global airplane_mode_on | tr -d '\r')" != "0" ]]; then
  echo "FAIL airplane mode is on: reports cannot upload (not changing someone else's device setting)"; exit 1
fi

# Only remove the package afterwards if this run installed it: on a shared device it may be someone's.
preinstalled=no
packages="$(a shell pm list packages "$PKG" 2>/dev/null | tr -d '\r' || true)"
grep -q "^package:${PKG}\$" <<<"$packages" && preinstalled=yes
installed_by_us=no
# shellcheck disable=SC2329  # invoked by the EXIT trap
cleanup() {
  if [[ "$installed_by_us" == yes && "${KEEP:-0}" != 1 ]]; then
    a uninstall "$PKG" >/dev/null 2>&1 && echo "uninstalled $PKG (set KEEP=1 to keep it)"
  elif [[ "$preinstalled" == yes ]]; then
    echo "left $PKG installed: it was on the device before this run"
  fi
}
trap cleanup EXIT

echo "== install $APK"
if ! install_out="$(a install -r "$APK" 2>&1)"; then
  echo "FAIL install: $install_out"; exit 1
fi
echo "   ${install_out##*$'\n'}"
installed_by_us=yes
[[ "$preinstalled" == yes ]] && echo "   note: replaced an existing $PKG (cleanup will uninstall it)"

status=0
# Read the output into a variable first: `pm dump | grep -q` makes adb die of SIGPIPE under pipefail.
manifest="$(a shell pm dump "$PKG" 2>/dev/null || true)"
if grep -q "UnityWrapperProvider" <<<"$manifest"; then
  echo "   ok wrapper provider registered in the installed manifest"
else
  echo "   FAIL wrapper provider missing from the installed manifest"; status=1
fi

# A fresh run id per launch (see the header): the scenario name alone would make `native native` skip the second.
run=""
launch() {
  run="$1-$(date +%s)-$RANDOM"
  a shell am start -n "$PKG/$ACTIVITY" --es bugsee_e2e "$1" --es bugsee_e2e_run "$run" ${PRESET:+--es bugsee_preset "$PRESET"} >/dev/null
}

# How many times a log on stdin shows this run's scenario armed (-c reads all input: no SIGPIPE under pipefail).
armed_count() { grep -F "E2E armed scenario=$1 " | grep -cF "run=$run"; }

# Wait until the app logs that it armed this run's scenario (Start() ran and the SDK was launched).
wait_armed() {
  local deadline=$((SECONDS + READY_WAIT)) dump
  while (( SECONDS < deadline )); do
    dump="$(a logcat -d 2>/dev/null || true)"
    [[ "$(armed_count "$1" <<<"$dump")" -gt 0 ]] && return 0
    sleep 1
  done
  return 1
}

for scenario in "$@"; do
  scenario="$(tr '[:upper:]' '[:lower:]' <<<"$scenario")"   # the app lower-cases what it logs
  echo "== scenario: $scenario"
  a shell am force-stop "$PKG"; a logcat -c
  launch "$scenario"
  log="$OUT/$scenario.log"
  if ! wait_armed "$scenario"; then
    a logcat -d -v time > "$log"
    echo "   FAIL the app did not arm '$scenario' within ${READY_WAIT}s; this scenario proves nothing (log: $log)"
    status=1; continue
  fi
  # Android restarts the top activity after a crash, so "a process is running" proves nothing.
  # Compare the pid from before the scenario fires (it fires 5 s after arming) with the pid afterwards.
  pid_before="$(pid_of)"
  if [[ -z "$pid_before" ]]; then
    echo "   FAIL the app was not running right after it armed '$scenario'; this scenario proves nothing"
    status=1; continue
  fi
  sleep "$WAIT"
  a logcat -d -v time > "$log"
  armed="$(armed_count "$scenario" < "$log")"
  if [[ "$armed" -gt 1 ]]; then
    echo "   FAIL the scenario was armed $armed times in one run: the restart after a crash fired it again"; status=1
  elif grep -qF "E2E run $run already ran" "$log"; then
    echo "   ok the restart after the crash got the same run and armed nothing"
  fi

  if grep -q "Bugsee Android SDK" "$log"; then
    echo "   ok SDK launched: $(grep -m1 -o 'Bugsee Android SDK [^ ]* \[[^]]*\]' "$log")"
  else
    echo "   FAIL no SDK banner: the SDK did not launch, so nothing below means anything"; status=1
  fi

  pid_after="$(pid_of)"
  if [[ -n "$pid_before" && "$pid_before" == "$pid_after" ]]; then died=no; else died=yes; fi
  if liveness_unjudged "$scenario"; then
    echo "   info process died=$died (not judged: an ANR from the blocked UI thread may kill it)"
  elif fatal_scenario "$scenario"; then
    if [[ "$died" == yes ]]; then echo "   ok process died as intended"
    else echo "   FAIL process survived a scenario meant to kill it"; status=1; fi
  else
    if [[ "$died" == no ]]; then echo "   ok process still running"
    else echo "   FAIL process died unexpectedly"; status=1; fi
  fi
  echo "   crash markers: $(grep -cE 'FATAL EXCEPTION|Fatal signal|SIGSEGV|SIGABRT' "$log")  (BugseeTrackerBluetooth SecurityException spam excluded: $(grep -E ' E/(Unity|Bugsee)' "$log" | grep -vc BugseeTrackerBluetooth) other error lines)"
done

# Clocked like the scenarios: UPLOAD_WAIT starts once the app armed `none` (the SDK was launched), not at
# `am start`. The force-stop may cut short an upload by the restart after the last crash; this launch redoes it.
echo "== relaunch to recover and upload (${UPLOAD_WAIT}s after the app is up)"
a shell am force-stop "$PKG"; a logcat -c
launch none
if wait_armed none; then
  sleep "$UPLOAD_WAIT"
  a logcat -d -v time > "$OUT/recovery.log"
  echo "   RelaunchedAfterCrash seen: $(grep -c 'RelaunchedAfterCrash' "$OUT/recovery.log")"
else
  a logcat -d -v time > "$OUT/recovery.log"
  echo "   FAIL the recovery launch did not arm within ${READY_WAIT}s: crash reports were not uploaded (log: $OUT/recovery.log)"
  status=1
fi

echo "logs in $OUT"
exit $status
