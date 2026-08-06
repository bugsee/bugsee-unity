# Bugsee Unity SDK

Bug reporting and session replay for Unity games and apps on iOS and Android.

## Requirements

- Unity **2021.3** or newer
- [External Dependency Manager for Unity (EDM4U)](https://github.com/googlesamples/unity-jar-resolver) (declared as a package dependency)
- iOS builds: Xcode with Swift Package Manager support
- Android builds: custom Gradle templates enabled (EDM4U will prompt as needed)

## Installation

Package Manager → **+** → **Add package from git URL**:

```
https://github.com/bugsee/bugsee-unity.git
```

## Native dependencies

- **Android:** resolved at build time from Maven Central via EDM4U.
- **iOS:** a local Swift package is linked into the generated Xcode project until the nextgen SDK is published to [bugsee/spm](https://github.com/bugsee/spm).

Full product documentation: [docs.bugsee.com/sdk/unity](https://docs.bugsee.com/sdk/unity/).
