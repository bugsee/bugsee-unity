# Bugsee Unity SDK — Design

UPM package `com.bugsee.unity` (repo: `bugsee/bugsee-unity`).  
Legacy foundation: `cross/unity`. Min Unity: **2021.3**.

## Understanding

- **What:** UPM-first Unity SDK whose public C# API mirrors Android SDK **7.x** (`Bugsee` facade + public `contracts`), with platform bridges for Android (Maven, declared in the generated Gradle project) and iOS (remote SPM `github.com/bugsee/spm`).
- **Why:** Replace legacy `.unitypackage` / `Assets/Plugins` distribution; align with 7.0 redesign and other wrappers (Flutter 7.x pattern).
- **Who:** Unity game/app developers; Bugsee maintainers.
- **Non-goals (initial implementation):** OpenUPM publish, dual release repo, Asset Store `.unitypackage`, full iOS nextgen API parity deep-dive before Android bridge works, RN 6.x bridge patterns.

## Assumptions

1. Android pin: Maven `com.bugsee:bugsee-android:7.5.0` + `bugsee-android-ndk:7.5.0`, injected into the generated `unityLibrary/build.gradle` by an `IPostGenerateGradleAndroidProject` step (no EDM4U, no custom templates); Gradle plugin `4.1.1`. Core publishes no transitives (no kotlin-stdlib / fragment pull); NDK must be declared. Feedback AAR stays optional: opt-in with the scripting define `BUGSEE_FEEDBACK` (or env `BUGSEE_FEEDBACK=1`), which also links the iOS `BugseeFeedback` package; see Documentation~/index.md.
2. iOS: SPM-only. Pin [`bugsee/spm`](https://github.com/bugsee/spm) `7.0.1` (`IOS_SDK_VERSION` in `Tools~/versions.env`). Xcode resolves the remote package at export; no CocoaPods and no vendored xcframework.
3. Public C# surface mirrors Android 7.0; intentional C#/.NET deviations allowed (see below).
4. SDK-internal contracts (`capture` aggregators, most `contracts.internal.*` except what wrappers need for `BugseeWrapper`) are **not** public C# API.
5. Optional Gradle network extensions (OkHttp/Ktor/Cronet) are out of default Unity deps unless explicitly added later.

## Decision log

| Decision | Choice | Alternatives | Why |
|---|---|---|---|
| Repo layout | UPM package at repo root | `package/` subfolder; Sentry dual-repo | Simplest Git URL install |
| Android natives | Maven, declared by a post-generate Gradle step | EDM4U (needs OpenUPM scoped registry + resolve step); vendored Unity AAR | Install is just the UPM package; matches Flutter/RN 7.x; smaller repo |
| iOS natives | Remote SPM `github.com/bugsee/spm` | Vendored xcframework; CocoaPods | iOS 7.x is SPM-only |
| Min Unity | 2021.3 | 2018.3 / Unity 6 only | SPM `PBXProject` APIs + practical floor |
| Primary C# API | Android 7.0 mirror | Keep legacy `BugseePlugin` names | Clean 7.0 surface; legacy shims optional later |
| Launch options shape | Shared base + platform subclasses | Single shared type; two independent types | Legacy Unity DX; platform-only props stay typed |
| Launch options API | Write-only properties (no getters) | Mutable get/set; fluent-only builder | Omit unset keys → native defaults; no false “read your writes” |
| Launch options defaults | Empty until set | Constructor-filled defaults | Native SDK owns defaults when key omitted |
| Launch overloads | Single `Dictionary` param (+ implicit from typed/`OptionsBuilder`) | Separate typed + `IDictionary` overloads | Avoids CS0121 on `Launch(token, null)`; typed still feels first-class |
| Options secondary API | Keep `Options.*` keys + `OptionsBuilder` | Hide keys; drop builder | Advanced / custom keys without forcing the map path alone |
| Options property names | Android 7.0 contract names | Legacy Unity names; dual aliases | Clean 7.0 mirror; **revisit unify when iOS SDK is RC/stable** |
| `BugseeWrapper` | Unity implements + `Bugsee.setWrapper` | App-only listeners | Required wrapper contract for metadata, secure rects, report/lifecycle hooks, `requestData` |
| Filters/handlers | First-class C# callbacks + JNI proxies | Omit (legacy gap) | Parity with Android/Flutter |
| C# deviations | Allowed where idioms win | Pure Java-shaped API | Better Unity/.NET DX |
| Managed exception signatures | One key per event; Unity primary when strong (RVA / file:line); weak = canonical method tokens; no append | Worker always rehash; dual Unity+native keys | Merge set must stay bounded; raw stacks are not device-stable. See `Documentation~/exception-signatures.md` |

### C# idiom deviations (approved)

| Android | C# |
|---|---|
| `EventFilter.filter(T, Callback1<T>)` | Prefer sync `Func<T,T>` (null/drop = omit); async completion only when needed |
| `Callback1` / `Runnable` | `Action` / `Action<T>` |
| `Map<String,Serializable>` options | Typed write-only `*LaunchOptions` (primary) **+** `OptionsBuilder` / raw dictionary (secondary) |
| `Bugsee.ext(Feedback.class)` | `Bugsee.Feedback` property (resolves extension under the hood) |
| Java listeners | C# `event` / `Action<>` on `Bugsee` |
| Lifecycle string constants | `LifecycleEvents` consts **+** optional enum for known events |
| `IssueSeverity` | Same values on both 7.x SDKs: `Critical = 4` exists and matches Android and iOS (`VeryLow = 1` is iOS `Low`; iOS `0` is unset); no mapping is needed |

## Launch options (approved)

Legacy reference: `cross/unity/.../LaunchOptions/` (`BugseeLaunchOptions` + `AndroidLaunchOptions` / `IOSLaunchOptions`).

### Types

| Type | Role |
|---|---|
| `BugseeLaunchOptions` | Base; internal map; shared write-only props; `SetCustomOption(key, value)`; serialize via `ToDictionary()` (not for direct `new` by apps) |
| `AndroidLaunchOptions` | Android-only write-only props (video mode, notification/broadcast triggers, …) |
| `IOSLaunchOptions` | iOS-only write-only props (stub/minimal until iOS bridge); shared props via base |

### Semantics

- **Write-only properties** — setters write into the map; no public getters.
- **Omit unset keys** — empty until set; no constructor defaults that pre-fill the map. Native SDK applies defaults for omitted keys.
- **Wire keys** — property setters store under Android 7.0 `Options.*` (the iOS SDK reads the same key strings; the bridge does not translate keys). Enums as wire ints/bytes; bridge coerces to native enums (`AndroidOptionsMapper`).
- **Platform gate** — the bridge does not translate keys (only `AndroidOptionsMapper` coerces the enum-valued Android keys to SDK enums and boxes scalars; byte/short/uint/array values are dropped with a warning). `OptionPlatformGate` drops, before the native call, the keys the other SDK does not know, because iOS keeps an unknown key verbatim (it shows in `GetLaunchOptions` and in `sdk.options` of every report): `ForIos` removes the Android-only keys (the exit sub-reasons `detect.exit.*` but not the shared `detect.exit`, notification/broadcast triggers, video mode, custom muxer, secure scrolling, fullscreen remember/keep-running, report-handler callback timeout, performance upload mode, NDK/sampling/pending-report keys, ...), including the leak-module keys `Options` has no constant for (the wrapper does not ship that module); `MaxDataSize` and `NotifyFlushDelay` are shared and not gated; `ForAndroid` removes the iOS-only `capture.crash-handler-type` (Android 7.4.2 drops unregistered keys itself, so that is defensive). A test requires every `Options` constant to be classified as Android-only, iOS-only, shared or wrapper. The `unity.*` wrapper keys are read by the wrapper from the managed dictionary and are not gated (iOS keeps them in `sdk.options`; gating them is a possible follow-up).
- **Custom keys** — `SetCustomOption(string, object)` on the base (and/or raw map / `OptionsBuilder`).

### Facade

- Primary: `Bugsee.Launch(token, androidOptions)` / `iosOptions` via implicit conversion to `Dictionary<string, object>` (single optional-map overload — avoids `Launch(token, null)` ambiguity across typed types).
- Secondary: `Launch(token, dictionary)`, `OptionsBuilder` (same implicit conversion), public `Options.*` constants, `SetCustomOption`.
- `Relaunch` mirrors `Launch`, except that its options replace the option set instead of merging.

### Naming revisit (explicit follow-up)

Property / key naming currently follows **Android 7.0**. When the Bugsee **iOS** SDK reaches **RC or stable**, revisit options naming to properly unify the cross-platform typed surface (base vs platform splits and any divergent key vocabularies).

### Do not copy (stale / wrong)

- Flutter still calling removed `setAdditionalDataCapture` / `setSystemSecureRects` → use **`BugseeWrapper`** instead.
- React Native Android bridge (still 6.x packages).
- Legacy Unity V4 `Snapshot` / `onNewFrame` path.
- Bundled 6.x `Bugsee-Unity.aar` / `BugseeUnityAdapter`.

## Native packaging (already scaffolded)

- `package.json` → `com.bugsee.unity`, no package dependencies
- `Editor/BugseeAndroidMavenDependencies.cs` → declares Maven 7.5.0 (+ NDK, both explicit) in the generated Gradle project; text edits in `Runtime/WrapperPolicy/GradleMavenInjection.cs`. A project-declared Bugsee dependency (EDM4U, custom template, local AAR) is kept and a version mismatch warned about; `.gradle.kts` files (project-converted; Unity generates Groovy) are edited in Kotlin syntax and `repositoriesMode` (`FAIL_ON_PROJECT_REPOS`) is respected; a project declaration is only a direct runtime-configuration argument in a top-level `dependencies {}` (never compileOnly/test/exclude/platform/constraints/variables), and a filtered `mavenCentral { content {} }` counts only if its filter serves `com.bugsee`. `maven { url <Central> content {} }` and `exclusiveContent` filters are read too. A declaration nested in an `if`/`else`/`with` block suppresses injection but never removes an earlier one. Not interpreted: version-catalog accessors, flavor-prefixed configurations
- `Editor/BugseeAndroidGradleSetup.cs` → applies the Bugsee Gradle plugin on AGP 8.6+; text edits in `Runtime/WrapperPolicy/GradlePluginInjection.cs` (Groovy and Kotlin DSL, root `plugins {}` or `buildscript` classpath, launcher `apply plugin:` or `plugins {}` after AGP; never ahead of AGP, never when it cannot be done safely: a warning names the lines to add) and `GradleVersionCatalog.cs` (AGP version through `libs.versions.toml`); `pluginManagement` repositories get Maven Central when they list neither it nor the plugin portal. The plugin cannot see an app token in a Unity build (it reads only its DSL, `bugsee.properties` and the manifest, never the environment), so its own mapping/native/build-info uploads skip; the Editor's upload is the symbol path for a build the Editor runs. For an Export Project (nothing for the Editor to upload) the `bugsee { }` block reads `BUGSEE_APP_TOKEN`/`BUGSEE_ENDPOINT`/`BUGSEE_CLI_PATH` from the environment of the Gradle process at Gradle time (`GradlePluginInjection.BlockLines(gradleUploads: true)`), so no token is stored in the exported project; enabling it for every build would only repeat the Editor's mapping and ELF uploads and add build-record registration to Editor builds.
- `Editor/BugseeIosSpmPostProcess.cs` → remote SPM `github.com/bugsee/spm` `7.0.1`
- `Plugins/iOS/BugseeUnityBridge.mm` + `Plugins/Android/UnityManagedException.java`
- `Tools~/scripts/update-native-sdks.sh` + `versions.env`
- `Tools~/scripts/update-options-manifest.sh` + `Tests~/WrapperPolicy/Fixtures/options-manifest/` → the native SDKs' published option manifests (`download.bugsee.com/sdk/options/<android|ios>/<version>.json`, spec `bugsee/specs` `sdk/options`) for the pinned versions, vendored verbatim with their sha256. `OptionsManifestTests` checks the option surface against them, offline: every `Options` constant is a native key or a listed wrapper-only key; setter types and enum values (the SDK's internal ints) agree; every non-hidden native option is exposed or in an allow-list with its issue (Android #96, iOS #97: the allow-lists now hold only deliberate non-exposures); hidden options are not exposed unless listed (#108); the wrapper's hardcoded enum fallbacks and the documented iOS crash-handler default equal the manifest defaults. A bump of an SDK pin without its manifest fails `Vendored_manifests_are_verbatim_copies_of_the_pinned_versions`; a native option nobody decided about fails with the instruction to add a constant and setter or an allow-list entry; an allow-list entry that is no longer needed fails too, so the lists only shrink. Defaults are compared only against the manifest of the platform they apply to (they differ between the SDKs on purpose). The sha256 in `sources.txt` only detects an accidental edit of a vendored file, it is not a signature (re-recording it passes; the manifest's own `sdkVersion` and the immutable published URL are the provenance). Not checked by it: whether `OptionPlatformGate` drops every Android-only key on iOS, and so the spec's per-platform "strict on keys" rule is only approximated (a constant must be a key of at least one manifest, not of the manifest of every platform it is sent to) until the gate's classification (#148) can be cross-checked. Open: `FrameRate.Raw` goes to iOS through the shared setter although `BugseeFrameRate` has no such value (frozen in `CsharpEnumValuesIosLacks`).

## Runtime project structure

```
Runtime/
├── Bugsee.cs                         # Static facade mirroring Bugsee.java
├── Bugsee.Runtime.asmdef
│
├── Contracts/                        # Public mirror of com.bugsee.library.contracts
│   ├── Options/                      # Options keys, enums, OptionsBuilder,
│   │                                 # BugseeLaunchOptions + Android/IOS subclasses
│   ├── Exchange/                     # NetworkEvent, LogEvent, Breadcrumb, filters, factory
│   ├── Reporting/                    # Report, Attachment, ReportHandler, …
│   ├── Lifecycle/                    # LifecycleEvents, BugseeStatus, listener
│   ├── Appearance/                   # IAppearance + key constant types
│   ├── Performance/                  # Span, Transaction, …
│   ├── Feedback/                     # IFeedbackListener, …
│   └── Capture/                      # VideoFrameConsumer RowOrder (bridge-facing)
│
├── Callbacks/                        # App-facing events wired from contracts
│   └── BugseeCallbacks.cs            # Network/Log/Breadcrumb filters, ReportHandler,
│                                     # Lifecycle, Feedback (completion semantics explicit)
│
├── Platform/
│   ├── IBugseeNativeBridge.cs
│   ├── Android/
│   │   ├── AndroidBridge.cs
│   │   ├── AndroidOptionsMapper.cs
│   │   ├── Proxies/
│   │   │   ├── BugseeWrapperProxy.cs     # REQUIRED: setWrapper(impl)
│   │   │   ├── EventFilterProxy.cs
│   │   │   ├── ReportHandlerProxy.cs
│   │   │   ├── LifecycleListenerProxy.cs
│   │   │   ├── FeedbackListenerProxy.cs
│   │   │   └── JavaCallbackProxies.cs    # Callback1 / Runnable
│   │   ├── AndroidReport.cs
│   │   └── AndroidExchangeFactory.cs
│   ├── iOS/
│   │   └── IOSBridge.cs                  # P/Invoke → BugseeUnityBridge.mm + xcframework
│   └── Editor/
│       └── EditorBridge.cs               # Editor / unsupported no-op (SetAttribute still validates)
│
├── Capture/
│   └── BugseeFrameCapturer.cs            # VideoMode.DirectBuffers → getVideoFrameConsumer()
│
├── Internal/
│   ├── ExceptionPipeline.cs              # Managed capture (ILogHandler + AppDomain)
│   ├── ManagedExceptionPayload.cs        # UnityManagedException JSON contract
│   ├── MainThreadDispatcher.cs
│   └── BugseePackageVersion.cs
│
└── Components/
    ├── BugseeLauncher.cs
    └── BugseeBehaviour.cs                # GameObject host if needed
```

**Editor/**: Android Maven dependency injection, Gradle NDK enablement, iOS SPM post-process, IL2CPP linemap + symbol orchestrator (dSYM/ELF/ProGuard).

**Samples~/BugseeMiniGame/**: standalone Unity 2021.3 QA project (`file:../../..` → this package, relative to `Packages/`). Uses the `~` suffix so UPM does not import the minigame (avoids nested `Library` / duplicate `PlayerSettings`). Arena + Bugsee HUD + in-world stations.

## Bridge duties on Launch (Android)

Always register (Flutter-style), regardless of whether the app subscribed yet:

1. **`Bugsee.setWrapper(unityWrapperImpl)`** — identity, `requestData`, secure rects, wrapper report hooks, `onLifecycleEvent`
   `requestData` answers one flow, `"vh"` (managed view hierarchy): the SDK asks on its main thread, C# reads the scene on Unity's thread (`Runtime/Internal/ManagedViewHierarchy.cs`; serialization, privacy and the request rules in `WrapperPolicy/ManagedViewTree.cs`) and replies within the SDK's 500 ms budget. Every other type, a request before C# is ready, and a request made while Unity's main thread is blocked in a synchronous SDK call (logged exceptions) are answered `null`.
2. `setNetworkEventFilter` / `setLogEventFilter` / `setBreadcrumbFilter` → C# (passthrough if no subscribers)
3. `setReportHandler` → app `IReportHandler` / events (wrapper runs first inside SDK)
4. `setLifecycleEventsListener` → forward **all** 7.0 `LifecycleEvents` (+ feedback lifecycle strings when present)
5. `Bugsee.ext(Feedback)` → `setOnNewFeedbackListener` when feedback AAR present
6. If `Options.CaptureVideoMode == DirectBuffers` → start `BugseeFrameCapturer`

## Public surface checklist (must not miss)

### Facade (`Bugsee`)

Lifecycle: `Launch`, `Relaunch`, `Stop`, `GetLaunched`/`Status`, `GetLaunchOptions`, `DeleteCollectedDataOnDevice` (also `DeleteCollectedDataOnDeviceAsync` and a `DeleteScope`)  
Blackout: `StartBlackout`, `EndBlackout`, `IsBlackout` (obsolete Pause/Resume → blackout if shimmed)  
Logging: `Log`, `Trace`, `Event`, `LogException`, `LogUnhandledException`, `TestCrash`  
Reporting: `ShowReportDialog`, `Upload`, `CreateReport`, `SetReportHandler`, `Upload(Report)`  
Filters: network / log / breadcrumb  
Exchange: `GetExchangeFactory`, `AddNetworkEvent`, `AddBreadcrumb`  
Privacy: secure rectangles (Unity-relevant; `AddSecureRectangle` covers Unity-drawn content). Native `addSecureActivity` (Android only), `addSecureView` and `addSecureWebView` take a native Activity, View or WebView that a Unity script does not own, so they are not exposed as-is. A WebView an app embeds through a plugin goes through `Bugsee.AddSecureWebView` / `RemoveSecureWebView`, which take the plugin's native handle (Android `android.webkit.WebView`, iOS `WKWebView*`; #103)
Identity: `SetUserIdentifier` / `Get` / `Clear` (not email-as-primary)  
Attributes: set/get/clear/clearAll/getAll  
Appearance: `GetAppearance()`  
Video: `GetVideoFrameConsumer` (internal to capturer); `ResetVideoCapturePermission`  
APM: `StartTransaction` returning a managed `ISpan` with child spans (no global `StartSpan` / `GetActiveSpan`: the native active span is per thread)  
Feedback: `Bugsee.Feedback` → show UI, greeting, listener (opt-in native add-on: define `BUGSEE_FEEDBACK`)  
Wrapper: **internal registration only** — not an app-facing `SetWrapper`

### Contracts to port (public)

`Options` (the `Options.*` key constants: native keys are checked against the vendored manifests by `Tests~/WrapperPolicy/OptionsManifestTests.cs`, plus wrapper-only `unity.*` keys), option enums, `OptionsBuilder`, `BugseeLaunchOptions` / `AndroidLaunchOptions` / `IOSLaunchOptions`  

`EventFilter`, `NetworkEvent`, `LogEvent`, `Breadcrumb`, `BugseeExchangeFactory`  
`Report`, `Attachment`, `ReportHandler`, `ReportCreationListener`  
`LifecycleEvents`, `LifecycleEventListener`, `BugseeStatus`, feedback lifecycle consts  
`Appearance`, `ReportAppearance`, `NotificationAppearance`, `FeedbackAppearance`  
`VideoFrameConsumer.RowOrder`  
Performance: `Span`, `Transaction`, `SpanStatus`, …  
Feedback: `FeedbackListener`, …  

### Wrapper-only (implement, don’t expose as app API)

`contracts.internal.BugseeWrapper` (+ `DataRequestProvider` / `DataRequestResultCallback`)

## Reference sources

- Android facade: `android/sdk/library/.../Bugsee.java`
- Contracts: `android/sdk/**/com/bugsee/library/contracts/`
- Docs: `android/sdk/docs/api-7.x.md`, `migration-6.x-to-7.x.md` (may lag source)
- Wrapper option map: `cross/_WRAPPER_OPTION_SPEC.md`
- Flutter 7.x bridge patterns: `cross/flutter/android/.../BugseePlugin.java` (minus stale APIs)
- Legacy Unity inventory: `cross/unity/.../Plugins/Bugsee/` (reference only)

## Implementation order (suggested)

1. `Contracts/Options` + enums + `OptionsBuilder` + typed `*LaunchOptions` (write-only)
2. `IBugseeNativeBridge` + Android facade methods (no callbacks yet)
3. `BugseeWrapperProxy` + `setWrapper` on launch
4. Filters + `ReportHandler` + lifecycle proxies (**done** Android JNI + iOS MonoPInvokeCallback)
5. Feedback extension property
6. `BugseeFrameCapturer` (DirectBuffers)
7. Appearance + exchange factory + APM
8. iOS bridge against nextgen SPM (**crash/identity/filters/handler/lifecycle wired**; remote SPM when published)
9. Optional legacy `BugseePlugin` compat shim (if product needs it)

## Releasing (wrapper build id)

`environment.sdk.wrapper.build` is the build id stamped into the package; `main` keeps the placeholder `dev`, which the wrapper reports as no build.

1. Branch from `main` at the commit to release (`git switch -c release/vX.Y.Z origin/main`). Bump the version in `Tools~/versions.env` and run `Tools~/scripts/update-native-sdks.sh` if it changes, and update `CHANGELOG.md`; commit.
2. `Tools~/scripts/stamp-wrapper-build.sh` writes the short hash of that commit (it refuses a dirty tree) into the C# and Java constants; commit it. The id names the commit before the stamp commit, so after it only CHANGELOG and the build/version lines of the two constant files, `package.json` and `versions.env` may change (an SDK pin or other code change fails the check; re-stamp instead).
3. `Tools~/scripts/check-release-build.sh vX.Y.Z`, then `git tag vX.Y.Z` and `git push origin vX.Y.Z`. Run the check before pushing the tag: the `Release check` workflow runs it again on the pushed `v<digit>*` tag, but it only reports, it cannot stop a tag that exists. A prerelease tag (`v0.1.0-rc1`) needs `package.json` `"version": "0.1.0-rc1"`.
4. Do not merge the release branch into `main`: that would make `main` report the release hash. The CI job "Wrapper build id is the dev placeholder" fails a push or PR that does. Carry release-only edits (CHANGELOG) to `main` by cherry-pick.

## Risks

- JNI proxy threading: always marshal app callbacks to Unity main thread; honor `isTerminating` (no async round-trip).
- Local iOS SPM pbxproj injection is custom (no Unity local-SPM API) — verify on real Xcode export.
- Doc drift in `api-7.x.md` — trust `Bugsee.java` + contracts source.
- Options property naming is Android-first until iOS SDK RC/stable — then unify typed names across platforms.

## iOS Crash Report Extension (post-process)

Goal: iOS 27+ devices report launch watchdog (0x8badf00d, "Main thread locked"), foreground jetsam ("Foreground Out of memory") and background jetsam (non-fatal) through the Bugsee SPM product `BugseeCrashExtension` (present since `bugsee/spm` 7.0.0; the pinned 7.0.1 has it; 7.0.0-beta7 does not). Sources of truth: the Xcode 27 "Crash Reporter Extension" template (ExtensionKit extension, product type `com.apple.product-type.extensionkit-extension`, Info.plist `EXAppExtensionAttributes/EXExtensionPointIdentifier = com.apple.crash-reporter.extension`, not `NSExtension`) and the Bugsee iOS doc.

- Split: pure logic in `Runtime/WrapperPolicy/IosCrashExtensionPolicy.cs` (decision, Xcode version, group id, plist/entitlements/Swift text, version resolve) and `XcodeCrashExtensionProject.cs` (pbxproj text edits); Unity glue in `Editor/BugseeIosCrashExtensionPostProcess.cs` (order 1100, after the SPM link 999 and symbol upload 1000). Text edits because Unity's PBXProject API only has `AddAppExtension` (app-extension product type), not ExtensionKit.
- Gate: iOS only; Xcode >= 27 (`xcodebuild -version`); setting Off/Auto/On (env `BUGSEE_IOS_CRASH_EXTENSION`, EditorPrefs menu). Default Auto = on only in an interactive Editor build with Automatically Sign + team (off in `-batchmode`: scripted signing without `-allowProvisioningUpdates`, manual profiles and fastlane match would break on the new entitlement; `BUGSEE_IOS_CRASH_EXTENSION=1` forces it), since the App Group needs explicit App IDs that a manual wildcard profile cannot give, and a default that breaks device signing of existing projects is not acceptable. Skipped (never an error) for a local package without the product, a foreign crash-reporter extension, an invalid group id, or any failure; the pbxproj is edited in memory and written last.
- Target: `BugseeCrashReport`, bundle id `<app>.BugseeCrashReport`, `IPHONEOS_DEPLOYMENT_TARGET=27.0` on the extension only, `SWIFT_VERSION=5.0`, `CODE_SIGN_STYLE=Automatic` with the app's team, embedded by an "Embed ExtensionKit Extensions" copy phase (`dstSubfolderSpec=16`, `dstPath=$(EXTENSIONS_FOLDER_PATH)`, i.e. `<app>.app/Extensions`). It links only `BugseeCrashExtension` from the package reference the app already uses; `CrashReportExtension.framework` is never linked explicitly (the Simulator SDK lacks it; the package compiles a stub there).
- App Group: `group.<bundle id>` or `BUGSEE_IOS_APP_GROUP`; entitlement on the app (new `Unity-iPhone.entitlements` or the file the target names) and the extension; `BugseeAppGroup` in the extension Info.plist. Versions copied from the app's Info.plist (or the build setting it names) onto the extension's `MARKETING_VERSION` / `CURRENT_PROJECT_VERSION`.
- App Group ownership: the export records the app entitlements files it added the group to (`BugseeAppGroupAddedTo` in the extension Info.plist, an unknown key Xcode/codesign tolerate; verified with an ad-hoc codesign of the built appex) and removes a group, on Off/skip or a bundle-id change, only from those files and only inside the `application-groups` array; a pre-existing group is never removed. Pure code: `IosCrashExtensionPolicy.RewriteEntitlements` / `RetireEntitlements` / `ReadAppGroupState`.
- Append: an existing target is refreshed (bundle id, version, build, team, signing style, entitlements path) and removed, with its files and the app's group, when the decision is no longer Enabled. The mode setting is an EditorPrefs value (the package has no project-level settings store). Unity 2021.3/2022.3 players abort on iOS 27 without UIScene; the extension only helps on a Unity that runs there.
- Reporting scope in Unity: Bugsee starts at `Bugsee.Launch` (IOSBridge -> `_bugsee_launch`), after iOS's launch watchdog window, so 0x8badf00d is normally not reported (a 60 s sleep in Start/Awake did not trigger it; a native block in scene connect did, and with Bugsee not launched in that process the extension recorded run=0 and nothing reached the dashboard, as the Bugsee doc says). Foreground/background out-of-memory kills and exits after `Bugsee.Launch` are reported. Dashboard issues currently carry the summary 'Application process exited'; the kind is in the description/attributes.
- Verified on an iPhone 16 Pro, iOS 27.2 (Unity 2022.3 plus a scratch UIScene shim): foreground jetsam, background jetsam, launch watchdog (only when Bugsee was already launched natively in that process), and a native crash giving a single report; an iOS 18.7.9 device installs and runs the app with the iOS-27.0 extension.
- Unverified: Release archive and App Store validation, Unity 6000.x, a simulator build of the exported project in Unity, a shipping Unity setup that runs on iOS 27 without a shim (Unity 2021.3/2022.3 abort at launch on iOS 27 without UIScene adoption), and that a later `PBXProject.WriteToFile` keeps the ExtensionKit target intact on 2021.3 / 6000.0.

## iOS native early launch (opt-in)

Goal: start the iOS SDK before Unity runs C#. EXPECTED per the SDK docs (/sdk/ios/lifecycle, crash-report-extension), UNVERIFIED on device here: the SDK installs its crash handler at once, the real session starts when the app becomes active, and a watchdog kill in that window is filed as a crash without a recording; nothing is filed with a debugger attached. Default OFF: with no setting, nothing is written and nothing changes.

- SDK facts (bugsee-cocoa 7.0.1 headers, device logs): the SDK defers `launchWithToken:` until the app becomes active (`Bugsee.handleActiveNotification, deferred launch`), `status` stays Stopped meanwhile; wrapper identity is read from the registered `BugseeWrapper` (`+setWrapper:`), so the early launch sets version/build/context through the existing `_bugsee_ensure_wrapper` / `_bugsee_set_wrapper_context` first. The headers do not document a second `launch` call while running (the wrapper docs say it is ignored); the design never makes one: the C# Launch skips the native call when it adopts.
- Config: Editor post-process (`Editor/BugseeIosEarlyLaunchPostProcess.cs`, order 1200) writes Info.plist dictionary `BugseeEarlyLaunch` (Token, Options JSON string, wrapper version/build, Unity version, product name) by text edit (`IosEarlyLaunchPolicy.SetInfoPlistEntry`, idempotent, removed on an Append export when off). Settings: `BUGSEE_IOS_EARLY_LAUNCH` (1/0), `BUGSEE_IOS_APP_TOKEN`, `BUGSEE_IOS_EARLY_LAUNCH_OPTIONS` (JSON, same keys as C# options), or the Bugsee/iOS/Early Launch window (EditorPrefs). The token is in the app bundle anyway. Options are plain JSON stored in Info.plist; Xcode expands build settings in plist strings (`$$` becomes `$`, `$(NAME)` expands), which dropped the internal `com.bugsee.option.$$ENDPOINT` key, so `$` is written as the JSON escape `\u0024` (`IosEarlyLaunchPolicy.PlistSafeJson`; JSON parsing restores it). Callbacks, filters, attributes and user are set from C# later. Android-only keys are dropped as for a normal Launch.
- Native: new `Plugins/iOS/BugseeEarlyLaunch.mm` (the protected bridge files are untouched). `+load` registers a `UIApplicationDidFinishLaunchingNotification` observer only when the dictionary exists; no subclassing of UnityAppController. No-op on Simulator / Mac Catalyst / without the SDK. Hook choice: didFinishLaunching is reachable without swizzling and before Unity loads the first scene; a direct launch from `+load` would be earlier but runs before UIApplication exists, and was not validated, so it is not used. Crashes inside Unity's own `didFinishLaunching` are therefore not covered.
- Adoption: the first C# `Launch` calls `_bugsee_early_launch_adopt(token)` (after wrapper registration). Adopts whenever the early launch ran (`launchWithToken` was called), whatever the status: the SDK defers the start until the app is active (status Stopped), so adopting only on Launching/Launched would make C# issue a second launch. Only a stop in flight (status Stopping) is left to the normal Launch. No native launch; managed side is set up as usual (exception pipeline, log forwarder, launch buffers), Launching/Launched lifecycle events the callback missed are replayed once (`EarlyLifecycleReplay`: events that fire after a deferred adoption pass once, a racing duplicate is dropped), the started callback gets true. A different token: adopt anyway with a warning (silent re-routing would be worse than a clear message). Options that differ are launch-only: warned, not applied. Answers once: after `Stop`, `Launch`/`Relaunch` behave exactly as without early launch. If the early session is not running (launch failed, stopped), Launch starts normally.
- Not carried over: `RelaunchedAfterCrash` is not replayed (it fires before C# listens); `wrapper_info.fps` is 30 (no target frame rate is known yet); the Unity-side DirectBuffers capture is Android-only so unaffected.
- Known gaps: `wrapper_info.fps` fixed 30; replayed Launched has null data; coverage could start earlier via `+load` (not validated); consent: the SDK starts before any C# consent check. The feature depends on the crash-extension files (`IosCrashExtensionPolicy`, `BugseeIosCrashExtensionPostProcess`, `XcodeCrashExtensionProject`, same uncommitted tree): commit together.
- Verification state: see the report of the change; device tests of watchdog arrival could not be completed because both test phones were blocked by a Local Network permission dialog of another BugseeMiniGame app that cannot be dismissed remotely.

## Phase B — IL2CPP LineNumberMappings & address contract

Authoritative design: `bugsee-cli/docs/unity-il2cpp-linenumber-mappings.md` and epic `il2cpp_linenumbermaps_epic`.

### Symbol upload

Editor/CI discovers `LineNumberMappings.json` (+ `MethodMap.tsv`, `il2cppFileRoot.txt`) and shells:

```text
bugsee-cli debug-files upload <path> --type il2cpp-linemap \
  --version … --build … --uuid <libil2cpp-or-UnityFramework-uuid…>
```

Alongside `dsym` / `elf` / `proguard` uploads (`BugseeSymbolUpload`).

**Editor failure policy (never fail the Unity build):**

- Auto-discovery miss → warn + continue
- Explicit `BUGSEE_IL2CPP_MAPPING` missing → warn + skip
- Unresolved UUID → warn + skip (iOS often deferred to archive Run Script)
- CLI non-zero / timeout → warn + continue
- `BUGSEE_NO_SYMBOL_UPLOAD` or `BUGSEE_NO_IL2CPP_MAPPING` set → silent skip (the iOS Run Script also reads `BUGSEE_NO_SYMBOL_UPLOAD` at archive time and exits without uploading)
- No `BUGSEE_APP_TOKEN` → silent skip of the upload (opt-in); the mapping files are still emitted by the build (`--emit-source-mapping --emit-method-map`, any IL2CPP build unless `BUGSEE_NO_IL2CPP_MAPPING`), so a later CI job can upload them; the iOS Run Script prints a `warning:` for an archive (a `note:` for other builds) for a missing token and for every failed upload (with the exit code's meaning), and never `error:` (which would fail the archive, also when the script exits 0): the CLI prints `error:` on stderr for every failure, so its output is rewritten (`error -`) while the exit code is kept

Standalone `bugsee-cli` may still exit non-zero when used directly in CI scripts.

### Event payload (who emits addresses)

| Event class | Payload | Apply path | Dependency |
|---|---|---|---|
| Native fatal (iOS) | module UUID + PC (native SDK) | primary (dSYM → cpp → LNM) | archive dSYM + linemap |
| Native fatal (Android) | module UUID + PC | primary | NDK + ELF/Breakpad **line** info + linemap |
| Managed fatal (IL2CPP) | UnityManagedException JSON; `address` when present in stack lines; else MethodMap strings | primary if addrs; else MethodMap | ExceptionPipeline / bridge |

Managed stacks without instruction addresses use MethodMap demangle only (not LNM file/line).
