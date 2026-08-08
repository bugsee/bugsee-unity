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
| **Android** | [EDM4U](https://github.com/googlesamples/unity-jar-resolver) → Maven Central | Default: `bugsee-android:7.0.4`. NDK deferred until Android SDK **7.1.0**. |
| **iOS** | Swift Package Manager | **Local** package under `Native~/ios/Bugsee` (nextgen xcframework) until published to [`bugsee/spm`](https://github.com/bugsee/spm) |

After nextgen iOS is released, the Editor post-process will switch to remote SPM:

```
https://github.com/bugsee/spm.git
```

**Android stack**

| Piece | Status |
|---|---|
| `bugsee-android` | Default via EDM. **7.0.4** `.module` still lists `kotlin-stdlib` — Unity templates exclude it until **7.1.0**. |
| `bugsee-android-ndk` | **Omitted** until Android SDK 7.1.0 |
| Gradle plugin `4.0.2` | Applied on launcher by `BugseeAndroidGradleSetup` on Unity 6+ (AGP ≥ 8.6); no `ndk { enabled }` until 7.1.0 |
| `bugsee-android-feedback` | Optional / avoid on Unity 2021.3 (Compose/D8) |

### Refreshing native artifacts

```bash
./Tools~/scripts/update-native-sdks.sh
```

See `Tools~/versions.env` for pinned versions and paths.

## Layout

```
Runtime/           # C# runtime API + asmdef
Editor/            # EDM Dependencies.xml, iOS SPM post-process
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

(`Samples~` is excluded from UPM package import so the minigame’s `Library`/`ProjectSettings`
are not nested into consuming projects.)

## Status

Android-first implementation in progress:

- Contracts (Options, Lifecycle, Reporting, Exchange, Appearance, Feedback)
- Public `Bugsee` facade mirroring Android 7.x
- Android bridge: launch/options mapping, `BugseeWrapper` registration, report handler, lifecycle, feedback extension
- Network/log/breadcrumb **filters**: API present; full field mapping still expanding
- iOS bridge: deferred until Android path is solid
- Frame capturer (DirectBuffers): next

See [DESIGN.md](DESIGN.md) for the full plan.

## License

MIT — see [LICENSE](LICENSE).
