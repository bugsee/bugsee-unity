# Bugsee Unity SDK

Bug reporting, crash capture, and session replay for Unity games and apps on iOS and Android.

## Requirements

- Unity **2021.3** or newer
- iOS builds: Xcode with Swift Package Manager support; **iOS 15+** (Bugsee 7.0.0-beta6); `bugsee-cli` on the PATH for archive-time symbol upload
- Android builds: nothing to set up (no EDM4U, no custom Gradle templates; the build needs Maven Central, which Unity's Gradle project already lists); `bugsee-cli` on the PATH for Editor symbol upload

## Installation

Package Manager → **+** → **Add package from git URL**:

```
https://github.com/bugsee/bugsee-unity.git
```

That is all: there is no scoped registry to add and no dependency-resolve step. On Android, every build
declares `com.bugsee:bugsee-android` and `com.bugsee:bugsee-android-ndk` in the generated
`unityLibrary/build.gradle` (and adds `mavenCentral()` to the project repositories if Unity's Gradle layout
lacks it). Projects that use EDM4U for other SDKs are unaffected. If the project already declares the Bugsee
Android SDK itself (EDM4U, a custom `mainTemplate.gradle`, a local AAR), that declaration wins: nothing is added
next to it, and the build log warns if its version differs from the package's pin or the NDK artifact is missing.

## Launch

```csharp
#if UNITY_ANDROID
var options = new AndroidLaunchOptions
{
    DetectAndReportCrash = true,          // native process death (Java + NDK)
    CaptureManagedExceptions = true,      // Unity ExceptionPipeline (default on if omitted)
    DetectAndReportHang = true,
    DetectAndReportExitNotResponding = true, // ApplicationExitInfo ANR
    DetectAndReportExitLowMemory = true,     // OOM / low memory exits
};
Bugsee.Launch(appToken, options);
#elif UNITY_IOS
var options = new IOSLaunchOptions
{
    DetectAndReportCrash = true,
    CaptureManagedExceptions = true,
    DetectAndReportHang = true,
};
Bugsee.Launch(appToken, options);
#endif
```

`CaptureManagedExceptions` is independent of `DetectAndReportCrash`. Omit it to keep the default (enabled).

## Lifecycle: Launch, Relaunch, Stop, Delete

`Launch`, `Relaunch`, `Stop` and `DeleteCollectedDataOnDevice` do not wait for the native SDK to finish. They are handed to the Unity main thread. Called from another thread, on Android the call is queued there and never refused (it waits for the main thread). On iOS the caller waits up to 5 s for the main thread to take it; if it does not, the request does not run (an error is logged, `LaunchAsync`/`RelaunchAsync` complete `false`, `StopAsync` faults with `TimeoutException`).

Ordering differs per platform, and holds for the wrapper's scheduling before the hard deadline below:

- **Android**: `Launch`, `Relaunch` and `DeleteCollectedDataOnDevice` run one at a time, in request order. A queued `Launch` replaces earlier queued `Launch`/`Relaunch` requests after the last queued Delete, and a `Relaunch` folds its options into the one before it. `Stop` is not queued behind them: it starts (or joins) a native stop at once and drops every queued `Launch`/`Relaunch`. A queued Delete is never dropped, and a Delete does not cancel `Launch`/`Relaunch` requests queued before it: they run first.
- **iOS**: only stops and the delete's wipe are serialized, one native operation at a time. A `Launch`/`Relaunch` requested while a stop or a wipe is in flight is deferred until the SDK reports it finished. A later `Launch`/`Relaunch` replaces a deferred one, and a later `Stop` or `DeleteCollectedDataOnDevice` cancels it (it completes `false`). A Delete is never skipped because a `Launch` follows it: that `Launch` waits and runs after the wipe.

What each call guarantees:

- **Stop**: its completion runs when the SDK has reported the stop finished, or at the soft deadline. A `Launch`/`Relaunch` requested during the stop waits for it, and the completion runs before that `Launch` does. If the completion itself calls `Launch`, that later request replaces the waiting one (it completes `false`) and is the one that starts. On iOS the completion is dropped only if the call never ran (the 5 s hand-off) or an earlier completion of the same stop already launched; `StopAsync` completes either way. To change the token or options of a running SDK, call `Stop` and launch from its completion.
- **Launch / Relaunch**: starts a session when the SDK accepts it. A Launch while the SDK is already running ignores the new token and native options. On Android it also leaves the managed capture hooks unchanged; on iOS the managed capture options are applied again, so a repeated `Launch` with different capture options can change (or switch off) managed capture of a running session. Android ignores a `Relaunch` unless the SDK is `Launched`.
- **DeleteCollectedDataOnDevice**: stops the SDK if needed (joining a stop in flight), then deletes the data collected on the device, including finished report bundles, and leaves the SDK stopped: call `Launch` again to resume. It has no result. A failure is only logged: on iOS a warning when the SDK reports the wipe failed or had not finished stopping, on Android a warning when the native delete fails or does not answer in time.

Async variants:

- `LaunchAsync` / `RelaunchAsync` return `Task<bool>`: `true` when the SDK accepted the launch, `false` when it refused it or a later request cancelled it (which requests cancel a waiting launch is the per-platform ordering above). If the SDK gives no answer within 10 s, they complete `true` when its status is `Launching`/`Launched` and `false` otherwise.
- `StopAsync` returns `Task` and completes when the SDK has reported the stop, even if a Launch follows. It faults with `TimeoutException` when the SDK has not reported the stop within 10 s of that call (a call that joins a stop in flight has its own 10 s), and on iOS also when the main thread did not take the call within 5 s. On Android the Task stays pending if the Unity main thread is stalled, because the 10 s deadline is driven from the main thread.

Deadlines, for an SDK that does not report back:

- **10 s (soft)**, from when the call started: the caller is answered with a warning, and nothing else is released. A `Stop` completion runs, `StopAsync` faults, and on Android an in-flight `Launch`/`Relaunch`/`Delete` answers its internal completion. The native operation stays in flight: a `Launch`/`Relaunch` requested meanwhile still waits for it, and the SDK may still finish it. On Android that includes a delete already handed to native, which can complete after the 10 s warning.
- **60 s (hard)**: the wrapper stops waiting for a stop and moves on, with a warning. On iOS this also covers a wipe; on Android also an in-flight launch, relaunch or delete. A late report from the SDK is ignored by token. An iOS `Launch`/`Relaunch` already handed to the SDK has no hard deadline, only the 10 s status answer.

Residual races after the hard deadline (the native SDKs cannot cancel an operation or report a refusal, so these are accepted):

- **iOS**: a deferred `Launch` is released at the give-up, so if the native stop is in fact still running it can stop a `Launch` made after the hard deadline. A wipe still running when its deferred `Launch` is released overlaps that launch in the same way.
- **Android**: a late `Launch`/`Relaunch` result arriving after the reset is ignored when no stop intent is set (no `Stop` requested and no `Delete` started since), so the managed capture hooks can be out of step with that session until the next lifecycle call. With a stop intent set, a session started by a late result is stopped again.
- **Android**: after the reset of a delete, a queued `Launch` can run while the native delete is still wiping. A `Delete` started after the reset of a relaunch can overlap a relaunch that completes later still.

## Crash & exception capture

| Source | API / option | Severity default |
|---|---|---|
| Manual | `Bugsee.LogException` | non-fatal |
| Manual unhandled | `Bugsee.LogUnhandledException` | fatal |
| Unity `Debug.LogException` / routed exceptions | ExceptionPipeline | non-fatal (set `TreatUnityLogExceptionsAsFatal`) |
| `AppDomain.UnhandledException` | ExceptionPipeline | fatal |
| `UnobservedTaskException` | ExceptionPipeline | non-fatal |
| Native abort / signal | `DetectAndReportCrash` + native SDK | fatal (next launch) |
| Android hang | `DetectAndReportHang` | hang report |
| Android ANR / OOM | `DetectAndReportExitNotResponding` / `DetectAndReportExitLowMemory` | exit reports |

Managed exceptions use the **UnityManagedException** JSON-in-reason contract (`name`, `reason`, `frames[{trace,address?}]`, `signature`, `buildID`, `moduleUUID`, optional nested `cause`).

### How to get C# class + file:line on `LogException`

| Mode | Build / capture | Backend | File:line? |
|---|---|---|---|
| **A. Rich managed stacks** | Mono, or IL2CPP **Method Name, File Name, and Line Number** (or Script Debugging) | Parse `frames[].trace` | **Yes** (from string) |
| **B. Release Method-only + LNM** | IL2CPP; thrown exception; SDK emits native IPs via `il2cpp_native_stack_trace` | PC → dSYM/ELF → LineNumberMappings | **In progress** (needs symbols + `il2cpp-linemap`; absolute IPs need module load address) |
| **C. MethodMap only** | IL2CPP mangled names; map uploaded | MethodMap.tsv demangle | **Names only** (no invented `.cs:line`) |

Mode **B** follows the same capture idea as Sentry (`il2cpp_native_stack_trace` on a thrown exception). Prefer:

```csharp
try { DoThing(); }
catch (Exception ex) { Bugsee.LogException(ex); } // real thrown exception
```

`new Exception("…")` never thrown cannot get Mode B lines. Unity **Stack Trace Log Type** does not rewrite `Exception.StackTrace`.

### Competitors (summary)

| | Managed `Notify`/`CaptureException` file:line on Release IL2CPP | Upload LNM | Native fatal → C# line |
|---|---|---|---|
| **Sentry** | Yes — IL2CPP backend IPs + server LNM | Yes | Yes |
| **Bugsnag** | Yes when maps+symbols uploaded (mobile) | Yes (CLI fail-closed unless opt-out) | Yes |
| **Firebase** | Relies on Unity managed stack settings; strong native symbols.zip | Not emphasized | Yes (NDK/dSYM) |
| **Backtrace** | Prefer real `Exception` object; WebGL no C# lines | symbols.zip focus | Yes |
| **Bugsee** | Mode A; Mode B path shipping (IPs + LNM; prove end-to-end); Mode C names via MethodMap | Yes | Yes (S1/S2) |

IL2CPP **C# file/line** via LNM always needs uploaded `il2cpp-linemap` + matching dSYM/ELF with **line** info (and for Mode B, instruction addresses on the event).

## Symbol upload (Editor / CI)

Set `BUGSEE_APP_TOKEN` in the environment (opt-in). Optionally `BUGSEE_CLI_PATH`, `BUGSEE_VERSION`, `BUGSEE_BUILD`, `BUGSEE_IL2CPP_MAPPING`, `BUGSEE_IL2CPP_UUIDS`, `BUGSEE_BUILD_UUID`.

### Where `bugsee-cli` comes from (Android Editor uploads)

A Unity Editor started from the Hub or the Finder does not inherit your shell's `PATH`, so a CLI installed with Homebrew or `install.sh` is often invisible to it. The Editor uploads therefore look, in order, at: `BUGSEE_CLI_PATH`; the Editor's `PATH`; the places the installers use (`/opt/homebrew/bin`, `/usr/local/bin`, `~/.local/bin`, `~/.cargo/bin`, `~/.bugsee/bin`, `%LOCALAPPDATA%\Bugsee\bin`); the package's cache (`~/Library/Caches/Bugsee/cli` on macOS, `$XDG_CACHE_HOME/bugsee/cli` or `~/.cache/bugsee/cli` on Linux, `%LOCALAPPDATA%\Bugsee\cache\cli` on Windows, else `Library/Bugsee/cli` in the project). An installed CLI older than 0.8.0 is passed over.

When none is found and a token is set, the pinned release (0.8.1, the Bugsee Gradle plugin's default) is downloaded from `https://download.bugsee.com/cli/v<version>/bugsee-cli-<triple>.tar.xz` (`.zip` on Windows), checked against the published `.sha256`, unpacked into that cache and run once to prove it works. The checksum comes from the same host as the archive, so it detects corruption, not a compromised host: set `BUGSEE_CLI_PATH` to a binary you vetted if that matters. `BUGSEE_NO_CLI_DOWNLOAD=1` turns the download off, `BUGSEE_CLI_VERSION` pins another release and `BUGSEE_CLI_BASE_URL` points at a mirror (https only), as for `install.sh`. If nothing can be found or downloaded the build warns once, naming what was tried, and continues without uploading. The iOS Run Script still starts a bare `bugsee-cli` (or `BUGSEE_CLI_PATH`) and does not use this lookup.

The iOS Run Script uploads for **every** Xcode configuration (Debug included, so a crash from a development build is symbolicated) and runs on every build that has the token in its environment, not only archives. Set `BUGSEE_SKIP_UPLOAD_ON_DEBUG=1` to skip the Debug configuration, for example for local Xcode runs.

| Artifact | When | CLI type |
|---|---|---|
| `LineNumberMappings.json` (+ MethodMap) | Android post-build; iOS at **Xcode build** (every configuration) | `il2cpp-linemap` |
| Android `symbols.zip` / ELF | Android post-build | `elf` |
| ProGuard / R8 `mapping.txt` | Android post-build when present | `proguard` |
| iOS `.dSYM` | Xcode Run Script (`Bugsee Upload Symbols`), every configuration | `dsym` |

Failure policy: **never fail the Unity build**. Missing maps/UUIDs/CLI → warn and continue.

### Player Settings checklist

**Android**

1. Scripting Backend: **IL2CPP** is the supported configuration for fully symbolicated native crashes. With Mono, frames inside the Mono runtime (`libmonobdwgc-2.0.so`, `libmono-native.so`, `libMonoPosixHelper.so`) cannot be symbolicated: Unity 2021.3 has no Mono ARM64 (ARMv7 only) and those libraries have no GNU build-id. Frames in `libunity.so` and `libmain.so` still symbolicate. A Mono Android build logs a one-time warning at build time (whether or not symbol upload is configured) and is never failed
2. Create symbols.zip / native debug symbols enabled
3. For primary LNM apply: native debug symbol level **FULL** (DWARF lines), not SYMBOL_TABLE-only
4. No custom Gradle templates needed: Bugsee patches the generated Gradle project (the Bugsee Gradle plugin with `ndk { enabled = true }` when it pins AGP 8.6+, i.e. Unity 6; on older AGPs, see below); a template of your own that pins another AGP major than your Unity's default fails the build and is reported

### Unity 2021.3 and 2022.3 (no Bugsee Gradle plugin)

Both ship AGP 7.4.2 / Gradle 7.5.1; Gradle plugin 4.0.8 needs AGP 8.6+ / Gradle 8.7+ and fails to load there (checked on 2022.3.62f3: `NoClassDefFoundError: KotlinCompilerPluginSupportPlugin`, then `NoSuchMethodError` on `OutOperationRequest.toListenTo`). What it does for a Unity app, and what stands in for it:

| Gradle plugin function | Unity 6 | Unity 2021.3 / 2022.3 |
|---|---|---|
| Declare `bugsee-android`, `bugsee-android-ndk` | Package (Maven injection) | Same |
| `assets/bugsee_build_id.properties` = UUID of the R8 mapping (the id the SDK reports) | Plugin | The package adds the same asset task to the launcher (`GradleBuildIdBlock`); needs AGP 7.4 to 8.5. With R8 off no asset is written and the SDK derives its own id |
| `com.bugsee.android.BUILD_UUID` manifest meta-data (fallback id when R8 is off) | Plugin | Not added: the SDK derives `UUID(token, versionName, versionCode)` itself when it finds neither; the uploads use that id (`AndroidBuildUuid`) |
| Mapping upload (`--uuid`, `--icon`) | Package Editor upload (the plugin finds no app token in a Unity build, so its own upload skips) | Same: mapping, same id as the asset, launcher icon attached |
| Native symbol upload | Package Editor upload of Unity's `symbols.zip` (same reason) | Same |
| NDK enabled | Plugin | Not applicable: the NDK artifact is declared and the Editor uploads the symbols |
| Bytecode instrumentation (OkHttp, HttpEngine, `android.util.Log`, thread, main-thread misuse, operation dispatch, Compose, startup tracing), SDK-extension provider stripping, Compose compiler plugin | Plugin | **Not recovered** (needs AGP 8.6+). They act on the app module's own JVM bytecode, which in a Unity app is a launcher stub, so little is lost, but a Java/Kotlin plugin of yours that uses OkHttp or `HttpEngine` is not auto-captured on these Unity versions |
| Build-info / size-analysis upload, dependency and timing collection | Plugin (skips, no token) | **Not recovered** |

Releases built without R8 (Minify off, the Unity default) have no mapping and no Java names to restore. If you do minify, add keep rules for the Unity wrapper classes: the package's `bugsee-unity-wrapper.proguard` is not applied by Unity unless you enable Player Settings > Publishing Settings > Custom Proguard File and copy the rules into `Assets/Plugins/Android/proguard-user.txt`, and that file does not yet cover `com.bugsee.unity.UnityManagedException` or `UnityWrapperIdentity` (R8 removes them: `ClassNotFoundException` at launch or on the first managed exception).

**iOS**

1. Scripting Backend: IL2CPP
2. Enable source mapping / Script Debugging as needed for LineNumberMappings generation
3. Build or archive in Xcode: the Run Script uploads the dSYMs of the configuration being built. Debug has a dSYM only with `DEBUG_INFORMATION_FORMAT = dwarf-with-dsym` (Unity exports Debug as `dwarf`); without one the script prints a warning and uploads no dSYM
4. Ensure `BUGSEE_APP_TOKEN` is visible to the Xcode Run Script phase
5. The iOS export sets `DEBUG_INFORMATION_FORMAT=dwarf-with-dsym` on every configuration of Unity-iPhone and UnityFramework, so Debug builds have a dSYM too. `dsymutil` on UnityFramework adds time to each Debug build; to keep Unity's own setting, tick **Bugsee → iOS → Keep Xcode Debug Info Format (skip dSYM force)** or set `BUGSEE_NO_DSYM_FORCE` in the Editor's environment (CI)

### Script Debugging vs dSYM

- **Script Debugging** / IL2CPP stacktrace information controls managed stack quality and LineNumberMappings generation.
- **dSYM / ELF** carry native line programs used for the primary LNM remap (native PC → C++ file/line → C#).
- You need **both** the linemap upload and native symbols with line info for viewer C# file/line on native fatals.

### Managed stacks: MethodMap vs LNM file/line

- ExceptionPipeline / `LogException` send **UnityManagedException** JSON (`frames[].trace`, optional `frames[].address`, `moduleUUID`, nested `cause`, `signature`, `buildID`).
- On IL2CPP, `frames[].address` is filled from **`il2cpp_native_stack_trace`** when the exception was thrown (paired index-for-index with managed frames; UUID matches Bugsee ELF/dSYM upload identity), or from native-looking hex in the stack string. Short Mono/IL offsets like `[0x00023]` are ignored.
- Worker: MethodMap demangles mangled names; when addresses + symbols + linemap match, **primary LNM** remaps to C# file:line. Absolute IPs require a matching crash module load address; relative offsets use base `0`.
- Multi-ABI Android: `moduleUUID` may be a CSV; worker tries each UUID (dashed/undashed).

## Field validation (S1 / S2)

Prerequisites (tooling + optional build-dir scan):

```bash
Tools~/scripts/validate-field-symbols.sh
Tools~/scripts/validate-field-symbols.sh /path/to/unity/build/output
```

Device/CI proof remains manual.

### Milestone S1 — iOS C# file/line

1. IL2CPP iOS build with LineNumberMappings generated.
2. `BUGSEE_APP_TOKEN` set for Unity export + Xcode archive env.
3. Archive; confirm Run Script uploads dSYM + `il2cpp-linemap` (UUID from `UnityFramework`).
4. Trigger a native fatal (`Bugsee.TestCrash` / signal) with `DetectAndReportCrash`.
5. Viewer shows **C# file/line** on the crashing frames; wrong/missing map leaves native frames intact.

### Milestone S2 — Android C# file/line

1. IL2CPP Android build; Create symbols.zip / **FULL** native debug symbols.
2. NDK enabled (`bugsee-android-ndk:7.4.1` + Gradle `ndk { enabled = true }` on Unity 6+).
3. Post-build uploads `elf` + `il2cpp-linemap` (multi-ABI UUIDs).
4. Native fatal on device; viewer shows **C# file/line**.

### Milestone C1 — managed auto-capture / LogException

1. `CaptureManagedExceptions` enabled (default).
2. Unhandled managed exception / `LogUnhandledException` appears once (no dual-hook duplicates).
3. **Mode A:** MethodFileLineNumber / Mono → viewer shows C# file:line from stack strings.
4. **Mode B (in progress):** Release IL2CPP + uploaded dSYM/ELF + `il2cpp-linemap` → `LogException` on a **thrown** exception emits native IPs for worker LNM. Validate on device before treating as parity with Sentry.
5. **Mode C:** MethodMap demangles mangled names when module UUID(s) match (names only if no addresses/lines).

Managed error/crash **grouping** (one signature per event, Unity-primary when strong): [exception-signatures.md](exception-signatures.md).

### Secure rectangles: coordinate origin

`Bugsee.AddSecureRectangle(RectInt)` and `RemoveSecureRectangle(RectInt)` take screen pixels with the origin at the **top-left** of the screen, y growing downward. This is what both native SDKs expect (Android `Rect` in display pixels; iOS points with a top-left origin, which the wrapper derives by dividing by the screen scale). The wrapper passes the rectangle through as given and does not flip it. Unity's `Screen`, `Input.mousePosition` and `Rect` from screen-space APIs use a **bottom-left** origin, so convert before adding:

```csharp
// Unity screen rect (bottom-left origin) -> rect for AddSecureRectangle (top-left origin).
static RectInt ToSecureRect(Rect unityScreenRect)
{
    int x = Mathf.FloorToInt(unityScreenRect.xMin);
    int y = Mathf.FloorToInt(Screen.height - unityScreenRect.yMax);
    int w = Mathf.CeilToInt(unityScreenRect.xMax) - x;
    int h = Mathf.CeilToInt(Screen.height - unityScreenRect.yMin) - y;
    return new RectInt(x, y, w, h);
}
```

A `RectTransform` corner from `GetWorldCorners` in a screen-space overlay canvas is also bottom-left: build the `Rect` from those corners, then convert. Rectangles are not flipped, scaled or moved on rotation or resize; rectangles are keyed by their exact left/top/right/bottom, so when the screen size changes remove the old rectangle (or call `RemoveAllSecureRectangles()`) before adding the new one.

**Limitation:** the rectangle must already be in the native display's coordinate space; the wrapper does no scaling or offsetting beyond the iOS points conversion. With Android Resolution Scaling or `Screen.SetResolution`, or a non-fullscreen Unity view (status bar or notch offset), `Screen.height` and pixels differ from native pixels, so scale or offset the rectangle yourself. On iOS the wrapper divides by `UIScreen.scale`, which is wrong if the render size differs from points x scale. A rectangle is removed with the same top-left rectangle it was added with.

## Native dependencies

- **Android:** Maven `com.bugsee:bugsee-android:7.4.1` + `com.bugsee:bugsee-android-ndk:7.4.1`, declared in the generated Gradle project by `Editor/BugseeAndroidMavenDependencies.cs` (Gradle plugin `4.0.8`, patched into the generated project when it pins AGP 8.6+). Native symbols for upload come from Unity's `symbols.zip` (Player Settings > Publishing Settings > Create symbols.zip). Unity 6 defaults it to Disabled and Public holds symbol tables only; native/IL2CPP file:line symbolication needs Debugging (about ten times larger, mind Google Play's limits). The build warns when a Bugsee app token is set and the level is not Debugging; `Bugsee > Android > Use Full Native Symbols (Debugging) For Symbol Upload` (or `BUGSEE_ANDROID_FULL_SYMBOLS=1`) builds with Debugging and restores the setting. On 2021.3 release builds also get `debugSymbolLevel 'FULL'`. Core publishes no transitives; NDK is a separate artifact and must be declared. Fragment secure-view overloads are typed as `Object`, so Unity JNI does not need `androidx.fragment`.
- **iOS:** SPM-only. Xcode resolves [`bugsee/spm`](https://github.com/bugsee/spm) `7.0.0-beta6` (iOS 15+). C bridge: `Plugins/iOS/BugseeUnityBridge.mm` + `BugseeUnityCallbacks.mm`.

### iOS bridge status

Wired: Launch/Stop, blackout, log/trace/event, exceptions (JSON-in-reason), TestCrash, report/upload (+ labels), identity (email API), attributes, secure rects, appearance colors/strings, feedback UI, network/log/breadcrumb filters, report handler (via `BugseeWrapper`), lifecycle listener (via wrapper `onLifecycleEvent` → `Bugsee.LifecycleEvent` / `ILifecycleEventListener`).

### Attributes

`SetAttribute` accepts a string (up to 1024 UTF-16 units), a bool or a number within the signed 64-bit range; anything else throws `ArgumentException`. `GetAttribute` returns the kind that was set: `string`, `bool`, an integer or a `double`. The numeric width is not promised (Android returns the boxed type that was set, with `sbyte`/`ushort` stored as `int`, `uint`/`ulong` as `long` and `decimal` as `double`; iOS returns `long` or `double`), and a 1.0 set as floating point stays floating. A container set natively reads as a `Dictionary` on Android and as JSON text on iOS. On iOS the bridge returns the value as a type tag and payload (`s:`, `b:`, `i:`, `d:`, `j:`; see `AttributeWire`); the SDK stores the plain value, so reports show it unwrapped.

### Launch options and notifications

`Bugsee.GetLaunchOptions()` returns the options the SDK is running with (its defaults with the ones given to `Launch` merged over them) as a read-only snapshot, or `null` while the SDK is not launched. Keys are each platform's own option names, so they are not the same on Android and iOS. Values are `string`, `bool`, `long` or `double` on both platforms (the SDK's narrower `int`/`float` defaults are widened), or a nested `Dictionary<string, object>` / `List<object>`; a value with no such form (a date, a Java array) reads as its text. The SDK's internal keys (those starting with two dollar signs) are not included.

`Bugsee.Notify(title, body = null, severity = IssueSeverity.Medium, fields = null, urgent = false)` relays a lightweight notification to the app's messaging integrations without creating a report (no video, logs or events). It is stored offline and sent in batches; `urgent` asks for immediate delivery. A call with an empty title, or before the SDK is launched, is ignored by the native SDK (the title case also logs a Unity warning). `fields` are extra string pairs; entries with a null key or value are dropped. The severity is always sent, so the native default for the shorter overloads is not used.

### Performance transactions and spans (APM)

```csharp
using (var checkout = Bugsee.StartTransaction("checkout", "user.flow", new Dictionary<string, object> { ["cart"] = 3 }))
using (var pay = checkout.StartChild("payment", "charge card"))
{
    pay.SetAttribute("provider", "stripe");
    // ... work ...
    pay.Finish();                     // Dispose without Finish also finishes with Ok
    checkout.SetStatus(SpanStatus.Ok);
}
```

`Bugsee.StartTransaction(name, operation, attributes = null)` returns an `ISpan` (`SpanId`, `TraceId`, `Operation`, `IsFinished`, `SetName`, `SetDescription`, `SetAttribute`, `SetStatus`, `StartChild(operation, description = null)`, `Finish()` / `Finish(SpanStatus)`, `Dispose` = `Finish()`). Attribute values follow the `SetAttribute` rules; a rejected value is dropped with a Unity warning (spans never throw). The statuses are `Ok`, `Error`, `Timeout`, `Cancelled`, `DeadlineExceeded` and `Unknown`.

A span wraps a native span, so finish or dispose every span you start. Finishing (or disposing) a span releases the native object; finishing a parent first finishes its still-live children as `Cancelled`. A span that is dropped without finishing is not reported as finished, and on iOS its native handle is released by a finalizer. After `Finish` every call on the span does nothing. `StartTransaction` returns an inert span that accepts every call (empty ids, reads as finished after `Finish`) while the SDK is not launched or when the name or operation is empty. With performance monitoring off the native SDK returns its shared no-op span (ids read `"0"`), which is wrapped like any other span. While the SDK is not launched (including while it is still launching) the wrapper itself returns an inert span with empty ids. No call on a span throws: invalid input and native failures log a Unity warning. If a child finishes on another thread while its parent finishes, the status that reached the child first wins. Spans are only created from a parent: `startSpan` and `getActiveSpan` are not exposed, because the native SDKs track the active span per thread (on iOS it is not propagated across `dispatch_async`). A span can be used from any thread, including thread-pool and `Task` threads: on Android a managed thread-pool thread is attached for the duration of each call (one attach for a whole `Finish`) and detached again, because the JVM aborts the process on a JNI call from an unattached thread; the main thread, Java-owned threads and threads you attached yourself are never attached or detached by the wrapper, so do not use spans from a pool thread you attached yourself, and on iOS the handle is an id. A parent finished on one thread while a child finishes on another is safe.

Full product documentation: [docs.bugsee.com/sdk/unity](https://docs.bugsee.com/sdk/unity/).

## Experimental: Unity frame pusher (Android)

Not benchmarked beyond one tablet; do not enable it in production without measuring. Nothing here changes unless you opt in: by default the Android SDK captures the window itself (`VideoMode.V2`), and the capturer only runs for a launch that asks for `VideoMode.DirectBuffers` (or an explicit `Enable()` call).

**Unity versions.** Supported: release builds (channel `f` or `p`) of Unity 2022.2.0f1 and newer, Android player builds, with the Screen Capture module. `AndroidJNI.NewDirectByteBuffer` first appears in 2022.2.0a11 (checked against Unity's reference source: 2022.2.0a1, 2022.1 and 2021.3 do not have it; 2022.2.0b1, all of 2022.3 LTS and 6000.x do), but alpha and beta builds are treated as unsupported. **Not supported: Unity 2021.3 and 2022.1.** The only alternatives there (a Java `byte[]` per frame, or a file) were measured and rejected (about 0.9 s per 1 MB frame, or disk writes). The minimum is one constant, `UnityCapturePolicy.MinimumUnityVersion`, plus the `BUGSEE_UNITY_CAPTURE` version define (`2022.2.0`) in `Bugsee.Runtime.asmdef`.

**Falling back to the default capture.** `VideoMode.DirectBuffers` means "the app pushes the frames", and in Unity the only frame source is this capturer. So you ask for it with the launch option alone (no `Enable()` call needed): `Bugsee.Launch` / `Relaunch` start the capturer, with `BugseeFrameCapturer.DefaultOptions` (set it before launching). If it cannot run, the wrapper itself, before the options reach the native SDK, replaces `DirectBuffers` with the default capture (`VideoMode.V2`) and logs one warning with the reason, so the session never runs in `DirectBuffers` without a frame source (that records no real video). Reasons: the player was not probed yet (a launch from another thread before Unity finished starting), Unity that is not a release build of 2022.2.0f1 or newer, no Screen Capture module, no `AsyncGPUReadback` on the device, a graphics API whose readback row order was not measured (OpenGLES3 and Vulkan are measured; set `DefaultOptions.RowOrder` to declare another), or the capturer failing to start after the launch was accepted (then the wrapper relaunches with the default capture, unless a newer Launch/Relaunch is waiting, which then decides). The capturer follows what the native SDK actually did: an ignored Launch (SDK already running) or a refused one leaves it alone, and an accepted one is read back from the SDK's effective launch options. Other video modes are never touched. Apps that push frames themselves through the native `getVideoFrameConsumer()` are affected too: use a native mode there or this fallback applies.

```csharp
// using Bugsee.Capture;
#if UNITY_ANDROID && !UNITY_EDITOR
BugseeFrameCapturer.DefaultOptions = new BugseeFrameCapturerOptions { TargetFps = 5, MaxDimension = 640 }; // optional
Bugsee.Launch(token, new AndroidLaunchOptions { CaptureVideo = true, CaptureVideoMode = VideoMode.DirectBuffers });
#endif
```

How it works: at the end of each due frame it calls `ScreenCapture.CaptureScreenshotIntoRenderTexture`, scales into a small RenderTexture and reads it back with `AsyncGPUReadback` (never a synchronous read). One request is in flight at a time; frames that come due meanwhile are dropped, not queued. The readback is copied once into a persistent native buffer, secure rectangles are blanked in place, and a push thread wraps that memory as a Java direct `ByteBuffer` for `VideoFrameConsumer.addFrame` with the SDK's own clock as the timestamp. No Java array, and the wrapper writes no frame to disk (the SDK itself still writes its recorded video as usual). `BugseeFrameCapturer.Stats` shows requested, pushed, failed and dropped counts; `BugseeFrameCapturerOptions.LogStats` also logs them every 10 s. `Enable()` called off the main thread is queued and returns true before it ran; check `IsEnabled`.

What to know before using it:

- Needs `VideoMode.DirectBuffers`. With any other mode the SDK ignores the frames; with `DirectBuffers` and no `Enable()` nothing pushes frames, so there is no real video.
- It records Unity's final composite, including screen-space overlay UI. Native Android views (WebViews, dialogs, system UI) are not in the video.
- Redaction: the SDK does not mask frames pushed this way: no secure rectangles, no secure views, and no pixelation. The pusher blanks rectangles registered with `Bugsee.AddSecureRectangle`, read as native window pixels (top-left origin). They are mapped relative to where Unity's view sits in the window (its position and size, read again when each capture starts and when its pixels arrive, so a status bar, cutout or multi-window offset is accounted for) and scaled to the frame; if that position cannot be read, or the surface found does not have the aspect ratio of Unity's screen (a plugin's SurfaceView, or a view caught mid-rotation), the whole frame is blanked while a rectangle is registered. A rectangle is masked if it was registered when the capture started or when the pixels arrived. Secure native views are not known to it. Blackout is honoured by the SDK.
- Row order of the readback was measured for Vulkan (Galaxy Tab S5e, Android 11, Adreno 615, Unity 6000.0.84f1: TopDown) and for OpenGLES3 (Android 16 arm64 emulator, swiftshader, Unity 6000.0.84f1: BottomUp, with a red-over-blue pattern; no GLES3 device was measured). For any other graphics API (OpenGLES2) it is unverified: the launch falls back to the default capture (above), and a capturer started directly with `Enable()` fails closed (every frame fully black while a secure rectangle is registered). Check the order on your device with `BugseeFrameCapturerOptions.LogOrientationProbe` (the sample's `bugsee_frame_pattern=1` shows a red-over-blue pattern; red in the first logged row means TopDown) and then set `BugseeFrameCapturerOptions.RowOrder`, which declares it verified.
- Readback is refused when the device lacks `SystemInfo.supportsAsyncGPUReadback`, or on Vulkan with Unity older than 2021.3.18 (not reachable on supported versions). After 5 failed readbacks in a row it turns itself off.
- The SDK caps pushed frames by `CaptureVideoFrameRate` as well; a `TargetFps` above that is dropped by the SDK.
- Measured once, on a Galaxy Tab S5e (Android 11, Unity 6000.0.84f1 IL2CPP development build, Vulkan, the sample scene, 400x640 capture at `TargetFps` 5, about 100 s per run, pusher on versus the SDK's default capture): about 4.6 frames/s pushed, 0 failures, 1-3 frames dropped in flight; readback latency about 81-91 ms; `addFrame` 18-19 ms on the push thread. Unity frame rate 37.5-38.1 versus 37.6-37.9 fps, frame time p50 33.1 ms and p95 33.2 ms in both. CPU is comparable, not lower: the push thread used about 10% of a core, against about 9% for the default capture's `BugseeScreenCap` thread; main thread about 50-57% either way; total PSS 391-396 MB versus 374-382 MB (about 15 MB more for the render textures and buffers). So it is not lighter than the default capture on this device. One device and one light scene, not a benchmark of heavier games.
- Decoded report videos (640x640 h264 from staging reports UNITYTA-35 to 43; red-top/blue-bottom pattern, a top-left and a bottom-right secure rectangle): red above blue (no mirroring) on both Vulkan and OpenGLES3 (the tablet also ran OpenGLES3 in one build: BottomUp, same result); frames before a rectangle was registered are unmasked, then every frame while it was registered has both rectangles black at the right place, then after removal the same areas show the pattern again, with no unmasked frame inside the masked period and no masked frame after removal. A rectangle added after a rotation (landscape to portrait, driven by `Screen.orientation` from the sample) is masked at the right place, and the view region followed the rotation (2560x1600 to 1600x2560). With the status bar shown the Unity view was 2560x1492; a rectangle at window pixels (0,0)-(800,400) was masked to exactly the area it covers in that view (frame x 0-199, y to 233), i.e. relative to the view, not the window. A launch from Unity 2021.3.45 with DirectBuffers requested logged the fallback warning and recorded a normal default-capture video (UNITYTA-43). A `Bugsee.Launch` from a worker thread with DirectBuffers works (after a fix: `AndroidBridge.Launch` read Application.buildGUID off the main thread). Limits: the wrapper learns a launch's outcome only until the lifecycle deadline (10 s); if the native SDK answers a request later than that, the answer is not delivered, the pusher follows the earlier refusal, and a native session that then runs in DirectBuffers without a pusher is not detected. Not verified on a device: a view whose top offset is non-zero (the status bar here sat at the bottom), split-screen or a display cutout, the fail-closed blanking when the view position cannot be read (unit tests only), OpenGLES2, Unity 2022.3 and Unity 2022.2 (not built), and heavier scenes.
