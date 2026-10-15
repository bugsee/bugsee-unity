#!/bin/sh
# Type-checks Runtime/ and Editor/ for each Unity define set the package is built under, as two
# assemblies like the asmdefs (Editor references Runtime). References are community repackagings of
# Unity's assemblies on NuGet, pinned by version + lock files in Tools~/CompileCheck. No Unity license.
# Limits: the Editor reference is Unity 2020.2 plus small source stubs (UnityEditorStubs.cs,
# UnityEditorXcodeStubs.cs) for newer/missing APIs. Stubs cannot detect API gaps vs Unity 2021.3 itself.
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
'

for p in Runtime Editor; do
  dotnet restore "$dir/$p/Bugsee.$p.csproj" --locked-mode --nologo -v m || { echo "restore failed for Bugsee.$p.csproj"; exit 2; }
done

fail=0
for cfg in $configs; do
  name="${cfg%%|*}"; rest="${cfg#*|}"; player="${rest%%|*}"; defs="${rest#*|}"
  if [ "$player" = true ]; then proj="$dir/Runtime/Bugsee.Runtime.csproj"; else proj="$dir/Editor/Bugsee.Editor.csproj"; fi
  out="$(dotnet build "$proj" --no-restore --no-incremental --nologo -v q \
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
