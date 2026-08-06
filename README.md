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

Scaffold only — C# API and sample project will be ported from the legacy Unity SDK next.

## License

MIT — see [LICENSE](LICENSE).
