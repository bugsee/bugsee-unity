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

### Notes

- Symbol upload is opt-in via `BUGSEE_APP_TOKEN` and never fails the Unity build.
- iOS linemap UUID upload is performed at Xcode archive time when Unity export has no `UnityFramework` UUID yet.
