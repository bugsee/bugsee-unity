# Bugsee Unity SDK

UPM package for the Bugsee crash and session-replay SDK on Unity (**2021.3+**).

> This repository replaces the legacy `.unitypackage` / `Assets/Plugins` distribution.
> The previous repo remains available as a reference foundation.

## Install

In Unity Package Manager → **Add package from git URL**:

```
https://github.com/bugsee/bugsee-unity.git
```

Pin a version/tag when available:

```
https://github.com/bugsee/bugsee-unity.git#0.1.0
```

That is the whole install: no scoped registry, no EDM4U, no dependency-resolve step and no custom
Gradle templates to enable. On Android the package declares the Bugsee SDK in the Gradle project Unity
generates for each build (see below); on iOS it adds the Bugsee Swift package to the Xcode project.

Projects that use [EDM4U](https://github.com/googlesamples/unity-jar-resolver) for other SDKs keep working.
If the project already declares `com.bugsee:bugsee-android` itself (through EDM4U, a custom
`mainTemplate.gradle` or a local AAR), that declaration is kept, nothing is added next to it, and the build
log warns when its version differs from the one this package pins.

## Native SDKs

| Platform | Integration | Current pin |
|---|---|---|
| **Android** | Maven Central, declared in the generated Gradle project by `BugseeAndroidMavenDependencies` | `bugsee-android:7.4.1` + `bugsee-android-ndk:7.4.1` |
| **iOS** | Swift Package Manager + `Plugins/iOS/BugseeUnityBridge.mm` | [`bugsee/spm`](https://github.com/bugsee/spm) `7.0.0-beta7` |

**Android stack**

| Piece | Status |
|---|---|
| `bugsee-android` | **7.4.1**, added to `unityLibrary/build.gradle` at build time (no Maven transitives) |
| `bugsee-android-ndk` | Same version, declared alongside (native fatal capture for IL2CPP primary LNM) |
| `mavenCentral()` | Added to the generated project repositories only if Unity's layout lacks it |
| Gradle plugin `4.0.8` | Patched into the generated Gradle project by `BugseeAndroidGradleSetup` when it pins AGP 8.6+ (Unity 6; `ndk { enabled = true }`), in Groovy or Kotlin DSL files, see [Gradle file layouts](Documentation~/index.md#gradle-file-layouts). On Unity 2021.3 / 2022.3 (AGP 7.4.2) and Unity 6000.0.1-44 (AGP 8.3.0) it cannot run; the build-id asset is added instead and symbols are uploaded by the Editor with `bugsee-cli` found or downloaded automatically, see [Documentation~/index.md](Documentation~/index.md#unity-20213-and-20223-no-bugsee-gradle-plugin) |

### Refreshing native artifacts

```bash
./Tools~/scripts/update-native-sdks.sh
```

See `Tools~/versions.env` for pinned versions and paths.

## Crashes & symbols

See [Documentation~/index.md](Documentation~/index.md) for:

- ExceptionPipeline vs `DetectAndReportCrash`
- Hang / ANR / OOM launch options
- `BUGSEE_APP_TOKEN` Editor uploads (`il2cpp-linemap`, `elf`, `proguard`)
- iOS archive-time dSYM + linemap Run Script
- Create symbols.zip / FULL native debug symbols vs SYMBOL_TABLE

## Layout

```
Runtime/           # C# runtime API + asmdef
Editor/            # Android Gradle injection, iOS SPM, linemap + symbol upload
Plugins/           # iOS ObjC++ bridge, Android UnityManagedException.java
Native~/ios/Bugsee # Unused local SPM stub (Xcode uses github.com/bugsee/spm)
Tools~/            # Version pins + maintenance scripts
Documentation~/    # Package docs shown in Package Manager
Samples~/
  BugseeMiniGame/  # Standalone QA minigame (excluded from UPM via ~)
```

## Sample project

Open [`Samples~/BugseeMiniGame`](Samples~/BugseeMiniGame) in Unity Hub (2021.3+) to try a
small arena with **HUD buttons** and **in-world Bugsee stations**. See that folder’s
[README](Samples~/BugseeMiniGame/README.md).

## Status

- Android + iOS bridges (Launch, exceptions, filters, report handler, lifecycle, native crash options)
- ExceptionPipeline (managed auto-capture) + UnityManagedException JSON contract
- Editor symbol orchestrator (linemap / ELF / ProGuard / iOS archive dSYM)
- Contracts (Options, Lifecycle, Reporting, Exchange, Appearance, Feedback)

See [DESIGN.md](DESIGN.md) for the full plan.

## License

MIT — see [LICENSE](LICENSE).
