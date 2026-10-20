# Bugsee Anteater Fields Sample

Standalone Unity **2021.3+** project that exercises the local `com.bugsee.unity` package
inside an **open grassy field with hills** — a procedural, animal-like coral anteater,
third-person camera, and twofold Bugsee controls:

1. **Floating HUD** (top-right) — Report, Upload, Blackout, Log, Feedback, Identity, Debug…
   (**Blackout** also toggles day ↔ night: sun/shadows ↔ moon/stars/weaker shadows)
2. **Hilltop actions** — each hill has one Bugsee API; walk onto it and **tap the action bubble**

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
| E / Space | Optional keyboard shortcut while in range |
| `` ` `` or F1 | Toggle full debug panel |
| Yellow orbs | Score + `Bugsee.Event` |
| Red hazard orbs | `LogException` |

Walk cycle + swaying trees + drifting pollen keep continuous motion visible for stutter QA.

### Hilltop actions

Beacons cover nearly the full public facade, including Launch / Relaunch / Stop / Status,
ShowReportDialog, Upload, TestCrash, Log / Trace / Event / LogException, Blackout,
Identity + attributes, secure rectangles, CaptureViewHierarchy, ResetVideoCapturePermission,
Feedback, Appearance, filters, report handler, and lifecycle listener.

SetSecureRect / SetAttribute / SetUserIdentifier / Appearance open IMGUI forms for input
(secure LTRB + Random, attribute key/value, user id, and a scrollable color table with pickers).

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
