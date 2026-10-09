# Wrapper bridge and public API alignment

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Unity package a 7.x wrapper: one native wrapper registered before `launch`, host data submitted on `BugseeWrapperChannel`, and a public C# facade that matches the bridge rules in `DESIGN.md`.

**Architecture:** Platform-independent rules live in `Runtime/WrapperPolicy/` (no `UnityEngine` usings) and are proven with `dotnet test`. Android and iOS bridges call those rules and stop creating a second wrapper from `Bugsee.Launch`. Native code keeps the single wrapper instance, the channel, secure-rect pull buffer, and the `isTerminating` short-circuit.

**Tech Stack:** C# (Unity 2021.3, `netstandard2.0` policy sources), NUnit via `dotnet test`, Java `ContentProvider` under `Plugins/Android/`, Objective-C++ in `Plugins/iOS/`.

**Spec:** `DESIGN.md` in this repo, which applies [`sdk/wrapper-channel`](https://github.com/bugsee/specs/tree/main/sdk/wrapper-channel), [`sdk/wrapper-data-requests`](https://github.com/bugsee/specs/tree/main/sdk/wrapper-data-requests), and [`sdk/wrapper-workbook`](https://github.com/bugsee/specs/tree/main/sdk/wrapper-workbook). Those three win if this plan drifts.

## Global Constraints

- Android pin `com.bugsee:bugsee-android:7.3.0` and `com.bugsee:bugsee-android-ndk:7.3.0`. Gradle plugin `4.0.8` (floor 4.0.7). Do not add `mavenLocal()`.
- iOS pin `github.com/bugsee/spm` `7.0.0-beta5`. `IPHONEOS_DEPLOYMENT_TARGET` 15.0. SPM only.
- Android `minSdk` stays 21.
- One wrapper instance per process. `setWrapper` of a new instance is forbidden after the native registration. Same-instance `setWrapper` is a no-op and is not how identity is refined.
- Android provider simple class name must not match `Bugsee[A-Za-z0-9_]+InitProvider`. Use `com.bugsee.unity.UnityWrapperProvider`, `android:initOrder="200"`, extend `BugseeExtensionInitProviderBase`.
- Channel log sources that are accepted: `Custom` 98, `WebView` 5, `StdOut` 1, `StdErr` 2, `Unknown` 0. Missing source becomes `Custom` (98) in the bridge.
- Channel `log` has no filtering flag. `addNetworkEvent` always passes `requiresFiltering = true`.
- Breadcrumb levels map by name. Android: debug 1, info 2, warning 3, error 4, fatal 5. iOS: debug 4, info 3, warning 2, error 1, fatal stored as error 1.
- Secure rectangles: convert to Android physical pixels or iOS points, then round outward (floor left/top, ceil right/bottom). `r` and `b` are exclusive.
- `requestData` always calls `onResult`. Unknown types and `vh` answer `null` in this plan.
- `isTerminating == true` completes natively and does not enter C#.
- Attachments cross as a file path or bytes. Do not call `createAndAddAttachment` / `createAndAddAttachmentWithName:`.
- iOS severity wire `0` is unset. It is not `VeryLow`.
- Reject attribute numbers that are non-finite or `|v| >= 9223372036854775808`. Strings longer than 1024 UTF-16 units are rejected. Error text names the key and the bound, never the value.
- Empty and null user identifiers clear. Reads of `""` come back as null.
- A throwing or missing filter drops the event (`keep = 0`). It does not pass the original through.
- iOS `launch`, `stop`, `showReportDialog`, and `deleteCollectedDataOnDevice` run on the main thread. Channel submit, filter install, report completion, and `vh` replies do not hop.
- Native logs and exceptions name the operation or exception class. They do not include the native message or a user value.
- Do not swallow one of the two Android 7.3.0 reports from `logUnhandledException`.

## Out of scope (follow-on plans)

- Walking uGUI / UI Toolkit for `vh`. `null` is the conforming answer until that plan.
- Span and transaction handle registry.
- Declaring `bugsee-android-feedback` by default. `Bugsee.Feedback` stays a logged no-op when the artefact is absent.
- IL2CPP symbol-upload changes. Phase B in `DESIGN.md` stays as it is.
- Automatic capture of every `UnityWebRequest`. Unity 2021.3 has no process-wide completion event, and this package does not weave IL. Games record those calls with `AddNetworkEvent` (Task 12). `DESIGN.md` still describes the desired channel submit; this plan does not pretend a source-string check implements it.

## File structure

| File | Responsibility |
|---|---|
| `Runtime/WrapperPolicy/*.cs` | Pure rules. No `UnityEngine`. |
| `Tests~/WrapperPolicy/` | `dotnet test` project that compiles those sources. |
| `Plugins/Android/UnityWrapperProvider.java` | Process-lifetime Android wrapper. `setWrapper` once. |
| `Plugins/Android/BugseeUnityWrapper.androidlib/` or manifest snippet merged by the provider's `AndroidManifest.xml` next to the Java file | `ContentProvider` at `initOrder="200"`. |
| `Runtime/Platform/Android/AndroidBridge.cs` | Stops calling `setWrapper`. Refines context on the existing Java object. Applies policy before JNI. |
| `Plugins/iOS/BugseeUnityCallbacks.mm` | `+load` registration, channel storage, `isTerminating` short-circuit, fail-closed filters. |
| `Plugins/iOS/BugseeUnityBridge.mm` | Main-thread hop for the four UI/lifecycle calls. Channel exports. Secure-rect pull from the registry buffer. |
| `Runtime/Bugsee.cs` | Public methods added in Task 12. `Launch` no longer creates the wrapper. |
| `Runtime/Contracts/Reporting/IReport.cs` | Nullable severity. Path/bytes attachments. |

## Review Focus

These are the inputs a reasonable caller will hit that no single happy-path test covers. Each line is pinned by the test named in its task.

1. A second `setWrapper` from C# during `Launch` — the crash-recovery wrapper goes deaf. Task 7 test `AndroidBridge_source_does_not_call_setWrapper`.
2. A missing log source — iOS would store `Unknown` (0). Task 1 test `Missing_source_is_Custom`.
3. Breadcrumb name `debug` sent as Android int `1` to iOS — stored as error. Task 2 test `Debug_is_1_on_android_and_4_on_ios`.
4. A 10×10 Unity-pixel rect on a 3× iOS screen, rounded before dividing — leaves a sub-pixel gap. Task 5 test `Ios_scale_3_rounds_outward_after_convert`.
5. A filter that throws — the original line is kept and the secret ships. Task 10 test `Throwing_filter_completion_is_drop`.

---

### Task 1: Policy test project and log-source allow-list

**Files:**
- Create: `Runtime/WrapperPolicy/WrapperLogSourcePolicy.cs`
- Create: `Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj`
- Create: `Tests~/WrapperPolicy/WrapperLogSourcePolicyTests.cs`
- Test: `Tests~/WrapperPolicy/WrapperLogSourcePolicyTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `Bugsee.WrapperPolicy.WrapperLogSourcePolicy.Resolve(int? source)` returns the wire int. Allowed values stay unchanged. Null and every other int return `98`.

- [ ] **Step 1: Write the failing test**

`Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <IsPackable>false</IsPackable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="NUnit" Version="4.2.2" />
    <PackageReference Include="NUnit3TestAdapter" Version="4.6.0" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="..\..\Runtime\WrapperPolicy\**\*.cs" />
    <Compile Include="**\*Tests.cs" />
  </ItemGroup>
</Project>
```

`Tests~/WrapperPolicy/WrapperLogSourcePolicyTests.cs`:

```csharp
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class WrapperLogSourcePolicyTests
    {
        [Test]
        public void Missing_source_is_Custom()
        {
            Assert.That(WrapperLogSourcePolicy.Resolve(null), Is.EqualTo(98));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(5)]
        [TestCase(98)]
        public void Allow_list_is_unchanged(int source)
        {
            Assert.That(WrapperLogSourcePolicy.Resolve(source), Is.EqualTo(source));
        }

        [TestCase(3)]
        [TestCase(4)]
        [TestCase(99)]
        [TestCase(-1)]
        public void Anything_else_is_Custom(int source)
        {
            Assert.That(WrapperLogSourcePolicy.Resolve(source), Is.EqualTo(98));
        }
    }
}
```

- [ ] **Step 2: Run the test and confirm it fails**

Run: `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj --filter Missing_source_is_Custom`

Expected: FAIL because `WrapperLogSourcePolicy` does not exist.

- [ ] **Step 3: Implement**

`Runtime/WrapperPolicy/WrapperLogSourcePolicy.cs`:

```csharp
namespace Bugsee.WrapperPolicy
{
    public static class WrapperLogSourcePolicy
    {
        public const int Unknown = 0;
        public const int StdOut = 1;
        public const int StdErr = 2;
        public const int WebView = 5;
        public const int Custom = 98;

        public static int Resolve(int? source)
        {
            if (!source.HasValue) return Custom;
            switch (source.Value)
            {
                case Unknown:
                case StdOut:
                case StdErr:
                case WebView:
                case Custom:
                    return source.Value;
                default:
                    return Custom;
            }
        }
    }
}
```

- [ ] **Step 4: Re-run**

Run: `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Runtime/WrapperPolicy/WrapperLogSourcePolicy.cs Tests~/WrapperPolicy
git commit -m "Add the wrapper log-source allow-list."
```

---

### Task 2: Breadcrumb levels by name

**Files:**
- Create: `Runtime/WrapperPolicy/BreadcrumbLevelMap.cs`
- Create: `Tests~/WrapperPolicy/BreadcrumbLevelMapTests.cs`
- Test: `Tests~/WrapperPolicy/BreadcrumbLevelMapTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `BreadcrumbLevelMap.TryParse(string name, out BreadcrumbLevelName level)` and `ToAndroid(BreadcrumbLevelName)` / `ToIos(BreadcrumbLevelName)`. `TryParse` is case-sensitive on the lowercase wire names `debug`, `info`, `warning`, `error`, `fatal`. Unknown names return false.

- [ ] **Step 1: Write the failing test**

```csharp
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class BreadcrumbLevelMapTests
    {
        [Test]
        public void Debug_is_1_on_android_and_4_on_ios()
        {
            Assert.That(BreadcrumbLevelMap.TryParse("debug", out var level), Is.True);
            Assert.That(BreadcrumbLevelMap.ToAndroid(level), Is.EqualTo(1));
            Assert.That(BreadcrumbLevelMap.ToIos(level), Is.EqualTo(4));
        }

        [Test]
        public void Fatal_is_5_on_android_and_error_on_ios()
        {
            Assert.That(BreadcrumbLevelMap.TryParse("fatal", out var level), Is.True);
            Assert.That(BreadcrumbLevelMap.ToAndroid(level), Is.EqualTo(5));
            Assert.That(BreadcrumbLevelMap.ToIos(level), Is.EqualTo(1));
        }

        [Test]
        public void Unknown_name_is_rejected()
        {
            Assert.That(BreadcrumbLevelMap.TryParse("verbose", out _), Is.False);
            Assert.That(BreadcrumbLevelMap.TryParse("Debug", out _), Is.False);
        }
    }
}
```

- [ ] **Step 2: Run**

Run: `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj --filter BreadcrumbLevelMapTests`

Expected: FAIL, type missing.

- [ ] **Step 3: Implement**

```csharp
namespace Bugsee.WrapperPolicy
{
    public enum BreadcrumbLevelName
    {
        Debug,
        Info,
        Warning,
        Error,
        Fatal
    }

    public static class BreadcrumbLevelMap
    {
        public static bool TryParse(string name, out BreadcrumbLevelName level)
        {
            switch (name)
            {
                case "debug": level = BreadcrumbLevelName.Debug; return true;
                case "info": level = BreadcrumbLevelName.Info; return true;
                case "warning": level = BreadcrumbLevelName.Warning; return true;
                case "error": level = BreadcrumbLevelName.Error; return true;
                case "fatal": level = BreadcrumbLevelName.Fatal; return true;
                default:
                    level = default;
                    return false;
            }
        }

        public static int ToAndroid(BreadcrumbLevelName level)
        {
            switch (level)
            {
                case BreadcrumbLevelName.Debug: return 1;
                case BreadcrumbLevelName.Info: return 2;
                case BreadcrumbLevelName.Warning: return 3;
                case BreadcrumbLevelName.Error: return 4;
                default: return 5;
            }
        }

        public static int ToIos(BreadcrumbLevelName level)
        {
            switch (level)
            {
                case BreadcrumbLevelName.Debug: return 4;
                case BreadcrumbLevelName.Info: return 3;
                case BreadcrumbLevelName.Warning: return 2;
                default: return 1;
            }
        }
    }
}
```

- [ ] **Step 4: Re-run the filter from Step 2.** Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Runtime/WrapperPolicy/BreadcrumbLevelMap.cs Tests~/WrapperPolicy/BreadcrumbLevelMapTests.cs
git commit -m "Map breadcrumb levels by name per platform."
```

---

### Task 3: Attribute bounds and user-identifier clear

**Files:**
- Create: `Runtime/WrapperPolicy/AttributePolicy.cs`
- Create: `Runtime/WrapperPolicy/UserIdentifierPolicy.cs`
- Create: `Tests~/WrapperPolicy/AttributePolicyTests.cs`
- Test: `Tests~/WrapperPolicy/AttributePolicyTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `AttributeDecision AttributePolicy.Evaluate(string key, object value)` with `bool Accepted` and `string Error`. `Error` is null when accepted. When rejected, `Error` contains `key` and does not contain `value.ToString()`.
  - Accepted types: `string`, `bool`, and .NET numbers that fit before the SDK clamps: `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`. `long.MaxValue`, `uint.MaxValue`, and `(ulong)long.MaxValue` are accepted. Reject `ulong` values **greater than** `long.MaxValue` (including `ulong.MaxValue`) so Android cannot persist `Long.MAX_VALUE`. Reject NaN, infinities, and floating magnitudes `>= 9223372036854775808d`. Strings whose `Length` is greater than 1024 are rejected. Non-numeric types (for example `DateTime`) are rejected.
  - `string UserIdentifierPolicy.ForSet(string value)` returns null when value is null or `""`, otherwise the value.
  - `string UserIdentifierPolicy.ForGet(string value)` returns null when value is null or `""`.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AttributePolicyTests
    {
        [Test]
        public void Long_max_is_accepted_and_two_to_the_63_is_rejected_without_echoing_it()
        {
            var ok = AttributePolicy.Evaluate("lives", long.MaxValue);
            Assert.That(ok.Accepted, Is.True);

            double tooBig = 9223372036854775808d;
            var bad = AttributePolicy.Evaluate("lives", tooBig);
            Assert.That(bad.Accepted, Is.False);
            Assert.That(bad.Error, Does.Contain("lives"));
            Assert.That(bad.Error, Does.Not.Contain(tooBig.ToString()));
        }

        [Test]
        public void Non_finite_and_unsupported_types_are_rejected()
        {
            Assert.That(AttributePolicy.Evaluate("n", double.NaN).Accepted, Is.False);
            Assert.That(AttributePolicy.Evaluate("when", DateTime.UtcNow).Accepted, Is.False);
        }

        [Test]
        public void Unsigned_in_range_and_decimal_are_accepted_ulong_max_is_rejected()
        {
            Assert.That(AttributePolicy.Evaluate("u", uint.MaxValue).Accepted, Is.True);
            Assert.That(AttributePolicy.Evaluate("ul", (ulong)long.MaxValue).Accepted, Is.True);
            Assert.That(AttributePolicy.Evaluate("d", 0.1m).Accepted, Is.True);

            var bad = AttributePolicy.Evaluate("ul", ulong.MaxValue);
            Assert.That(bad.Accepted, Is.False);
            Assert.That(bad.Error, Does.Contain("ul"));
            Assert.That(bad.Error, Does.Not.Contain(ulong.MaxValue.ToString()));
        }

        [Test]
        public void String_over_1024_utf16_units_is_rejected()
        {
            var bad = AttributePolicy.Evaluate("note", new string('a', 1025));
            Assert.That(bad.Accepted, Is.False);
            Assert.That(bad.Error, Does.Contain("1024"));
            Assert.That(bad.Error, Does.Not.Contain("aaaa"));
        }

        [Test]
        public void Empty_user_identifier_clears_on_set_and_get()
        {
            Assert.That(UserIdentifierPolicy.ForSet(null), Is.Null);
            Assert.That(UserIdentifierPolicy.ForSet(""), Is.Null);
            Assert.That(UserIdentifierPolicy.ForSet("ada"), Is.EqualTo("ada"));
            Assert.That(UserIdentifierPolicy.ForGet(""), Is.Null);
        }
    }
}
```

- [ ] **Step 2: Run** `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj --filter AttributePolicyTests`

Expected: FAIL, types missing.

- [ ] **Step 3: Implement both types**

`AttributePolicy.Evaluate` switches on `TypeCode` / runtime type (same approach as task PR #7). **Integers** (`byte` … `long`, `sbyte`, `ushort`, `uint`, `ulong`): compare to `long.MaxValue` / `long.MinValue` without `Convert.ToDouble` — `(double)long.MaxValue` is IEEE `2^63` and would falsely reject `long.MaxValue`. For `ulong`, accept only when `value <= (ulong)long.MaxValue`. **Float/double:** reject NaN and infinities; reject when `>= 9223372036854775808d` or `<= -9223372036854775808d`. **Decimal:** reject when truncated to integer would exceed the same bounds. Rejection messages are fixed strings: `"attribute 'lives' exceeds 9223372036854775808"` and `"attribute 'note' exceeds 1024 UTF-16 units"` and `"attribute 'when' must be string, bool, or number"`. Build the message from `key` only.

`UserIdentifierPolicy` is the two methods in the interface block.

- [ ] **Step 4: Re-run Step 2.** Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Runtime/WrapperPolicy/AttributePolicy.cs Runtime/WrapperPolicy/UserIdentifierPolicy.cs Tests~/WrapperPolicy/AttributePolicyTests.cs
git commit -m "Reject attribute values the SDKs would clamp or drop."
```

Wire-up into `AndroidBridge.SetAttribute` / `IOSBridge.SetAttribute` and `Bugsee.SetUserIdentifier` is Task 11. This task only lands the rules.

---

### Task 4: Severity wire 0 is unset

**Files:**
- Create: `Runtime/WrapperPolicy/IssueSeverityWire.cs`
- Modify: `Runtime/Contracts/Options/IssueSeverity.cs`
- Create: `Tests~/WrapperPolicy/IssueSeverityWireTests.cs`
- Test: `Tests~/WrapperPolicy/IssueSeverityWireTests.cs`

**Interfaces:**
- Consumes: `IssueSeverity` values 1–5 already in `IssueSeverity.cs`
- Produces: `bool IssueSeverityWire.TryFromWire(int value, out IssueSeverity severity)`. Returns false for `0` and for any value outside 1–5. Does not return `VeryLow` for `0`.

`IssueSeverityExtensions.FromIntValue` currently maps every unknown int, including `0`, to `VeryLow`. Change its default path to throw `ArgumentOutOfRangeException` naming the integer, and add `TryFromWire` that returns false for `0`. Keep `FromIntValue` for call sites that already hold a known 1–5.

- [ ] **Step 1: Write the failing test**

```csharp
using Bugsee.Contracts.Options;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IssueSeverityWireTests
    {
        [Test]
        public void Zero_is_unset()
        {
            Assert.That(IssueSeverityWire.TryFromWire(0, out _), Is.False);
        }

        [Test]
        public void One_is_VeryLow_and_four_is_Critical()
        {
            Assert.That(IssueSeverityWire.TryFromWire(1, out var low), Is.True);
            Assert.That(low, Is.EqualTo(IssueSeverity.VeryLow));
            Assert.That(IssueSeverityWire.TryFromWire(4, out var critical), Is.True);
            Assert.That(critical, Is.EqualTo(IssueSeverity.Critical));
        }
    }
}
```

The test project must also compile `Runtime/Contracts/Options/IssueSeverity.cs`. Add that `Compile Include` to the csproj.

- [ ] **Step 2: Run** `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj --filter IssueSeverityWireTests`

Expected: FAIL.

- [ ] **Step 3: Implement `IssueSeverityWire` in `Runtime/WrapperPolicy/` and stop `FromIntValue` from mapping `0` to `VeryLow`.** `FromIntValue` calls `TryFromWire` and throws `ArgumentOutOfRangeException(nameof(value))` when it returns false. The exception message is `"severity wire value is unset or out of range"` plus the integer, never a label string supplied by an app.

- [ ] **Step 4: Re-run Step 2.** Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Runtime/WrapperPolicy/IssueSeverityWire.cs Runtime/Contracts/Options/IssueSeverity.cs Tests~/WrapperPolicy
git commit -m "Treat iOS severity 0 as unset."
```

---

### Task 5: Secure-rectangle registry

**Files:**
- Create: `Runtime/WrapperPolicy/SecureRectRegistry.cs`
- Create: `Tests~/WrapperPolicy/SecureRectRegistryTests.cs`
- Test: `Tests~/WrapperPolicy/SecureRectRegistryTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `SecureRectRegistry` with
  - `void Set(string ownerId, int displayId, int left, int top, int right, int bottom)` in Unity pixels, exclusive right/bottom
  - `void RemoveOwner(string ownerId)`
  - `int[] Snapshot(int displayId, float pixelsPerNativeUnit)` — native unit is 1 Android pixel or 1 iOS point. `pixelsPerNativeUnit` is `1` on Android and `Screen.scale` (points divisor) on iOS, so iOS passes `3` on a 3× screen. The returned array is `[version, count, l, t, r, b, ...]`.
  - A repeated `Set` of the same pixels does not change `version`.
  - The first **empty** all-clear for a display uses version `1` (matches Task 13 native pre-push `[1, 0]`).
  - The first snapshot that includes **non-empty** rects for a display must use version **≥ 2**, so the SDK sees a version change after the empty baseline.
  - Removing one owner republishes the other owner's rects and bumps the version when the union changes.
  - Conversion divides by `pixelsPerNativeUnit`, then floors `l`/`t` and ceils `r`/`b`.

- [ ] **Step 1: Write the failing test**

```csharp
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class SecureRectRegistryTests
    {
        [Test]
        public void Ios_scale_3_rounds_outward_after_convert()
        {
            var registry = new SecureRectRegistry();
            registry.Set("hud", 0, 0, 0, 10, 10);
            int[] snap = registry.Snapshot(0, 3f);
            Assert.That(snap, Is.EqualTo(new[] { 2, 1, 0, 0, 4, 4 }));
        }

        [Test]
        public void Same_rect_does_not_bump_version_and_other_owner_survives()
        {
            var registry = new SecureRectRegistry();
            registry.Set("a", 0, 0, 0, 2, 2);
            int version = registry.Snapshot(0, 1f)[0];
            registry.Set("a", 0, 0, 0, 2, 2);
            Assert.That(registry.Snapshot(0, 1f)[0], Is.EqualTo(version));

            registry.Set("b", 0, 5, 5, 8, 8);
            registry.RemoveOwner("a");
            int[] snap = registry.Snapshot(0, 1f);
            Assert.That(snap[0], Is.GreaterThan(version));
            Assert.That(snap[1], Is.EqualTo(1));
            Assert.That(snap[2], Is.EqualTo(5));
        }
    }
}
```

`10/3 = 3.333`, ceil exclusive edge is `4`. That is the assertion.

- [ ] **Step 2: Run** `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj --filter SecureRectRegistryTests`

Expected: FAIL.

- [ ] **Step 3: Implement the registry**

Store `Dictionary<(string owner, int display), int[]>` of four ints. `Snapshot` unions that display's rects in owner-id sort order so the buffer is stable. Compare the converted int buffer to the last published buffer for that display; assign a new version only when it differs. Track per display whether an empty `[1, 0]` baseline was already implied (native pre-push). The first empty union publishes `[1, 0]`. The first non-empty union publishes with version **2** even if no prior C# push occurred, so the SDK never treats real rects as “unchanged” from `[1, 0]`. Use `Math.Floor` on left/top and `Math.Ceiling` on right/bottom after dividing by `pixelsPerNativeUnit`. Reject a scale `<= 0` by throwing `ArgumentOutOfRangeException(nameof(pixelsPerNativeUnit))`.

- [ ] **Step 4: Re-run Step 2.** Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Runtime/WrapperPolicy/SecureRectRegistry.cs Tests~/WrapperPolicy/SecureRectRegistryTests.cs
git commit -m "Publish secure rectangles in native units with a per-display version."
```

Bridges read this registry in Task 9. Do not call `addSecureRectangle` from new code.

---

### Task 6: Drop Android-only option keys on iOS

**Files:**
- Create: `Runtime/WrapperPolicy/OptionPlatformGate.cs`
- Modify: `Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj` to compile `Runtime/Contracts/Options/Options.cs`
- Create: `Tests~/WrapperPolicy/OptionPlatformGateTests.cs`
- Test: `Tests~/WrapperPolicy/OptionPlatformGateTests.cs`

**Interfaces:**
- Consumes: `Bugsee.Contracts.Options.Options` constants
- Produces: `IDictionary<string, object> OptionPlatformGate.ForIos(IDictionary<string, object> options)`. Copies entries whose keys are in the iOS allow-list. Drops keys listed on `OptionPlatformGate.AndroidOnly`. Does not add defaults.

`Options.cs` today has no pending-report keys. Do not invent their strings. Mark these existing constants Android-only because they name Android exit reasons and have no iOS counterpart in the current C# surface: every `DetectAndReportExit*` constant, `ReportingTriggerByNotification`, `ReportingTriggerByBroadcast`. Shared keys, including `Options.Endpoint` and `Options.Debug`, pass through. A follow-up that imports a published `sdk/options` manifest may only add keys; it may not remove a key from `AndroidOnly` without a test change.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using Bugsee.Contracts.Options;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class OptionPlatformGateTests
    {
        [Test]
        public void Ios_launch_drops_android_exit_and_trigger_keys()
        {
            var options = new Dictionary<string, object>
            {
                [Options.CaptureVideo] = true,
                [Options.DetectAndReportExit] = true,
                [Options.ReportingTriggerByNotification] = true,
                [Options.Endpoint] = "https://example.test"
            };

            var ios = OptionPlatformGate.ForIos(options);

            Assert.That(ios.ContainsKey(Options.CaptureVideo), Is.True);
            Assert.That(ios.ContainsKey(Options.Endpoint), Is.True);
            Assert.That(ios.ContainsKey(Options.DetectAndReportExit), Is.False);
            Assert.That(ios.ContainsKey(Options.ReportingTriggerByNotification), Is.False);
        }
    }
}
```

- [ ] **Step 2: Run** `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj --filter OptionPlatformGateTests`

Expected: FAIL.

- [ ] **Step 3: Implement `ForIos`.** Build a `HashSet<string>` from the Android-only constants listed above. Copy every other key. Null input returns an empty dictionary.

- [ ] **Step 4: Re-run Step 2.** Expected: PASS.

- [ ] **Step 5: Call `OptionPlatformGate.ForIos` in `IOSBridge.Launch` and `IOSBridge.Relaunch` before JSON encoding.** Android launch sends the dictionary unchanged.

- [ ] **Step 6: Commit**

```bash
git add Runtime/WrapperPolicy/OptionPlatformGate.cs Runtime/Platform/IOS/IOSBridge.cs Tests~/WrapperPolicy
git commit -m "Stop forwarding Android-only launch keys to iOS."
```

---

### Task 7: Register the Android wrapper before launch

**Files:**
- Create: `Plugins/Android/UnityWrapperProvider.java`
- Create: `Plugins/Android/AndroidManifest.xml`
- Create: `Plugins/Android/proguard-user.txt` (merged by the Gradle plugin / consumer rules)
- Create or update: `Plugins/Android/*.meta` so Java and manifest are included in Android builds
- Modify: `Runtime/Platform/Android/AndroidBridge.cs` (`EnsureWrapperRegistered`, around the `setWrapper` call)
- Modify: `Runtime/Bugsee.cs` `Launch` so it does not treat wrapper creation as its job
- Create: `Tests~/WrapperPolicy/RegistrationSourceTests.cs`
- Test: `Tests~/WrapperPolicy/RegistrationSourceTests.cs`

**Interfaces:**
- Consumes: Android SDK `Bugsee.setWrapper` and `BugseeExtensionInitProviderBase` (compile classpath after EDM). C# talks to `com.bugsee.unity.UnityWrapper.refineContext(String unityVersion, String platform, String scriptingBackend, String productName, String wrapperVersion)`.
- Produces: one Java wrapper for the process. `AndroidBridge.EnsureWrapperRegistered` calls `refineContext` only.

- [ ] **Step 1: Write the failing source test**

```csharp
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class RegistrationSourceTests
    {
        static string RepoFile(string relative)
        {
            return Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../..", relative));
        }

        [Test]
        public void AndroidBridge_source_does_not_call_setWrapper()
        {
            string text = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            Assert.That(text, Does.Not.Contain("\"setWrapper\""));
        }

        [Test]
        public void Provider_class_name_survives_the_gradle_plugin_strip()
        {
            string java = File.ReadAllText(RepoFile("Plugins/Android/UnityWrapperProvider.java"));
            Assert.That(Regex.IsMatch(java, @"class\s+Bugsee[A-Za-z0-9_]+InitProvider\b"), Is.False);
            Assert.That(java, Does.Contain("class UnityWrapperProvider"));
            Assert.That(java, Does.Contain("initOrder"));
            Assert.That(java, Does.Contain("Bugsee.setWrapper"));
        }
    }
}
```

`TestDirectory` for `dotnet test` is `Tests~/WrapperPolicy/bin/Debug/net8.0`, so four `..` segments land at the repo root. Adjust the relative path if the csproj output is deeper, and assert the file exists before reading it.

- [ ] **Step 2: Run** `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj --filter RegistrationSourceTests`

Expected: FAIL. `AndroidBridge.cs` contains `"setWrapper"`. The Java file does not exist.

- [ ] **Step 3: Add the Java provider and manifest**

`onCreate` on `BugseeExtensionInitProviderBase` is `final`. Subclasses override `onExtensionCreate()` and return `false` (same as `BugseeNdkInitProvider`). `Plugins/Android/UnityWrapperProvider.java` holds both the provider and the wrapper, so there is a single class file to compile beside `UnityManagedException.java`:

```java
package com.bugsee.unity;

import com.bugsee.library.Bugsee;
import com.bugsee.library.BugseeExtensionInitProviderBase;
import java.util.HashMap;

public final class UnityWrapperProvider extends BugseeExtensionInitProviderBase {
    @Override
    protected boolean onExtensionCreate() {
        UnityWrapper.install();
        return false;
    }
}

final class UnityWrapper implements com.bugsee.library.contracts.internal.BugseeWrapper {
    private static final UnityWrapper INSTANCE = new UnityWrapper();
    private static volatile boolean installed;
    private String unityVersion = "unknown";
    private String platform = "unknown";
    private String scriptingBackend = "unknown";
    private String productName = "unknown";
    private String wrapperVersion = "unknown";

    static void install() {
        if (installed) return;
        installed = true;
        Bugsee.setWrapper(INSTANCE);
    }

    public static void refineContext(String unityVersion, String platform, String scriptingBackend, String productName, String wrapperVersion) {
        install();
        if (unityVersion != null && unityVersion.length() > 0) INSTANCE.unityVersion = unityVersion;
        if (platform != null && platform.length() > 0) INSTANCE.platform = platform;
        if (scriptingBackend != null && scriptingBackend.length() > 0) INSTANCE.scriptingBackend = scriptingBackend;
        if (productName != null && productName.length() > 0) INSTANCE.productName = productName;
        if (wrapperVersion != null && wrapperVersion.length() > 0) INSTANCE.wrapperVersion = wrapperVersion;
    }

    public String getWrapperType() { return "unity"; }
    public String getWrapperVersion() { return wrapperVersion != null ? wrapperVersion : "unknown"; }
    public String getWrapperBuild() { return "unknown"; }

    public HashMap<String, String> getContext() {
        HashMap<String, String> map = new HashMap<String, String>();
        map.put("unity_version", unityVersion);
        map.put("unity_platform", platform);
        map.put("scripting_backend", scriptingBackend);
        map.put("product_name", productName);
        return map;
    }

    public void requestData(String dataType, com.bugsee.library.contracts.common.DataRequestResultCallback callback) {
        if (callback != null) callback.onResult(null);
    }
}
```

`getWrapperVersion()` returns the `wrapperVersion` field, which starts as `"unknown"` and is set from the fifth `refineContext` argument. Do not hard-code `"0.1.0"`. The C# call below passes `BugseePackageVersion.Version` as that argument. `install()` is the only `Bugsee.setWrapper` call.

`Plugins/Android/AndroidManifest.xml`:

```xml
<manifest xmlns:android="http://schemas.android.com/apk/res/android">
  <application>
    <provider
      android:name="com.bugsee.unity.UnityWrapperProvider"
      android:authorities="${applicationId}.bugsee.unitywrapper"
      android:exported="false"
      android:directBootAware="true"
      android:initOrder="200" />
  </application>
</manifest>
```

The strip regex looks at the simple class name. `UnityWrapperProvider` does not match it. Also put `android:initOrder="200"` in a comment in the Java file so the source test can see it (`// initOrder 200`).

**R8 / ProGuard.** C# resolves `com.bugsee.unity.UnityWrapper` and its static methods by name. Add `Plugins/Android/proguard-user.txt`:

```proguard
-keep class com.bugsee.unity.UnityWrapper { *; }
-keep class com.bugsee.unity.UnityWrapperProvider { *; }
```

Tasks 9 and 13 add JNI-called static methods on `UnityWrapper` (`channelLog`, `channelAddNetwork`, `setSecureBuffer`). Extend the same `-keep` block when those land; do not narrow it to individual method names. Wire the file through the Bugsee Gradle plugin consumer rules (same pattern as other Bugsee Unity Android plugins).

Extend `RegistrationSourceTests` to assert the manifest contains `directBootAware` and that `proguard-user.txt` keeps `UnityWrapper`.

- [ ] **Step 4: Replace `AndroidBridge.EnsureWrapperRegistered`**

Delete the `CallStatic("setWrapper", ...)` path and the `BugseeWrapperProxy` construction. Call:

```csharp
using (var wrapper = new AndroidJavaClass("com.bugsee.unity.UnityWrapper"))
{
    wrapper.CallStatic("refineContext",
        Application.unityVersion ?? "unknown",
        Application.platform.ToString(),
        scriptingBackend,
        Application.productName ?? "unknown",
        BugseePackageVersion.Version);
}
```

`scriptingBackend` is `"il2cpp"` when `ScriptingImplementation.IL2CPP`, otherwise `"mono"`. `Bugsee.Launch` may still call `EnsureWrapperRegistered` so context is refined on the existing instance before `launch`. It must not construct a proxy.

Leave `BugseeWrapperProxy.cs` in the tree until Task 9 moves secure rectangles off it, then delete it in that task. This task's source test forbids `"setWrapper"` in `AndroidBridge.cs` only.

- [ ] **Step 5: Re-run Step 2.** Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Plugins/Android/UnityWrapperProvider.java Plugins/Android/AndroidManifest.xml Runtime/Platform/Android/AndroidBridge.cs Tests~/WrapperPolicy/RegistrationSourceTests.cs
git commit -m "Register the Android wrapper from a ContentProvider before launch."
```

---

### Task 8: Register the iOS wrapper at load, and hop only the UI calls

**Files:**
- Modify: `Plugins/iOS/BugseeUnityCallbacks.mm` (`BugseeUnityWrapper`, `_bugsee_ensure_wrapper`)
- Modify: `Plugins/iOS/BugseeUnityBridge.mm` (`_bugsee_launch`, `_bugsee_stop`, `_bugsee_show_report`)
- Modify: `Runtime/Platform/IOS/IOSBridge.cs` `Launch` / `Stop` / `ShowReportDialog`
- Create: `Tests~/WrapperPolicy/IosRegistrationSourceTests.cs`
- Test: `Tests~/WrapperPolicy/IosRegistrationSourceTests.cs`

**Interfaces:**
- Consumes: `+[Bugsee setWrapper:]`, `dispatch_async` onto the main queue
- Produces: `BugseeUnityWrapper` created in `+load`. `_bugsee_ensure_wrapper` writes version, build, and context strings onto that object and does not allocate a second wrapper. New export `void _bugsee_delete_collected_data(void)` used by Task 12.

- [ ] **Step 1: Write the failing source test**

```csharp
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosRegistrationSourceTests
    {
        [Test]
        public void Wrapper_is_created_in_load_and_ensure_does_not_allocate()
        {
            string text = File.ReadAllText(Path.GetFullPath(Path.Combine(
                TestContext.CurrentContext.TestDirectory, "../../../../Plugins/iOS/BugseeUnityCallbacks.mm")));
            Assert.That(text, Does.Contain("+ (void)load"));
            Assert.That(text, Does.Contain("[Bugsee setWrapper:"));
            int ensure = text.IndexOf("void _bugsee_ensure_wrapper");
            int next = text.IndexOf("void _bugsee_", ensure + 10);
            string body = text.Substring(ensure, next - ensure);
            Assert.That(body, Does.Not.Contain("BugseeUnityWrapper new"));
            Assert.That(body, Does.Not.Contain("[Bugsee setWrapper:"));
        }
    }
}
```

- [ ] **Step 2: Run** `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj --filter IosRegistrationSourceTests`

Expected: FAIL. `+load` is absent, and `_bugsee_ensure_wrapper` contains `[Bugsee setWrapper:`.

- [ ] **Step 3: Change the Objective-C wrapper**

Add to `BugseeUnityWrapper`:

```objc
+ (void)load
{
    if (!gWrapper) gWrapper = [BugseeUnityWrapper new];
    [Bugsee setWrapper:gWrapper];
}
```

`_bugsee_ensure_wrapper` becomes field writes only:

```objc
void _bugsee_ensure_wrapper(const char *version, const char *build)
{
    if (version) gWrapperVersion = [NSString stringWithUTF8String:version];
    if (build) gWrapperBuild = [NSString stringWithUTF8String:build];
    if (!gWrapper) gWrapper = [BugseeUnityWrapper new];
}
```

The `if (!gWrapper)` line is the editor/non-`+load` stub path. The source test forbids `BugseeUnityWrapper new` inside `_bugsee_ensure_wrapper`. Put the allocation in `+load` only, and in `_bugsee_ensure_wrapper` log the operation name `ensure-wrapper` and return if `gWrapper` is nil. Do not call `setWrapper` there.

Add a context dictionary `gWrapperContext` updated by a new export `_bugsee_set_wrapper_context(const char *json)` that parses a string-to-string JSON object and drops non-strings. `-context` returns that dictionary, or `@{}` when unset. Missing version/build getters return `@"unknown"` rather than `@"0.1.0"` / `@"dev"`.

- [ ] **Step 4: Hop four calls onto main in `BugseeUnityBridge.mm`**

```objc
static void BugseeRunOnMain(dispatch_block_t block)
{
    if ([NSThread isMainThread]) block();
    else dispatch_async(dispatch_get_main_queue(), block);
}
```

Wrap the bodies of `_bugsee_launch`, `_bugsee_relaunch`, `_bugsee_stop`, and `_bugsee_show_report` in `BugseeRunOnMain`. Copy the C strings to `NSString` before the block. Do not wrap `_bugsee_log`, filter completion, or report completion.

- [ ] **Step 5: Re-run Step 2.** Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Plugins/iOS/BugseeUnityCallbacks.mm Plugins/iOS/BugseeUnityBridge.mm Tests~/WrapperPolicy/IosRegistrationSourceTests.cs
git commit -m "Register the iOS wrapper at load and hop launch onto main."
```

---

### Task 9: Submit host events on the wrapper channel

**Files:**
- Modify: `Plugins/Android/UnityWrapperProvider.java` (store the channel)
- Modify: `Plugins/iOS/BugseeUnityCallbacks.mm` (`onWrapperChannelAvailable:`)
- Modify: `Plugins/iOS/BugseeUnityBridge.mm` (new exports)
- Modify: `Runtime/Platform/Android/AndroidBridge.cs` `Log`
- Modify: `Runtime/Platform/IOS/IOSBridge.cs` `Log`
- Create: `Runtime/Internal/HostLogForwarder.cs`
- Modify: `Runtime/Internal/ExceptionPipeline.cs` (install the log forwarder once; exceptions still use `logException`, not the channel)
- Create: `Runtime/WrapperPolicy/ChannelSubmit.cs`
- Create: `Tests~/WrapperPolicy/ChannelSubmitTests.cs`

**Interfaces:**
- Consumes: `WrapperLogSourcePolicy.Resolve`, `BreadcrumbLevelMap`
- Produces:
  - `ChannelSubmit.LogSourceForHostDebug()` returns `98`
  - `bool ChannelSubmit.NetworkRequiresFiltering()` returns `true`
  - Android: `UnityWrapper.onWrapperChannelAvailable` stores the channel in a static volatile field before returning. New static `UnityWrapper.channelLog(String message, int level, int source)` calls `channel.log(null, message, level, source)` when the channel is non-null, mapping `source` with `LogSource.fromRawValue` and defaulting to `Custom`. New static `UnityWrapper.channelAddNetwork(NetworkEvent event)` calls `channel.addNetworkEvent(event, true)` when the channel is non-null.
  - iOS exports: `void _bugsee_channel_log(const char *message, int level)`, `void _bugsee_channel_network(...)`, `void _bugsee_channel_breadcrumb(const char *name, int iosLevel)`. The log export calls `Wrapper`... from ObjC it passes `BGSLogEventSource` value `98` via `WrapperLogSourcePolicy` equivalent constant `98`, not a raw `0`.
  - `Bugsee.Log` keeps using the public native `log` (app-curated, source Bugsee). A new internal `HostLogForwarder` subscribes `Application.logMessageReceived` (and wraps `Debug.unityLogger.logHandler` / `ILogHandler` when needed) so Unity `Debug` output hits the channel exports with `WrapperLogSourcePolicy.Resolve(null)`. `ExceptionPipeline` installs the forwarder once at startup; it does not retarget exception filing onto the channel. Automatic `UnityWebRequest` observation is out of scope.

- [ ] **Step 1: Write the failing test**

```csharp
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ChannelSubmitTests
    {
        [Test]
        public void Host_debug_source_is_custom_and_network_events_require_filtering()
        {
            Assert.That(ChannelSubmit.LogSourceForHostDebug(), Is.EqualTo(98));
            Assert.That(ChannelSubmit.NetworkRequiresFiltering(), Is.True);
        }
    }
}
```

- [ ] **Step 2: Run** `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj --filter ChannelSubmitTests`

Expected: FAIL.

- [ ] **Step 3: Add `ChannelSubmit` with those two methods.**

- [ ] **Step 4: Store the channel and add the exports**

Android, inside `UnityWrapper`. `LogLevel` values match `Runtime/Contracts/Options/LogLevel.cs` (`Error` 1 … `Verbose` 5). `LogSource.Custom` is byte 98 on Android 7.3.0.

```java
private static volatile com.bugsee.library.contracts.internal.BugseeWrapperChannel channel;

public void onWrapperChannelAvailable(com.bugsee.library.contracts.internal.BugseeWrapperChannel value) {
    channel = value;
}

public static void channelLog(String message, int level) {
    com.bugsee.library.contracts.internal.BugseeWrapperChannel current = channel;
    if (current == null || message == null) return;
    com.bugsee.library.contracts.options.LogLevel nativeLevel;
    switch (level) {
        case 1: nativeLevel = com.bugsee.library.contracts.options.LogLevel.Error; break;
        case 2: nativeLevel = com.bugsee.library.contracts.options.LogLevel.Warning; break;
        case 4: nativeLevel = com.bugsee.library.contracts.options.LogLevel.Debug; break;
        case 5: nativeLevel = com.bugsee.library.contracts.options.LogLevel.Verbose; break;
        default: nativeLevel = com.bugsee.library.contracts.options.LogLevel.Info; break;
    }
    current.log(null, message, nativeLevel, com.bugsee.library.contracts.internal.LogSource.Custom);
}
```

Add to `RegistrationSourceTests`:

```csharp
[Test]
public void Android_channel_log_passes_custom_source()
{
    string java = File.ReadAllText(RepoFile("Plugins/Android/UnityWrapperProvider.java"));
    Assert.That(java, Does.Contain("onWrapperChannelAvailable"));
    Assert.That(java, Does.Contain("LogSource.Custom"));
    Assert.That(java, Does.Not.Contain("LogSource.Bugsee"));
}
```

iOS `onWrapperChannelAvailable:` stores `gChannel` in a static before returning, and does no other work. `_bugsee_channel_log` calls `[gChannel logWithTag:nil message:... level:... source:]` with the source from `WrapperLogSourcePolicy.Resolve(null)` when `gChannel` responds to the selector. `_bugsee_channel_network` calls `addNetworkEvent:requiresFiltering:` with `YES`. Android `channelAddNetwork` is the twin of that export: source-test that `UnityWrapperProvider.java` contains `addNetworkEvent` and `true`.

`IOSBridge` public `Log` stays on `_bugsee_log`. Add `internal void ChannelLog(string message, LogLevel level)` that P/Invokes `_bugsee_channel_log` after `WrapperLogSourcePolicy.Resolve(98)`.

- [ ] **Step 4b: `HostLogForwarder`**

Create `Runtime/Internal/HostLogForwarder.cs` with `static void InstallOnce(IBugseeNativeBridge bridge)` guarded by a process flag. Map `LogType` → `LogLevel`, call `bridge.ChannelLog` (add to `IBugseeNativeBridge` as internal). Subscribe in `Application.logMessageReceived` on the main thread. Add `HostLogForwarderSourceTests` asserting `HostLogForwarder.cs` contains `logMessageReceived` and `ExceptionPipeline.cs` calls `HostLogForwarder.InstallOnce`.

- [ ] **Step 5: Re-run ChannelSubmitTests and RegistrationSourceTests.** Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Runtime/WrapperPolicy/ChannelSubmit.cs Plugins/Android/UnityWrapperProvider.java Plugins/iOS/BugseeUnityCallbacks.mm Plugins/iOS/BugseeUnityBridge.mm Runtime/Platform Tests~/WrapperPolicy
git commit -m "Submit wrapper-captured logs on the wrapper channel."
```

---

### Task 10: Filters fail closed

**Files:**
- Modify: `Plugins/iOS/BugseeUnityCallbacks.mm` `_bugsee_set_log_filter_enabled`, `_bugsee_complete_filter`
- Modify: `Runtime/Platform/IOS/IosNativeCallbacks.cs` `OnFilter` catch path (around the `keep = 1` completion)
- Modify: `Runtime/Platform/Android/Proxies/EventFilterProxy.cs` or `CallbackProxies.cs` if the Android filter proxy completes with the original event on exception
- Create: `Tests~/WrapperPolicy/FilterCompletionTests.cs`

**Interfaces:**
- Consumes: nothing from earlier policy types
- Produces: `FilterCompletion.Drop` is `0` and `FilterCompletion.Keep` is `1`. `FilterCompletion.OnThrow` returns `Drop`.

- [ ] **Step 1: Write the failing test**

```csharp
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class FilterCompletionTests
    {
        [Test]
        public void Throwing_filter_completion_is_drop()
        {
            Assert.That(FilterCompletion.OnThrow, Is.EqualTo(FilterCompletion.Drop));
            Assert.That(FilterCompletion.Drop, Is.EqualTo(0));
        }
    }
}
```

- [ ] **Step 2: Run** `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj --filter Throwing_filter_completion_is_drop`

Expected: FAIL.

- [ ] **Step 3: Add `FilterCompletion` and fix both bridges**

In `IosNativeCallbacks.OnFilter`, the `catch` and the `default` branch call `_bugsee_complete_filter(requestId, FilterCompletion.Drop, null)`. A null managed filter, once the native filter is installed, also completes with `Drop`.

In `_bugsee_set_log_filter_enabled` and the network/breadcrumb twins: `enabled == 0` does not call `setLogEventFilter:nil`. It sets a static `gFilterCallbackInstalled = NO` and leaves the SDK filter in place. The block checks that flag: when it is NO, call `decisionBlock(event)` (keep). When it is YES and `gFilterCb` is NULL, call `decisionBlock(nil)` (drop).

Search `Android` proxies for a catch that invokes the filter callback with the original event. Change that path to pass null / drop. Name the log `filter-failed` and log `ex.GetType().Name` only.

- [ ] **Step 4: Add a source assertion** that `IosNativeCallbacks.cs` does not contain `_bugsee_complete_filter(requestId, 1, json)` inside `OnFilter`.

- [ ] **Step 5: Re-run Step 2 and the new source test.** Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Runtime/WrapperPolicy/FilterCompletion.cs Runtime/Platform Plugins/iOS/BugseeUnityCallbacks.mm Tests~/WrapperPolicy/FilterCompletionTests.cs
git commit -m "Drop filtered events when the Unity callback throws or is missing."
```

---

### Task 11: Report handlers, attachments, and attributes

**Files:**
- Modify: `Runtime/Contracts/Reporting/IReport.cs`
- Modify: `Runtime/Contracts/Reporting/IAttachment.cs`
- Modify: `Runtime/Platform/Android/AndroidReport.cs`
- Modify: `Runtime/Platform/IOS/IosReport.cs`
- Modify: `Plugins/iOS/BugseeUnityCallbacks.mm` `forwardReport:` and `BugseeUnityApplyReportDict`
- Modify: `Plugins/Android/UnityWrapperProvider.java` (Android `onBeforeReportCreated` / `onAfterReportCreated` on the process wrapper when `isTerminating`)
- Modify: `Runtime/Platform/Android/Proxies/CallbackProxies.cs` `ReportHandlerProxy` (never invoke C# when `isTerminating`)
- Modify: `Runtime/Platform/Android/AndroidBridge.cs` `SetAttribute`, `SetUserIdentifier`
- Modify: `Runtime/Platform/IOS/IOSBridge.cs` `SetAttribute`, `SetUserIdentifier`
- Modify: `Runtime/Platform/Editor/EditorBridge.cs` so it still compiles

**Interfaces:**
- Consumes: `AttributePolicy.Evaluate`, `UserIdentifierPolicy.ForSet` / `ForGet`, `IssueSeverityWire.TryFromWire`
- Produces: `IReport.Severity` becomes `IssueSeverity?`. `IReport.AddAttachmentFile(string path, string name, string mimeType)` and `AddAttachmentBytes(byte[] data, string name, string mimeType)` replace `CreateAndAddAttachment`. Both return null when the SDK returns null.

- [ ] **Step 1: Change `IReport` and fix the compile errors in `AndroidReport`, `IosReport`, and `EditorBridge`.**

`SetAttribute` on the bridges:

```csharp
public void SetAttribute(string key, object value)
{
    var decision = AttributePolicy.Evaluate(key, value);
    if (!decision.Accepted)
        throw new ArgumentException(decision.Error, nameof(value));
    // existing native set
}
```

After the native set, read the attribute back. If the read is null, throw `ArgumentException` with message `"attribute '" + key + "' was dropped"`. Do not include `value`.

`SetUserIdentifier`:

```csharp
string normalized = UserIdentifierPolicy.ForSet(userIdentifier);
if (normalized == null) ClearUserIdentifier();
else /* native set normalized */;
```

`GetUserIdentifier` returns `UserIdentifierPolicy.ForGet(native)`.

- [ ] **Step 2: `isTerminating` stays native on both platforms**

**iOS `forwardReport:`** — If `isTerminating` is true, call `completion()` and return. Do not call `gReportCb`.

**Android** — In `ReportHandlerProxy.Invoke`, when `isTerminating` is true, invoke the Java `completion` runnable immediately and return; do not call the C# `IReportHandler`. Optionally implement the same early completion on `UnityWrapper`'s wrapper-level report hooks if the SDK dispatches there during termination. Add `ReportPathSourceTests` asserting `CallbackProxies.cs` (or `ReportHandlerProxy`) completes without `_handler` when `isTerminating`.

In `BugseeUnityApplyReportDict`, delete the `createAndAddAttachmentWithName:` loop. Attachments in the result JSON use `path` or `dataBase64`. Call `addAttachmentWithFilePath:name:mimeType:move:` or `addAttachmentWithData:name:mimeType:`. A null return leaves that entry off the report.

Android `AndroidReport.CreateAndAddAttachment` is removed. `AddAttachmentBytes` calls `addAttachment(byte[], name, mimeType)`. `AddAttachmentFile` calls `addAttachment(File, name, mimeType, false)`.

- [ ] **Step 3: Severity reads**

Where Android or iOS report code maps a wire int through `FromIntValue`, use `IssueSeverityWire.TryFromWire`. False becomes a null `Severity`.

- [ ] **Step 4: Source test**

`ReportPathSourceTests` reads `Plugins/iOS/BugseeUnityCallbacks.mm` and asserts `createAndAddAttachmentWithName` is absent, and asserts `forwardReport` contains `isTerminating` before `gReportCb`.

- [ ] **Step 5: Run** `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj`

Expected: PASS, including the new source test.

- [ ] **Step 6: Commit**

```bash
git add Runtime Plugins/iOS/BugseeUnityCallbacks.mm Tests~/WrapperPolicy
git commit -m "Keep dying-process report work on the native side and attach by path or bytes."
```

---

### Task 12: Public API holes the facade still skips

**Files:**
- Modify: `Runtime/Platform/IBugseeNativeBridge.cs`
- Modify: `Runtime/Bugsee.cs`
- Modify: `Runtime/Platform/Android/AndroidBridge.cs`
- Modify: `Runtime/Platform/IOS/IOSBridge.cs`
- Modify: `Runtime/Platform/Editor/EditorBridge.cs`
- Modify: `Plugins/iOS/BugseeUnityBridge.mm`

**Interfaces:**
- Consumes: `OptionPlatformGate`, `UserIdentifierPolicy`, channel exports from Task 9, main-thread hop from Task 8
- Produces these `Bugsee` methods:
  - `void DeleteCollectedDataOnDevice()`
  - `IReport CreateReport()`
  - `void AddBreadcrumb(string category, string message, string levelName)`
  - `void AddNetworkEvent(INetworkEvent evt)` which submits through the channel with `ChannelSubmit.NetworkRequiresFiltering()`

**Launch-gated network (iOS).** `DESIGN.md`: during `Launching`, iOS drops channel `addNetworkEvent`. Buffer public `AddNetworkEvent` submits until lifecycle reaches `Launched`, then flush in order. Drop the buffer on `Stopped`. Android submits immediately through `UnityWrapper.channelAddNetwork`, which Task 9 adds next to `channelLog`. There is no automatic `UnityWebRequest` hook; the game calls `AddNetworkEvent`. Implement the buffer in `Runtime/WrapperPolicy/` or `Runtime/Internal/` (no `UnityEngine` in the policy type if the lifecycle signal can live beside the bridge).

`AddBreadcrumb` parses `levelName` with `BreadcrumbLevelMap.TryParse`. An unknown name throws `ArgumentException` whose message is `"breadcrumb level"` and does not include `levelName` if you treat it as a value; the level name is a developer-chosen identifier and may appear (`"breadcrumb level 'verbose' is unknown"` is allowed). The iOS bridge passes `BreadcrumbLevelMap.ToIos`. The Android bridge passes `ToAndroid`.

`DeleteCollectedDataOnDevice` on iOS P/Invokes `_bugsee_delete_collected_data`, whose body is `BugseeRunOnMain(^{ [Bugsee deleteCollectedDataOnDevice]; })`. On Android it calls `Bugsee.deleteCollectedDataOnDevice` and returns immediately when `GetLaunched()` is true (the SDK refuses while launched).

`CreateReport` returns the platform `IReport`. Do not open a second concurrent create on iOS: if `_openReport` is non-null, throw `InvalidOperationException("a report is already open")`.

`EditorBridge` implements each method as a no-op or a null return so the editor facade compiles.

- [ ] **Step 1: Add the methods to `IBugseeNativeBridge` and `EditorBridge` first so the solution still compiles, with `EditorBridge` throwing `NotSupportedException` only for `CreateReport` when an app calls it in the editor. `AddBreadcrumb("nope", "m", "verbose")` throws on every bridge, including the editor, via `BreadcrumbLevelMap`.**

- [ ] **Step 2: Implement Android and iOS bodies.** Android `AddNetworkEvent` builds a `NetworkEvent` with `Bugsee.getExchangeFactory()` if that Java method exists on 7.3.0, then `UnityWrapper.channelAddNetwork(event)`. If the factory method is absent, skip the submit and log `network-factory-missing` once. Do not call public `addNetworkEvent` for this path.

- [ ] **Step 3: Source test** `PublicApiSourceTests` asserts `Runtime/Bugsee.cs` contains `DeleteCollectedDataOnDevice`, `CreateReport`, `AddBreadcrumb`, and `AddNetworkEvent`.

- [ ] **Step 4: Run** `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Runtime Plugins/iOS/BugseeUnityBridge.mm Tests~/WrapperPolicy/PublicApiSourceTests.cs
git commit -m "Expose delete-data, create-report, and channel submit on the C# facade."
```

---

### Task 13: Secure rectangles come from the registry, not `addSecureRectangle`

**Files:**
- Modify: `Runtime/Bugsee.cs` `AddSecureRectangle` / `RemoveSecureRectangle` / `RemoveAllSecureRectangles`
- Modify: `Plugins/Android/UnityWrapperProvider.java` `getSecureRectangles`
- Modify: `Plugins/iOS/BugseeUnityCallbacks.mm` add `-secureRectanglesForDisplay:`
- Delete: uses of `addSecureRectangle` in `AndroidBridge` and `_bugsee_add_secure_rect`'s call to `[Bugsee addSecureRectangle:]` once the pull path is in place
- Modify: `Runtime/Platform/Android/Proxies/BugseeWrapperProxy.cs` — delete the file if nothing references it

**Interfaces:**
- Consumes: `SecureRectRegistry`
- Produces: a process-wide `SecureRectRegistry Shared` in `Runtime/WrapperPolicy/SecureRectRegistry.cs` (static `Instance`). `Bugsee.AddSecureRectangle` calls `Instance.Set("manual", 0, ...)`. `RemoveAllSecureRectangles` calls `Instance.RemoveOwner("manual")`.

Android `getSecureRectangles(int display)` returns `Instance.Snapshot(display, 1f)` as a Java `int[]`. iOS `-secureRectanglesForDisplay:` builds `NSData` of little-endian int32 from `Snapshot(display, UIScreen.mainScreen.scale)`.

C# cannot run inside the Java/ObjC pull. The registry therefore also has `int[] LastSnapshot(int display)` updated whenever `Set`/`RemoveOwner` publishes, and the native side reads a buffer C# pushed:

Add `void _bugsee_set_secure_buffer(int display, int[] packed)` and the Android twin `UnityWrapper.setSecureBuffer(int display, int[] packed)`. `Bugsee.AddSecureRectangle` updates the registry, then pushes `Snapshot` for display `0`. Android scale is `1`. iOS scale is passed from C# as `Screen.dpi` is wrong; pass a new export argument `float pixelsPerPoint` that `IOSBridge` reads from a P/Invoke ` _bugsee_screen_scale()` returning `[UIScreen mainScreen].scale`.

- [ ] **Step 1: Add `SecureRectRegistry.Instance` and push-on-write from `Bugsee.AddSecureRectangle`.**

- [ ] **Step 2: Implement the native buffers.** `getSecureRectangles` returns the last pushed array for that display, or `[1, 0]` when nothing has been pushed (version 1, count 0, the first empty publish).

- [ ] **Step 3: Source test** asserts `AndroidBridge.cs` does not contain `"addSecureRectangle"` and `BugseeUnityBridge.mm` does not contain `addSecureRectangle:`.

- [ ] **Step 4: Run** `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj`

Expected: PASS. Existing `Ios_scale_3_rounds_outward_after_convert` still passes.

- [ ] **Step 5: Commit**

```bash
git add Runtime Plugins Tests~/WrapperPolicy
git commit -m "Serve secure rectangles from the versioned pull buffer."
```

---

### Task 14: Marked Gradle edits

**Files:**
- Create: `Runtime/WrapperPolicy/MarkedGradleBlock.cs`
- Modify: `Editor/BugseeAndroidGradleSetup.cs` `WriteBaseProjectTemplate` / `WriteLauncherTemplate`
- Create: `Tests~/WrapperPolicy/MarkedGradleBlockTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `MarkedGradleBlock.Apply(string existing, string anchorLine, string markedLine)`. `anchorLine` is the exact line `plugins {` or `apply plugin: 'com.android.application'`. If that line occurs once, at depth 0, and is not inside a block comment, insert `markedLine` once after it when the file is not already patched. Treat the file as already patched when it contains `// bugsee:gradle-plugin`, the legacy comment `// Bugsee Gradle plugin` that `BugseeAndroidGradleSetup` writes today, or an active `id` / `apply plugin` line for `com.bugsee.android.gradle`. A commented-out plugin line does not count. If already patched, return `existing` unchanged (or rewrite that same region in place). Do not insert a second plugin line. If the anchor is missing, duplicated, or the line contains `/*` without a closing `*/` on the same line, throw `InvalidOperationException` whose message contains the anchor text and does not contain any other line from `existing`.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class MarkedGradleBlockTests
    {
        const string Anchor = "plugins {";
        const string Marked = "    id 'com.bugsee.android.gradle' version '4.0.8' apply false // bugsee:gradle-plugin";

        [Test]
        public void Second_apply_is_a_no_op_and_a_commented_anchor_is_refused()
        {
            string once = MarkedGradleBlock.Apply(Anchor + "\n}\n", Anchor, Marked);
            string twice = MarkedGradleBlock.Apply(once, Anchor, Marked);
            Assert.That(twice, Is.EqualTo(once));

            Assert.Throws<InvalidOperationException>(() =>
                MarkedGradleBlock.Apply("plugins { /*\n}\n", Anchor, Marked));
        }

        [Test]
        public void Legacy_bugsee_marker_is_not_duplicated()
        {
            const string existing =
                "plugins {\n" +
                "    id 'com.bugsee.android.gradle' version '4.0.8' apply false // Bugsee Gradle plugin\n" +
                "}\n";
            Assert.That(MarkedGradleBlock.Apply(existing, Anchor, Marked), Is.EqualTo(existing));
        }
    }
}
```

- [ ] **Step 2: Run** `dotnet test Tests~/WrapperPolicy/Bugsee.WrapperPolicy.Tests.csproj --filter MarkedGradleBlockTests`

Expected: FAIL.

- [ ] **Step 3: Implement `MarkedGradleBlock` and call it from `BugseeAndroidGradleSetup` instead of rewriting a template that already exists.**

Base `plugins { }` files get only the marked id line (`id 'com.bugsee.android.gradle' version '…' apply false // bugsee:gradle-plugin`). Launcher files, whose anchor is `apply plugin: 'com.android.application'`, get the marked apply line **and** the ndk block:

```
apply plugin: 'com.bugsee.android.gradle' // bugsee:gradle-plugin

bugsee {
    ndk { enabled = true }
} // bugsee:gradle-ndk
```

Do not insert `id … apply false` into a launcher file. `Apply` upserts both regions. A second apply does not duplicate the plugin line or the ndk block. When the file is missing, keep today's full-template write, using those marked lines. An existing file that already has `// Bugsee Gradle plugin`, `// bugsee:gradle-plugin`, or an active `com.bugsee.android.gradle` line is already patched. Catch `InvalidOperationException` and `Debug.LogWarning` the exception message. Do not overwrite the customer's file in that case.

Add a test whose input is a default launcher (`apply plugin: 'com.android.application'` and no Bugsee block). The first `Apply` adds the apply line and the `bugsee { ndk { enabled = true } }` region. The second `Apply` returns the same text.

- [ ] **Step 4: Re-run Step 2.** Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Runtime/WrapperPolicy/MarkedGradleBlock.cs Editor/BugseeAndroidGradleSetup.cs Tests~/WrapperPolicy/MarkedGradleBlockTests.cs
git commit -m "Insert the Gradle plugin only at a canonical anchor."
```

---

### Task 15: Changelog

**Files:**
- Modify: `CHANGELOG.md` under `[0.1.0]`

- [ ] **Step 1: Add bullets** for pre-launch wrapper registration, wrapper-channel submit, secure-rect pull buffer, fail-closed filters, severity `0`, and the new facade methods `DeleteCollectedDataOnDevice`, `CreateReport`, `AddBreadcrumb`, `AddNetworkEvent`.

- [ ] **Step 2: Commit**

```bash
git add CHANGELOG.md
git commit -m "Note the 7.x wrapper alignment in the changelog."
```

---

## Self-review

Spec coverage against `DESIGN.md` wrapper contract:

| Requirement | Task |
|---|---|
| Register before launch, one instance | 7, 8 |
| Provider name avoids `Bugsee*InitProvider` | 7 |
| Identity type `unity`, context strings, `"unknown"` | 7, 8 |
| Channel log source allow-list and missing → Custom | 1, 9 |
| Network `requiresFiltering = true` | 9, 12 |
| Hold iOS network until `Launched` | 12 |
| Host `Debug` on wrapper channel | 9 |
| Game-supplied network events (`AddNetworkEvent`), including `UnityWebRequest` the game records itself | 12 |
| Automatic observation of every `UnityWebRequest` | out of scope (no global completion event) |
| R8 keep `UnityWrapper`, Direct Boot provider | 7 |
| Breadcrumb ints by name | 2, 12 |
| `vh` answers null | 7 (`requestData` → `onResult(null)`) and existing iOS `requestDataWithType` |
| Secure rects, version, units, exclusive edges | 5, 13 |
| `isTerminating` stays native | 11 |
| Attachments by path or bytes | 11 |
| Severity 0 unset | 4, 11 |
| Attribute bounds, empty user id | 3, 11 |
| iOS main hop only for launch/stop/dialog/delete | 8, 12 |
| Filters drop on throw | 10 |
| Android-only option keys | 6 |
| Gradle anchor | 14 |
| Do not drop an Android double unhandled report | no task changes `logUnhandledException` report count |

Not in this plan, called out under Out of scope: `vh` tree walk, spans, feedback artefact, symbol upload, automatic `UnityWebRequest` capture.
