using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using UnityEngine;
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
using Bugsee.WrapperPolicy;
#endif

namespace Bugsee.Sample
{
    /// <summary>
    /// Device check for the launch-option presets (BUGSEE_E2E_SCENARIO=presets, issue #118). It relaunches with the SDK's defaults
    /// (only the endpoint and video setting kept) to learn each option's default, then with every preset, and compares every option
    /// the sample sent with <c>Bugsee.GetLaunchOptions()</c>. What this proves: the native SDK took and reports each option, a
    /// relaunch replaces the option set (what a preset leaves out is back at its default), and each preset really differs from the
    /// default. It does not prove an option has an effect (iOS stores host options as given, also ones it ignores); two options with a
    /// visible effect are checked apart (breadcrumb and network capture on and off). The rest is not observable from the sample.
    /// Logs <c>[Sample] presets PASS|FAIL name :: detail</c> and a final <c>done failures=N</c>.
    /// </summary>
    static class PresetsE2E
    {
        const float WaitSeconds = 40f;
        const int MinProofKeys = 8; // options of a non-default preset that differ from the SDK default
        const int MinComparedKeys = 5;
        // Kept in the defaults relaunch because the sample cannot run without them (the iOS Simulator crashes in video capture), so
        // their default is the sample's own value and they are left out of the "differs from the default" checks.
        static readonly string[] KeptKeys = { Options.Endpoint, Options.Debug, Options.CaptureVideo, Options.CaptureVideoMode };
        static int _failures;
        static bool _replaced; // the SDK runs with options the sample did not remember
        static Dictionary<string, object> _defaults = new Dictionary<string, object>();

        public static IEnumerator Run(BugseeSampleBootstrap b)
        {
            _failures = 0;
            _replaced = false;
            var original = b.LaunchPreset;
            Debug.Log("[Sample] presets e2e start launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status + " preset=" + original);
            var body = Body(b, original);
            while (true)
            {
                bool more;
                try { more = body.MoveNext(); }
                catch (Exception ex)
                {
                    Check("scenario completed without an exception", false, ex.GetType().Name + ": " + ex.Message);
                    break;
                }
                if (!more) break;
                yield return body.Current;
            }

            // Put the launch the scenario started with back, whatever happened.
            try { SampleFilters.BreadcrumbMarker = null; b.ApplyFilters(false); } catch { }
            if (b.LaunchPreset != original || _replaced || !Bugsee.IsLaunched)
            {
                Task<bool> restore = null;
                try { restore = b.RelaunchWithPreset(original); }
                catch (Exception ex) { Debug.Log("[Sample] presets restore threw " + ex.Message); }
                yield return AwaitLaunched(restore);
                Check("restore relaunch succeeds", restore != null && restore.IsCompleted && !restore.IsFaulted && restore.Result && Bugsee.IsLaunched && b.LaunchPreset == original,
                    Describe(restore) + " launched=" + Bugsee.IsLaunched + " preset=" + b.LaunchPreset);
            }
            Debug.Log("[Sample] presets e2e done failures=" + _failures + " launched=" + Bugsee.IsLaunched + " preset=" + b.LaunchPreset);
        }

        static IEnumerator Body(BugseeSampleBootstrap b, string original)
        {
            // 0. The preset table.
            var resolved = true;
            foreach (var name in LaunchPresets.Names) resolved &= LaunchPresets.Resolve(name) == name;
            Check("every preset name resolves to itself", resolved, string.Join(",", LaunchPresets.Names));
            Check("an unknown preset falls back to default", LaunchPresets.Resolve("no-such-preset") == LaunchPresets.Default, "");

            // 1. The launch the app started with.
            yield return CheckReadBack("launch with preset " + original, Snapshot(b.LastLaunchOptions));
            var line = b.LaunchOptionsLine;
            Check("the debug panel line names the preset and the key count", line.StartsWith("preset=" + original + " | ") && line.EndsWith("launch option keys"), line);

            // 2. The SDK's defaults: a relaunch that carries only what the sample needs to keep running (endpoint, debug, video).
            var baseOptions = Snapshot(b.LastLaunchOptions);
            var probe = new Dictionary<string, object>();
            foreach (var key in KeptKeys)
                if (baseOptions.TryGetValue(key, out var v)) probe[key] = v;
            _replaced = true;
            var probeTask = Bugsee.RelaunchAsync(probe);
            yield return AwaitLaunched(probeTask);
            _defaults = Snapshot(ToDictionary(Bugsee.GetLaunchOptions()));
            Check("a relaunch with no options reports the SDK defaults", probeTask.IsCompleted && probeTask.Result && _defaults.Count >= 5,
                Describe(probeTask) + " keys=" + _defaults.Count);

            // 3. Every preset: relaunch, wait for the SDK, compare, and prove it differs from the defaults and from the previous preset.
            var everProof = new HashSet<string>();
            var union = new HashSet<string>();
            var previous = _defaults;
            foreach (var preset in LaunchPresets.Names)
            {
                var task = b.RelaunchWithPreset(preset);
                yield return AwaitLaunched(task);
                Check("relaunch with preset " + preset + " is accepted and remembered",
                    task.IsCompleted && task.Result && Bugsee.IsLaunched && b.LaunchPreset == preset,
                    Describe(task) + " launched=" + Bugsee.IsLaunched + " preset=" + b.LaunchPreset);
                var expected = Snapshot(b.LastLaunchOptions);
                yield return CheckReadBack("relaunch with preset " + preset, expected);

                var proof = new List<string>();
                var weak = new List<string>();
                foreach (var pair in expected)
                {
                    if (Skipped(pair.Key) || Array.IndexOf(KeptKeys, pair.Key) >= 0) continue;
                    union.Add(pair.Key);
                    if (_defaults.TryGetValue(pair.Key, out var def) && Same(pair.Key, pair.Value, def)) weak.Add(Short(pair.Key));
                    else { proof.Add(pair.Key); everProof.Add(pair.Key); }
                }
                if (preset != LaunchPresets.Default)
                    Check("preset " + preset + " sets at least " + MinProofKeys + " options that differ from the SDK default", proof.Count >= MinProofKeys,
                        proof.Count + " differ, " + weak.Count + " equal the default (not counted): " + string.Join(",", weak.Take(12).ToArray()));

                var readBack = Snapshot(ToDictionary(Bugsee.GetLaunchOptions()));
                var changed = readBack.Keys.Count(k => !Skipped(k) && previous.TryGetValue(k, out var pv) && !Same(k, pv, readBack[k]));
                Check("preset " + preset + " reads back different from the previous launch", changed >= 1, changed + " options changed");
                previous = readBack;

                if (preset == LaunchPresets.Minimal || preset == LaunchPresets.Detect)
                    yield return CheckCaptureEffect(b, preset == LaunchPresets.Detect);
            }
            var never = union.Where(k => !everProof.Contains(k)).Select(Short).ToArray();
            Check("every option the presets set differs from the SDK default in at least one preset", never.Length == 0, "never different: " + string.Join(",", never));

            // 4. The Relaunch bubble: it must pass the whole set again, not a few options (the last preset, privacy, is the one running).
            var beforeBubble = Snapshot(b.LastLaunchOptions);
            b.RelaunchSdk();
            yield return AwaitLaunched(b.LastRelaunch);
            Check("Relaunch bubble relaunch is accepted", b.LastRelaunch != null && b.LastRelaunch.IsCompleted && b.LastRelaunch.Result && Bugsee.IsLaunched,
                Describe(b.LastRelaunch) + " launched=" + Bugsee.IsLaunched);
            yield return CheckReadBack("Relaunch bubble keeps every option", beforeBubble);

            // 5. A relaunch with only Duration replaces the option set: the option it carries applies, the others are back at their defaults.
            var partial = new Dictionary<string, object> { { Options.Duration, 77 } };
            foreach (var key in KeptKeys) // what the sample cannot run without (see KeptKeys) goes along
                if (baseOptions.TryGetValue(key, out var kept)) partial[key] = kept;
            var partialTask = Bugsee.RelaunchAsync(partial);
            _replaced = true;
            yield return AwaitLaunched(partialTask);
            var afterPartial = ToDictionary(Bugsee.GetLaunchOptions());
            afterPartial.TryGetValue(Options.Duration, out var duration);
            Check("a relaunch with only Duration applies it", partialTask.IsCompleted && partialTask.Result && Same(Options.Duration, 77, duration),
                "duration=" + (duration ?? "(absent)") + " " + Describe(partialTask));
            var stale = beforeBubble.Keys.Where(k => !Skipped(k) && Array.IndexOf(KeptKeys, k) < 0 && !k.EndsWith("duration", StringComparison.Ordinal)
                && _defaults.TryGetValue(k, out var dv) && !Same(k, dv, beforeBubble[k]) && afterPartial.TryGetValue(k, out var av) && !Same(k, dv, av)).Select(Short).ToArray();
            Check("a relaunch replaces the option set: what it does not carry is back at the SDK default", stale.Length == 0, "still carried over: " + string.Join(",", stale));

            // 6. Back to the sample's own options: the keys only the other presets set must be back at their defaults too.
            var back = b.RelaunchWithPreset(LaunchPresets.Default);
            yield return AwaitLaunched(back);
            _replaced = false;
            Check("relaunch back to preset default is accepted", back.IsCompleted && back.Result && Bugsee.IsLaunched && b.LaunchPreset == LaunchPresets.Default,
                Describe(back) + " launched=" + Bugsee.IsLaunched);
            var baseExpected = Snapshot(b.LastLaunchOptions);
            yield return CheckReadBack("relaunch back to preset default", baseExpected);
            var after = ToDictionary(Bugsee.GetLaunchOptions());
            var leftOver = union.Where(k => !baseExpected.ContainsKey(k) && _defaults.TryGetValue(k, out var dv) && (!after.TryGetValue(k, out var av) || !Same(k, dv, av)))
                .Select(Short).ToArray();
            Check("options only other presets set are back at the SDK default", leftOver.Length == 0, "left over: " + string.Join(",", leftOver));
        }

        // The two options of minimal and detect whose effect the sample can see: with CaptureBreadcrumbs a breadcrumb reaches the
        // breadcrumb filter, without it none does (#117); with CaptureNetwork a network event reaches the network filter.
        static IEnumerator CheckCaptureEffect(BugseeSampleBootstrap b, bool captureOn)
        {
            var c = b.Catalog;
            b.ApplyFilters(true);
            SampleFilters.ResetCounters();
            SampleFilters.BreadcrumbMarker = "e2e-preset";
            c.Interactions.AddBreadcrumb("e2e-preset", "preset effect one", "info");
            c.Interactions.AddBreadcrumb("e2e-preset", "preset effect two", "info");
            c.Run(BugseeDemoAction.NetworkEvent);
            if (captureOn)
                yield return WaitFor(() => SampleFilters.BreadcrumbMarkerDropped >= 2 && SampleFilters.NetworkSeen >= 2, 15f);
            else
                yield return new WaitForSeconds(4f);
            var name = captureOn ? "detect" : "minimal";
            if (captureOn)
            {
                Check("preset " + name + ": breadcrumbs reach the breadcrumb filter", SampleFilters.BreadcrumbMarkerDropped >= 2, SampleFilters.Summary + " marker=" + SampleFilters.BreadcrumbMarkerDropped);
                Check("preset " + name + ": network events reach the network filter", SampleFilters.NetworkSeen >= 2, SampleFilters.Summary);
            }
            else
            {
                Check("preset " + name + ": no breadcrumb reaches the breadcrumb filter", SampleFilters.BreadcrumbMarkerDropped == 0, SampleFilters.Summary + " marker=" + SampleFilters.BreadcrumbMarkerDropped);
                Check("preset " + name + ": no network event reaches the network filter", SampleFilters.NetworkSeen == 0, SampleFilters.Summary);
            }
            SampleFilters.BreadcrumbMarker = null;
            b.ApplyFilters(false);
        }

        static Dictionary<string, object> Snapshot(IDictionary<string, object> options) =>
            options == null ? new Dictionary<string, object>() : new Dictionary<string, object>(options);

        static Dictionary<string, object> ToDictionary(IReadOnlyDictionary<string, object> options) =>
            options == null ? new Dictionary<string, object>() : options.ToDictionary(p => p.Key, p => p.Value);

        /// <summary>The relaunch's answer, then the SDK back at Launched, then a frame for the sample's own bookkeeping.</summary>
        static IEnumerator AwaitLaunched(Task<bool> task)
        {
            yield return WaitFor(() => task == null || task.IsCompleted, WaitSeconds);
            yield return WaitFor(() => Bugsee.IsLaunched && Bugsee.Status == BugseeStatus.Launched, WaitSeconds);
            yield return null;
            yield return null;
        }

        // The SDK may need a moment after Launched to report the new options, so the comparison is retried before it is judged.
        static IEnumerator CheckReadBack(string name, Dictionary<string, object> expected)
        {
            string detail = null;
            int compared = 0, mismatches = -1;
            var until = Time.realtimeSinceStartup + 10f;
            while (true)
            {
                mismatches = Compare(expected, Bugsee.GetLaunchOptions(), out compared, out detail);
                if (mismatches == 0 || Time.realtimeSinceStartup >= until) break;
                yield return null;
            }
            // A comparison of few keys would pass without proving anything.
            Check(name + ": every option reads back", mismatches == 0 && compared >= MinComparedKeys, "compared=" + compared + " mismatches=" + mismatches + detail);
        }

        static int Compare(Dictionary<string, object> expected, IReadOnlyDictionary<string, object> actual, out int compared, out string detail)
        {
            compared = 0;
            var mismatches = 0;
            var sb = new StringBuilder();
            if (actual == null)
            {
                detail = " (GetLaunchOptions returned null)";
                return expected.Count + 1;
            }
            foreach (var pair in expected)
            {
                if (Skipped(pair.Key)) continue;
                compared++;
                actual.TryGetValue(pair.Key, out var got);
                if (Same(pair.Key, pair.Value, got)) continue;
                mismatches++;
                if (mismatches <= 6)
                    sb.Append("\n  ").Append(Short(pair.Key)).Append(" expected=").Append(pair.Value).Append(" got=").Append(got ?? "(absent)");
            }
            detail = sb.ToString();
            return mismatches;
        }

        // Internal keys the SDK does not report, and options the iOS SDK has no counterpart for. The Android SDK also never reports the
        // Unity-only options (observed: absent from GetLaunchOptions), so those are not compared there; the iOS SDK echoes them.
        static bool Skipped(string key)
        {
            if (key.Contains("$$")) return true;
#if UNITY_ANDROID && !UNITY_EDITOR
#if UNITY_6000_0_OR_NEWER
            // Unity 6 with the Gradle plugin: the NDK extension is not registered, so the SDK never reports its keys (#226). Elsewhere
            // an absent NDK key is a mismatch.
            if (key == Options.DetectAndReportCrashNdk || key == Options.CrashNdkIncludeTombstoneLogs)
            {
                var native = Bugsee.GetLaunchOptions();
                if (native == null || !native.ContainsKey(key)) return true;
            }
#endif
            if (key.StartsWith("com.bugsee.option.unity.", StringComparison.Ordinal)) return true;
            // iOS-only options (the privacy preset sets some through the builder): the platform gate drops them, so they never come back.
            if (System.Linq.Enumerable.Contains(OptionPlatformGate.IosOnly, key)) return true;
#endif
#if UNITY_IOS && !UNITY_EDITOR
            if (System.Linq.Enumerable.Contains(OptionPlatformGate.AndroidOnly, key)) return true;
#endif
            return false;
        }

        // The enum options go out as integers; the Android SDK reports them by name (V2, High, Verbose), the iOS SDK as integers.
        static readonly Dictionary<string, Type> EnumKeys = new Dictionary<string, Type>
        {
            { Options.CaptureVideoMode, typeof(VideoMode) },
            { Options.CaptureVideoQuality, typeof(VideoQuality) },
            { Options.CaptureVideoFrameRate, typeof(FrameRate) },
            { Options.CaptureLogsLevel, typeof(LogLevel) },
            { Options.ReportingDefaultCrashPriority, typeof(IssueSeverity) },
            { Options.ReportingDefaultErrorPriority, typeof(IssueSeverity) },
            { Options.ReportingDefaultBugPriority, typeof(IssueSeverity) },
        };

        static bool Same(string key, object expected, object actual)
        {
            if (actual == null || expected == null) return false;
            if (expected is string es && actual is string asn && EnumKeys.ContainsKey(key)) return string.Equals(es, asn, StringComparison.OrdinalIgnoreCase);
            if (actual is string name && IsNumber(expected) && EnumKeys.TryGetValue(key, out var type))
                return string.Equals(Enum.GetName(type, Convert.ToInt32(expected, CultureInfo.InvariantCulture)), name, StringComparison.OrdinalIgnoreCase);
            if (expected is string eStr && IsNumber(actual) && EnumKeys.TryGetValue(key, out var type2))
                return string.Equals(eStr, Enum.GetName(type2, Convert.ToInt32(actual, CultureInfo.InvariantCulture)), StringComparison.OrdinalIgnoreCase);
            if (expected is string e) return actual is string a && a == e;
            if (expected is bool eb)
                return actual is bool ab ? ab == eb : IsNumber(actual) && ToDouble(actual) == (eb ? 1 : 0);
            if (actual is bool && IsNumber(expected)) return Same(key, actual, expected);
            if (!IsNumber(expected) || !IsNumber(actual)) return false;
            return Math.Abs(ToDouble(expected) - ToDouble(actual)) < 1e-4;
        }

        static bool IsNumber(object v) => v is sbyte || v is byte || v is short || v is ushort || v is int || v is uint || v is long || v is ulong || v is float || v is double;
        static double ToDouble(object v) => Convert.ToDouble(v, CultureInfo.InvariantCulture);

        static string Short(string key) => key.StartsWith("com.bugsee.option.", StringComparison.Ordinal) ? key.Substring("com.bugsee.option.".Length) : key;

        static string Describe(Task<bool> task)
        {
            if (task == null) return "task=(none)";
            if (task.IsFaulted) return "faulted: " + task.Exception.GetBaseException().Message;
            return task.IsCompleted ? "result=" + task.Result : "result=(pending)";
        }

        static IEnumerator WaitFor(Func<bool> condition, float seconds)
        {
            var until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                if (condition()) yield break;
                yield return null;
            }
        }

        static void Check(string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            Debug.Log("[Sample] presets " + (ok ? "PASS " : "FAIL ") + name + " :: " + detail);
        }
    }
}
