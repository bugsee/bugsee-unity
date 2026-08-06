# Bugsee Unity SDK — Design

UPM package `com.bugsee.unity` (repo: `bugsee/bugsee-unity`).  
Legacy foundation: `cross/unity`. Min Unity: **2021.3**.

## Understanding

- **What:** UPM-first Unity SDK whose public C# API mirrors Android SDK **7.x** (`Bugsee` facade + public `contracts`), with platform bridges for Android (EDM4U/Maven) and iOS (SPM; local package until nextgen is on `bugsee/spm`).
- **Why:** Replace legacy `.unitypackage` / `Assets/Plugins` distribution; align with 7.0 redesign and other wrappers (Flutter 7.x pattern).
- **Who:** Unity game/app developers; Bugsee maintainers.
- **Non-goals (initial implementation):** OpenUPM publish, dual release repo, Asset Store `.unitypackage`, full iOS nextgen API parity deep-dive before Android bridge works, RN 6.x bridge patterns.

## Assumptions

1. Android pin: Maven `com.bugsee:bugsee-android:7.0.4` (+ feedback) via EDM4U.
2. iOS: local SPM under `Native~/ios/Bugsee` until remote `github.com/bugsee/spm`.
3. Public C# surface mirrors Android 7.0; intentional C#/.NET deviations allowed (see below).
4. SDK-internal contracts (`capture` aggregators, most `contracts.internal.*` except what wrappers need for `BugseeWrapper`) are **not** public C# API.
5. Optional Gradle network extensions (OkHttp/Ktor/Cronet) are out of default Unity deps unless explicitly added later.

## Decision log

| Decision | Choice | Alternatives | Why |
|---|---|---|---|
| Repo layout | UPM package at repo root | `package/` subfolder; Sentry dual-repo | Simplest Git URL install |
| Android natives | EDM4U → Maven | Vendored Unity AAR | Matches Flutter/RN 7.x; smaller git |
| iOS natives | SPM (local path now → remote later) | Vendored xcframework only; CocoaPods | Aligns with Bugsee iOS distribution |
| Min Unity | 2021.3 | 2018.3 / Unity 6 only | SPM `PBXProject` APIs + practical floor |
| Primary C# API | Android 7.0 mirror | Keep legacy `BugseePlugin` names | Clean 7.0 surface; legacy shims optional later |
| `BugseeWrapper` | Unity implements + `Bugsee.setWrapper` | App-only listeners | Required wrapper contract for metadata, secure rects, report/lifecycle hooks, `requestData` |
| Filters/handlers | First-class C# callbacks + JNI proxies | Omit (legacy gap) | Parity with Android/Flutter |
| C# deviations | Allowed where idioms win | Pure Java-shaped API | Better Unity/.NET DX |

### C# idiom deviations (approved)

| Android | C# |
|---|---|
| `EventFilter.filter(T, Callback1<T>)` | Prefer sync `Func<T,T>` (null/drop = omit); async completion only when needed |
| `Callback1` / `Runnable` | `Action` / `Action<T>` |
| `Map<String,Serializable>` options | Typed options builder **and** raw dictionary escape hatch |
| `Bugsee.ext(Feedback.class)` | `Bugsee.Feedback` property (resolves extension under the hood) |
| Java listeners | C# `event` / `Action<>` on `Bugsee` |
| Lifecycle string constants | `LifecycleEvents` consts **+** optional enum for known events |
| `IssueSeverity.Critical` (6.x) | Not in 7.x — map legacy Critical → `High` or `Blocker` if compat shim added |

### Do not copy (stale / wrong)

- Flutter still calling removed `setAdditionalDataCapture` / `setSystemSecureRects` → use **`BugseeWrapper`** instead.
- React Native Android bridge (still 6.x packages).
- Legacy Unity V4 `Snapshot` / `onNewFrame` path.
- Bundled 6.x `Bugsee-Unity.aar` / `BugseeUnityAdapter`.

## Native packaging (already scaffolded)

- `package.json` → `com.bugsee.unity`, depends on `com.google.external-dependency-manager`
- `Editor/BugseeAndroidDependencies.xml` → Maven 7.0.4
- `Native~/ios/Bugsee/Package.swift` + `Editor/BugseeIosSpmPostProcess.cs`
- `Tools~/scripts/update-native-sdks.sh` + `versions.env`

## Runtime project structure

```
Runtime/
├── Bugsee.cs                         # Static facade mirroring Bugsee.java
├── Bugsee.Runtime.asmdef
│
├── Contracts/                        # Public mirror of com.bugsee.library.contracts
│   ├── Options/                      # Options keys, enums, OptionsBuilder/Container
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
│   │   └── IOSBridge.cs                  # Throws NotSupported until nextgen bridge lands
│   └── Editor/
│       └── EditorBridge.cs               # Editor / unsupported no-op
│
├── Capture/
│   └── BugseeFrameCapturer.cs            # VideoMode.DirectBuffers → getVideoFrameConsumer()
│
├── Internal/
│   ├── ExceptionPipeline.cs
│   ├── MainThreadDispatcher.cs
│   └── BugseePackageVersion.cs
│
└── Components/
    ├── BugseeLauncher.cs
    └── BugseeBehaviour.cs                # GameObject host if needed
```

**Editor/** (existing): EDM deps, iOS SPM post-process, launcher inspector.

## Bridge duties on Launch (Android)

Always register (Flutter-style), regardless of whether the app subscribed yet:

1. **`Bugsee.setWrapper(unityWrapperImpl)`** — identity, `requestData`, secure rects, wrapper report hooks, `onLifecycleEvent`
2. `setNetworkEventFilter` / `setLogEventFilter` / `setBreadcrumbFilter` → C# (passthrough if no subscribers)
3. `setReportHandler` → app `IReportHandler` / events (wrapper runs first inside SDK)
4. `setLifecycleEventsListener` → forward **all** 7.0 `LifecycleEvents` (+ feedback lifecycle strings when present)
5. `Bugsee.ext(Feedback)` → `setOnNewFeedbackListener` when feedback AAR present
6. If `Options.CaptureVideoMode == DirectBuffers` → start `BugseeFrameCapturer`

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

`Options` (+ ~69 keys), option enums, `OptionsContainer`  
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

1. `Contracts/Options` + enums + `OptionsBuilder`
2. `IBugseeNativeBridge` + Android facade methods (no callbacks yet)
3. `BugseeWrapperProxy` + `setWrapper` on launch
4. Filters + `ReportHandler` + lifecycle proxies
5. Feedback extension property
6. `BugseeFrameCapturer` (DirectBuffers)
7. Appearance + exchange factory + APM
8. iOS bridge against nextgen SPM
9. Optional legacy `BugseePlugin` compat shim (if product needs it)

## Risks

- JNI proxy threading: always marshal app callbacks to Unity main thread; honor `isTerminating` (no async round-trip).
- `logException` cannot carry structured foreign frames in 7.0 — degrade to message/labels (known platform gap).
- Local iOS SPM pbxproj injection is custom (no Unity local-SPM API) — verify on real Xcode export.
- Doc drift in `api-7.x.md` — trust `Bugsee.java` + contracts source.
