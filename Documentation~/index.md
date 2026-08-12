# Bugsee Unity SDK

Bug reporting, crash capture, and session replay for Unity games and apps on iOS and Android.

## Requirements

- Unity **2021.3** or newer
- [External Dependency Manager for Unity (EDM4U)](https://github.com/googlesamples/unity-jar-resolver) (declared as a package dependency)
- iOS builds: Xcode with Swift Package Manager support; `bugsee-cli` on the PATH for archive-time symbol upload
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

Managed exceptions use the **UnityManagedException** JSON-in-reason contract (`name`, `reason`, `frames[{trace,address?}]`, `signature`, `buildID`, `moduleUUID`) so the worker can MethodMap-demangle IL2CPP stacks. Instruction `address` is filled when the stack line contains a hex address; otherwise MethodMap string demangle applies.

IL2CPP **C# file/line** (primary LineNumberMappings apply) needs native PC + module UUID + uploaded `il2cpp-linemap` + matching dSYM/ELF with **line** info.

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

- ExceptionPipeline sends **UnityManagedException** JSON (`frames[].trace`, optional `frames[].address`, `moduleUUID`, `signature`, `buildID`).
- `frames[].address` is filled only when the managed stack line already contains a hex `0x…` token (common on some IL2CPP builds; not guaranteed).
- Without addresses, the worker demangles mangled names via **MethodMap** — that is **not** the same as primary LNM C# file/line.
- Primary C# file/line still comes from **native** fatals (PC + module UUID + dSYM/ELF line info + linemap).

## Field validation (S1 / S2)

Manual device/CI proof — not automated in this package.

### Milestone S1 — iOS C# file/line

1. IL2CPP iOS build with LineNumberMappings generated.
2. `BUGSEE_APP_TOKEN` set for Unity export + Xcode archive env.
3. Archive; confirm Run Script uploads dSYM + `il2cpp-linemap` (UUID from `UnityFramework`).
4. Trigger a native fatal (`Bugsee.TestCrash` / signal) with `DetectAndReportCrash`.
5. Viewer shows **C# file/line** on the crashing frames; wrong/missing map leaves native frames intact.

### Milestone S2 — Android C# file/line

1. IL2CPP Android build; Create symbols.zip / **FULL** native debug symbols.
2. NDK enabled (`bugsee-android-ndk:7.1.1` + Gradle `ndk { enabled = true }` on Unity 6+).
3. Post-build uploads `elf` + `il2cpp-linemap` (multi-ABI UUIDs).
4. Native fatal on device; viewer shows **C# file/line**.

### Milestone C1 — managed auto-capture

1. `CaptureManagedExceptions` enabled (default).
2. Unhandled managed exception / `LogUnhandledException` appears once (no dual-hook duplicates).
3. Mangled IL2CPP names demangle when MethodMap was uploaded for the same module UUID(s).

## Native dependencies

- **Android:** Maven `com.bugsee:bugsee-android:7.1.1` + `bugsee-android-ndk:7.1.1` via EDM4U (Gradle plugin `4.0.5`).
- **iOS:** local Swift package under `Native~/ios/Bugsee` until nextgen is on [bugsee/spm](https://github.com/bugsee/spm). C bridge: `Plugins/iOS/BugseeUnityBridge.mm`.

### iOS bridge status

Wired: Launch/Stop, blackout, log/trace/event, exceptions (JSON-in-reason), TestCrash, report/upload (+ labels), identity (email API), attributes, secure rects, appearance colors/strings, feedback UI.

Not yet: network/log/breadcrumb filters, report handler, lifecycle listener callbacks (need a Unity↔ObjC callback channel).

Full product documentation: [docs.bugsee.com/sdk/unity](https://docs.bugsee.com/sdk/unity/).
