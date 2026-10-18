#!/bin/sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname "$0")/../.." && pwd)"
BUILD="$ROOT/Tests~/iOS/.build"
ZIP="$BUILD/Bugsee-7.0.0-beta7.zip"
URL="https://download.bugsee.com/sdk/ios/spm/Bugsee-7.0.0-beta7.zip"
CHECKSUM="21b8209c2541c632b39085702e4b71930cf08673cbe05406cbd0b1783f87043b"

# A local SDK build (Tools~/scripts/assemble-local-ios-sdk.sh) replaces the published zip: pass its package
# directory (or its Bugsee.xcframework), or set BUGSEE_IOS_SDK_LOCAL_PACKAGE, as for the Unity export.
LOCAL="${1:-${BUGSEE_IOS_SDK_LOCAL_PACKAGE:-}}"

mkdir -p "$BUILD"
rm -rf "$BUILD/sdk"
mkdir -p "$BUILD/sdk"
if [ -n "$LOCAL" ]; then
    case "$LOCAL" in
        */Bugsee.xcframework) FRAMEWORK="$LOCAL" ;;
        *) FRAMEWORK="$LOCAL/Bugsee.xcframework" ;;
    esac
    if [ ! -d "$FRAMEWORK" ]; then
        echo "Bugsee.xcframework not found at $FRAMEWORK" >&2
        exit 1
    fi
    echo "Testing against the local SDK at $FRAMEWORK"
else
    if [ ! -f "$ZIP" ] || ! printf '%s  %s\n' "$CHECKSUM" "$ZIP" | shasum -a 256 -c - >/dev/null 2>&1; then
        curl -fsSL "$URL" -o "$ZIP"
        printf '%s  %s\n' "$CHECKSUM" "$ZIP" | shasum -a 256 -c -
    fi
    unzip -q "$ZIP" -d "$BUILD/sdk"
    FRAMEWORK="$(find "$BUILD/sdk" -type d -name Bugsee.xcframework | head -n 1)"
    if [ -z "$FRAMEWORK" ]; then
        echo "Bugsee.xcframework was not in the SPM zip" >&2
        exit 1
    fi
fi

case "$(uname -m)" in
    arm64) TARGET="arm64-apple-ios15.0-simulator" ;;
    *) TARGET="x86_64-apple-ios15.0-simulator" ;;
esac

# The device slice cannot run here, but the bridge has device-only branches (the simulator slice is the
# crash-reporter-less stub), so build both bridge files against it and link them, which also catches a symbol
# one file declares and the other does not define.
DEVICE_SLICE="$FRAMEWORK/ios-arm64"
if [ -d "$DEVICE_SLICE" ]; then
    clang -x objective-c++ -fobjc-arc -std=c++17 -dynamiclib \
        -target arm64-apple-ios15.0 \
        -isysroot "$(xcrun --sdk iphoneos --show-sdk-path)" \
        -F "$DEVICE_SLICE" \
        -framework Bugsee -framework Foundation -framework UIKit -framework CoreGraphics -framework QuartzCore \
        -lc++ -ObjC \
        "$ROOT/Plugins/iOS/BugseeUnityCallbacks.mm" \
        "$ROOT/Plugins/iOS/BugseeUnityBridge.mm" \
        -o "$BUILD/bridge-device.dylib"
    echo "Device slice: bridge compiles and links"
fi

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
