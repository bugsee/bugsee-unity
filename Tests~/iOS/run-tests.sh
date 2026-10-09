#!/bin/sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname "$0")/../.." && pwd)"
BUILD="$ROOT/Tests~/iOS/.build"
ZIP="$BUILD/Bugsee-7.0.0-beta5.zip"
URL="https://download.bugsee.com/sdk/ios/spm/Bugsee-7.0.0-beta5.zip"
CHECKSUM="af8f9670fddd3f94d7ee7d7fe2b3696c2d9f46ad49e5e523758a10e29f6f8123"

mkdir -p "$BUILD"
if [ ! -f "$ZIP" ] || ! printf '%s  %s\n' "$CHECKSUM" "$ZIP" | shasum -a 256 -c - >/dev/null 2>&1; then
    curl -fsSL "$URL" -o "$ZIP"
    printf '%s  %s\n' "$CHECKSUM" "$ZIP" | shasum -a 256 -c -
fi

rm -rf "$BUILD/sdk"
mkdir -p "$BUILD/sdk"
unzip -q "$ZIP" -d "$BUILD/sdk"
FRAMEWORK="$(find "$BUILD/sdk" -type d -name Bugsee.xcframework | head -n 1)"
if [ -z "$FRAMEWORK" ]; then
    echo "Bugsee.xcframework was not in the SPM zip" >&2
    exit 1
fi

case "$(uname -m)" in
    arm64) TARGET="arm64-apple-ios15.0-simulator" ;;
    *) TARGET="x86_64-apple-ios15.0-simulator" ;;
esac

SDK="$(xcrun --sdk iphonesimulator --show-sdk-path)"
SLICE="$FRAMEWORK/ios-arm64_x86_64-simulator"
BINARY="$BUILD/bridge-tests"

clang -x objective-c++ -fobjc-arc -std=c++17 \
    -target "$TARGET" \
    -isysroot "$SDK" \
    -mios-simulator-version-min=15.0 \
    -DBUGSEE_UNITY_TESTS=1 \
    -F "$SLICE" \
    -framework Bugsee \
    -framework Foundation \
    -framework UIKit \
    -framework CoreGraphics \
    -framework QuartzCore \
    -framework SystemConfiguration \
    -framework Security \
    -framework CFNetwork \
    -lc++ \
    -ObjC \
    "$ROOT/Plugins/iOS/BugseeUnityCallbacks.mm" \
    "$ROOT/Plugins/iOS/BugseeUnityBridge.mm" \
    "$ROOT/Tests~/iOS/BridgeTests.mm" \
    -o "$BINARY"

rm -rf "$BUILD/Bugsee.framework"
cp -R "$SLICE/Bugsee.framework" "$BUILD/Bugsee.framework"
install_name_tool -add_rpath @executable_path "$BINARY"
codesign --force --sign - "$BUILD/Bugsee.framework" >/dev/null
codesign --force --sign - "$BINARY" >/dev/null

UDID="$(python3 - <<'PY'
import json, subprocess, sys
data = json.loads(subprocess.check_output(["xcrun", "simctl", "list", "devices", "available", "-j"]))
for runtime, devices in data["devices"].items():
    if "iOS" not in runtime:
        continue
    for device in devices:
        if device.get("isAvailable") and device["name"].startswith("iPhone"):
            print(device["udid"])
            sys.exit(0)
sys.exit("No available iPhone simulator")
PY
)"

xcrun simctl boot "$UDID" >/dev/null 2>&1 || true
xcrun simctl bootstatus "$UDID" -b >/dev/null
xcrun simctl spawn "$UDID" "$BINARY"
