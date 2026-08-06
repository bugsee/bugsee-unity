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

## Native SDKs

| Platform | Integration | Current pin |
|---|---|---|
| **Android** | [EDM4U](https://github.com/googlesamples/unity-jar-resolver) → Maven Central | `com.bugsee:bugsee-android:7.0.4` (+ feedback) |
| **iOS** | Swift Package Manager | **Local** package under `Native~/ios/Bugsee` (nextgen xcframework) until published to [`bugsee/spm`](https://github.com/bugsee/spm) |

After nextgen iOS is released, the Editor post-process will switch to remote SPM:

```
https://github.com/bugsee/spm.git
```

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
```

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
