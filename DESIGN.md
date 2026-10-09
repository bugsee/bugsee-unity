# Bugsee Unity SDK — Design

UPM package `com.bugsee.unity` (repo: `bugsee/bugsee-unity`).  
Legacy foundation: `cross/unity`. Min Unity: **2021.3**.

## Authority

The wrapper contract lives in [`bugsee/specs`](https://github.com/bugsee/specs/tree/main/sdk), not in this file. Where this page and a spec disagree, the spec wins.

| Spec | Role for Unity |
|---|---|
| [`sdk/wrapper-channel`](https://github.com/bugsee/specs/tree/main/sdk/wrapper-channel) | How this package injects host logs, network events, and breadcrumbs |
| [`sdk/wrapper-data-requests`](https://github.com/bugsee/specs/tree/main/sdk/wrapper-data-requests) | `requestData` flows (`vh` today) |
| [`sdk/wrapper-workbook`](https://github.com/bugsee/specs/tree/main/sdk/wrapper-workbook) | Implementation order and traps. Living notes; a linked spec overrides it |
| [`sdk/options`](https://github.com/bugsee/specs/tree/main/sdk/options), [`sdk/report-contract`](https://github.com/bugsee/specs/tree/main/sdk/report-contract) | Launch keys and what an embedder may do to a `Report` |

React Native is the reference wrapper those pages were written from. Unity copies the contracts and the traps. It does not copy React Native's bridge dispatch (void calls queued, returning calls inline). Unity JNI and P/Invoke run on the caller's thread.

## Understanding

- **What:** UPM-first Unity SDK whose public C# API mirrors Android SDK **7.x** (`Bugsee` facade + public `contracts`), with platform bridges for Android (EDM4U/Maven) and iOS (remote SPM `github.com/bugsee/spm`). Internally it is a 7.x wrapper: one native wrapper object per process, registered before `launch`, speaking the channel and `requestData` contracts.
- **Why:** Replace legacy `.unitypackage` / `Assets/Plugins` distribution; align with the 7.0 redesign and the cross-wrapper specs.
- **Who:** Unity game/app developers; Bugsee maintainers.
- **Non-goals:** OpenUPM publish, dual release repo, Asset Store `.unitypackage`, RN 6.x bridge patterns, a public `SetWrapper`, JS source-map upload, inventing a second managed runtime inside one player process.

## Assumptions

1. Pins live in one file, `Tools~/versions.env`: Android `com.bugsee:bugsee-android:7.3.0` **and** `bugsee-android-ndk:7.3.0` (core publishes no transitives); Gradle plugin `4.0.8` (floor is 4.0.7 — 4.0.6 strips extension providers and never injects them). Feedback AAR stays optional and is a separate artefact.
2. iOS is SPM-only. Pin [`bugsee/spm`](https://github.com/bugsee/spm) `7.0.0-beta5`. Deployment target **15.0**. CocoaPods is retired upstream. Workbook floor is beta3; this package takes beta5 because crash recovery and the own-traffic URL filter land there.
3. Android `minSdk` stays **21** (the SDK floor). This package does not raise it.
4. Public C# surface mirrors Android 7.x; intentional C#/.NET deviations are listed below.
5. SDK-internal contracts (`BugseeWrapper`, `BugseeWrapperChannel`, `requestData`, most `contracts.internal.*`) are **not** public C# API. `getExchangeFactory()` stays public.
6. Optional network extensions (OkHttp/Ktor/Cronet) are out of the default graph. Unity's own `UnityWebRequest` is not OkHttp and is not NSURLSession, so the native interceptors do not see it.
7. One Unity player runtime per process. A second engine in-process is out of scope; its surface lanes would collide with the single-slot bridges.
8. `sdk/wrapper-workbook` Part 14's iOS appearance shape (writable `report*Color` on `BugseeTheme`) is older than the pin. iOS 7.0.0-beta5 appearance is `BGSAppearance` via `getAppearance`. The pinned headers win.

## Decision log

| Decision | Choice | Alternatives | Why |
|---|---|---|---|
| Repo layout | UPM package at repo root | `package/` subfolder; Sentry dual-repo | Simplest Git URL install |
| Android natives | EDM4U → Maven | Vendored Unity AAR | Matches Flutter/RN 7.x; smaller git |
| iOS natives | Remote SPM `github.com/bugsee/spm` | Vendored xcframework; CocoaPods | iOS 7.x is SPM-only |
| Min Unity | 2021.3 | 2018.3 / Unity 6 only | SPM `PBXProject` APIs + practical floor |
| Primary C# API | Android 7.0 mirror | Keep legacy `BugseePlugin` names | Clean 7.0 surface; legacy shims optional later |
| Launch options shape | Shared base + platform subclasses | Single shared type; two independent types | Legacy Unity DX; platform-only props stay typed |
| Launch options API | Write-only properties (no getters) | Mutable get/set; fluent-only builder | Omit unset keys → native defaults; no false “read your writes” |
| Launch options defaults | Empty until set | Constructor-filled defaults | Native SDK owns defaults when key omitted |
| Launch overloads | Single `Dictionary` param (+ implicit from typed/`OptionsBuilder`) | Separate typed + `IDictionary` overloads | Avoids CS0121 on `Launch(token, null)`; typed still feels first-class |
| Options secondary API | Keep `Options.*` keys + `OptionsBuilder` | Hide keys; drop builder | Advanced / custom keys without forcing the map path alone |
| Options property names | Android 7.0 contract names | Legacy Unity names; dual aliases | Clean 7.0 mirror; **revisit unify when iOS SDK is RC/stable** |
| `BugseeWrapper` | One native instance per process, registered **before** `launch` | Register a new C# proxy inside `Launch` | Recovery during `launch()` reads the wrapper; a second instance retires the channel |
| Wrapper channel | Host `Debug` / `UnityWebRequest` / breadcrumbs go through `BugseeWrapperChannel` | Public `Bugsee.log` / `addNetworkEvent` for host output | Public `log` stamps `source: Bugsee` and, on iOS, a filtering flag can skip redaction |
| `vh` | `requestData` always answers. A later walk is synchronous and bounded; `null` is a valid answer | Async hop to the player loop | Android does not wait when the snapshot is created on the main thread; a slow async reply drops iOS to a 50 ms budget |
| Secure-rect units | Convert per platform (Android physical pixels, iOS points), then round outward | One shared unit | Unification is undecided (`specs` PR #28); a per-platform conversion stays correct either way |
| iOS main thread | Hop `launch`, `stop`, `showReportDialog`, `deleteCollectedDataOnDevice` only | Hop every native call | Channel submit, filter install, report completion, and the `vh` reply stay on the caller |
| Feedback | Optional separate artefact; core package no-ops | Treat feedback as part of core SPM | Android `bugsee-android-feedback` and the iOS Feedback module pin the core exactly and ship apart from it |
| Android unhandled exception | Leave both 7.3.0 reports in place | Drop one in the wrapper | `logUnhandledException` files a crash and an error until the SDK fix ships |
| Enum crossing | By wire value; breadcrumb levels by name | Shared integer or ordinal | Android `Breadcrumb.Level` and iOS `BugseeLogLevel` are inverted |
| Gradle edits | Insert only at a canonical anchor, marked, idempotent; otherwise refuse | Regex insert into the customer's template | A wrong anchor lands mid-statement or inside a comment and grows on every resolve |
| Filters/handlers | First-class C# callbacks + JNI proxies | Omit (legacy gap) | Parity with Android/Flutter |
| C# deviations | Allowed where idioms win | Pure Java-shaped API | Better Unity/.NET DX |
| Managed exception signatures | One key per event; Unity primary when strong (RVA / file:line); weak = canonical method tokens; no append | Worker always rehash; dual Unity+native keys | Merge set must stay bounded; raw stacks are not device-stable. See `Documentation~/exception-signatures.md` |

### C# idiom deviations (approved)

| Android | C# |
|---|---|
| `EventFilter.filter(T, Callback1<T>)` | App API is `Func<T,T>` (null = drop). The native side completes asynchronously when the callback arrives off the Unity thread, so the SDK thread is not blocked waiting for the player |
| `Callback1` / `Runnable` | `Action` / `Action<T>` |
| `Map<String,Serializable>` options | Typed write-only `*LaunchOptions` (primary) **+** `OptionsBuilder` / raw dictionary (secondary) |
| `Bugsee.ext(Feedback.class)` | `Bugsee.Feedback` property (resolves extension under the hood) |
| Java listeners | C# `event` / `Action<>` on `Bugsee` |
| Lifecycle string constants | `LifecycleEvents` consts **+** optional enum for known events |
| `IssueSeverity` | Map by value. Android `VeryLow=1`, iOS `Low=1`; `Medium=2` … `Blocker=5` match, including `Critical=4`. iOS `0` is unset |

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
- **Wire keys** — property setters store under Android 7.x `com.bugsee.option.*` keys from [`sdk/options`](https://github.com/bugsee/specs/tree/main/sdk/options). Enums as wire values, never ordinals. Re-derive shared vs platform-only, types, and defaults on every pin bump. iOS drops unknown keys silently, so Android-only keys (the 7.3.0 pending-report caps, for example) are not forwarded. `$$ENDPOINT` is passed without an API version. Hidden keys (`$$ENDPOINT`, `$$WRAPPER`, `$$DEBUG`) must not appear in uploaded `sdk.options`.
- **Custom keys** — `SetCustomOption(string, object)` on the base (and/or raw map / `OptionsBuilder`).

### Facade

- Primary: `Bugsee.Launch(token, androidOptions)` / `iosOptions` via implicit conversion to `Dictionary<string, object>` (single optional-map overload — avoids `Launch(token, null)` ambiguity across typed types).
- Secondary: `Launch(token, dictionary)`, `OptionsBuilder` (same implicit conversion), public `Options.*` constants, `SetCustomOption`.
- `Relaunch` mirrors `Launch`.

### Naming revisit (explicit follow-up)

Property / key naming currently follows **Android 7.0**. When the Bugsee **iOS** SDK reaches **RC or stable**, revisit options naming to properly unify the cross-platform typed surface (base vs platform splits and any divergent key vocabularies).

### Do not copy (stale / wrong)

- Flutter still calling removed `setAdditionalDataCapture` / `setSystemSecureRects` → use **`BugseeWrapper`** instead.
- React Native Android bridge (still 6.x packages).
- Legacy Unity V4 `Snapshot` / `onNewFrame` path.
- Bundled 6.x `Bugsee-Unity.aar` / `BugseeUnityAdapter`.

## Native packaging (already scaffolded)

- `package.json` → `com.bugsee.unity`, depends on `com.google.external-dependency-manager`
- `Editor/BugseeAndroidDependencies.xml` → Maven 7.3.0 (+ NDK, both explicit)
- `Editor/BugseeIosSpmPostProcess.cs` → remote SPM `github.com/bugsee/spm` `7.0.0-beta5`
- `Plugins/iOS/BugseeUnityBridge.mm` + `Plugins/Android/UnityManagedException.java`
- `Tools~/scripts/update-native-sdks.sh` + `versions.env`

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
│   │   └── IOSBridge.cs                  # P/Invoke → BugseeUnityBridge.mm; remote SPM at export
│   └── Editor/
│       └── EditorBridge.cs               # Editor / unsupported no-op
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

**Editor/**: EDM deps, Gradle NDK enablement, iOS SPM post-process, IL2CPP linemap + symbol orchestrator (dSYM/ELF/ProGuard).

**Samples~/BugseeMiniGame/**: standalone Unity 2021.3 QA project (`file:../../..` → this package, relative to `Packages/`). Uses the `~` suffix so UPM does not import the minigame (avoids nested `Library` / duplicate `PlayerSettings`). Arena + Bugsee HUD + in-world stations.

## Wrapper contract

This is the 7.x embedder. The public facade below is what a game calls. This section is what the package does toward the native SDKs, and it is not an app-facing API.

Minimum that has to be true before the product surface (exceptions, dialogs, spans, appearance, feedback, symbols) can be trusted:

1. Versions pinned in `Tools~/versions.env`, with a test that fails when another copy drifts. No `mavenLocal()` / snapshot resolution once the pin is a release.
2. `bugsee-android-ndk` declared beside the core AAR. A Java `TestCrash` does not prove it; a real signal does (`libbugsee-crashcp`, `libbugsee-crashpad-handler`, `libbugsee-crashpad-trampoline`, `libbugsee-native`).
3. Gradle plugin ≥ 4.0.7.
4. Wrapper registered before `launch`, under a class name the plugin will not strip.
5. Identity: type, version, build, string context.
6. Lifecycle names forwarded, including names this package does not recognise.
7. Host logs and framework HTTP submitted on the wrapper channel, latest channel stored, network events with `requiresFiltering = true`.
8. `requestData` always answers.
9. Secure rectangles versioned, in the platform's unit.
10. Report handlers always complete, and never round-trip a dying process into C#.

### Registration

The SDK reads the wrapper while building a report environment and while dispatching handlers, including recovery of the previous run's crash. Both happen inside `launch()`. A wrapper created from C# in `Bugsee.Launch` is late for that run. The failure shows up only on the relaunch after a crash.

**One wrapper instance per process, for the life of the process.** Identity refinement writes fields on that instance. Replacing it with a new object delivers a fresh channel and retires the old one, which then accepts calls and drops them. Android does not warn. iOS warns once per process after a 5 s grace. Re-registering the same reference is a no-op on both platforms (identity is `==` / pointer equality, never value equality). If the channel callback threw before the channel was stored, recovery is `setWrapper(null)` then `setWrapper(w)`.

Store the channel in a field the forwarders actually read, as the first act of `onWrapperChannelAvailable`, then return. Flush any pre-launch buffer after `setWrapper` returns. Register from one thread. Holding a channel does not mean the SDK is running: calls before launch, after `stop()`, and between stop and relaunch are dropped.

**Android.** A `ContentProvider` in the package AAR, `android:initOrder="200"`, extending `BugseeExtensionInitProviderBase`, calls `Bugsee.setWrapper` from `onExtensionCreate()`. The class name must not match `Bugsee<Anything>InitProvider` — the Gradle plugin strips that pattern from every APK, on every plugin version, and the build stays green. Suggested simple name: `UnityWrapperProvider`.

**iOS.** `+[Bugsee setWrapper:]` has no launch ordering of its own. Register from native load (`+load` in the bridge), before any `+launchWithToken:`. There is no auto-init race.

`com.bugsee.option.$$WRAPPER` (Android) and `wrapper_info` (iOS) are fallbacks read only when no wrapper object is registered. They are not a second source of identity. Context values that are not strings are dropped on both routes.

### Identity

| Field | Value |
|---|---|
| type | `unity` (one spelling, native and managed) |
| version | `BugseePackageVersion.Version`; missing fact is `"unknown"`, never `""` |
| build | package build id, or `"unknown"` |
| context | `String → String` only: `unity_version`, `unity_platform`, `scripting_backend`, `product_name`. Drop non-strings. Cache from the main thread; the getter runs off it |

### Wrapper channel

`BugseeWrapperChannel` / `BGSWrapperChannel` is how this package submits data it captured in the Unity runtime. Every method is default / `@optional`, so an older SDK keeps working. The channel is delivered inside `setWrapper`, not at launch.

| Call | Rule |
|---|---|
| `log(tag, message, level, source)` | No filtering flag. Every accepted source is filtered. |
| `addNetworkEvent(event, requiresFiltering)` | Build with the public factory, submit here. Always `requiresFiltering = true`. `false` on Android skips the customer filter and `NetworkDataSanitizer`. |
| `addBreadcrumb(breadcrumb)` | Build with the public factory, submit here. |

`getExchangeFactory()` stays on the public `Bugsee` facade. Apps mint their own events with it. Attribution is the route, not a field on the object.

**Source allow-list** for channel logs: `Custom` (98), `WebView` (5), `StdOut` (1), `StdErr` (2), `Unknown` (0). Anything else is coerced to `Custom` and reported once per process. Unity `Debug` / `Application.logMessageReceived` output is `Custom`. A missing source is resolved to `Custom` in the bridge before the call: iOS reads a nil source as `0` (`Unknown`, accepted quietly) and Android maps null to `Custom`.

`tag` is written on Android and omitted on iOS. Nothing that must survive on both platforms goes in it.

**UnityWebRequest.** The SDK does not see it. Record finished requests with `addNetworkEvent` and `requiresFiltering = true`. Do not also patch `UnityWebRequest` if a native interceptor already sees that client (it does not, by default). During `Launching`, Android accepts log, breadcrumb, and network submits; iOS keeps log and breadcrumbs and **drops** `addNetworkEvent`. Hold early network events until `Launched`.

**Breadcrumb levels** are mapped by name in C#. The integers are inverted:

| Name | Android 7.3.0 | iOS `BugseeLogLevel` |
|---|---|---|
| debug | 1 | 4 |
| info | 2 | 3 |
| warning | 3 | 2 |
| error | 4 | 1 |
| fatal | 5 | error (1); iOS has no fatal rung |

iOS verbose (5) is not a C# name. An unknown name is rejected before it crosses.

**Filters.** The customer's filter runs synchronously on the thread that called the channel. Do not call the channel while holding a lock that filter might need, and do not block that thread waiting for a C# decision. Android propagates a throwing filter to the caller; iOS swallows it and drops the line. Guard the native call site either way.

When this package installs a filter so the SDK can ask about a line *it* captured:

- Subscribe the managed listener, then enable the native filter. The other order leaves an iOS gap request pending until process death.
- A missing or throwing listener **drops** the line. Failing open leaks the value the filter exists to remove.
- Log and network: forget the pending request strictly inside Android's 10 s recycle (9 s). A late reply must not write a pooled entry. iOS has no timer; a late decision still records, so do not add one.
- Breadcrumbs: no wrapper timer on either platform. A late reply still writes the entry.
- In debug builds the SDK also captures logcat / stdout. Drop the native echo of a line this package already forwarded, once, and only when the line is not user-protected. The managed forward still happens.

**Ordering.** A later C# call must not overtake an earlier one that changed state it reads (filter installed, then a log). Unity's direct JNI / P/Invoke calls already share the caller's thread, so they stay direct. Do not post those setters through `UnitySendMessage` or a background queue. Setters that must hit UIKit (`appearance`, span attribute writes that touch UI) hop to the main thread synchronously and return a value, so they stay ordered with the `finish` or `get` that follows.

### Data requests

`requestData` is required. One reply per request. Unknown `dataType` → `onResult(null)`. A throw is treated as null. Silence spends the SDK's whole budget. The reply may arrive on any thread. Null means "nothing to contribute".

The only registered flow is **`vh`** (managed view hierarchy), asked on the **main thread** during each non-best-effort view-hierarchy pass and on `captureViewHierarchy()`. Budget **500 ms**, starting when `requestData` **returns**. The string is stored byte for byte under `viewtree.json`'s `managed` key. The SDK does not parse or redact it.

**Unity answer.** Today both bridges reply `null`, which is conforming. The tree the viewer needs is not the native `UIView` / `android.view` walk (one `UnityPlayer` surface). When this package grows a walk, it reads uGUI `Canvas` / `RectTransform` and UI Toolkit `VisualElement` trees. IMGUI has no retained tree and is omitted.

Because `requestData` is already on the main thread, where Unity UI lives, the walk replies **synchronously before return** when it finishes inside a small bound, and replies `null` when it would not. An async reply misses Android snapshots created on the main thread, and three consecutive timeouts cut iOS to a 50 ms budget until the next capture start.

Privacy is entirely this package's: no text, no field values, no free-text keys. Identifier-like props only (a test id / accessibility id). A secure node emits no identifiers on itself or its descendants. Fail closed: a secure-check error treats the node as secure; a hidden-check error drops the node. Walk iteratively, cycle-safe, bounded by time, depth, visited nodes, and emitted nodes. Mark every truncation the same way. `bounds` is `[x, y, width, height]` in the **native** tree's space for that platform: Android display pixels including the root's on-screen origin, iOS points.

### Secure rectangles

This is the privacy channel that works. The game is one host surface, so `addSecureView` and `FLAG_SECURE` do not attach to a Unity control. The same set masks video, the report screenshot, and the input hit-test.

The SDK pulls `[version, count, l, t, r, b, …]` (`int[]` on Android, little-endian `int32` `NSData` on iOS). `r` and `b` are exclusive. Degenerate rects are ignored. Round outward **after** unit conversion (floor left/top, ceil right/bottom).

- Version changes on every real change, per display, and does not change on a no-op. A stale version keeps masking the old region.
- Publish immutable snapshots stored **outside** the wrapper object, so an identity refresh cannot drop them.
- One registry keyed by owner (imperative call vs live component) and by surface. Publish the per-display union. The first publish for a display always crosses, including an empty all-clear. Disposing an owner removes only that owner.
- **Units.** Unity screen space is pixels. Android wants physical pixels. iOS wants points (`pixels / native scale`, read per call). A 3× Android device fed unscaled values redacts a third of the region.
- Re-measure on a timer (~100 ms, the SDK's pull cadence), not only on layout. On measure failure keep the last rect (fail closed).
- Android display origin is applied at pull time, including when no rect changed (immersive / split-screen). A second surface (a native dialog, a second `Display`) has its own origin. An unknown origin is served as one display-bounds rectangle until it is known.
- Clamp every served rectangle to the display.

### Lifecycle

`onLifecycleEvent` is dispatched **by name**, prefixed `com.bugsee.lifecycle.`. Strip that prefix once, natively. Forward an unrecognised name unchanged. Events arrive off the main thread. An event fired before `setWrapper` is lost on Android.

`RelaunchedAfterCrash` means `ApplicationExitInfo` said the last process ended in a crash. It is not evidence that a report exists.

Derive `Launching` / `Launched` / `Stopping` / `Stopped` from these events. Do not subscribe iOS `bugseeDidChangeStatus:` — that delegate belongs to the app. `Before/AfterFeedbackShown` never reach the wrapper. `Before/AfterReportShown` carry an id; on Android it may be a request id, so do not join on it.

### Report handlers

`onBeforeReportCreated` / `onAfterReportCreated` enrich a report. `isTerminating == true` means the process is about to die: do the work synchronously in native code and call completion before return. Do not cross into C#. The budget is about 3 s and the runtime may already be gone.

`isTerminating == false` does not mean the handler may take its time. iOS recovery and Android early-crash recovery are a fresh process (`false`) under a 3 s cap, and the SDK does not wait for completion. iOS recovered-report edits that land after the handler returns are best-effort until the pending iOS fix ships; do not build a feature that depends on them, and do not assert them in a test. Android recovered reports dispatched on `BugseeReportHandlerThread` do wait.

| Path | Android | iOS |
|---|---|---|
| Live | `false`, dedicated thread, never main. 30 s / handler, 60 s / chain | `false`, **main** thread, same caps |
| Recovered next launch | `false`. Live handler thread when launch happens after the runtime exists | `false`, off main, 3 s, completion not waited |
| Early-crash recovery | `false`, bounded thread, 3 s, completion is a no-op; may run inline on main inside `Application.onCreate` | as recovered |
| Dying process | `true` only for an uncaught Java exception, `onBefore` only | Never `true` today |

`onBefore` is at most once and may be skipped. `onAfter` is at least once and must be idempotent. There is no supported way to detect "this is a recovered crash" from `type` + `isTerminating`.

A wedged Android handler (never returns, twice) causes every later report in the process to ship with handlers skipped. Always complete.

The C# `Report` is a proxy (opaque handle + operations) live only until completion. Severity, labels, and attributes cross the bridge. Attachments cross as a **file path or bytes**; `createAndAddAttachmentWithName` can return an object that was never added. iOS severity `0` is unset and maps to C# null, never to a level. Labels use Android `setLabels` and iOS `replaceLabels:` (atomic). A cleared summary is `""` on iOS and `null` on Android, so the bridge must keep nulls. Enums cross by internal value. `IssueSeverity` names diverge at 1 (`VeryLow` on Android, `Low` on iOS); 2–5 match.

`upload`, `showReportDialog`, and `createReport` are three operations. `upload` files now. The dialog does not file by itself. `createReport` returns a live handle. Allow one created report at a time: iOS keeps attributes in file-scope globals, and a second create resets the first. iOS `upload` files `source.type = "unknown"`; Android files `code_upload`. Record both. `deleteCollectedDataOnDevice` runs on the main thread and refuses while launched.

### Attributes and user identity

Accept only `string | number | boolean`. Reject non-finite numbers and `|v| ≥ 2^63` before they cross (Android's writer clamps those to `Long.MAX_VALUE`). Cap strings at 1024 UTF-16 units so both platforms reject the same input; the iOS archive limit has no exact character cutoff, and about 800 ASCII characters is the safe band. After every set, read back. Android's persisted copy stores fractional numbers as float32 (`0.1` → `0.10000000149011612`); document that on the setter. An empty or null user identifier clears on both platforms. Error text names the key and the bound, never the rejected value.

### Threading (Unity)

| Call | Thread |
|---|---|
| iOS `launch`, `stop`, `showReportDialog`, `deleteCollectedDataOnDevice` | Main. Inline if already there; asynchronous otherwise. A synchronous hop from a queue the SDK then needs deadlocks |
| Channel `log` / `addNetworkEvent` / `addBreadcrumb`, filter install, report completion, `vh` reply | Caller's thread. Do not hop |
| Appearance and span setters that touch UIKit | Synchronous main hop, and they return a value so a following `get` / `finish` cannot overtake them |
| App-facing C# callbacks | Unity main thread, except `isTerminating`, which never enters C# |

### Bridge errors

A native exception message often contains the attribute, path, URL, or token that failed. Rejects and native logs name the **operation** or the exception **class**. Developer-chosen identifiers (option key, attribute name) may appear. Values may not. One audited reader handles native error text, and only for this package's own error domains.

### What still runs at `Launch`

`Launch` itself does not create the wrapper. By the time C# calls it, the native instance already exists. `Launch` then:

1. Refines context fields on that same instance.
2. Installs network / log / breadcrumb filters (passthrough when the app has no subscriber).
3. Installs the app report handler (the wrapper's own hooks already run inside the SDK).
4. Forwards lifecycle names.
5. Resolves `Bugsee.ext(Feedback)` only when the feedback artefact is present.
6. Starts `BugseeFrameCapturer` when `CaptureVideoMode == DirectBuffers`.

### Managed exceptions

Native crash capture does not see an exception that stays in Mono/IL2CPP. `logException` and `logUnhandledException` are that path. The payload is `UnityManagedException` JSON built in C# and handed across verbatim. Signature rules stay in `Documentation~/exception-signatures.md`. The debug id on each frame is the id of the symbol file uploaded for the loaded artifact.

One incident is one report from this package's side: install the runtime handlers once, and do not file a second report for the same object. Android 7.3.0 still files **two** SDK reports (crash and error) for one `logUnhandledException`. Leave both until an SDK release contains the fix (`e1d56ed65` is not in 7.3.0). Swallowing one hides the incident the SDK kept.

`includeVideo` on an exception is a dead option in 7.x. Do not promise it. Do not prove iOS exception capture on the simulator against a pin that compiles those entry points out; beta5 on device is the pin this package asserts recovery against.

### Appearance, spans, feedback

Appearance uses 7.x constants. On Android that is `setColor` / `setString` with the 7.x property ids (6.x names are a different generation). On iOS 7.0.0-beta5 it is `id<BGSAppearance>` from `getAppearance` (`setColor:forProperty:`, `colorForProperty:`, and the string pair). A name with no binding on this platform: read returns empty, write fails in C#. An unknown name fails on both. Feedback colors belong to the feedback artefact.

Spans: the bridge holds each native span until `finish`, then drops that handle and any child the parent finish cancelled. The first `finish` kills the handle. A null span is not retained. Invalidate drops the registry and does not finish the spans. Setters and `finish` stay the same dispatch kind so they cannot reorder.

Feedback is `bugsee-android-feedback` on Android (`Bugsee.ext(Feedback.class)`) and a separate iOS module that pins the core exactly. The core SPM package does not contain it. Absent the artefact, `Bugsee.Feedback` is a logged no-op. Declaring the artefact is the integration.

### Build-file edits

`BugseeAndroidGradleSetup` and the iOS post-process edit files the customer owns. Insert only at a canonical anchor line, recognised by a lexer that knows comments, strings, and CRLF. If the anchor is missing, duplicated, nested, Allman-braced, or ends inside a block comment, refuse with a message that names the file and the anchor, and do not quote a value from the file. Recognise this package's own lines by marker plus the version it wrote. Never edit a dependency line the customer wrote. A second run changes nothing; opt-out returns the file to its previous bytes. Property keys are compared locale-insensitively (`tr_TR` folds `I` to a dotless ı).

Symbol upload stays in Phase B. The CLI adds the API version path itself. Do not append `/v2`. An unset token skips; a present empty string is a token and must not be sent. dSYMs go out from an Archive post-action, gated on Archive and Release. A placeholder token never contacts the real endpoint. A managed frame with no position is sent as it is — do not invent `0`. JS source-map upload (workbook 16.17–16.19) is not this package's path; IL2CPP linemaps are.

## Public surface checklist (must not miss)

### Facade (`Bugsee`)

Lifecycle: `Launch`, `Relaunch`, `Stop`, `GetLaunched`/`Status`, `GetLaunchOptions`, `DeleteCollectedDataOnDevice`  
Blackout: `StartBlackout`, `EndBlackout`, `IsBlackout` (obsolete Pause/Resume → blackout if shimmed)  
Logging: `Log`, `Trace`, `Event`, `LogException`, `LogUnhandledException`, `TestCrash`  
Reporting: `ShowReportDialog`, `Upload`, `CreateReport`, `SetReportHandler`, `Upload(Report)`  
Filters: network / log / breadcrumb  
Exchange: `GetExchangeFactory`, `AddNetworkEvent`, `AddBreadcrumb`  
Privacy: secure rectangles (Unity-relevant); skip Activity/View/WebView-specific APIs or no-op with docs  
Identity: `SetUserIdentifier` / `Get` / `Clear` (not email-as-primary)  
Attributes: set/get/clear/clearAll/getAll  
Appearance: `GetAppearance()`  
Video: `GetVideoFrameConsumer` (internal to capturer); `ResetVideoCapturePermission`  
APM: `StartTransaction`, `StartSpan`, `GetActiveSpan`  
Feedback: `Bugsee.Feedback` → show UI, greeting, listener  
Wrapper: **internal registration only** — not an app-facing `SetWrapper`

### Contracts to port (public)

`Options` (+ ~69 keys), option enums, `OptionsBuilder`, `BugseeLaunchOptions` / `AndroidLaunchOptions` / `IOSLaunchOptions`  

`EventFilter`, `NetworkEvent`, `LogEvent`, `Breadcrumb`, `BugseeExchangeFactory`  
`Report`, `Attachment`, `ReportHandler`, `ReportCreationListener`  
`LifecycleEvents`, `LifecycleEventListener`, `BugseeStatus`, feedback lifecycle consts  
`Appearance`, `ReportAppearance`, `NotificationAppearance`, `FeedbackAppearance`  
`VideoFrameConsumer.RowOrder`  
Performance: `Span`, `Transaction`, `SpanStatus`, …  
Feedback: `FeedbackListener`, …  

### Wrapper-only (implement, don’t expose as app API)

`BugseeWrapper` / `BGSBugseeWrapper`, `BugseeWrapperChannel` / `BGSWrapperChannel`, `DataRequestProvider` (`requestData` / `onResult`). The app never calls `SetWrapper`.

## Reference sources

- Specs: `sdk/wrapper-channel`, `sdk/wrapper-data-requests`, `sdk/wrapper-workbook`, `sdk/options`, `sdk/report-contract` in `bugsee/specs`
- Android facade and contracts: `android/sdk` `Bugsee.java`, `contracts/internal/BugseeWrapper.java`, `BugseeWrapperChannel.java`
- iOS: `bugsee-cocoa` `Bugsee.h`, `BGSContracts.h` at the pinned SPM tag
- `android/sdk/docs/api-7.x.md` may lag source — trust the contracts and the specs
- Legacy Unity inventory: `cross/unity/.../Plugins/Bugsee/` (reference only)

## Implementation order

The workbook's minimum (registration, identity, channel, `vh` answer, secure rects, handlers) comes before treating the product surface as done. Shipped pieces stay; the order below is the gap.

1. Move Android registration into `UnityWrapperProvider` (`initOrder="200"`), and iOS registration into `+load`. Stop constructing a second wrapper from C# `Launch`.
2. Store `onWrapperChannelAvailable` and route host logs / `UnityWebRequest` / breadcrumbs through it.
3. Secure-rect registry: per-owner, per-display version, platform units, pull-time origin.
4. Report path: `isTerminating` stays native; attachments by path or bytes; severity `0` is unset.
5. Re-derive the option table from `sdk/options` for 7.3.0 and 7.0.0-beta5.
6. Attribute read-back, empty user id clears, breadcrumb levels by name.
7. Optional `vh` walk (synchronous, bounded). `null` remains valid until then.
8. Feedback artefact, frame capturer, spans — after the list above.
9. Optional legacy `BugseePlugin` shim, if a product needs it.

## Risks

- A C# `setWrapper` inside `Launch` still looks correct in a cold start and misses crash recovery. That is the registration bug this design closes.
- Filter callbacks that block the Unity player, or that the player waits on, deadlock. Complete them off the wait, inside the provider's deadline, and drop on timeout.
- Secure rects in Unity pixels on a high-density Android device mask the wrong region while still looking enabled.
- iOS recovered-report edits are best-effort on the current pin.
- Android 7.3.0 emits two reports for one unhandled managed exception.
- Options property naming is Android-first until the iOS SDK is RC; then unify the typed C# names. Keys already follow `sdk/options` per platform.

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
- No `BUGSEE_APP_TOKEN` → silent skip (opt-in)
- A present empty token is not "unset" — do not build a request URL from it
- The CLI adds the API version itself; the orchestrator does not append `/v2`

NDK capture and symbol upload travel together on plugin 4.x via `plugin.ndk.enabled`. An opt-out removes the line and the flag this package wrote, and leaves a line the app wrote. The debug id injected into the player must be the id of the file that gets uploaded, on the artifact the player loads.

Standalone `bugsee-cli` may still exit non-zero when used directly in CI scripts.

### Event payload (who emits addresses)

| Event class | Payload | Apply path | Dependency |
|---|---|---|---|
| Native fatal (iOS) | module UUID + PC (native SDK) | primary (dSYM → cpp → LNM) | archive dSYM + linemap |
| Native fatal (Android) | module UUID + PC | primary | NDK + ELF/Breakpad **line** info + linemap |
| Managed fatal (IL2CPP) | UnityManagedException JSON; `address` when present in stack lines; else MethodMap strings | primary if addrs; else MethodMap | ExceptionPipeline / bridge |

Managed stacks without instruction addresses use MethodMap demangle only (not LNM file/line).
