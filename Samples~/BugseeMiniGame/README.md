# Bugsee Anteater Fields Sample

Standalone Unity **2021.3+** project that exercises the local `com.bugsee.unity` package
inside an **open grassy field with hills** — a procedural, animal-like coral anteater,
third-person camera, and twofold Bugsee controls:

1. **Floating HUD** (top-right) — Report, Upload, Blackout, Log, Feedback, Identity, Debug…
   (**Blackout** also toggles day ↔ night: sun/shadows ↔ moon/stars/weaker shadows)
2. **Hilltop actions** — each hill groups related Bugsee APIs; walk onto it and **tap the action bubble**

Every action is reachable without passing through any other.

## Open

1. Unity Hub → **Open** → select this folder (`Samples~/BugseeMiniGame`).
2. Use Unity **2021.3** (matches the SDK package).
3. Package Manager resolves `com.bugsee.unity` via `file:../../..` (repo root; path is relative to `Packages/`).
4. Nothing else to install: the package has no dependencies (no EDM4U, no scoped registry).

## Set your app token

Either:

- Select `MiniGameBootstrap` in the scene and set **Bugsee App Token**, or
- Edit `Assets/Scripts/MiniGame/MiniGameBootstrap.cs` inspector field after first import.

Without a token, Launch is skipped and the HUD status explains why.

## Play

On first launch you get a short onboarding strip (until you move, or ~8s):
**WASD or left stick · climb a hill · tap the bubble**.

| Control | Action |
|---------|--------|
| WASD / arrows | Move anteater |
| Left half of screen (drag) | Virtual stick (mobile) |
| Approach hilltop beacon | Action bubble appears — **tap/click** to run |
| E / Space | Run the selected action of the hill in range (shown as `> name` in the bubble list) |
| Q / Tab | Select the next action of the hill in range |
| `` ` `` or F1 | Toggle full debug panel |
| Yellow orbs | Score + `Bugsee.Event` |
| Red hazard orbs | `LogException` |

Walk cycle + swaying trees + drifting pollen keep continuous motion visible for stutter QA.

### Hilltop actions

Beacons cover the public facade, including Launch / Relaunch / Stop / Status,
ShowReportDialog, Upload, TestCrash, Log / Trace / Event / LogException, Blackout,
Identity + attributes, secure rectangles, CaptureViewHierarchy, ResetVideoCapturePermission,
Feedback, Appearance, filters, report handler, and lifecycle listener.

| Hill | Bubbles (SDK call) |
|------|--------------------|
| Reporting | Report (`ShowReportDialog`), Upload, Report Hdlr, Hierarchy, **Notify** (`Notify`: form with title, body, severity, `key=value` fields, urgent) |
| Reports | **New Report** (`CreateReport`), **Edit Report** (form: `Description`, `Email`, `Severity`, `SetLabels`/`AddLabel`/`ClearLabels`, `SetAttribute`/`RemoveAttribute`/`ClearAllAttributes`, `AddAttachmentBytes` + `IAttachment.SetData(string)`, `AddAttachmentFile`, `ClearAttachments`), **Send Report** (`Upload(IReport)`), **Discard Report** (`Discard`); one draft at a time (stopping the SDK or deleting its data closes the SDK's open report, so the sample drops the draft: make a new one) |
| Exchange | **Breadcrumb** (`AddBreadcrumb`: category, message, level), **Network Event** (`GetExchangeFactory` + `AddNetworkEvent`: a started and a completed POST to a `.../password-reset` URL, the kind of URL the Filters bubble's network filter redacts; the filter counters on the HUD show it ran) |
| APM | **Start Txn** (`StartTransaction`), **Child Span** (`StartChild`), **Finish Txn** (`Finish`); while a transaction is open, every yellow orb you collect becomes a finished child span |
| Logging | Log Bundle, Log Levels, Exception, **Log Unhandled** (`LogUnhandledException`), Test Crash |
| Attributes, Identity | Set / Get / Clear Attr, Clear Attrs; Set User ID, Get ID, Identity, Clear ID |
| Blackout | Blackout, Pause/Resume (old) |
| Privacy | Set Secure Rect, **Remove Rect** (`RemoveSecureRectangle` of the rect last added), Clear Rects, Reset Video |
| Feedback | Feedback, Greeting, FB Listen |
| Session | Launch, Relaunch, Stop, Status, Lifecycle (listener) |
| Lifecycle | **Launch Async**, **Relaunch Async**, **Stop Async** (`LaunchAsync`/`RelaunchAsync`/`StopAsync`; the status line shows the result), **Launch Options** (`GetLaunchOptions`, all keys in the log, values of token/endpoint-like keys redacted), **Delete Data** (`DeleteCollectedDataOnDevice` behind a confirm; the SDK stays stopped until you launch it again) |
| Appearance | Appearance (colors), **Texts** (`Appearance.SetString`/`GetString` for the report placeholders; an empty field restores the default), Filters |

SetSecureRect / SetAttribute / SetUserIdentifier / Appearance and the new Breadcrumb / Edit Report / Notify / Texts bubbles
open IMGUI forms for input (they block game input while open); Delete Data opens a confirm dialog.
There are 13 hills now. The field layout used to count how many hills fit on a ring by arc length instead of chord length, so
a full ring lost its last hill; with the chord count 13 hills place for every world seed (14 would not, at the current gap), and a
warning is logged if the world ever has fewer summits than hills.

Not reachable from the sample: `IReport.Summary` beyond the report handler.

## Platforms

| Platform | Expectation |
|----------|-------------|
| **Editor** | Game + HUD work; Bugsee uses no-op bridge (`IsLaunched` stays false). |
| **Android** | Full path: Launch with `DirectBuffers`, reporting, blackout, filters, etc. |
| **iOS** | Launch + managed/native crash path via `BugseeUnityBridge` + remote SPM (`github.com/bugsee/spm` `7.0.0-beta7`). Requires **iOS 15+** (sample Player Settings and the export post-process both set `IPHONEOS_DEPLOYMENT_TARGET` to 15.0). |

### iOS Simulator

`BUGSEE_E2E_IOS_SIM=1` with `E2EBuild.BuildIos` exports a Simulator player (`BUGSEE_E2E_IOS_SIM_ARCH`, default arm64 on an Apple silicon Mac). Unity 2021.3 only ships an x86_64 Simulator player, which Apple silicon iOS 26+ simulator runtimes cannot install; use 2022.3 or newer. Built with the iOS 27 SDK, Unity 2022.3's player aborts at launch on an iOS 27 runtime (`UIScene life cycle is required for apps built with this SDK`): run it on an iOS 26 runtime, or stamp an older SDK into the built executable (`vtool -set-build-version iossim 15.0 26.0 -replace -output <exe> <exe>`, then `codesign --force --sign - --deep <app>`). On the Simulator the iOS SDK files no logged exception and no native crash, so those need a device.

## Build Android

1. Switch platform to Android.
2. Set a real Bugsee app token on `MiniGameBootstrap`.
3. Build & Run on device.

Each build declares `bugsee-android:7.4.2` + `bugsee-android-ndk:7.4.2` in the generated
`unityLibrary/build.gradle` (core publishes no transitives; NDK is explicit); no custom Gradle templates are needed (the sample ships none; the package copies Unity's own default templates, so it builds on Unity 2021.3 and Unity 6).
Gradle plugin `4.0.8` is applied on Unity 6+ launcher templates (`ndk { enabled = true }`).

### Interaction check (`interactions` e2e scenario)

`BUGSEE_E2E_SCENARIO=interactions` (iOS env var; Android intent extra `bugsee_e2e`, e.g. `am start -n com.bugsee.minigame/com.unity3d.player.UnityPlayerActivity --es bugsee_e2e interactions`) waits for the SDK to launch (up to 60 s, then starts anyway and the checks fail), then runs every action of the hills above through the same
`BugseeActionCatalog.Run` item a bubble calls (and the same `BugseeInteractions` method a form's apply button calls), reads the
effect back, and logs `[Sample] interactions PASS|FAIL <name> :: <detail>` lines ending in `interactions e2e done failures=N`.
It covers the report draft (every field and attachment reads back, upload and discard), Notify, Breadcrumb, network events,
the APM transaction with a child span and orb-pickup spans, `LogUnhandledException`, `RemoveSecureRectangle`, the Appearance
strings, `GetLaunchOptions`, and the async lifecycle (`StopAsync`, `LaunchAsync`, `RelaunchAsync`, then a confirmed Delete Data
and a final launch), and ends with the SDK launched. It does not tap bubbles or type into the forms: it checks that the
bubbles' forms open and calls what their apply buttons call. Staging does not show these uploads right away; read the
`[Sample] interactions` lines in the device log.

### Launch presets and the presets check (`presets` e2e scenario)

The sample launches with the same options as before (the `default` preset) unless a preset is chosen: the intent extra
`bugsee_preset` (Android) or the variable `BUGSEE_PRESET` (iOS) set to `minimal`, `detect` or `privacy`; `PRESET=detect
Tools~/scripts/e2e-android.sh ...` passes it on Android. The presets (`LaunchPresets.cs`) sit on top of the sample's own options.
`minimal` and `detect` set every switch (detectors, capture, triggers, the Android exit reasons and WebView options) to opposite
values, so each of those options differs from the SDK default in at least one of them; `detect` also lowers the hang levels, scales
the screenshot and, on Android, sets a WebView domain allowlist; `privacy` is built with `OptionsBuilder` (scaled video, a 1 KB
network body limit, required reporting-form fields, default priorities, `PerformanceUploadMode`; it does not capture screenshots).
The iOS crash handler is set through the typed `IOSLaunchOptions.CrashHandlerType` (`BUGSEE_E2E_CRASH_HANDLER` = 1, 2 or 3 still
overrides it). Two things to know before running a preset by hand: `detect` makes a logged exception fatal
(`TreatUnityLogExceptionsAsFatal`, so the hazard orbs' `LogException` is reported as unhandled), and `privacy` uploads over Wi-Fi
only, which can hold reports on a device or emulator without it. The debug panel (`` ` ``) has a **Launch options** section: the
preset in use, the number of option keys the SDK reports, one button per preset that relaunches with it, and a `GetLaunchOptions`
button that logs every key.

A relaunch REPLACES the SDK's option set on both platforms (the native SDKs do not merge): the options that are not passed again go
back to their defaults, including the managed `unity.*` options and the endpoint, and `Relaunch()` without options resets everything
to the defaults. The Relaunch bubble therefore passes the whole option set of the last launch again (on Android it used to pass
only the video options, on iOS nothing).

`BUGSEE_E2E_SCENARIO=presets` waits for the SDK to launch, relaunches with no options to learn each option's SDK default, then
relaunches with each preset in turn (`RelaunchAsync`) and compares every option the sample sent with what
`Bugsee.GetLaunchOptions()` reports (numbers by value; the Android SDK reports enum options by name, such as `V2` or `Verbose`, and
never reports the Unity-only options, so those are not compared there; the SDK's own `$$` keys and, on iOS, the Android-only options
are left out; at least 5 keys must be compared). An option whose value equals the SDK default proves nothing, so the scenario counts
only the options that differ from the default: every non-default preset must have at least 8, every option the presets set must
differ from the default in at least one preset, and each preset must read back differently from the launch before it. It also
checks that the Relaunch bubble keeps the whole set, that a relaunch with only `Duration` applies it and leaves the others at their
defaults, and that after returning to the default preset the options only the other presets set are back at their defaults.

What this does not prove: on iOS the readback shows the SDK stored the host options as given (also Android-only ones such as
`ReportHandlerCallbackTimeout`), not that each has an effect. Two options of `minimal` and `detect` have a visible effect and are
checked as such: with `CaptureBreadcrumbs` a breadcrumb reaches the breadcrumb filter and without it none does; with `CaptureNetwork`
a network event reaches the network filter and without it none does. The effect of the other options is not observable from the sample.
It logs `[Sample] presets PASS|FAIL ...` lines ending in `presets e2e done failures=N`.

### Filter check (`filters` e2e scenario)

`BUGSEE_E2E_SCENARIO=filters` (iOS env var, Android intent extra `bugsee_e2e`) waits for the SDK to launch, turns the Filters
bubble on, and sends what the Network Event and Breadcrumb bubbles send plus, on Android, a Debug line written to logcat. It
waits for each filter's counter to move: the network filter redacts the `password-reset` URL and the body, the breadcrumb filter
drops every breadcrumb (the scenario counts its own `e2e` breadcrumbs apart from the SDK's `http` ones), and the log filter drops
Debug and Verbose lines. `Bugsee.Log` lines skip the log filter on both platforms (the wrapper submits them without filtering), so
the log filter only sees what the SDK captures itself (the Unity log, logcat on Android); on iOS which Debug lines it drops depends
on what the SDK logs, so the scenario only requires that it ran there. It then turns the filters off and checks that the SDK calls
no filter any more (the seen counters stay 0). It logs `[Sample] filters PASS|FAIL ...` lines ending in
`filters e2e done failures=N`. The sample launches with `CaptureBreadcrumbs = true`: the option is off by default, and without it no
breadcrumb reaches the breadcrumb filter. The HUD shows the live counters on its `flt on|off nR/S lD/S bD/S` line (redacted or
dropped / seen, for the network, log and breadcrumb filter); the network and breadcrumb filters also log each redaction or drop.

### Action check (`actions` e2e scenario)

`BUGSEE_E2E_SCENARIO=actions` (iOS env var, Android intent extra `bugsee_e2e`) waits for the SDK to launch, then runs the same
action-catalog items the bubbles call (Set/Get/Clear Attr, Clear ID, Pause/Resume, Exception) and the hill selection used by
E/Space, and logs `[Sample] actions PASS|FAIL ...` lines ending in `actions e2e done failures=N`.

Notes: `Pause()`/`Resume()` are obsolete aliases of `StartBlackout()`/`EndBlackout()`, so the Pause/Resume bubble follows
`IsBlackout` (a blackout started by the Blackout bubble makes the next tap a Resume). The `count` option of the Exception bubble is
only a marker in the message and options: it does not change report grouping (the signature uses type and stack), and the iOS beta7
bridge honours only `skipFrames`.

## Character

The player prefers the Meshy **rigged** FBX at `Resources/Anteater/ScarletSnout`
(quadruped Auto-Rig + Walking). Walk samples the FBX `AnimationClip` directly each
frame (`SampleAnimation`) so it works on Editor and device without Legacy conversion
or a Mecanim controller. Stick scales playback with move speed. If the FBX is missing,
falls back to the static remesh OBJ, then the procedural organic anteater.

## Layout

```
Assets/Art/Anteater/           # logo + Generated FBX/OBJ + LICENSE
Assets/Resources/Anteater/     # runtime ScarletSnout FBX + logo + mesh fallback
Assets/Editor/                 # ScarletSnout Generic import + Animator setup
Assets/Scripts/MiniGame/       # bootstrap, HUD, stations, collectibles
Assets/Scripts/MiniGame/World/ # field builder, tree sway, particles
Assets/Scripts/MiniGame/Player/# anteater mesh, controller, third-person camera
Assets/Scripts/BugseeHarness/  # catalog, SDK bootstrap, floating HUD, debug panel
Assets/Scenes/MiniGame.unity   # single bootstrap object; world built at runtime
```

All Bugsee demo calls go through `BugseeActionCatalog` so HUD and stations stay aligned.
