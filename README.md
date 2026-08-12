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

The package depends on [EDM4U](https://openupm.com/packages/com.google.external-dependency-manager/).
Add the OpenUPM scoped registry to your project’s `Packages/manifest.json` if it is not already present:

```json
{
  "scopedRegistries": [
    {
      "name": "package.openupm.com",
      "url": "https://package.openupm.com",
      "scopes": [
        "com.google.external-dependency-manager"
      ]
    }
  ]
}
```

## Native SDKs

| Platform | Integration | Current pin |
|---|---|---|
| **Android** | [EDM4U](https://github.com/googlesamples/unity-jar-resolver) → Maven Central | `bugsee-android:7.1.1` + `bugsee-android-ndk:7.1.1` |
| **iOS** | Swift Package Manager + `Plugins/iOS/BugseeUnityBridge.mm` | **Local** package under `Native~/ios/Bugsee` until [`bugsee/spm`](https://github.com/bugsee/spm) |

**Android stack**

| Piece | Status |
|---|---|
| `bugsee-android` | Default via EDM **7.1.1** |
| `bugsee-android-ndk` | Enabled (native fatal capture for IL2CPP primary LNM) |
| Gradle plugin `4.0.5` | Applied on launcher by `BugseeAndroidGradleSetup` on Unity 6+ (`ndk { enabled = true }`) |
| kotlin-stdlib | Still excluded in Unity `mainTemplate` if `.module` lists it |

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
Editor/            # EDM, Gradle, iOS SPM, linemap + symbol upload
Plugins/           # iOS ObjC++ bridge, Android UnityManagedException.java
Native~/ios/Bugsee # Local SPM package (Package.swift + xcframework)
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

- Android + iOS bridges (Launch, exceptions, native crash options)
- ExceptionPipeline (managed auto-capture) + UnityManagedException JSON contract
- Editor symbol orchestrator (linemap / ELF / ProGuard / iOS archive dSYM)
- Contracts (Options, Lifecycle, Reporting, Exchange, Appearance, Feedback)

See [DESIGN.md](DESIGN.md) for the full plan.

## License

MIT — see [LICENSE](LICENSE).
