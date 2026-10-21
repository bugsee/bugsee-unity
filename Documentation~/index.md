# Bugsee Unity SDK

Bug reporting, crash capture, and session replay for Unity games and apps on iOS and Android.

## Requirements

- Unity **2021.3** or newer
- iOS builds: Xcode with Swift Package Manager support; **iOS 15+** (Bugsee 7.0.0-beta7); `bugsee-cli` on the PATH for archive-time symbol upload
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

`CaptureManagedExceptions` is independent of `DetectAndReportCrash`. Omit it to keep the default (enabled). `CaptureUnobservedTaskExceptions` (default enabled, needs `CaptureManagedExceptions`) controls whether `TaskScheduler.UnobservedTaskException` is reported; set it to `false` for apps whose fire-and-forget tasks fault on purpose. A `Relaunch` applies a changed value. Unity raises that event only after the faulted `Task` is garbage-collected, so the handled error arrives late and at a time you do not control.

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

### Lifecycle events

`Bugsee.LifecycleEvent` and `ILifecycleEventListener.OnEvent(eventType, data)` behave alike on both platforms. `data` is `null` or a `string` (the snapshot or request id of the report events); the native SDKs send nothing else today. If an SDK ever sends a map it arrives as a `Dictionary<string, object>` on both, with numbers inside it as `long`/`double` on iOS and `int`/`float` on Android; on iOS any other payload type arrives as its description text, and a map nested deeper than the JSON reader allows as raw JSON text. Events the SDK announces before the managed side is listening (for example from a launch the host made natively before any C# call) are held, up to 128, and delivered in order shortly after the wrapper starts listening (on iOS on the main queue after the `Launch` or `Relaunch` call that registered it, on Android on the next main-looper pass), on both iOS and Android. `Relaunch` registers the wrapper the same way `Launch` does. Handlers run on the Unity main thread on iOS and Android alike.

Other platform differences, not aligned:

- Before `Launched`, iOS holds network and breadcrumb events in the wrapper and submits them at launch, while Android hands them straight to the SDK.
- `IReport.Email` set on a report from `CreateReport` is used on Android; on iOS it is dropped with a warning (the managed upload cannot carry it): use `SetUserIdentifier` there.
- `IAppearance.ToMap()` returns what the SDK reports on Android; on iOS it lists only the properties set through the wrapper that the native theme applied.
- `Trace(name, value)` with a collection or other non-primitive value: iOS sends it as JSON, Android as its `ToString()`. Pass a string or a primitive to get the same result on both.

## Crash & exception capture

| Source | API / option | Severity default |
|---|---|---|
| Manual | `Bugsee.LogException` | non-fatal |
| Manual unhandled | `Bugsee.LogUnhandledException` | fatal |
| Unity `Debug.LogException` / routed exceptions | ExceptionPipeline | non-fatal (set `TreatUnityLogExceptionsAsFatal`) |
| `AppDomain.UnhandledException` | ExceptionPipeline | fatal |
| `UnobservedTaskException` | ExceptionPipeline (`CaptureUnobservedTaskExceptions`, default on) | non-fatal |
| Native abort / signal | `DetectAndReportCrash` + native SDK | fatal (next launch) |
| Android hang | `DetectAndReportHang` | hang report |
| Android ANR / OOM | `DetectAndReportExitNotResponding` / `DetectAndReportExitLowMemory` | exit reports |

**iOS and tvOS simulator:** the SDK's simulator build has no crash reporter (bugsee-cocoa#241), so native crashes (`DetectAndReportCrash`, `Bugsee.TestCrash`), hang detection, `LogError` and managed exceptions, handled (`LogException`, `Debug.LogException`) and unhandled (`LogUnhandledException`, `AppDomain.UnhandledException`), are **not reported** there; they are reported only from a device. Manual bug reports and uploads, logs, events and video still work. The package warns once at the first launch (`[Bugsee] Running on the simulator` in the Unity log, and in the native log). Validate crash and exception reporting on a physical device.

Managed exceptions use the **UnityManagedException** JSON-in-reason contract (`name`, `reason`, `frames[{trace,address?}]`, `signature`, `buildID`, `moduleUUID`, optional nested `cause`).

### How to get C# class + file:line on `LogException`

| Mode | Build / capture | Backend | File:line? |
|---|---|---|---|
| **A. Rich managed stacks** | Mono **Development Build** (a portable `.pdb` ships next to each assembly), or IL2CPP **Method Name, File Name, and Line Number** (or Script Debugging) | Parse `frames[].trace` | **Yes** (from string) |
| **A0. Mono Release** | Mono without Development Build: no `.pdb` in the player, the text is `at Ns.C.M (System.String a) [0x0000b] in <module-guid>:0` | Parse `frames[].trace` | **No**: method names only. The IL offset is not resolved anywhere (the CLI's `PortablePdb` type is a scaffold; the worker reads no Portable PDB). A Development Build has file:line for assemblies Unity compiles (not for store builds; a managed plug-in without a `.pdb` stays IL-offset only); IL2CPP has it only where Unity offers IL2CPP line information. A Release Mono Android build logs a build-time warning. |
| **B. Release Method-only + LNM** | IL2CPP; thrown exception; SDK emits native IPs via `il2cpp_native_stack_trace` | PC → dSYM/ELF → LineNumberMappings | **In progress** (needs symbols + `il2cpp-linemap`; addresses are module-relative) |
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

**Build token vs `Launch` token (Android).** Symbols, mappings and the Android synthesized build id follow `BUGSEE_APP_TOKEN` at build time, crashes follow the token given to `Bugsee.Launch`. When they name different apps, symbols sit in another app than the crashes and nothing says so. When an Android symbol upload is on (a token set, `BUGSEE_NO_SYMBOL_UPLOAD` unset), the build records a fingerprint of its token (the first 8 bytes of SHA-256 of the trimmed token, as 16 hex characters; never the token) as the Resources asset `BugseeBuildTokenFingerprint`, present only while the build runs (removed afterwards; nothing stays in `Assets`, an empty `Assets/Resources` the build created is removed too). `Bugsee.Launch` in a player logs one warning, naming both fingerprints, when its token has another fingerprint (once per process: a Stop then Launch with another token does not warn again). It is silent when they match, in the Editor, and for a build that recorded none. Set `BUGSEE_NO_TOKEN_CHECK` for the build to record nothing, e.g. when the token is chosen at runtime (staging vs production app). Not done on iOS and tvOS: their symbols are uploaded by the Xcode Run Script with the token of the Xcode environment, which the Unity build cannot see. The fingerprint asset is a file in `Assets/Resources` while the build runs: a version-control plugin that locks or tracks files (Perforce, Plastic) may notice it.

### Where `bugsee-cli` comes from (Editor uploads)

This lookup serves every Editor upload on any platform that uses the CLI from the Editor (the Android mapping and symbols, and the IL2CPP linemap upload of a standalone or Android build). A Unity Editor started from the Hub or the Finder does not inherit your shell's `PATH`, so a CLI installed with Homebrew or `install.sh` is often invisible to it. The Editor uploads therefore look, in order, at: `BUGSEE_CLI_PATH`; the Editor's `PATH`; the places the installers use (`/opt/homebrew/bin`, `/usr/local/bin`, `~/.local/bin`, `~/.cargo/bin`, `~/.bugsee/bin`, `%LOCALAPPDATA%\Bugsee\bin`); the package's cache (`~/Library/Caches/Bugsee/cli` on macOS, `$XDG_CACHE_HOME/bugsee/cli` or `~/.cache/bugsee/cli` on Linux, `%LOCALAPPDATA%\Bugsee\cache\cli` on Windows, else `Library/Bugsee/cli` in the project). An installed CLI older than 0.8.1 is passed over (0.8.1 replaces a stored symbol table with DWARF for the same build id without `--force`, which the uploads never pass; the pinned release is downloaded instead when a token is set; only the Android ELF upload needs 0.8.1, but the IL2CPP linemap upload shares the lookup and so the floor: with `BUGSEE_NO_CLI_DOWNLOAD=1` and only a 0.8.0 installed it finds no CLI). A too-old CLI you name with `BUGSEE_CLI_PATH` is used, with a warning that stored symbol tables are not upgraded.

When none is found and a token is set, the pinned release (0.8.1, the Bugsee Gradle plugin's default) is downloaded from `https://download.bugsee.com/cli/v<version>/bugsee-cli-<triple>.tar.xz` (`.zip` on Windows, for x64 and ARM64), checked against the SHA-256 pinned in the package for that release (a release you pin with `BUGSEE_CLI_VERSION` is checked against the published `.sha256` only), unpacked into that cache and run once to prove it works. For a release pinned in the package the checksum does not depend on the download host; for any other it comes from the same host as the archive, so it detects corruption, not a compromised host: set `BUGSEE_CLI_PATH` to a binary you vetted if that matters. Credentials in `BUGSEE_CLI_BASE_URL` are not logged. Not verified on a Windows host (the zip and `.exe` paths, the Windows PATH and cache layout and the ARM64 Windows release are covered by unit tests that simulate Windows semantics; see [Windows Editor](#windows-editor) for what a Windows Editor still has to confirm). `BUGSEE_NO_CLI_DOWNLOAD=1` turns the download off, `BUGSEE_CLI_VERSION` pins another release and `BUGSEE_CLI_BASE_URL` points at a mirror (https only), as for `install.sh`. If nothing can be found or downloaded the build warns once, naming what was tried, and continues without uploading. The iOS Run Script has its own lookup, below, and never downloads.

The iOS Run Script cannot rely on the Editor's lookup: Xcode runs build phases with a minimal `PATH`. `BUGSEE_CLI_PATH` and the CLI the Editor's own lookup finds when it exports the project, without downloading (baked into `BugseeScripts/upload-symbols.sh`; only on a macOS host, with `BUGSEE_APP_TOKEN` set and `BUGSEE_NO_SYMBOL_UPLOAD` unset) are used as given, if the file is executable. Otherwise the first of these that runs, prints a version (`bugsee-cli 0.8.1`) and is at least 0.8.0 (a pre-release such as `0.8.0-beta.1` counts, as in the Editor) wins, in the Editor's order: the build phase's `PATH`; `/opt/homebrew/bin`, `/usr/local/bin`, `~/.local/bin`, `~/.cargo/bin`, `~/.bugsee/bin`; and the package's cache (`~/Library/Caches/Bugsee/cli`, `$XDG_CACHE_HOME/bugsee/cli` or `~/.cache/bugsee/cli`, the pinned or `BUGSEE_CLI_VERSION` release first). Every `PATH` entry is tried, and a `--version` probe is stopped after 15 s (killed 2 s later if it ignores the signal). The script never downloads. The baked path is absolute, so it contains the exporting user's home directory (and username): if you commit the exported Xcode project, set `BUGSEE_NO_SYMBOL_UPLOAD` or unset `BUGSEE_APP_TOKEN` before exporting and nothing is baked. When none qualifies it prints a `warning: Bugsee: bugsee-cli was not found` line naming every path tried and why each was passed over, uploads nothing, and the build continues.

The iOS Run Script uploads for **every** Xcode configuration (Debug included, so a crash from a development build is symbolicated) and runs on every build that has the token in its environment, not only archives. Set `BUGSEE_SKIP_UPLOAD_ON_DEBUG=1` to skip the Debug configuration, for example for local Xcode runs.

The phase declares the app's and UnityFramework's dSYM files as its inputs (and "Based on dependency analysis" off), so Xcode runs it after the tasks that write them; a configuration without a dSYM leaves those inputs missing, which Xcode accepts. The export also sets `ENABLE_USER_SCRIPT_SANDBOXING = NO` on the Unity-iPhone target: with the sandbox on (the default of new Xcode projects) Xcode refuses to start the phase's script ("Operation not permitted" on `BugseeScripts/upload-symbols.sh`; the build continues and nothing is uploaded), and the script could not read the dSYM folder or reach the network anyway. A project-wide or command-line `ENABLE_USER_SCRIPT_SANDBOXING=YES` still wins over the target's setting, and Unity's own IL2CPP build phase fails under it too. The il2cpp-linemap uploaded is the one this build produced. Unity copies a map file into an "Append" export only when it changed, so an unchanged Append export into the same folder leaves this build's map there with the earlier export's time. At the end of an iOS/tvOS export the package therefore compares `LineNumberMappings.json`, `MethodMap.tsv` and `il2cppFileRoot.txt` in `Il2CppOutputProject/Source/il2cppOutput/Symbols` with the build's own output (`Library/Bee/artifacts/<platform>/il2cppOutput/cpp/Symbols`): an equal file is current and gets a fresh modification time, and one that differs from it, or that this build did not write at all, and is older than the export is deleted (with the upload on: token set in the Editor's environment, `BUGSEE_NO_IL2CPP_MAPPING` and `BUGSEE_IL2CPP_MAPPING` unset; otherwise it is left alone). Only that folder is looked at, and nothing behind a link. The build's output folder counts only when one of its generated `.cpp` files is identical to the export's copy; where it is not found or does not match (another Unity layout, or a folder an earlier build left), or a link is in the way, or a file's time cannot be set, nothing is deleted and the export time is not given to the Run Script, which then uploads the map it finds without an age check (a warning says so when the upload is on). A `MethodMap.tsv` or `il2cppFileRoot.txt` beside the map that is older than the export is not sent with it: the Run Script uploads the map from a temporary folder holding only the current files. The pending-map pointer (`BugseePendingIl2CppLinemap.txt`) is recreated by every export, and the Run Script skips a map inside the project that is more than two minutes older than the export (`skip il2cpp-linemap ... older than this export`). A map named by `BUGSEE_IL2CPP_MAPPING` is used as given by the script when it is outside the project (inside it, the same age check applies) and is never deleted. Unity does not write `LineNumberMappings.json` unless the IL2CPP arguments ask for it; with none written the Run Script uploads no line map.

| Artifact | When | CLI type |
|---|---|---|
| `LineNumberMappings.json` + `MethodMap.tsv` (the package passes `--emit-source-mapping --emit-method-map` to IL2CPP for every IL2CPP build, token or not) | Android post-build; iOS at **Xcode build** (every configuration) | `il2cpp-linemap` |
| Android `symbols.zip` / ELF | Android post-build | `elf` |
| ProGuard / R8 `mapping.txt` | Android post-build when present | `proguard` |
| iOS `.dSYM` | Xcode Run Script (`Bugsee Upload Symbols`), every configuration | `dsym` |

Failure policy: **never fail the Unity build**. Missing maps/UUIDs/CLI → warn and continue.

### Windows Editor

Everything about the Windows Editor path is covered by simulated tests only (`Tests~/WrapperPolicy/WindowsEditorPathTests.cs`, which run the CLI lookup with Windows separators, `;` PATH entries, case-insensitive de-duplication, `.exe` names, quoted and extension-less `BUGSEE_CLI_PATH`, drive-root, UNC and over-260-character cache paths, CRLF `--version` output and checksum files) and by reading the code; no Windows host has run it. The iOS Run Script is macOS-only by design: a Windows Editor exports an Xcode project whose script is POSIX text (LF only) and bakes no CLI path (that happens only in the macOS Editor); the script itself runs in Xcode on a Mac. Known limits that a Windows run should confirm or refute:

- The Editor-side exclusion of build-tool folders (`/Library/`, `/Temp/`, `/Bee/`, `/obj/`) tests the whole absolute path, so a project or build output that lives under such a folder (for example `C:\Users\me\AppData\Local\Temp\...` on a CI runner) is skipped: no mapping, symbols zip or linemap is found. This is the same behaviour on every host (issue 124).
- Folders deeper than 260 characters: the scans catch `PathTooLongException` per root and warn, so the build continues without that upload. Whether the Editor's Mono and `bugsee-cli.exe` handle such paths is unconfirmed.
- Process arguments go through `ProcessStartInfo.ArgumentList`; the Unity 2021.3 `System.dll` quotes them with the standard `PasteArguments` rules (read from its IL, not run on Windows), so spaces, quotes and trailing backslashes in paths should survive.
- The quote and extension-less forms of `BUGSEE_CLI_PATH` are understood by the Editor's lookup only. The Gradle plugin block of an Android Export Project and the CLI child process read the raw value, so there such a value is ignored (the plugin falls back to its own lookup); use a plain full path to `bugsee-cli.exe` when you export a Gradle project.
- `bugsee-cli.exe` is found as `.exe` only; a `.cmd` or `.bat` wrapper is not looked up.

Manual checklist for a Windows machine (Unity 2021.3.45f1 or 2022.3 LTS with Android Build Support, `BUGSEE_APP_TOKEN` set to a staging or test app token in the Editor's environment, `BUGSEE_ENDPOINT` if not production):

1. No CLI anywhere (not on PATH, nothing in `%LOCALAPPDATA%\Bugsee\bin`, no `BUGSEE_CLI_PATH`): build an Android IL2CPP release with Create symbols.zip. Expect the Console to log `Downloading bugsee-cli 0.8.1 for x86_64-pc-windows-msvc` (`aarch64-pc-windows-msvc` only on a native ARM64 Windows Editor; the x64 Unity 2021.3 and 2022.3 Editors run emulated on ARM64, report X64 and get the x64 build, which runs there), `Using bugsee-cli 0.8.1 (downloaded): %LOCALAPPDATA%\Bugsee\cache\cli\0.8.1\<triple>\bugsee-cli.exe`, then the mapping and ELF uploads with exit 0, and the symbols to appear in the dashboard. A second build must say `cached download` and not download again.
2. With `BUGSEE_NO_CLI_DOWNLOAD=1` and no CLI: expect one warning naming `install.ps1` and the paths tried, and the build to succeed.
3. Install with `powershell -ExecutionPolicy ByPass -c "irm https://download.bugsee.com/cli/install.ps1 | iex"`, restart the Hub and Editor without touching PATH: the build must find `%LOCALAPPDATA%\Bugsee\bin\bugsee-cli.exe` (`install directory`).
4. `BUGSEE_CLI_PATH` as `"C:\Program Files\x\bugsee-cli.exe"` with the quotes, and as `C:\Program Files\x\bugsee-cli` without `.exe`: both must resolve in the Editor's own uploads. Then repeat with an Android Export Project and note which CLI the Gradle plugin uses (expected: its own lookup for the two odd forms, your path for a plain full path).
5. Build into a folder with a space (`C:\Build Output\My Game\game.apk`), then one under a 250-character path, then one under `%TEMP%`: record which uploads are found (the last is expected to be skipped, see above).
6. Export an iOS project from the Windows Editor: `BugseeScripts/upload-symbols.sh` must contain no `\r` and `BAKED_CLI=''`.

### Player Settings checklist

**Android**

1. Scripting Backend: **IL2CPP** is the supported configuration for fully symbolicated native crashes. With Mono, frames inside the Mono runtime (`libmonobdwgc-2.0.so`, `libmono-native.so`, `libMonoPosixHelper.so`) cannot be symbolicated: Unity 2021.3 has no Mono ARM64 (ARMv7 only) and those libraries have no GNU build-id. Frames in `libunity.so` and `libmain.so` still symbolicate. A Mono Android build logs a one-time warning at build time (whether or not symbol upload is configured) and is never failed. A Mono build that is not a Development Build logs a second one-time warning: its managed frames have IL offsets only (`[0x0000b] in <module-guid>:0`), no C# file or line
2. Create symbols.zip / native debug symbols enabled
3. For primary LNM apply: native debug symbol level **FULL** (DWARF lines), not SYMBOL_TABLE-only
4. No custom Gradle templates needed: Bugsee patches the generated Gradle project (the Bugsee Gradle plugin with `ndk { enabled = true }` when it pins AGP 8.6+, i.e. Unity 6; on older AGPs, see below); a template of your own that pins another AGP major than your Unity's default fails the build and is reported (file layouts the patching understands: [Gradle file layouts](#gradle-file-layouts))
5. **Minify** (R8) may be enabled for Release and Debug with no extra setup: the package appends its keep rules (`Plugins/Android/bugsee-unity-wrapper.proguard`) to the generated `unityLibrary/proguard-unity.txt` on every Android build, because Unity does not read that file from a package. The rules keep the wrapper classes C# looks up by name over JNI (`UnityWrapper`, `UnityManagedException`) and the SDK interfaces implemented from C#. They sit between `# bugsee:proguard begin` / `end` markers and are replaced, not duplicated, on the next build. If you add your own JNI code, keep its classes in your Custom Proguard File

### Gradle file layouts

Unity 2021.3, 2022.3 and 6000.0.84f1 generate and read only Groovy files (`*.gradle`): the editors' `GradleTemplates` folders hold no `.kts` file and the Android extension assembly of all three knows no `.kts` name (checked 2026-10-17), so a `launcherTemplate.gradle.kts` dropped into `Assets/Plugins/Android` is not used. Kotlin DSL files exist only when your project converts the generated Gradle project in its own `IPostGenerateGradleAndroidProject` step (run it with a `callbackOrder` below 10 so the package patches the converted files). The package edits what is there, in whichever DSL, and never fails the build over the Gradle plugin step. Dependencies, `mavenCentral()` and the plugin step are separate:

| File shape | Result |
|---|---|
| Unity's Groovy layout (root `plugins { }`, launcher `apply plugin: 'com.android.application'`), also with custom base/launcher/settings/main templates that keep those lines | Root `id 'com.bugsee.android.gradle' version '...' apply false`, launcher `apply plugin:` after the AGP line (checked on Unity 2021.3, 2022.3 and 6000.0.84f1) |
| Root declares AGP with `buildscript { dependencies { classpath } }` and no `plugins { }` | The plugin becomes a root `classpath` entry (and `mavenCentral()` is added to the buildscript repositories if missing), the launcher applies it |
| Kotlin DSL files (`build.gradle.kts`, `settings.gradle.kts`), `libs.versions.toml` aliases (the `libs` catalog only, entries in `[plugins]` as inline or `[plugins.name]` tables, camelCase and dashed names) | The launcher's `plugins { }` block gets `id("com.bugsee.android.gradle") version "..."` after the AGP entry and `bugsee { ndk { enabled.set(true) } }` follows the block; the AGP version for the 8.6 check is read through `gradle/libs.versions.toml` (default location only) |
| `pluginManagement { repositories { ... } }` in settings whose repositories serve the plugin through none of `gradlePluginPortal()` (or its `plugins.gradle.org/m2` URL), Maven Central, or a proxy whose `content { }` filter includes `com.bugsee` and `com.bugsee.android.gradle` (an `exclusiveContent` filter that does not include them does not count) | `mavenCentral()` is appended after the existing entries, so a mirror you list first keeps priority, with a warning saying what was added and why (same for a root `buildscript` repositories block that needs it). Set the environment variable `BUGSEE_NO_GRADLE_REPO_INJECTION` (any value) to forbid changing repositories: the plugin is then not applied and the warning lists the lines to add. A `pluginManagement` whose repositories are written in a shape the package cannot read (all on one line, or `repositories {` on the outer brace's line) is treated the same way: the plugin is not applied, with the lines to add |
| Plugin you declared yourself (`id`, `apply plugin`, `classpath`) | Kept as written, nothing is added next to it |
| Launcher that applies `com.android.application` with `apply` (no `plugins { }` block), a one-line `plugins { }`, a plugins block without AGP | Left alone. A warning (on every build) names the lines to add, for the shape of the project's root: for a launcher `plugins { }` block, `id("com.bugsee.android.gradle") version "<plugin version>"` (Groovy: `id '...' version '...'`) after the AGP entry and `bugsee { ndk { enabled.set(true) } }` below the block; for a launcher that uses `apply`, two edits, which depend on the root: with a root `plugins { }` block, the same `id(...) version ... apply false` line there and `apply(plugin = "com.bugsee.android.gradle")` after the AGP line (plus `configure<com.bugsee.android.gradle.BugseePluginExtension> { ndk { enabled.set(true) } }` for NDK support); with a root `buildscript { }` block, `classpath("com.bugsee:bugsee-android-gradle-plugin:<plugin version>")` in its dependencies (and `mavenCentral()` in its repositories) plus the same apply line. These manual forms are not run by the package. The plugin is never applied ahead of AGP: it looks the AGP extension up while being applied and would silently do nothing |
| AGP below 8.6, version unreadable (computed, catalog elsewhere) | No plugin; see the next section. An unreadable version gets a warning with the same lines, and Bugsee > Android > Force Gradle Plugin On Older AGP (unsupported) applies it regardless |

Verified with real Unity 6000.0.84f1 APK builds (Mono ARMv7, Gradle 9.3.1 / AGP 9.0.0; a scratch `IPostGenerateGradleAndroidProject` at order 0 reshaped the generated files): converted `.kts` root, launcher and settings; the same with a `libs.versions.toml` alias root; a Groovy root with `buildscript { classpath }`; settings whose `pluginManagement` lists only `google()`. All four built and their APKs carried the plugin's `assets/bugsee_build_id.properties`. The previous package version, on the same projects, applied no plugin to the Kotlin files (one warning) and failed Gradle on the other two (`Plugin with id 'com.bugsee.android.gradle' not found`, `Plugin [id: 'com.bugsee.android.gradle', version: '4.0.8', apply: false] was not found in any of the following sources`).

Limits, not fixed: the structure scanner works line by line, so Groovy slashy strings (`/.../`) and quotes nested inside a GString interpolation are not understood; files the package rewrites are written without a byte order mark; the release `debugSymbolLevel` edit looks for a single `release {` line and can pick `signingConfigs { release {` in a launcher that has one. A `bugsee { }` block you wrote yourself is kept and never replaced (the package's own is the one ending in `// bugsee:gradle-ndk`), and a `pluginManager.apply(...)`, `apply(plugin = ...)`, `id(...)` or `classpath` of the plugin counts as your own declaration. A regular expression in a repository content filter that cannot be read, or does not finish within half a second, makes the filter count as not serving Bugsee (an include filter does not match, an exclude filter does), so `mavenCentral()` is appended with its warning.

Kotlin launcher on AGP below 8.6: the build id asset task is Groovy code and is not added to a `.kts` file; the SDK derives its own build id. The release `ndk { debugSymbolLevel }` the package adds on 2021.3 is written in the launcher's own DSL.

### Export Project (Android)

An Export Project build leaves Gradle for later (Android Studio, or `gradlew` on a CI machine), so at export time there is no R8 mapping, no `symbols.zip` and no native library for the Editor to upload (it logs "no Android symbols.zip / libil2cpp.so found"). On AGP 8.6+ (Unity 6) the Bugsee Gradle plugin is applied to the exported launcher, but it reads the app token only from its own `bugsee { appToken(...) }` DSL, from `<root project>/bugsee.properties` (`app_token=`) and from the manifest, never from `BUGSEE_APP_TOKEN`, and a Unity app has the token only at runtime (`Bugsee.Launch`). Its mapping, native and build-info tasks therefore logged `Bugsee: Could not resolve appToken. Skipping ...` (checked on 6000.0.84f1, plugin 4.0.8, with `BUGSEE_APP_TOKEN` set in the environment).

For an Export Project the generated launcher's `bugsee { }` block now reads the token when Gradle runs:

```groovy
bugsee {
    if (System.getenv('BUGSEE_NO_SYMBOL_UPLOAD') == null && System.getenv('BUGSEE_APP_TOKEN')) {
        appToken(System.getenv('BUGSEE_APP_TOKEN'))
    }
    if (System.getenv('BUGSEE_ENDPOINT')) { endpoint = System.getenv('BUGSEE_ENDPOINT') }
    if (System.getenv('BUGSEE_CLI_PATH') && new File(System.getenv('BUGSEE_CLI_PATH')).isFile()) { cliPath = System.getenv('BUGSEE_CLI_PATH') }
    ndk {
        enabled = true
        useMergedNativeLibs = false
    }
}
```

(The Kotlin DSL form is the same in Kotlin syntax.) **No token is written to any file of the exported project**: the lines name an environment variable, so the project can be committed or shared. Build it with `BUGSEE_APP_TOKEN` set in the environment of the Gradle process: from a terminal (`BUGSEE_APP_TOKEN=... ./gradlew assembleRelease`), or in CI. Android Studio uses the environment it was started with (on macOS an app opened from the Dock does not see shell variables; start it from a terminal that has the variable, or run Gradle from a terminal); that case is not verified here. The Gradle user home is not consulted: the plugin reads `bugsee.properties` only from the root project, and putting the token there stores it in the project. Without the variable the plugin skips as before; `BUGSEE_NO_SYMBOL_UPLOAD` (any value) turns it off as for the Editor's uploads. `BUGSEE_ENDPOINT` (a URL such as `https://api.bugsee.com`: the plugin's build-info request needs the scheme) and `BUGSEE_CLI_PATH` (the path of an existing file; a relative path, a directory or a bare command name is ignored without a message and the plugin downloads its own `bugsee-cli`) are passed on the same way. The plugin also registers a build record for the release variant (build info; size analysis stays off); `plugin.buildInfo.enabled=false` in the root project's `bugsee.properties` turns that off.

What the exported project's Gradle build then uploads (real Unity 6000.0.84f1 export, Gradle 9.3.1 / AGP 9.0.0, `bugsee-cli` replaced by a logging stand-in so nothing was sent): the R8 `mapping.txt` with the launcher icon under the build id the APK carries; the native symbols in AGP's `build/outputs/native-debug-symbols/release/native-debug-symbols.zip` (`useMergedNativeLibs = false`): Unity's own symbol tables of `libil2cpp.so`, `libunity.so` and `libmain.so` (Unity's `setupSymbols.gradle` copies the full ones into it; same build id as the unstripped libraries in `unityLibrary/symbols`) and the AAR libraries that carry symbols; and the build-info registration. Why not the default (the `merged_native_libs` directory): it holds Unity's **stripped** copies of those three libraries (dynamic symbols only, same build id). `bugsee-cli` 0.8.1 labels such a library `symtab`, and the server keeps the first copy of a build id at a given level (a same-level copy is answered as a duplicate and the CLI exits 0), so sending the stripped ones first would block the real symbol tables from any later upload (read in the CLI and server code, not run against the server). Limits, not fixed:

- At Player Settings' Public level the symbol tables give function names, not file:line; IL2CPP file:line needs the Debugging level and the line map. An export writes no IL2CPP line map at export time (the Editor uploads nothing then); get it as described for the line-number mapping above and upload it with `bugsee-cli debug-files upload --type il2cpp-linemap`.
- Bugsee's own native libraries (`libbugsee-native.so`, `libbugsee-crashcp.so`, the crashpad libraries) ship **stripped** in the AAR (only `.dynsym`, checked with `llvm-readelf`), so an app build has nothing to upload for them.
- On AGP below 8.6 there is no plugin, so the exported project's Gradle build uploads nothing; use `bugsee-cli` on its output.
- The token is not safe from logs: Gradle with `--info` or `--debug` prints the `bugsee-cli` command line, which contains `--app-token <token>`, in the console and in the daemon log under `~/.gradle/daemon`; the token is also in the CLI's arguments (visible in `ps` while it runs) and in the CLI process's environment. Do not run CI builds of the export with `--info`/`--debug`, or mask the value. `plugin.debug=true` in `bugsee.properties` or `debug = true` in the DSL logs it too, and if the plugin falls back to its own uploader it logs the token when the server calls it invalid (plugin 4.0.8, `SymbolUploader`). These are plugin behaviours, not changed by this package.
- If the launcher has a `bugsee { }` block of your own (for example from a template), the package does not edit it and the Editor warns at export with the lines to add; without them the plugin skips.

Builds the Editor runs itself are unchanged: the block keeps its token-less form, because the Editor's own upload (after Gradle) already sends the mapping and the full `symbols.zip`. With the token enabled for those builds as well (tried), the plugin would repeat the Editor's mapping (same file, same id), register a build record from every release build, and send its native upload first: the stripped libraries of the merge step, which (as above) can then block the Editor's symbol tables of the same build ids.

### Unity 2021.3 and 2022.3 (no Bugsee Gradle plugin)

Both ship AGP 7.4.2 / Gradle 7.5.1; Gradle plugin 4.0.8 needs AGP 8.6+ / Gradle 8.7+ and fails to load there (checked on 2022.3.62f3: `NoClassDefFoundError: KotlinCompilerPluginSupportPlugin`, then `NoSuchMethodError` on `OutOperationRequest.toListenTo`). What it does for a Unity app, and what stands in for it:

| Gradle plugin function | Unity 6 with AGP 8.6+ (6000.0.84f1 has AGP 9.0.0; 6000.0.1-44 on AGP 8.3.0 gets the block, not the plugin) | Unity 2021.3 / 2022.3 (AGP 7.4.x), Unity 6000.0.1-44 (AGP 8.3.0) |
|---|---|---|
| Declare `bugsee-android`, `bugsee-android-ndk` | Package (Maven injection) | Same |
| `assets/bugsee_build_id.properties` = UUID of the R8 mapping (the id the SDK reports) | Plugin | The package adds the same asset task to the launcher (`GradleBuildIdBlock`), for AGP 7.4.x and 8.0 to 8.5 (7.0 to 7.3 and older get no block). With R8 off, or no block, no asset is written and the SDK derives its own id |
| `com.bugsee.android.BUILD_UUID` manifest meta-data (fallback id when R8 is off) | Plugin | Not added: the SDK derives `UUID(token, versionName, versionCode)` itself when it finds neither; the uploads use that id (`AndroidBuildUuid`) when the built app has no asset |
| Mapping upload (`--uuid`, `--icon`) | Package Editor upload (the plugin finds no app token in a Unity build, so its own upload skips; an Export Project is the exception, see [Export Project](#export-project-android)) | Same, keyed by the id read back from the built APK/AAB's asset (never assumed from the build script), else the SDK's synthesized id with a warning; launcher icon attached |
| Native symbol upload | Package Editor upload of Unity's `symbols.zip` (same reason) | Same |
| NDK enabled | Plugin | Not applicable: the NDK artifact is declared and the Editor uploads the symbols |
| Bytecode instrumentation (OkHttp, HttpEngine, `android.util.Log`, thread, main-thread misuse, operation dispatch, Compose, startup tracing), SDK-extension provider stripping, Compose compiler plugin | Plugin | **Not recovered** (needs AGP 8.6+). They act on the app module's own JVM bytecode, which in a Unity app is a launcher stub, so little is lost, but a Java/Kotlin plugin of yours that uses OkHttp or `HttpEngine` is not auto-captured on these Unity versions |
| Build-info / size-analysis upload, dependency and timing collection | Plugin (skips, no token) | **Not recovered** |

The mapping upload of an Android build is keyed by the id the built app reports: the APK/AAB's `bugsee_build_id.properties` if it has one, else (when `BUGSEE_BUILD_UUID` is not a UUID) the id the SDK derives from token, version name and version code (from the generated launcher, which is what the APK carries; a `versionNameSuffix` or version code changed outside Unity's generated launcher can still differ, and then a mapping uploaded under the derived id will not match; only the asset avoids that). The native upload uses the same id unless `BUGSEE_BUILD_UUID` is set, which keys it as before (any non-empty value); for the mapping `BUGSEE_BUILD_UUID` is used only when the app carries no asset and the value is a UUID, and never overrides the app's own id. The asset block is added on AGP 7.4.x whenever the build is Android, token or not (like the Unity 6 plugin, which is applied regardless); `BUGSEE_NO_GRADLE_BUILD_ID=1` stops the package adding it (and removes an earlier one) and leaves only the derived id. Its Groovy classes are compiled by Gradle for every build, and the part that runs per variant is guarded so a failure there only costs the id; a tokenless 2022.3 release build with R8 on was run to check they do not break Gradle (see the matrix). With the derived id, a rebuild with the same token, version name and code but different R8 output re-uses the id: the server keeps the first mapping uploaded under it, so bump the version code for a minified build whose obfuscation changed.

With Split APKs by target architecture the id is read from every APK; if they disagree (or only some carry one) the build warns and the upload uses the synthesized id. A launcher that overrides the version code per output (`versionCodeOverride`, what Unity's split version-code setting adds) is not trusted for the synthesized id's version code: Player Settings' is used.

Verified with real IL2CPP arm64 builds (staging token set, `bugsee-cli` 0.8.1, uploads to UNITYTA; the generated launcher's Gradle ran AGP 7.4.2 / Gradle 7.5.1 on both):

| Unity | Build | R8 | Result |
|---|---|---|---|
| 2022.3.62f3 | release | off | builds; no asset (nothing to match), game assets intact (24 `assets/bin` entries), no mapping uploaded, native symbols uploaded |
| 2022.3.62f3 | development | off | same |
| 2022.3.62f3 | development | on | builds; APK asset id equals the uploaded mapping id |
| 2022.3.62f3 | release | on | same, plus emulator native, Java and managed crashes symbolicated |
| 2022.3.62f3 | release | on, `BUGSEE_NO_GRADLE_BUILD_ID=1` | builds; no asset; mapping uploaded under the synthesized id with the warning; the Java crash on an emulator grouped with the symbolicated issue |
| 2022.3.62f3 | release, Split APKs by target architecture (ARMv7 + ARM64) | on | builds; Unity writes a folder `split.apk/` of per-ABI APKs: both carry the same asset id, equal to the uploaded mapping id; the symbols zip (named after `split`, not `split.apk`) is found and uploaded |
| 2022.3.62f3 | release, no app token | on | builds; the block is added regardless and the asset is in the APK; no upload, no warning |
| 2022.3.62f3 | Export Project | on | builds; no native libraries exist in the export, so no ELF upload (the CLI refuses a directory upload without `--uuid`, which every ELF upload now carries; checked with `bugsee-cli --dry-run`) |
| 6000.0.84f1 | release APK (Gradle 9.3.1, AGP 9.0.0) | on | with `BUGSEE_APP_TOKEN` set and a logging stand-in as `BUGSEE_CLI_PATH` (nothing uploaded): the plugin's mapping, native and build-info tasks logged `Could not resolve appToken. Skipping` and made no CLI call; only the Editor's three uploads (il2cpp-linemap, mapping, `symbols.zip`) ran. Unchanged by this package change |
| 6000.0.84f1 | Export Project, then `assembleRelease` of the export | on | Before: the same `Skipping` lines. Now: `uploadBugseeReleaseMapping` called the CLI for `mapping.txt` (+ icon, the APK's build id), `uploadBugseeReleaseApk` packed and registered the build, `uploadBugseeReleaseNative` called it once for `native-debug-symbols.zip` (`.sym` of libswappywrapper, libc++_shared, libmain, libil2cpp, libunity; the stripped `merged_native_libs` directory, 21 libraries, is not sent because of `useMergedNativeLibs = false`); no token in any `.gradle`, `.properties`, `.json`, `.xml` or `.txt` file of the project (only Unity's player data file that already carries it, and its build copies). Without `BUGSEE_APP_TOKEN`, or with `BUGSEE_NO_SYMBOL_UPLOAD=1`: `Skipping` and no CLI call, build succeeds |
| 2021.3.45f1 | release | off, development off | builds, as above |
| 2021.3.45f1 | release, development | on | builds; APK asset id equals the uploaded mapping id |

Unity 6000.0 early releases (6000.0.1 to 6000.0.44) ship AGP 8.3.0 / Gradle 8.4 per Unity's compatibility page (6000.0.45-60: AGP 8.7.2 / Gradle 8.11, 6000.0.61-78: 8.10.0 / 8.13, 6000.0.79-84: 9.0.0 / 9.1.0, 6000.0.85+: 9.1.1 / 9.3.1); those editors are not installed here, so their layouts are not checked in. The package adds the block, not the plugin, on AGP 8.0 to 8.5 (a log line says which AGP the project pins). The block was run, outside Unity, on a real 2022.3.62f3 Export Project with the AGP swapped in the root and a minified release assembled by standalone Gradle (JDK 17): AGP 8.0.2 / Gradle 8.0.2, 8.3.0 / 8.4 and 8.5.0 / 8.7 all built, the APK's `bugsee_build_id.properties` equalled the MD5-name UUID of `mapping.txt` and every game asset was intact; that, plus the 7.4.2 runs below, is the evidence for the gate (7.4.x and 8.0 to 8.5); it is not a Unity 6000.0.1-44 editor build, which is not installed.

Not verified: a Unity editor on AGP 8.0 to 8.5 end to end, Unity 2021.3 releases with AGP below 7.4, AAB output (its mapping upload is verified, see above; not the build id asset inside the bundle), build with Gradle configuration cache (the block avoids `Task.project` at execution time but was run without it), and Windows/Linux Editors.

When Minify is on and the upload found no usable mapping (none, or an empty one), the Editor logs one warning (the build is never failed): Java and Kotlin frames lose their line numbers and, for classes R8 may rename, their names in Bugsee (the wrapper and SDK classes are kept by their rules, so on a real device their names stay and only the line numbers go: `UnityWrapperProvider.java:1` instead of `:240`). It lists the folders searched and the manual call, `bugsee-cli debug-files upload <mapping.txt> --type proguard --version <version> --build <build number>` with `BUGSEE_APP_TOKEN` set. For an APK or AAB the mapping's age is not judged (Unity copies Gradle's file with its modification time, and an up-to-date R8 task leaves it unchanged); in an Export Project a `mapping.txt` older than the build was left by an earlier Gradle run (Unity never runs Gradle there), so it is not uploaded and the warning names it. Where the mapping lands (real builds with R8 on, identical on Unity 2021.3.45f1, 2022.3.62f3 and 6000.0.84f1): an **APK** and an **AAB** get `<player>_mapping.txt` beside the output, copied from Unity's Gradle project (`Library/Bee/Android/Prj/<IL2CPP|Mono2x>/Gradle/launcher/build/outputs/mapping/<variant>/mapping.txt`), so both upload as usual (when Gradle wrote none, Unity logs its own "Failed to copy mapping file" error and the package warns too); an **Export Project** has none, because Unity does not run Gradle for it: build the exported project with Gradle and upload `launcher/build/outputs/mapping/<variant>/mapping.txt` yourself (the warning says so on every such build; on AGP 8.6 and later the exported project carries the Bugsee Gradle plugin, which uploads it itself when it has the app token, and a second upload is harmless). With the upload off (no `BUGSEE_APP_TOKEN` in the Editor, or `BUGSEE_NO_SYMBOL_UPLOAD`) a Minify build whose mapping exists gets one log line with the same call; a missing mapping is not reported then, as nothing was going to be uploaded.

Releases built without R8 (Minify off, the Unity default) have no mapping and no Java names to restore. Minify may be turned on with no extra setup: the package appends its keep rules to the generated `unityLibrary/proguard-unity.txt` on every build (see the Player Settings checklist above), so `UnityWrapper` and `UnityManagedException` survive R8 and the SDK launches.

**iOS**

1. Scripting Backend: IL2CPP
2. Nothing to enable for LineNumberMappings: the package adds `--emit-source-mapping --emit-method-map` to the IL2CPP arguments of every IL2CPP build, with or without `BUGSEE_APP_TOKEN` in the Unity Editor's environment (`IL2CPP_ADDITIONAL_ARGS` of the Editor process for the build only; Player Settings are not edited). The export therefore has `LineNumberMappings.json` and `MethodMap.tsv`, and the Xcode Run Script (step 4) uploads them when the token is only in the Xcode or CI environment
3. Build or archive in Xcode: the Run Script uploads the dSYMs of the configuration being built. Debug has a dSYM only with `DEBUG_INFORMATION_FORMAT = dwarf-with-dsym` (Unity exports Debug as `dwarf`); without one the script prints a warning and uploads no dSYM
4. Ensure `BUGSEE_APP_TOKEN` is visible to the Xcode Run Script phase. The token is read when Xcode builds, never written into the exported project (which you may commit). A build started from the Xcode GUI does not see your shell's environment, so provide it one of these ways. Prefer the environment: the CI / Xcode Cloud environment, `export BUGSEE_APP_TOKEN=...` before `xcodebuild`, or `launchctl setenv BUGSEE_APP_TOKEN ...` (visible to a GUI-launched Xcode after it restarts). A plain environment variable is not printed into the build log. Or add a user-defined build setting named `BUGSEE_APP_TOKEN` to the Unity-iPhone target (Xcode exports build settings to Run Script phases as environment variables); that value is stored in the Xcode project, so do not commit the project after adding it. A Unity export in Replace mode regenerates `project.pbxproj`, so that setting (and an `.xcconfig` attached to the target) is lost and must be added again after each export; the package does not add one. Xcode prints build settings, including one from an `.xcconfig` or from `xcodebuild ... BUGSEE_APP_TOKEN=...`, into the build log, so such a log holds the token. An app token also ships inside the built app, so it is not a secret of the same order as a signing key, but do not publish the log. Without it an archive prints `warning: Bugsee: BUGSEE_APP_TOKEN unset` (a build that is not an archive prints `note:` instead) and nothing is uploaded
   - The script reports problems as `warning:` lines in the Xcode build log and issue navigator, and always exits 0 (it never prints `error:`, and rewrites the `error:` of the CLI's own output to `error -`, because Xcode fails an archive whose log has an `error:` line even when the script exits 0): a missing token, `bugsee-cli` not found, no dSYM, and a failed upload with the CLI's exit code in words (21: the server rejected the token; 30: server error; 31: network; 1/2: unexpected error or argument mismatch)
5. The iOS export sets `DEBUG_INFORMATION_FORMAT=dwarf-with-dsym` on every configuration of Unity-iPhone and UnityFramework, so Debug builds have a dSYM too. `dsymutil` on UnityFramework adds time to each Debug build; to keep Unity's own setting, tick **Bugsee → iOS → Keep Xcode Debug Info Format (skip dSYM force)** or set `BUGSEE_NO_DSYM_FORCE` in the Editor's environment (CI)

### Uploading the mapping from a later CI job

The build writes the mapping next to the generated C++ even without a token. Keep it as a build artifact and upload it after the build:

| Platform | Where the files are |
|---|---|
| Android | `<output>_BackUpThisFolder_ButDontShipItWithYourGame/il2cppOutput/Symbols/` (keep this folder; the Editor's `Library/Bee` copy is overwritten by the next build) |
| iOS, Unity 2022.3 and 6000 | `<Xcode export>/Il2CppOutputProject/Source/il2cppOutput/Symbols/` |
| iOS, Unity 2021.3 | `<Xcode export>/Classes/Native/Symbols/` |

```sh
bugsee-cli debug-files upload <Symbols>/LineNumberMappings.json \
  --type il2cpp-linemap --version <CFBundleShortVersionString/versionName> --build <build number> \
  --uuid <module UUIDs>
```

`MethodMap.tsv` and `il2cppFileRoot.txt` next to the JSON are sent with it. The UUIDs are those of the IL2CPP module: for Android the GNU build-id of each `libil2cpp.so` (one per ABI, comma separated; `llvm-readelf -n` from the NDK on the APK's `lib/<abi>/libil2cpp.so`, or the copy in `symbols.zip`), for iOS the Mach-O UUID of `UnityFramework` from the Xcode build (`dwarfdump --uuid` on the built `UnityFramework.framework/UnityFramework` or its dSYM; the export itself has no binary). Version and build must match the build's. Upload the matching native symbols (`elf` / `dsym`) as well. On iOS, the injected Run Script does all of this on its own when `BUGSEE_APP_TOKEN` is in the Xcode environment, so a manual upload is only for builds without it.

### Script Debugging vs dSYM

- **Script Debugging** / IL2CPP stacktrace information controls managed stack quality. LineNumberMappings.json and MethodMap.tsv come from the IL2CPP arguments the package adds for every IL2CPP build (not from Script Debugging), with or without `BUGSEE_APP_TOKEN`: the files are written so that a later CI job can upload them; the upload needs the token. Set `BUGSEE_NO_IL2CPP_MAPPING` to turn the emission and the upload off, or `BUGSEE_IL2CPP_MAPPING` to upload a file you made yourself (the arguments are still added for the build). The arguments are passed through the Editor process's `IL2CPP_ADDITIONAL_ARGS` environment variable for the build only (Player Settings and `ProjectSettings.asset` are not touched). They add `//<source_info:...>` comments, with absolute source paths, to the generated C++ (the player binary is unchanged but its build-id differs from a build without them) and a few MB of mapping per build (`MethodMap.tsv` is about 190 bytes per method); the first build after upgrading, and every toggle of `BUGSEE_NO_IL2CPP_MAPPING`, recompiles all generated C++ once. Set `BUGSEE_NO_IL2CPP_MAPPING` if you hand the generated C++ or an Xcode export to others. Unity 2022.3 and 6000 also add `--emit-source-mapping` themselves when Cloud Diagnostics crash reporting is on; the package does not add a flag your Additional IL2CPP Arguments or `IL2CPP_ADDITIONAL_ARGS` already has.
- **dSYM / ELF** carry native line programs used for the primary LNM remap (native PC → C++ file/line → C#).
- You need **both** the linemap upload and native symbols with line info for viewer C# file/line on native fatals.

### Managed stacks: MethodMap vs LNM file/line

- ExceptionPipeline / `LogException` send **UnityManagedException** JSON (`frames[].trace`, optional `frames[].address`, `moduleUUID`, nested `cause`, `signature`, `buildID`).
- On IL2CPP, `frames[].address` comes from **`il2cpp_native_stack_trace`**, which IL2CPP fills in the same loop as the managed trace (newest frame first, one IP per frame). The printed text can have fewer lines than IPs: `[StackTraceHidden]` throw helpers (`ExceptionDispatchInfo.Throw`) are native frames with no text, and earlier throws are printed as segments after a rethrow through `ExceptionDispatchInfo` or an await (`--- End of stack trace from previous location ---`). Only the **live segment** (after the last separator) is paired, anchored at the bottom (oldest text frame with the oldest IP); the hidden frames are at its top. Earlier segments, and a live segment longer than the IP list, get no address, so the throw site of an exception that was rethrown through an await has none (names still go through MethodMap; the worker only applies a line when the method at the address matches the frame). Verified on an iPhone XS (iOS) and an arm64 emulator (Android), Release IL2CPP, Unity 2022.3.62f3: sync, `throw;`, `throw ex;`, delegates, coroutines, inner and aggregate exceptions and a synchronous throw in the first part of an async method are fully paired (separator-free, equal counts); `ExceptionDispatchInfo.Throw` and await shapes pair the live segment only (every attached address resolved, with the dSYM / unstripped libil2cpp.so, to the method in its frame text). IL2CPP prints an async method by its own name, not as `<Run>d__1.MoveNext`; two segments joined without a separator were not seen. A frame whose text has a C# line above 0 (IL2CPP with a usym file; `in <hash>:LINE` too) gets none: its IP is a line-table address, not a return address. A usym entry with line 0 prints like a build without usym and cannot be told apart. Addresses are never read out of the stack text of a thrown exception; Mono builds send none (Mono's `<0xSTART + 0xOFFSET>` is a method start). Only the Unity log path (`FromUnityLog`, no native IPs) forwards a native-looking `0x...`/`#00 pc ...` from the text as written.
- The payload's `cause` (from `InnerException` and `AggregateException`) is sent as a nested object of the same shape: `name`, `reason` (the plain message), `frames[]`, `moduleUUID`, optional `cause`; at most 8 exceptions per payload, outermost first, aggregate siblings linearized after each other, and at most 256 K characters in total (the first exception is always kept whole; a cause that would pass the cap, and those after it, are dropped). Measured: staging accepted a 1 MB reason JSON from an Android device, so the cap is not a server limit; it stops a chain of exceptions with huge messages from multiplying a report's size. This is the contract the worker change will consume; today the worker ignores it. Unity's `JsonUtility` drops a field of its own class, so each level is serialized without it and the chain is joined by `ManagedPayloadJson`.
- **Address contract:** every address the SDK pairs to a frame of a thrown exception (`FromException`) is a hex **return address, module-relative** (instruction pointer minus the load address of the image that holds the IL2CPP code, subtracted by IL2CPP itself: UnityFramework on iOS, libil2cpp.so on Android; 32-bit values are zero-extended), resolved against the `moduleUUID` sent with the exception (that same image's Mach-O UUID / GNU build-id). For a thrown exception the SDK never sends absolute addresses; the Unity log path (`FromUnityLog`) is the exception and forwards log-text hex as written. The worker subtracts 1 to land in the calling instruction.
- Release IL2CPP walks the native stack, so a method the C++ compiler inlined into its caller has no frame of its own: the managed trace (and Android/iOS payloads alike) can lack the throw-site method even though the C# line is recoverable from the caller's address via the dSYM inline chain (done by the worker, not the SDK).
- Worker: MethodMap demangles mangled names; when addresses + symbols + linemap match, **primary LNM** remaps to C# file:line. The SDK sends module-relative offsets (see the address contract above), which the worker uses as is.
- **`moduleUUID`** is the UUID of the IL2CPP image the player is running (GNU build-id of `libil2cpp.so`, Mach-O UUID of UnityFramework), read at runtime through `il2cpp_native_stack_trace` (from a throwaway thrown exception, probed at launch and kept once found), so it is the UUID of that very build for thrown exceptions, exceptions that were never thrown, and exceptions read from the Unity log alike. A Development build that breaks on all exceptions in a debugger stops once at that probe's throw. It is empty when it cannot be known (Mono, Editor), which the worker treats as missing. No build step writes it into the project: the UUID of a build exists only after the player is built, and an earlier package version's `Assets/Resources/BugseeIl2CppModuleUuids.txt` was therefore one build stale; that file is no longer read and can be deleted. The worker still accepts a CSV.

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
2. NDK enabled (`bugsee-android-ndk:7.4.2` + Gradle `ndk { enabled = true }` on Unity 6+).
3. Post-build uploads `elf` + `il2cpp-linemap` (multi-ABI UUIDs).
4. Native fatal on device; viewer shows **C# file/line**.

### Milestone C1 — managed auto-capture / LogException

1. `CaptureManagedExceptions` enabled (default).
2. Unhandled managed exception / `LogUnhandledException` appears once (no dual-hook duplicates).
3. **Mode A:** MethodFileLineNumber / Mono Development build → viewer shows C# file:line from stack strings.
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

- **Android:** Maven `com.bugsee:bugsee-android:7.4.2` + `com.bugsee:bugsee-android-ndk:7.4.2`, declared in the generated Gradle project by `Editor/BugseeAndroidMavenDependencies.cs` (Gradle plugin `4.0.8`, patched into the generated project when it pins AGP 8.6+). Native symbols for upload come from Unity's `symbols.zip` (Player Settings > Publishing Settings > Create symbols.zip). Unity 6 defaults it to Disabled and Public holds symbol tables only; native/IL2CPP file:line symbolication needs Debugging (about ten times larger, mind Google Play's limits). The build warns when a Bugsee app token is set and the level is not Debugging; `Bugsee > Android > Use Full Native Symbols (Debugging) For Symbol Upload` (or `BUGSEE_ANDROID_FULL_SYMBOLS=1`) builds with Debugging and restores the setting. On 2021.3 release builds also get `debugSymbolLevel 'FULL'`. Core publishes no transitives; NDK is a separate artifact and must be declared. Fragment secure-view overloads are typed as `Object`, so Unity JNI does not need `androidx.fragment`.
- **iOS:** SPM-only. Xcode resolves [`bugsee/spm`](https://github.com/bugsee/spm) `7.0.0-beta7` (iOS 15+). C bridge: `Plugins/iOS/BugseeUnityBridge.mm` + `BugseeUnityCallbacks.mm`.

### iOS bridge status

Wired: Launch/Stop, blackout, log/trace/event, exceptions (JSON-in-reason), TestCrash, report/upload (+ labels), identity (`SetUserIdentifier` / `GetUserIdentifier` / `ClearUserIdentifier`; there is no email API), attributes, secure rects, appearance colors/strings, feedback UI, network/log/breadcrumb filters, report handler (via `BugseeWrapper`), lifecycle listener (via wrapper `onLifecycleEvent` → `Bugsee.LifecycleEvent` / `ILifecycleEventListener`).

### iOS managed exceptions by SDK version

The bridge builds against both native APIs. From iOS SDK 7.0.0-beta7 a logged exception takes only `domain` (the wrapper passes `UnityManagedException`, as on Android) and `skipFrames`, so `Bugsee.LogException(exception, options)` honours only the `skipFrames` key on iOS. `labels`, `includeVideo` and `mergingRules` are dropped (with a one-time warning in the Unity console and the native log): a logged exception takes no labels and always carries the live capture. (Labels are not attached through a report handler either: nothing on the report identifies it as the exception's, and a wrong label is worse than none.) On the simulator the SDK files no logged exception (see the simulator note above). Exceptions are only validated end to end on a device.

`DeleteCollectedDataOnDevice` uses the SDK's delete-everything form (it also deletes report bundles not uploaded yet and unsent crashes).

### Developing against an unreleased iOS SDK

To build the Unity project against a bugsee-cocoa checkout whose tag does not exist yet, assemble a local package and point the export at it:

```sh
Tools~/scripts/assemble-local-ios-sdk.sh <bugsee-cocoa checkout> <package dir> [7.0.0-betaN]
export BUGSEE_IOS_SDK_LOCAL_PACKAGE=<package dir>   # read by the iOS export post-process
sh Tests~/iOS/run-tests.sh <package dir>            # bridge tests against the same build
```

The script builds the xcframework as the beta release does (`scripts/build_new.sh`), then lays out the package `bugsee/spm` carries with a path `binaryTarget`. Nothing is published. With the variable set, the post-process copies that package next to the Xcode project instead of adding the pinned `bugsee/spm` tag and logs a warning; a directory without `Package.swift` and `Bugsee.xcframework` fails the build, and a release export (one built without `BuildOptions.Development`) ignores the variable unless `BUGSEE_IOS_SDK_LOCAL_PACKAGE_ALLOW_RELEASE=1`. Unset, the pinned tag is used. A relative `BUGSEE_IOS_SDK_LOCAL_PACKAGE` is resolved against the Unity process's working directory (the project folder in batchmode). The package is copied (also from a read-only source) before the Xcode project is touched, any failure of the SPM step fails the build, and a refused or failed export leaves the SPM link of the project as it was; later post-processors, such as the symbol-upload Run Script injection, still run. An export refuses (build error) to replace a `Bugsee/` folder at the Xcode project root that it did not copy there itself. Known edges: a re-export removes every earlier link to the official repository spelled as `https://github.com/bugsee/spm.git` and a local package at `Bugsee` that provides a product named `Bugsee`, including one of your own; the same repository under another spelling (no `.git`, other case) is not recognised, so the project then has two references and Xcode may pick yours over the pin.

### Attributes

`SetAttribute` accepts a string (up to 1024 UTF-16 units), a bool or a number within the signed 64-bit range; anything else throws `ArgumentException`. `GetAttribute` returns the kind that was set: `string`, `bool`, an integer or a `double`. The numeric width is not promised (Android returns the boxed type that was set, with `sbyte`/`ushort` stored as `int`, `uint`/`ulong` as `long` and `decimal` as `double`; iOS returns `long` or `double`), and a 1.0 set as floating point stays floating. A container set natively reads as a `Dictionary` on Android and as JSON text on iOS. On iOS the bridge returns the value as a type tag and payload (`s:`, `b:`, `i:`, `d:`, `j:`; see `AttributeWire`); the SDK stores the plain value, so reports show it unwrapped.

### Launch options and notifications

`Bugsee.GetLaunchOptions()` returns the options the SDK is running with (its defaults with the ones given to `Launch` merged over them) as a read-only snapshot, or `null` while the SDK is not launched. Keys are each platform's own option names, so they are not the same on Android and iOS. Values are `string`, `bool`, `long` or `double` on both platforms (the SDK's narrower `int`/`float` defaults are widened), or a nested `Dictionary<string, object>` / `List<object>`; a value with no such form (a date, a Java array) reads as its text. The SDK's internal keys (those starting with two dollar signs) are not included.

`Bugsee.Notify(title, body = null, severity = IssueSeverity.Medium, fields = null, urgent = false)` relays a lightweight notification to the app's messaging integrations without creating a report (no video, logs or events). It is stored offline and sent in batches; `urgent` asks for immediate delivery. A call with an empty title, or before the SDK is launched, is ignored by the native SDK (the title case also logs a Unity warning). `fields` are extra string pairs; entries with a null key or value are dropped. The severity is always sent, so the native default for the shorter overloads is not used.

`Bugsee.ShowReportDialog(summary, description, severity, labels)` and `Bugsee.Upload(summary, description, severity, labels)` take an optional `IssueSeverity? severity` (default `null`). Leave it out and the SDK picks its default, the `ReportingDefaultBugPriority` launch option (High when it is not set), on both platforms; an explicit severity is used as given. Code that passed `IssueSeverity.High` explicitly is unchanged. The parameter type changed from `IssueSeverity` to `IssueSeverity?` call sites compile as before (source compatible), but it is a binary-incompatible change: a precompiled assembly built against the old signature must be recompiled, and a delegate bound to these methods or an implementation of `IBugseeNativeBridge` must take `IssueSeverity?`. An explicit `(IssueSeverity)0` is not a severity and counts as omitted.

### Filters and the report handler while the Unity loop is paused

The network, log and breadcrumb filters and the report handler run on the Unity main thread. On Android the SDK calls them on its own threads and the wrapper hands each one to the main thread, which drains its queue in `Update`; while the app is in the background Unity's loop is paused (and Android may freeze the whole process after about a minute), so they wait until it resumes. On iOS the SDK calls the report handler on the main thread, where Unity runs, so it is not queued; filters are called on SDK threads and queued the same way. In the iOS SDK source the filter decision is a block the SDK stores and calls later, with no wait or semaphore in the filter paths, so no SDK thread is held for Unity (checked in the SDK source, not in the beta7 binary itself). The Android callbacks are asynchronous in the same way.

- **Filters** are not given a wrapper timeout and have no pass-through default. A filter that redacts must run before the event is recorded, and an unfiltered pass-through would leak what it exists to remove. A paused loop therefore only delays the reply: the queued filter runs on resume and its answer is applied, unless the SDK has given up on it first. Android's log filter is dropped by the SDK after 10 s without a reply. The network and breadcrumb filters have no SDK timeout in the 7.4.1 and 7.4.2 releases (the published 7.4.1 and 7.4.2 AARs contain the 10 s message for the log filter only; the SDK's source tags for those versions do have a 10 s network-filter timeout, which the published AARs do not contain, so the wrapper follows the AARs and gives the network filter no reply window; if a released AAR gains it the window must be extended), and an unanswered breadcrumb filter leaks one pooled entry in the SDK. iOS 7.0.0-beta7 has no filter timeout. A filter that throws drops the event, as before. A filter that blocks forever blocks the main thread, which no wrapper timeout can help.
- **Upstream (Android SDK), not fixed here:** the SDK reuses the log filter's callback object for the next event after its 10 s timeout and does not check which event a reply belongs to. A reply that arrives after the timeout, say from a queue that sat behind a paused loop, can therefore complete the next event with the old event's data. The wrapper withholds a log-filter reply that would come after the 10 s window (counted from the SDK's call), so it does not trigger this, but another caller of the SDK could. A filter the wrapper withholds the reply of, or skips, is logged as a warning. The network filter has no timeout in 7.4.1/7.4.2, so it has no reuse and no window.
- **The report handler** is bounded by the SDK: it carries on with the report unchanged after `ReportHandlerCallbackTimeout` (30 s by default; `0` disables only the per-handler timer and the SDK still ends the chain at 60 s). iOS 7.0.0-beta7 uses the same 30 s but does not read the option. The wrapper follows the same clock, started when the SDK calls it: a before-phase handler that only gets to run after the SDK has moved on is skipped (Android, where a paused loop can hold it; a warning is logged), while a late after-phase handler, usually a notification, still runs with a completion that only acknowledges, a reply that arrives after it is acknowledged without applying the handler's edits (iOS), and the completion callback is honoured once. A handler that never calls `completionCallback` is abandoned a second after that deadline, which completes the report unchanged and releases the native completion (Android), or the native entry, the report and its snapshot files (iOS); a reply after that does nothing. An iOS handler still holding the report then finds its attached snapshot files gone.
- Android: the report and its attachments handed to a handler stop accepting edits (they do nothing) once the handler's completion has run, whether it answered, was skipped or was abandoned, so a handler that kept the report cannot change it after the SDK moved on. Reads still work. A handler that is still running when the SDK moves on can make edits until its completion runs or the abandon fires a second after the deadline. The timeout is read from the launch options at most once a minute, and again after a Launch or Relaunch (the last value is kept if they cannot be read).
- Small window: the SDK's timer starts slightly before the wrapper's clock (on iOS, a hop to the main thread), so a reply that arrives in that gap can still write its edits after the SDK moved on.

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
