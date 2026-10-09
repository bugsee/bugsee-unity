# Bugsee Unity SDK — Design

UPM package `com.bugsee.unity` (repo: `bugsee/bugsee-unity`).  
Legacy foundation: `cross/unity`. Min Unity: **2021.3**.

## Understanding

- **What:** UPM-first Unity SDK whose public C# API mirrors Android SDK **7.x** (`Bugsee` facade + public `contracts`), with platform bridges for Android (EDM4U/Maven) and iOS (SPM; native pin is `bugsee-cocoa` nextgen).
- **Why:** Replace legacy `.unitypackage` / `Assets/Plugins` distribution; align with 7.0 redesign and other wrappers (Flutter 7.x pattern).
- **Who:** Unity game/app developers; Bugsee maintainers.
- **Non-goals (initial implementation):** OpenUPM publish, dual release repo, Asset Store `.unitypackage`, full iOS nextgen API parity deep-dive before Android bridge works, RN 6.x bridge patterns.

## Assumptions

1. Android pin: Maven `com.bugsee:bugsee-android:7.1.4` + `bugsee-android-ndk:7.1.4` via EDM4U; Gradle plugin `4.0.6`. Core publishes no transitives (no kotlin-stdlib / fragment pull); NDK must be declared. Feedback AAR stays optional.
2. iOS: pin [`bugsee/bugsee-cocoa`](https://github.com/bugsee/bugsee-cocoa) `nextgen` by commit (`IOS_SDK_COMMIT` in `Tools~/versions.env`). Xcode still consumes a vendored SPM wrapper under `Native~/ios/Bugsee` because cocoa’s `Package.swift` is a release template, not a consumable package.
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
| `IssueSeverity.Critical` (6.x) | Not in 7.x — map legacy Critical → `High` or `Blocker` if compat shim added |

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
- **Wire keys** — property setters store under Android 7.0 `Options.*` (or mapped iOS keys later). Enums as wire ints/bytes; bridge coerces to native enums (`AndroidOptionsMapper`).
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
- `Editor/BugseeAndroidDependencies.xml` → Maven 7.1.4 (+ NDK, both explicit)
- `Native~/ios/Bugsee/Package.swift` + `Editor/BugseeIosSpmPostProcess.cs`
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
│   │   └── IOSBridge.cs                  # P/Invoke → BugseeUnityBridge.mm + xcframework
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

`Options` (+ ~69 keys), option enums, `OptionsBuilder`, `BugseeLaunchOptions` / `AndroidLaunchOptions` / `IOSLaunchOptions`  

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

## Risks

- JNI proxy threading: always marshal app callbacks to Unity main thread; honor `isTerminating` (no async round-trip).
- Local iOS SPM pbxproj injection is custom (no Unity local-SPM API) — verify on real Xcode export.
- Doc drift in `api-7.x.md` — trust `Bugsee.java` + contracts source.
- Options property naming is Android-first until iOS SDK RC/stable — then unify typed names across platforms.

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

Standalone `bugsee-cli` may still exit non-zero when used directly in CI scripts.

### Event payload (who emits addresses)

| Event class | Payload | Apply path | Dependency |
|---|---|---|---|
| Native fatal (iOS) | module UUID + PC (native SDK) | primary (dSYM → cpp → LNM) | archive dSYM + linemap |
| Native fatal (Android) | module UUID + PC | primary | NDK + ELF/Breakpad **line** info + linemap |
| Managed fatal (IL2CPP) | UnityManagedException JSON; `address` when present in stack lines; else MethodMap strings | primary if addrs; else MethodMap | ExceptionPipeline / bridge |

Managed stacks without instruction addresses use MethodMap demangle only (not LNM file/line).
