# Bugsee Unity SDK

Bug reporting, crash capture, and session replay for Unity games and apps on iOS and Android.

## Requirements

- Unity **2021.3** or newer
- [External Dependency Manager for Unity (EDM4U)](https://github.com/googlesamples/unity-jar-resolver) (declared as a package dependency)
- iOS builds: Xcode with Swift Package Manager support; **iOS 15+** (Bugsee 7.0.0-beta5); `bugsee-cli` on the PATH for archive-time symbol upload
- Android builds: custom Gradle templates enabled (EDM4U will prompt as needed); `bugsee-cli` on the PATH for Editor symbol upload

## Installation

Package Manager → **+** → **Add package from git URL**:

```
https://github.com/bugsee/bugsee-unity.git
```

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

Set `BUGSEE_APP_TOKEN` in the environment (opt-in). Optionally `BUGSEE_CLI_PATH`, `BUGSEE_VERSION`, `BUGSEE_BUILD`, `BUGSEE_IL2CPP_MAPPING`, `BUGSEE_IL2CPP_UUIDS`.

| Artifact | When | CLI type |
|---|---|---|
| `LineNumberMappings.json` (+ MethodMap) | Android post-build; iOS at **Xcode archive** | `il2cpp-linemap` |
| Android `symbols.zip` / ELF | Android post-build | `elf` |
| ProGuard / R8 `mapping.txt` | Android post-build when present | `proguard` |
| iOS `.dSYM` | Xcode archive Run Script (`Bugsee Upload Symbols`) | `dsym` |

Failure policy: **never fail the Unity build**. Missing maps/UUIDs/CLI → warn and continue.

### Player Settings checklist

**Android**

1. Scripting Backend: IL2CPP
2. Create symbols.zip / native debug symbols enabled
3. For primary LNM apply: native debug symbol level **FULL** (DWARF lines), not SYMBOL_TABLE-only
4. Custom Base / Launcher Gradle templates (Bugsee injects `ndk { enabled = true }` on Unity 6+)

**iOS**

1. Scripting Backend: IL2CPP
2. Enable source mapping / Script Debugging as needed for LineNumberMappings generation
3. Archive in Xcode (dSYM + UUID for `UnityFramework` are available at archive time)
4. Ensure `BUGSEE_APP_TOKEN` is visible to the Xcode Run Script phase

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
2. NDK enabled (`bugsee-android-ndk:7.3.0` + Gradle `ndk { enabled = true }` on Unity 6+).
3. Post-build uploads `elf` + `il2cpp-linemap` (multi-ABI UUIDs).
4. Native fatal on device; viewer shows **C# file/line**.

### Milestone C1 — managed auto-capture / LogException

1. `CaptureManagedExceptions` enabled (default).
2. Unhandled managed exception / `LogUnhandledException` appears once (no dual-hook duplicates).
3. **Mode A:** MethodFileLineNumber / Mono → viewer shows C# file:line from stack strings.
4. **Mode B (in progress):** Release IL2CPP + uploaded dSYM/ELF + `il2cpp-linemap` → `LogException` on a **thrown** exception emits native IPs for worker LNM. Validate on device before treating as parity with Sentry.
5. **Mode C:** MethodMap demangles mangled names when module UUID(s) match (names only if no addresses/lines).

Managed error/crash **grouping** (one signature per event, Unity-primary when strong): [exception-signatures.md](exception-signatures.md).

## Native dependencies

- **Android:** Maven `com.bugsee:bugsee-android:7.3.0` + `com.bugsee:bugsee-android-ndk:7.3.0` via EDM4U (Gradle plugin `4.0.8`). Core publishes no transitives; NDK is a separate artifact and must be declared. Fragment secure-view overloads are typed as `Object`, so Unity JNI does not need `androidx.fragment`.
- **iOS:** SPM-only. Xcode resolves [`bugsee/spm`](https://github.com/bugsee/spm) `7.0.0-beta5` (iOS 15+). C bridge: `Plugins/iOS/BugseeUnityBridge.mm` + `BugseeUnityCallbacks.mm`.

### iOS bridge status

Wired: Launch/Stop, blackout, log/trace/event, exceptions (JSON-in-reason), TestCrash, report/upload (+ labels), identity (email API), attributes, secure rects, appearance colors/strings, feedback UI, network/log/breadcrumb filters, report handler (via `BugseeWrapper`), lifecycle listener (via wrapper `onLifecycleEvent` → `Bugsee.LifecycleEvent` / `ILifecycleEventListener`).

Full product documentation: [docs.bugsee.com/sdk/unity](https://docs.bugsee.com/sdk/unity/).
