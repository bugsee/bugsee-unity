# Changelog

All notable changes to this package are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - Unreleased

### Changed

- Documented the secure-rectangle origin: `AddSecureRectangle`/`RemoveSecureRectangle` take a top-left-origin pixel rectangle on both platforms and pass it to native unchanged (no flip from Unity's bottom-left screen space). Behaviour is unchanged; the docs now include a conversion helper.

- Documented what `Launch`, `Relaunch`, `Stop` and `DeleteCollectedDataOnDevice` guarantee on each platform, the 10 s soft and 60 s hard deadlines, and the residual races: iOS, a late native stop (or wipe) can still affect a `Launch` made after the 60 s give-up; Android, a late launch/relaunch result after the hard reset is ignored when no stop intent is set, a queued `Launch` can run while a native delete is still wiping after the reset, and a delete handed to native can finish after its 10 s warning.

- Android Mono builds cannot symbolicate native frames inside the Mono runtime (`libmonobdwgc-2.0.so`, `libmono-native.so`, `libMonoPosixHelper.so`): Unity 2021.3 has no Mono ARM64 and those libraries have no GNU build-id. IL2CPP is the supported configuration for fully symbolicated native crashes. Documented in the Player Settings checklist, and a Mono Android build now logs a one-time warning at build time regardless of symbol upload settings (the build is never failed).

### Fixed

- Android Gradle injection: a project counts as declaring the SDK only for a direct `implementation`/`api`/`runtimeOnly` (or `debug`/`release` prefixed) argument of a top-level `dependencies {}` statement (coordinate string, `group`/`name` pair, or local AAR); mentions in `compileOnly`, test configurations, `exclude(...)`, `platform(...)`, `fileTree(...)`, `constraints`, `resolutionStrategy` or variables never count, wrapped over several lines or not, and an earlier injection is removed only for a confirmed declaration. A `mavenCentral { content { ... } }` repository still is Maven Central, but it only satisfies the Bugsee requirement when its filter serves `com.bugsee:bugsee-android` and `bugsee-android-ndk` (no matching `exclude*`, and any `include*` covers both); otherwise an unfiltered `mavenCentral()` is added next to it. A declaration inside an `if`/`else`/`with`/`for`/`while` block counts only for not injecting a duplicate; it never causes an earlier injection to be removed (the condition is not evaluated). Known limitations: version-catalog accessors (`libs.bugsee...`), flavor-prefixed configurations other than `debug`/`release` (a duplicate of the same coordinate may be injected) are not interpreted. Repository filters are read for `mavenCentral { content {} }`, `maven { url <Maven Central> content {} }` and `exclusiveContent { forRepository { mavenCentral() } filter {} }`; other repository forms or unparsable filters are treated as not serving Bugsee, and an unfiltered `mavenCentral()` is added.
- Android Gradle injection now supports Kotlin-DSL projects (`build.gradle.kts`, `settings.gradle.kts`; dependencies are written as `implementation("g:a:v")`), and respects `repositoriesMode`: `mavenCentral()` is added to the settings repositories, also to root `allprojects` under `PREFER_PROJECT`/unset, and never to project repositories under `FAIL_ON_PROJECT_REPOS`. Bugsee declarations in `compileOnly`/test configurations or `exclude(...)` no longer count as the project declaring the SDK; `mavenCentral { ... }` blocks and a missing settings `repositories` block under `PREFER_SETTINGS`/`FAIL_ON_PROJECT_REPOS` are handled. Verified against the Unity 2021.3.45 Gradle templates and by exporting (not building) the Android Gradle project with Unity 6000.0.84f1 (default and custom templates); Unity 2022.3.62f3 was checked with real IL2CPP builds (APK build and Export Project; AGP 7.4.2, Gradle 7.5.1), its generated Gradle files are checked in under `Fixtures/Unity2022.3.62f3-*` and covered by `UnityGeneratedLayoutTests`. Injecting the Gradle plugin lines no longer adds an extra blank line at the end of a file that ends with a newline.
- iOS `Bugsee.LogException(exception, options)` dropped its options. `labels`, `includeVideo` (default kept on), `mergingRules` and the top-level `skipFrames` (as on Android) now reach the SDK (`BugseeExceptionLoggingOptions`); `domain` is wrapper-owned on both platforms. The unhandled path has no options in the SDK. Android-only launch options with no iOS counterpart (`CaptureLogsUseAllSources`, `CaptureRespectFlagSecure`, `CaptureVideoMode`, `CaptureVideoUsingCustomMuxer`, `ReportProcessingInProcess`, `WebViewDomainAllowlist`) are no longer sent to iOS. The wrapper callbacks now share the tested `JsonWire` string encoder.
- Android `IReport.Attributes` and `IReport.Attachments` returned empty stubs; they now read the native report. Attachment name/file name/MIME type setters are now called with the fluent `Attachment` return type, so they resolve over JNI.
- Added `INetworkEvent.IsOverride` (Android: maps `NetworkEvent.isOverride`; iOS: no native equivalent, kept on the managed event only).
- iOS appearance setters and getters were a no-op because `+[Bugsee getAppearance]` returns a `BugseeTheme`, not a `BGSAppearance`. They now map onto `BugseeTheme` (report/feedback colors, report placeholders). Properties with no iOS equivalent are skipped and read back null: Report `ActionBarTextColor`, `ActionBarButtonBackgroundClickedColor`, `SeverityLabelActiveColor`; Feedback `ActionBarButtonBackgroundClickedColor`, `DateTextColor`, `EmailSkipBackgroundClickedColor`, `EmailContinueClickedColor`, `InputTextHintColor`, `BottomDelimiterColor`, `LoadingBarBackgroundColor`, `LoadingTextColor`, `ErrorTextColor`, `VersionChangedBackgroundColor`, `VersionChangedTextColor`; `Notification::Title`.

### Added

- EXPERIMENTAL, Android only, off by default: `Bugsee.Capture.BugseeFrameCapturer.Enable()` pushes Unity's own screen to the SDK through `VideoMode.DirectBuffers` and `VideoFrameConsumer.addFrame` (`ScreenCapture.CaptureScreenshotIntoRenderTexture` + `AsyncGPUReadback`, one native copy into a reused buffer, masked in place and wrapped with `AndroidJNI.NewDirectByteBuffer`: no Java array; the wrapper writes no frame to disk, the SDK still writes its recorded video), one request in flight with frames dropped meanwhile, throttled to `TargetFps` (default 5). **Supported on release builds of Unity 2022.2.0f1 and newer (2022.3 LTS, 6000.x); not on 2021.3 or 2022.1** (`AndroidJNI.NewDirectByteBuffer` first appears in 2022.2.0a11; alphas and betas are treated as unsupported): there `Enable()` returns false with one warning and the SDK's own capture stays in effect. A launch that asks for `VideoMode.DirectBuffers` starts the capturer itself, and whenever it cannot run (old Unity, no Screen Capture module, no AsyncGPUReadback, a graphics API other than OpenGLES3 or Vulkan) the wrapper replaces `DirectBuffers` with the default capture (`VideoMode.V2`) before the native SDK launches, with one warning, and if the capturer cannot start after the launch was accepted (or stops itself later) it relaunches with the default capture, unless a newer Launch/Relaunch is waiting; it never launches `DirectBuffers` without a frame source. It captures Unity's final composite including screen-space overlay UI but not native Android views. Secure rectangles are blanked in the pushed frames (registered at capture start or at readback); pixelation and secure views are not applied; blackout is honoured by the SDK. The readback row order was measured for Vulkan (Galaxy Tab S5e) and OpenGLES3 (emulator), both on Unity 6000.0.84f1. On any other graphics API the row order is unverified: a launch falls back to the default capture, and a capturer started directly with `Enable()` fails closed (fully black frames while a secure rectangle is registered) until `BugseeFrameCapturerOptions.RowOrder` is set. Needs the `com.unity.modules.screencapture` module. Measured once on a Galaxy Tab S5e (Vulkan, light sample scene): it is not lighter than the SDK's default capture (same frame rate and about the same CPU, about 15 MB more PSS). Nothing changes unless you call `Enable()`. Refs #22 (benchmarking against the SDK's default capture on more devices is outstanding).
- `Bugsee.GetLaunchOptions()` (Android and iOS): the effective launch options (SDK defaults merged with the host's) as a read-only dictionary, or `null` while the SDK is not launched. Keys are the native SDKs' own option names.
- `Bugsee.Notify(title, body, severity, fields, urgent)` (Android and iOS): relays a lightweight notification to the app's messaging integrations without creating a report. An empty title is ignored with a warning.
- Initial UPM package scaffold (`com.bugsee.unity`).
- Android native SDK from Maven Central (`com.bugsee:bugsee-android:7.4.1` + `bugsee-android-ndk:7.4.1`), declared in the generated Gradle project at build time. Installing is just adding the package: no EDM4U, no OpenUPM scoped registry, no resolve step, no custom Gradle templates. A Bugsee dependency the project already declares (EDM4U, custom template, local AAR) is kept, and a different version is reported. Core AAR publishes no transitives; NDK is declared explicitly.
- Android Gradle plugin `4.0.8` (NDK enabled on Unity 6+ launcher templates).
- iOS SPM-only native SDK: Xcode resolves [`bugsee/spm`](https://github.com/bugsee/spm) `7.0.0-beta6` + `Plugins/iOS/BugseeUnityBridge.mm` P/Invoke bridge (7.x selectors; iOS 15 deployment target).
- ExceptionPipeline managed capture (`CaptureManagedExceptions`) with UnityManagedException JSON-in-reason.
- `Bugsee.LogUnhandledException` on Android and iOS.
- `Bugsee.LaunchAsync`, `RelaunchAsync` (`Task<bool>`) and `StopAsync` (`Task`) on iOS and Android: they complete when the SDK has answered. `StopAsync` faults with `TimeoutException` when the SDK has not reported the stop within 10 s of the call (the native stop may still be running, and later launches stay deferred until it reports, or 60 s pass); on iOS it also faults when the main thread did not take the call within 5 s. The existing `Launch`/`Relaunch`/`Stop` signatures are unchanged.
- On Android, `LaunchAsync`/`RelaunchAsync` complete true when the SDK accepted the launch and false when it refused it or the request was superseded; if the SDK gives no answer within 10 s they complete true when it is already Launching/Launched, otherwise false; `StopAsync` is not dropped by a Launch that follows it, and the 10 s deadline counts from each call, also for one that joins a stop already in flight. Android hands every lifecycle call to the Unity main thread (from another thread it is queued there and never refused), so a stalled main thread leaves the Task pending.
- Editor IL2CPP linemap upload (`BugseeIl2CppLinemapUpload`) and symbol orchestrator (`BugseeSymbolUpload`: ELF / ProGuard / archive dSYM).
- Hang / ANR / OOM launch-option docs and sample defaults.
- iOS bridge depth: identity get/set, attribute get, appearance, report/upload labels.
- iOS MonoPInvokeCallback channel (`BugseeUnityCallbacks.mm` + `IosNativeCallbacks`): network/log/breadcrumb filters, report handler via `BugseeWrapper`, lifecycle events.
- `Tools~/scripts/validate-field-symbols.sh` for S1/S2 prerequisite checks.
- IL2CPP native stack capture (`Il2CppNativeStack`) for managed Mode B file/line (`il2cpp_native_stack_trace`; UUID matches Bugsee symbol upload).
- Nested `cause` (InnerException / Aggregate) in UnityManagedException JSON.
- Sample: configurable e2e build helper, crash scenarios (`native`, `abort`, `fatal`, `java`, `hang`) and `Tools~/scripts/e2e-android.sh`, which checks device, SDK launch and process death by pid change.
- Docs: competitor matrix + Mode A/B/C for managed C# class/line (Mode B marked in progress until field-proven).
- Pre-launch native wrapper registration: one process-lifetime wrapper is registered before SDK launch—Android `UnityWrapperProvider` (`ContentProvider`, `initOrder` 200), iOS bridge `+load`—instead of constructing a second wrapper from C# `Launch`.
- Wrapper-channel submit: host logs and game-supplied network events go through the stored wrapper channel (Android `UnityWrapper.channelLog` / `channelAddNetwork`; iOS P/Invoke twins), not the public Bugsee log or `addNetworkEvent` APIs.
- Secure-rectangle pull buffer: `AddSecureRectangle`, `RemoveSecureRectangle`, and `RemoveAllSecureRectangles` push a versioned packed buffer; native `getSecureRectangles` (Android) and `secureRectanglesForDisplay:` (iOS) return the last pushed array.
- Fail-closed filter callbacks: network, log, and breadcrumb filters that throw drop the event instead of forwarding the original line.
- Severity `0` is unset, not `VeryLow`: omitted from create-report JSON; `IReport.Severity` is `IssueSeverity?`.
- Public facade: `DeleteCollectedDataOnDevice`, `CreateReport`, `AddBreadcrumb`, and `AddNetworkEvent`.

### Changed

Behaviour changes to be aware of. 0.1.0 is not released, so these correct earlier unreleased work.

- Native SDKs: Android `bugsee-android` / `bugsee-android-ndk` 7.4.1 and iOS `bugsee/spm` 7.0.0-beta6.
- Android `DeleteCollectedDataOnDevice` now stops the SDK whenever it is not stopped and also deletes finished
  report bundles, **including reports that were not uploaded yet**. Before, it kept all finished bundles
  (the native two-argument overload does not delete them) and could delete before the SDK had stopped.
- Android `TestCrash` now crashes the app with an uncaught exception thrown on the Android main looper, and
  returns before the process dies. Before, the exception came back to C# as a caught `AndroidJavaException`: no
  crash was reported and the app kept running.
- iOS `IsLaunched` is now true only while the SDK is in its `Launched` state (it is false during the short
  `Launching` window), and `Status` reports the SDK's own state, including `Launching` and `Stopping`.
  `Stop(completion)` runs the completion when the SDK reports the stop finished, and only one native stop runs at
  a time: a second `Stop` or a delete during it waits for it. A `Launch` or `Relaunch` requested while a stop is
  in flight is deferred until it finishes (the latest request wins; a later `Stop` or delete cancels it). A
  completion is dropped when a `Launch` or `Relaunch` happened before the stop finished, and when the stop could
  not be started because the main thread did not take the call within 5 s. After 10 s the call's own completion
  runs with a warning while the native stop stays in flight (launches stay deferred); after 60 s the wait is
  abandoned and deferred launches run.
- iOS `DeleteCollectedDataOnDevice` now runs in request order, as on Android, and is no longer skipped. Before, a
  delete did nothing (with a warning) when a `Launch` or `Relaunch` ran between its request and its teardown:
  `Stop(() => Launch(...)); DeleteCollectedDataOnDevice();` and, after the 60 s give-up on a stop, a delete
  queued behind it. Now the wipe runs first, and a `Launch` or `Relaunch` requested after it waits until the SDK
  reports the wipe finished (it clears its files on a background thread), with the same 60 s give-up as a stop.
  A delete that shares a stop with `Stop(() => Launch(...))` runs ahead of that completion. A delete that finds
  the SDK still stopping waits for that stop (up to 10 s) instead of stopping it again; if it never
  stops, or the SDK reports the wipe failed, a warning is logged and a waiting `Launch` still proceeds.
- Android `Launch`, `Relaunch` and `DeleteCollectedDataOnDevice` run one at a time, in request order, on the main
  thread, while `Stop` is not held in that order (it starts or joins a native stop at once, and drops queued
  `Launch`/`Relaunch` requests; only the hand-off from another thread is queued, as for every Android lifecycle call): a `Launch` or `Relaunch` during a stop is deferred, a
  `Relaunch` is ignored with a warning unless the SDK is `Launched`, a refused `Launch` no longer cancels a pending
  delete, and a late launch result after a newer `Stop` is stopped again. In-flight operations answer their callers
  after 10 s and are abandoned after 60 s.
- iOS wrapper-channel network events are sent with the SDK's own event types (`before`, `complete`, `cancel`,
  `error`) and a Unix-millisecond timestamp. Events with a stage iOS cannot represent (`Redirect`,
  `RequestTimingsReceived` and `WebSocket`) are **dropped with a warning**.
- `GetAttribute` keeps the kind of a scalar on both platforms: a string reads back as `string`, a bool as `bool`,
  an integer input as an integer and a floating-point input as floating point (a whole-valued one such as `1.0`
  stays floating point on iOS, it does not become an integer), so the string "1", the number 1 and `true`
  are distinct. The width is not promised (Android returns the boxed type that was set, with `sbyte`/`ushort`
  stored as `int`, `uint`/`ulong` as `long` and `decimal` as `double`; iOS returns `long` and `double`), so
  compare with `Convert` rather than a cast. A container set natively (not through this API) reads as a
  `Dictionary` on Android and as its JSON text on iOS. Reports still show the natural value.
- iOS launch and relaunch now send `wrapper_info` (type, version, build, metal, fps) unless the caller supplied
  one. This changes the SDK's video capture to its Unity path. `fps` is `Application.targetFrameRate` (30 when
  unset), never above `Screen.currentResolution.refreshRate` because the SDK uses it as a frame-skip divisor; it
  is read once at launch. A caller-supplied `wrapper_info` is kept only when it is a map whose `type` is a
  non-empty string; anything else is replaced, with a warning. Not yet validated on a device.
- Android lifecycle events reach C# through the wrapper only, so events fired before C# starts (auto-launch,
  crash recovery) are replayed once, in order, when the listener is attached.
- `Debug.LogException` called from a background thread now reports (it used to throw `UnityException` from the
  dedupe clock and report nothing).

### Fixed

- iOS: `SetAttribute`/`GetAttribute` crashed on string, number and bool values (`NSJSONSerialization` rejects a
  bare scalar).
- iOS: the wrapper channel survives `Stop` and `DeleteCollectedDataOnDevice`; the SDK never redelivers it, so
  clearing it silenced channel logs, network events and breadcrumbs for every later launch.
- iOS: `DeleteCollectedDataOnDevice` could leave the managed hooks installed over a stopped SDK while it was
  still launching; `Relaunch` never marked the SDK as launched.
- iOS: launch options with a tab or other control character, nested maps or lists, enums, or NaN/Infinity now
  produce valid JSON (they used to produce JSON the SDK rejected, so launch options silently fell back to
  defaults).
- iOS: the managed-upload cancel state is lock-guarded; the no-SDK stub compiles.
- iOS: the injected "Bugsee Upload Symbols" Run Script failed every iOS build with "Permission denied" (the script is
  written without an executable bit); it now runs through `sh` and cannot fail the build.
- iOS: dSYMs are generated for every build configuration of Unity-iPhone and UnityFramework
  (`DEBUG_INFORMATION_FORMAT=dwarf-with-dsym`, `GCC_GENERATE_DEBUGGING_SYMBOLS=YES`; opt out with
  `BUGSEE_NO_DSYM_FORCE` or the Bugsee > iOS menu toggle).
- iOS: the "Bugsee Upload Symbols" Run Script uploads for every Xcode configuration, Debug included (it used to
  skip all but Release unless `BUGSEE_UPLOAD_ON_DEBUG=1`); `BUGSEE_SKIP_UPLOAD_ON_DEBUG=1` skips Debug. An export
  appended over one made by an earlier version has its old failing phase replaced.
- Android: the pre-launch wrapper provider is injected into the generated manifest. Unity 2021.3 does not merge
  a package's loose `Plugins/Android/AndroidManifest.xml`, so it was missing from the APK and early registration
  never happened (a crash recovered on the next start was assembled without the wrapper).
- Android: the JNI completion callbacks work: `BooleanCallback1Proxy` never matched the unboxed `Boolean` the SDK
  passes (so the delete completion never fired), and the lifecycle listener threw "Ambiguous overloads" at launch.
- Android: `Relaunch` does nothing (with a warning) unless the SDK is `Launched`, as the native call would never
  call back, and a refused `Launch` no longer cancels a pending delete.
- Android: a launch the SDK refuses (made before a stop finished, empty or invalid token) switches the managed
  capture hooks off, without affecting a later launch that works.
- Android: the early wrapper reports the real version and build instead of `"unknown"`.
- Android: the runtime assembly did not compile (`=> checked { ... }` is not valid C#); two scripts lacked `.meta`
  files.
- Exception capture: every main-thread `Debug.LogException` was reported twice (once as an exception, once from
  Unity's log text, with different signatures); the log text is now recognised as the echo of the exception
  (per thread, around the inner `LogException` call), and an exception whose send failed is still reported by
  the echo.
- Android symbols: the R8 mapping Unity writes as `<player>_mapping.txt` was never uploaded; the wrong build's
  symbols zip or mapping could be chosen (another configuration's files looked newer); the IL2CPP linemap was
  skipped for want of a module id, which is now read from this build's symbols zip.
- `Tools~/scripts/update-native-sdks.sh` no longer stops on an offline `git ls-remote` before writing the Android
  identity file (`UnityWrapperIdentity.java`), so Java and C# keep agreeing on the version.

### Notes

- Symbol upload is opt-in via `BUGSEE_APP_TOKEN` and never fails the Unity build.
- The iOS `Bugsee Upload Symbols` Run Script uploads for every Xcode configuration; `BUGSEE_SKIP_UPLOAD_ON_DEBUG=1` skips Debug (replaces `BUGSEE_UPLOAD_ON_DEBUG`).
- iOS linemap UUID upload is performed at Xcode archive time when Unity export has no `UnityFramework` UUID yet.
- Managed `frames[].address`: IL2CPP thrown exceptions get native IPs when available (index-aligned, no reverse); short IL offsets (`[0x00023]`) are ignored.
- iOS terminating report handlers force-complete like Android (no async `done` wait).
- iOS filter/report JSON map normalize fail-closed (does not wipe headers/attributes on parse miss).
- Binary report attachments use base64 when bytes are not UTF-8 text.
