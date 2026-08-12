# Changelog

All notable changes to this package are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - Unreleased

### Added

- Initial UPM package scaffold (`com.bugsee.unity`).
- Android native SDK via EDM4U → Maven (`com.bugsee:bugsee-android:7.1.1` + `bugsee-android-ndk:7.1.1`).
- Android Gradle plugin `4.0.5` (NDK enabled on Unity 6+ launcher templates).
- iOS local SPM package (`Native~/ios/Bugsee`) + `Plugins/iOS/BugseeUnityBridge.mm` P/Invoke bridge.
- ExceptionPipeline managed capture (`CaptureManagedExceptions`) with UnityManagedException JSON-in-reason.
- `Bugsee.LogUnhandledException` on Android and iOS.
- Editor IL2CPP linemap upload (`BugseeIl2CppLinemapUpload`) and symbol orchestrator (`BugseeSymbolUpload`: ELF / ProGuard / archive dSYM).
- Hang / ANR / OOM launch-option docs and sample defaults.
- iOS bridge depth: identity get/set, attribute get, appearance, report/upload labels.
- iOS MonoPInvokeCallback channel (`BugseeUnityCallbacks.mm` + `IosNativeCallbacks`): network/log/breadcrumb filters, report handler via `BugseeWrapper`, lifecycle events.
- `Tools~/scripts/validate-field-symbols.sh` for S1/S2 prerequisite checks.
- IL2CPP native stack capture (`Il2CppNativeStack`) for managed Mode B file/line (`il2cpp_native_stack_trace`; UUID matches Bugsee symbol upload).
- Nested `cause` (InnerException / Aggregate) in UnityManagedException JSON.
- Docs: competitor matrix + Mode A/B/C for managed C# class/line (Mode B marked in progress until field-proven).

### Notes

- Symbol upload is opt-in via `BUGSEE_APP_TOKEN` and never fails the Unity build.
- iOS linemap UUID upload is performed at Xcode archive time when Unity export has no `UnityFramework` UUID yet.
- Managed `frames[].address`: IL2CPP thrown exceptions get native IPs when available (index-aligned, no reverse); short IL offsets (`[0x00023]`) are ignored.
- iOS terminating report handlers force-complete like Android (no async `done` wait).
- iOS filter/report JSON map normalize fail-closed (does not wipe headers/attributes on parse miss).
- Binary report attachments use base64 when bytes are not UTF-8 text.
