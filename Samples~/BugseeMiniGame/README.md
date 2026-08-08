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
4. EDM4U (`com.google.external-dependency-manager`) comes from the OpenUPM scoped registry already listed in `Packages/manifest.json`.

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
| **iOS** | Launch surfaces “not implemented” until the iOS bridge lands. |

## Build Android

1. Switch platform to Android; resolve EDM4U dependencies when prompted.
2. Set a real Bugsee app token on `MiniGameBootstrap`.
3. Build & Run on device.

EDM pulls **core** `bugsee-android:7.0.4` only. That artifact’s Gradle `.module` still
requires `kotlin-stdlib:2.1.0` (even though the POM is empty); `mainTemplate.gradle`
excludes it so Unity 2021.3 D8 can build. Remove the exclude after Android SDK **7.1.0**.

### EDM resolve fails with `org.codehaus.groovy.vmplugin.v7.Java7`

EDM4U’s Android resolver runs **Gradle 5.1.1** under `Temp/PlayServicesResolverGradle`. That process uses the editor’s environment `JAVA_HOME` / PATH `java`, **not** the JDK path shown under External Tools.

If your Mac’s default `java` is 17 or 21, resolve crashes even when “JDK Installed with Unity” is checked.

The `com.bugsee.unity` Editor script `BugseeEdmJavaHomeBootstrap` pins `JAVA_HOME` to Unity’s Android OpenJDK on load. After scripts recompile, run:

**Assets → External Dependency Manager → Android Resolver → Force Resolve**

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
