# CLAUDE.md

UPM package `com.bugsee.unity` (repo `bugsee/bugsee-unity`): the Bugsee crash, session-replay and diagnostics SDK for Unity on Android and iOS. Minimum Unity 2021.3. The public C# API mirrors the Android SDK 7.x contracts (`Bugsee` facade plus `Runtime/Contracts`); native SDKs are pulled in at build time, nothing native is vendored.

Read `DESIGN.md` (decision log), `README.md` (install and usage), `Documentation~/index.md` (user docs) and `CHANGELOG.md` before changing behavior.

## Layout

| Path | What lives there |
|---|---|
| `Runtime/` | Player code (`Bugsee.Runtime.asmdef`). `Bugsee.cs` is the public facade. |
| `Runtime/Contracts/` | Public API types: `Options`, `Reporting`, `Lifecycle`, `Apm`, `Appearance`, `Capture`, `Exchange`, `Feedback`. |
| `Runtime/Platform/` | `IBugseeNativeBridge` and its implementations: `Android/` (JNI + proxies), `IOS/` (P/Invoke to the `.mm` bridge), `Editor/` (no-op). |
| `Runtime/Internal/` | Managed exception pipeline, IL2CPP native stacks, main-thread dispatcher, managed view hierarchy walker (`ManagedViewHierarchy.cs`). |
| `Runtime/Capture/` | Frame capture. |
| `Runtime/WrapperPolicy/` | Pure, Unity-free logic (wire formats, policies, Gradle/Xcode text edits, view-hierarchy math). Everything here is unit-testable with plain .NET. Put new decision logic here, not in bridges. |
| `Editor/` | Build-time hooks: Gradle injection of the Maven artifacts, Android/iOS symbol and mapping upload (CLI runner), Xcode post-processing (SPM package, crash extension, early launch). |
| `Plugins/Android/` | Wrapper-side Java (`UnityWrapperProvider`, `UnityManagedException`, identity), manifest, proguard. |
| `Plugins/iOS/` | `BugseeUnityBridge.mm`, `BugseeUnityCallbacks.mm`, `BugseeEarlyLaunch.mm`. |
| `Native~/ios/Bugsee/` | `Package.swift` for the remote SPM package. No xcframework is committed. |
| `Tests~/WrapperPolicy/` | The main test suite (plain .NET, compiles `Runtime/WrapperPolicy` and selected sources). Includes source-text guard tests and vendored fixtures in `Fixtures/` (native option manifests, Android public-API snapshots). |
| `Tests~/iOS/`, `Tests~/Android/`, `Tests~/WindowsSmoke/` | Native bridge tests (`run-tests.sh`), a Gradle wrapper that compiles against the Android AAR, Windows smoke test. |
| `Tools~/` | `versions.env` (single source of native SDK pins), `CompileCheck/` (type-checks Runtime and Editor per define set without a Unity licence), `scripts/` (below). |
| `Samples~/BugseeMiniGame/` | Sample Unity project and the device harness (`Assets/Scripts/BugseeHarness/*E2E.cs`, run on real devices). |
| `.github/workflows/` | `ci.yml`, `release-check.yml`, `claude-review.yml`, `bugseeai.yml`. |

## Commands

- Unit tests: `dotnet test Tests~/WrapperPolicy` (.NET 8; this is what CI runs).
- Type-check all Unity define sets: `sh Tools~/scripts/compile-check.sh`.
- iOS bridge tests: `Tests~/iOS/run-tests.sh` (macOS).
- Bump native SDKs: see "Updating the underlying SDKs" below.
- Other scripts in `Tools~/scripts/`: `e2e-android.sh`, `check-release-build.sh`, `stamp-wrapper-build.sh`, `validate-field-symbols.sh`, `assemble-local-ios-sdk.sh`.

## Updating the underlying SDKs

The package wraps the native Bugsee Android SDK (Maven Central: `bugsee-android`, `bugsee-android-ndk`, `bugsee-android-feedback`) and iOS SDK (remote SPM `github.com/bugsee/spm`). When either releases, adopt it like this.

**1. Bump the pins**
1. Edit `Tools~/versions.env`: `ANDROID_SDK_VERSION`, or `IOS_SDK_VERSION` and `IOS_SDK_COMMIT` (the commit the SPM tag points to). This file is the only source of truth.
2. Run `Tools~/scripts/update-native-sdks.sh`. It regenerates `Runtime/BugseePackageVersion.cs` (keeping the wrapper build id), rewrites every pin listed in `Tools~/scripts/native-pins.txt` (README, DESIGN, `Documentation~/index.md`, the sample README, the current CHANGELOG section, `Tests~/Android/wrapper/build.gradle`, ...), checks the SPM tag exists, and then runs `update-options-manifest.sh`. Set `BUGSEE_OFFLINE=1` to skip the tag lookup.
3. A new place that spells out a version goes into `native-pins.txt`, so the script rewrites it and `WrapperIdentityDriftTests` catches drift. Do not hand-edit pins elsewhere.
4. Android only: run `Tools~/scripts/update-android-api-manifest.sh` (needs a JDK, curl, unzip). It rewrites the public-API snapshot `Tests~/WrapperPolicy/Fixtures/android-sdk-api/<version>.txt` from the pinned AARs; the JNI binding tests compare `Runtime/Platform/Android/*.cs` against it. Remove the old snapshot file.
5. Add the new version to the verified-version lists: `VerifiedAndroidSdkVersions` in `ReportHandlerDeadlineTests.cs` and `JudgedSdkVersions` in `LifecycleE2E.cs` (add it there only after a device run of the lifecycle scenarios, and record the run in that file's comment). Update the "checked in" version mentions in comments that cite the SDK (e.g. `CrashSignals.cs`, `ReportHandlerDeadline.cs`) when you re-checked the code against the new release.
6. iOS only: if the zip version moves, `Tests~/iOS/run-tests.sh` has a `CHECKSUM` of the zip that cannot be derived; the script warns and you update it by hand.
7. Add a CHANGELOG entry under Unreleased. Docs must not pin versions beyond what `native-pins.txt` already covers.

**2. Launch options golden files (the option manifests)**
- On each release the SDK's build and deploy jobs publish an *option manifest* to our CDN, generated from the SDK's live option registry in a Release build. It is immutable: a published version never changes. URL: `https://download.bugsee.com/sdk/options/<android|ios>/<version>.json` (spec: `bugsee/specs`, `sdk/options`). Every SDK that wraps a native SDK, this one included, must consult that file for the version it adopts and bring its own launch options in line.
- Shape: `manifestVersion`, `sdk`, `sdkVersion`, `buildConfiguration` (always `release`), `generatedAt`, then `options[]` with `key`, `type`, `default`, `module`, `hidden`, plus `enum` where the option has fixed values (and a deprecation marker where one applies).
- How it is reached: `Tools~/scripts/update-options-manifest.sh` downloads the manifest for each pinned version (https only, TLS 1.2+), verifies `manifestVersion`, `sdk`, `sdkVersion` and `buildConfiguration` match the pin, and copies it verbatim to `Tests~/WrapperPolicy/Fixtures/options-manifest/{android,ios}.json`. It records `sdk|version|url|sha256` in `sources.txt`. If the version is not published the script fails with the HTTP status and changes nothing. `ANDROID_MANIFEST_FILE` / `IOS_MANIFEST_FILE` take a manifest from a local SDK build instead (its `sdkVersion` must still equal the pin); `BUGSEE_OPTIONS_MANIFEST_BASE` overrides the base URL.
- How it is read: `Tests~/WrapperPolicy/OptionsManifestTests.cs` loads the vendored files offline (no network in tests) and checks the wrapper's surface against them: `Options` constants, typed setters and their types, enum values, defaults stated in docs, defaults the wrapper hardcodes, `CrashSignals`, and platform-only classification (`OptionPlatformGate`). It also checks the vendored files are byte-for-byte what `sources.txt` says.
- How to adopt the changes: run the suite after the bump and fix every failure in the wrapper, not by weakening the test. A new native option needs a decision: expose it (an `Options` constant, a typed property on the shared or platform launch-options class, mapping in `AndroidOptionsMapper` or the iOS bridge, docs stating the manifest default) or add it to the `*NotExposed` allow-list with a reason. A removed or renamed key, a changed default or type or enum, or a newly deprecated or hidden option must be reflected in the code and docs. Allow-list entries that are no longer needed fail the test, so lists only shrink. Diff the old and new vendored manifest to see what changed. Keys the native SDK does not know are silently ignored on device, which is why this check exists.

**3. Verify**
- `dotnet test Tests~/WrapperPolicy`, `sh Tools~/scripts/compile-check.sh`, and for iOS `Tests~/iOS/run-tests.sh`.
- A full local build of the sample for each changed platform and a smoke run on a device (the lifecycle scenario, an upload and a native crash). Read the SDK release notes for fixes the wrapper should mention or work around.

## Conventions

- Native SDK versions live only in `Tools~/versions.env` and the places the update script and the repo already pin. Do not add new version pins to docs.
- Launch options are write-only, unset keys are omitted so the native SDK owns defaults. Option names follow the Android contract; an option manifest test checks the surface against what each native SDK publishes.
- Unity APIs are main-thread only. Work from other threads goes through `MainThreadDispatcher`; the view-hierarchy walker is time-sliced (about 5% of a frame, 500 ms request budget). Avoid per-node allocations in `ManagedViewHierarchy.cs`; `Object.name` allocates a string per read and has no non-allocating public alternative.
- Do not use `GetHashCode()` as an object id in IL2CPP players; use `GetInstanceID()`.
- Managed exception signatures follow `Documentation~/exception-signatures.md`; changing the recipe needs fixture updates.
- Source-text guard tests in `Tests~/WrapperPolicy` pin call sites and wording on purpose; a failing one usually means a new call site must follow the existing pattern.
- Every user-visible change gets a CHANGELOG entry under Unreleased.

## Device testing

Real-device runs use the sample harness. Builds can take long; set `BUGSEE_NO_SYMBOL_UPLOAD=1` for local builds that do not need symbol upload. Hold a device lock while using shared hardware if your environment provides one.
