#!/bin/sh
# Type-checks Runtime/ and Editor/ for each Unity define set the package is built under, as two
# assemblies like the asmdefs (Editor references Runtime). References are community repackagings of
# Unity's assemblies on NuGet, pinned by version + lock files in Tools~/CompileCheck. No Unity license.
# Limits: the Editor reference is Unity 2020.2 plus small source stubs (UnityEditorStubs.cs,
# UnityEditorXcodeStubs.cs) for newer/missing APIs. Stubs cannot detect API gaps vs Unity 2021.3 itself.
# The android-player-unity-capture config type-checks the Unity 2022.2+ capture code against a local editor's Managed
# directory (UNITY_MANAGED_DIR, default: the installed 6000.0.84f1 editor); it is skipped with a notice when there is none,
# which is always the case on CI.
# The editor-*-no-xcode configs leave out the UnityEditor.iOS.Xcode stubs (BugseeNoXcode=true): an Editor without iOS Build
# Support, whose Bugsee.Editor assembly must still compile (issue #251).
# Usage: sh Tools~/scripts/compile-check.sh   (needs the .NET SDK; DOTNET_ROLL_FORWARD=Major is fine)
set -u

root="$(cd "$(dirname "$0")/../.." && pwd)"
dir="$root/Tools~/CompileCheck"

# name|player-only (true = Runtime assembly only, no UnityEditor)|defines
configs='
android-player|true|UNITY_ANDROID;BUGSEE_HAS_SCREENCAPTURE
android-player-il2cpp|true|UNITY_ANDROID;ENABLE_IL2CPP;BUGSEE_HAS_SCREENCAPTURE
ios-player-il2cpp|true|UNITY_IOS;ENABLE_IL2CPP;UNITY_2023_1_OR_NEWER
standalone-player|true|UNITY_STANDALONE_OSX
android-player-no-screencapture|true|UNITY_ANDROID
editor-android|false|UNITY_EDITOR;UNITY_ANDROID
editor-ios|false|UNITY_EDITOR;UNITY_IOS
editor-standalone-il2cpp|false|UNITY_EDITOR;UNITY_STANDALONE_OSX;ENABLE_IL2CPP
editor-android-no-xcode|false-noxcode|UNITY_EDITOR;UNITY_ANDROID
editor-module-less-no-xcode|false-noxcode|UNITY_EDITOR
'

for p in Runtime Editor; do
  dotnet restore "$dir/$p/Bugsee.$p.csproj" --locked-mode --nologo -v m || { echo "restore failed for Bugsee.$p.csproj"; exit 2; }
done

fail=0
unity_managed="${UNITY_MANAGED_DIR:-/Applications/Unity/Hub/Editor/6000.0.84f1/Unity.app/Contents/Managed}"
if [ -d "$unity_managed/UnityEngine" ]; then
  defs="UNITY_ANDROID;ENABLE_IL2CPP;BUGSEE_HAS_SCREENCAPTURE;BUGSEE_UNITY_CAPTURE"
  out="$(dotnet build "$dir/Runtime/Bugsee.Runtime.csproj" --nologo -v q -p:RestoreLockedMode=false "-p:NuGetLockFilePath=$dir/Runtime/obj-unity-capture/packages.lock.json" \
    -p:BaseIntermediateOutputPath="$dir/Runtime/obj-unity-capture/" -p:OutputPath="$dir/Runtime/bin-unity-capture/" \
    "-p:UnityManagedDir=$unity_managed" "-p:DefineConstants=$(printf '%s' "$defs" | sed 's/;/%3B/g')" 2>&1)"
  status=$?
  errors="$(printf '%s\n' "$out" | grep -E ': error ' | sed 's/ \[[^]]*\]$//' | sort -u)"
  if [ -z "$errors" ] && [ $status -ne 0 ]; then
    errors="build failed without a compiler diagnostic: $(printf '%s\n' "$out" | tail -n 5)"
  fi
  if [ -n "$errors" ]; then
    echo "FAIL  android-player-unity-capture ($defs)"; printf '%s\n' "$errors" | sed 's/^/    /'; fail=1
  else
    echo "ok    android-player-unity-capture ($defs)"
  fi
else
  echo "skip  android-player-unity-capture: no Unity 2022.2+ editor Managed directory (set UNITY_MANAGED_DIR)"
fi

for cfg in $configs; do
  name="${cfg%%|*}"; rest="${cfg#*|}"; player="${rest%%|*}"; defs="${rest#*|}"
  noxcode=false
  [ "$player" = false-noxcode ] && noxcode=true
  if [ "$player" = true ]; then proj="$dir/Runtime/Bugsee.Runtime.csproj"; else proj="$dir/Editor/Bugsee.Editor.csproj"; fi
  out="$(dotnet build "$proj" --no-restore --no-incremental --nologo -v q "-p:BugseeNoXcode=$noxcode" \
    "-p:DefineConstants=$(printf '%s' "$defs" | sed 's/;/%3B/g')" 2>&1)"
  status=$?
  errors="$(printf '%s\n' "$out" | grep -E ': error ' | sed 's/ \[[^]]*\]$//' | sort -u)"
  if [ -z "$errors" ] && [ $status -ne 0 ]; then
    errors="build failed without a compiler diagnostic: $(printf '%s\n' "$out" | tail -n 5)"
  fi
  if [ -n "$errors" ]; then
    echo "FAIL  $name ($defs)"; printf '%s\n' "$errors" | sed 's/^/    /'; fail=1
  else
    echo "ok    $name ($defs)"
  fi
done
exit $fail
