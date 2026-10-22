using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Bugsee.Sample
{
    /// <summary>
    /// Device check for the Android lifecycle (BUGSEE_E2E_SCENARIO=lifecycle, issue #176). Android only: it does nothing elsewhere.
    ///
    /// Part 1, "RAW": calls the native SDK's <c>Bugsee.relaunch / stop / launch</c> directly (bypassing the wrapper's lifecycle
    /// machine) and logs what the SDK itself answers, so the same scenario run against a build with SDK 7.4.1 and one with 7.4.2
    /// shows what changed: <c>[Sample] lifecycle RAW name :: ...</c> lines. They are information; PASS/FAIL is judged only when the
    /// build pins SDK 7.4.2 (the version the wrapper models).
    ///
    /// Part 2: the same sequences through the public facade (the lifecycle machine), judged on a 7.4.2 build:
    /// <c>[Sample] lifecycle PASS|FAIL name :: detail</c>, ending with <c>lifecycle e2e done failures=N</c>.
    /// </summary>
    static class LifecycleE2E
    {
        const float WaitSeconds = 40f;
        const float QuietSeconds = 6f; // long enough for a relaunch to finish, or for a never-coming callback to be missed
        static int _failures;

        // The version of the SDK that is really running (its BuildConfig), not the package's pin: a project can declare another.
        static string RunningSdkVersion()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var config = new AndroidJavaClass("com.bugsee.library.BuildConfig"))
                    return config.GetStatic<string>("VERSION_NAME");
            }
            catch (Exception ex) { Debug.Log("[Sample] lifecycle could not read the SDK version: " + ex.Message); }
#endif
            return BugseePackageVersion.AndroidSdkVersion;
        }
        static bool _judge;
        static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static IEnumerator Run(BugseeSampleBootstrap b)
        {
            _failures = 0;
            var sdkVersion = RunningSdkVersion();
            _judge = sdkVersion == "7.4.2";
            Debug.Log("[Sample] lifecycle e2e start sdk=" + sdkVersion + " (package pin " + BugseePackageVersion.AndroidSdkVersion + ") judged=" + _judge
                + " launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status);
#if UNITY_ANDROID && !UNITY_EDITOR
            var body = Body(b);
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
#else
            Debug.Log("[Sample] lifecycle SKIP :: Android only");
            yield break;
#endif
            Debug.Log("[Sample] lifecycle e2e done failures=" + _failures + " launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status);
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        sealed class BoolCallback : AndroidJavaProxy
        {
            readonly Action<bool> _on;
            public BoolCallback(Action<bool> on) : base("com.bugsee.library.contracts.common.Callback1") { _on = on; }
            public void run(object value)
            {
                var ok = value is bool flag ? flag : value is AndroidJavaObject o && o.Call<bool>("booleanValue");
                _on(ok);
            }
        }

        sealed class Runner : AndroidJavaProxy
        {
            readonly Action _on;
            public Runner(Action on) : base("java.lang.Runnable") { _on = on; }
            public void run() => _on();
        }

        // What the native SDK answered, in call order, with the time since the scenario started and the thread.
        sealed class Answers
        {
            public readonly List<string> Log = new List<string>();
            readonly object _lock = new object();
            public void Add(string what)
            {
                lock (_lock) Log.Add(what);
                Debug.Log("[Sample] lifecycle RAW   event " + what + " t=" + Clock.ElapsedMilliseconds + "ms");
            }
            public int Count(string prefix) { lock (_lock) return Log.FindAll(l => l.StartsWith(prefix)).Count; }
            public string Joined() { lock (_lock) return string.Join(",", Log); }
        }

        static AndroidJavaObject JavaOptions(int duration)
        {
            var map = new AndroidJavaObject("java.util.HashMap");
            using (var boxed = new AndroidJavaObject("java.lang.Integer", duration))
                map.Call<AndroidJavaObject>("put", Options.Duration, boxed)?.Dispose(); // put returns null for a new key
            return map;
        }

        static void RawRelaunch(AndroidJavaClass sdk, Answers a, string tag, int duration)
        {
            using (var map = JavaOptions(duration))
                sdk.CallStatic("relaunch", map, new BoolCallback(ok => a.Add("relaunch:" + tag + "=" + ok)));
        }

        static void RawStop(AndroidJavaClass sdk, Answers a, string tag) =>
            sdk.CallStatic("stop", new Runner(() => a.Add("stop:" + tag)));

        static void RawLaunch(AndroidJavaClass sdk, Answers a, string tag, string token, int duration)
        {
            using (var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unity.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var map = JavaOptions(duration))
                sdk.CallStatic("launch", activity, token, map, new BoolCallback(ok => a.Add("launch:" + tag + "=" + ok)));
        }

        static int ReadDuration()
        {
            var o = Bugsee.GetLaunchOptions();
            return o != null && o.TryGetValue(Options.Duration, out var v) && v != null
                && int.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), out var d) ? d : -1;
        }

        // The facade (and so the machine) brings the SDK to a known launched state between sequences.
        static IEnumerator Reset(BugseeSampleBootstrap b, string name)
        {
            var stop = Bugsee.StopAsync();
            yield return WaitFor(() => stop.IsCompleted, WaitSeconds);
            yield return WaitFor(() => Bugsee.Status == BugseeStatus.Stopped, WaitSeconds);
            var launch = Bugsee.LaunchAsync(b.AppToken, b.LastLaunchOptions);
            yield return WaitFor(() => launch.IsCompleted, WaitSeconds);
            yield return WaitFor(() => Bugsee.Status == BugseeStatus.Launched, WaitSeconds);
            Debug.Log("[Sample] lifecycle RAW   reset before " + name + " status=" + Bugsee.Status);
        }

        static IEnumerator Body(BugseeSampleBootstrap b)
        {
            yield return WaitFor(() => Bugsee.Status == BugseeStatus.Launched, WaitSeconds);
            Check("SDK launched at start", Bugsee.Status == BugseeStatus.Launched, "status=" + Bugsee.Status);
            if (Bugsee.Status != BugseeStatus.Launched) yield break;
            // A Duration the sample never uses, so the options a relaunch applied can be told apart.
            const int DurA = 41, DurB = 53;

            // ---- Part 1: the native SDK's own answers -------------------------------------------------------------
            using (var sdk = new AndroidJavaClass("com.bugsee.library.Bugsee"))
            {
                // 1. relaunch() refused because the SDK is stopped.
                yield return Reset(b, "refused");
                var a = new Answers();
                RawStop(sdk, a, "s1");
                yield return WaitFor(() => Bugsee.Status == BugseeStatus.Stopped, WaitSeconds);
                RawRelaunch(sdk, a, "r1", DurA);
                yield return new WaitForSeconds(QuietSeconds);
                Raw("refused relaunch (SDK stopped)", a, "relaunch calls back false", a.Joined().Contains("relaunch:r1=False"));

                // 2. two relaunches in a row.
                yield return Reset(b, "double");
                a = new Answers();
                RawRelaunch(sdk, a, "r1", DurA);
                RawRelaunch(sdk, a, "r2", DurB);
                yield return WaitFor(() => a.Count("relaunch:") >= 2 && Bugsee.Status == BugseeStatus.Launched, WaitSeconds);
                yield return new WaitForSeconds(QuietSeconds);
                var applied = ReadDuration();
                Debug.Log("[Sample] lifecycle RAW   double relaunch applied duration=" + applied + " (first=" + DurA + " second=" + DurB + ")");
                Raw("double relaunch", a, "both call back, the first one's options apply (7.4.2 joins)",
                    a.Joined().Contains("relaunch:r1=True") && a.Joined().Contains("relaunch:r2=True") && applied == DurA);

                // 3. stop() during the relaunch's stop.
                yield return Reset(b, "stop-in-relaunch");
                a = new Answers();
                RawRelaunch(sdk, a, "r1", DurA);
                RawStop(sdk, a, "s1");
                yield return new WaitForSeconds(QuietSeconds * 2);
                Debug.Log("[Sample] lifecycle RAW   stop during relaunch: final status=" + Bugsee.Status);
                Raw("stop during relaunch", a, "SDK stays stopped, relaunch calls back false",
                    Bugsee.Status == BugseeStatus.Stopped && a.Joined().Contains("relaunch:r1=False") && a.Count("stop:") == 1);

                // 4. launch() during the relaunch's stop.
                yield return Reset(b, "launch-in-relaunch");
                a = new Answers();
                RawRelaunch(sdk, a, "r1", DurA);
                RawLaunch(sdk, a, "l1", b.AppToken, DurB);
                yield return new WaitForSeconds(QuietSeconds * 2);
                Debug.Log("[Sample] lifecycle RAW   launch during relaunch: final status=" + Bugsee.Status + " duration=" + ReadDuration());
                Raw("launch during relaunch's stop", a, "launch refused (false), relaunch completes (true), SDK launched with the relaunch's options",
                    a.Joined().Contains("launch:l1=False") && a.Joined().Contains("relaunch:r1=True")
                    && Bugsee.Status == BugseeStatus.Launched && ReadDuration() == DurA);

                // 5. stop() while the relaunch is already launching (a plain stop). Timing dependent: stop() is issued as soon as
                // the SDK reports Launching.
                yield return Reset(b, "stop-in-launch-half");
                a = new Answers();
                RawRelaunch(sdk, a, "r1", DurA);
                yield return WaitFor(() => Bugsee.Status == BugseeStatus.Launching || Bugsee.Status == BugseeStatus.Launched, WaitSeconds);
                var statusAtStop = Bugsee.Status;
                RawStop(sdk, a, "s1");
                yield return new WaitForSeconds(QuietSeconds * 2);
                Debug.Log("[Sample] lifecycle RAW   stop in the launch half (status when stopping=" + statusAtStop + "): final status=" + Bugsee.Status);
                Raw("stop in the relaunch's launch half", a, "SDK stopped, stop and relaunch both call back",
                    Bugsee.Status == BugseeStatus.Stopped && a.Count("stop:") == 1 && a.Count("relaunch:") == 1);
            }

            // ---- Part 2: through the facade (the lifecycle machine) ------------------------------------------------
            // a. Relaunch refused: the SDK is stopped. Answered at once, not after the 10 s soft deadline.
            yield return Reset(b, "facade");
            var stopTask = Bugsee.StopAsync();
            yield return WaitFor(() => stopTask.IsCompleted, WaitSeconds);
            var started = Clock.ElapsedMilliseconds;
            var refused = Bugsee.RelaunchAsync(OptionSet(b, DurA));
            yield return WaitFor(() => refused.IsCompleted, 12f);
            var tookMs = Clock.ElapsedMilliseconds - started;
            Judged("refused Relaunch completes false at once", refused.IsCompleted && !refused.IsFaulted && !refused.Result && tookMs < 3000,
                Describe(refused) + " took=" + tookMs + "ms");
            Judged("a refused Relaunch leaves the SDK stopped", Bugsee.Status == BugseeStatus.Stopped, "status=" + Bugsee.Status);

            // b. Double Relaunch: joined, the first call's options apply.
            yield return Reset(b, "facade double");
            var r1 = Bugsee.RelaunchAsync(OptionSet(b, DurA));
            var r2 = Bugsee.RelaunchAsync(OptionSet(b, DurB));
            yield return WaitFor(() => r1.IsCompleted && r2.IsCompleted, WaitSeconds);
            yield return WaitFor(() => Bugsee.Status == BugseeStatus.Launched, WaitSeconds);
            yield return new WaitForSeconds(2f);
            Judged("a joined Relaunch completes true like the one it joined", Done(r1, true) && Done(r2, true), Describe(r1) + " / " + Describe(r2));
            Judged("the options of the first Relaunch apply", ReadDuration() == DurA, "duration=" + ReadDuration());

            // c. Stop right after a Relaunch: cancelled, the SDK stays stopped, and the machine does not stop it a second time.
            yield return Reset(b, "facade stop");
            var rc = Bugsee.RelaunchAsync(OptionSet(b, DurA));
            var stopped = false;
            // The completion order (relaunch before the Stop completion) is covered by a unit test: the Task's continuation
            // is asynchronous, so the two cannot be ordered reliably from here.
            Bugsee.Stop(() => stopped = true);
            yield return WaitFor(() => rc.IsCompleted && stopped, WaitSeconds);
            yield return new WaitForSeconds(QuietSeconds);
            Judged("a Relaunch cancelled by Stop completes false", Done(rc, false), Describe(rc));
            Judged("the Stop completion ran", stopped, "");
            Judged("the SDK stays stopped after Stop during Relaunch", Bugsee.Status == BugseeStatus.Stopped, "status=" + Bugsee.Status);

            // d. Launch right after a Relaunch: it waits for the relaunch, which wins.
            yield return Reset(b, "facade launch");
            var rl = Bugsee.RelaunchAsync(OptionSet(b, DurA));
            var ll = Bugsee.LaunchAsync(b.AppToken, OptionSet(b, DurB));
            yield return WaitFor(() => rl.IsCompleted && ll.IsCompleted, WaitSeconds);
            yield return WaitFor(() => Bugsee.Status == BugseeStatus.Launched, WaitSeconds);
            yield return new WaitForSeconds(2f);
            Judged("Relaunch then Launch: both complete true", Done(rl, true) && Done(ll, true), Describe(rl) + " / " + Describe(ll));
            Judged("Relaunch then Launch: the relaunch's options apply (the Launch finds a running SDK)", ReadDuration() == DurA, "duration=" + ReadDuration());

            // e. A Relaunch while a Launch is still launching waits for it and then relaunches.
            yield return Reset(b, "facade launching");
            var st = Bugsee.StopAsync();
            yield return WaitFor(() => st.IsCompleted, WaitSeconds);
            var lg = Bugsee.LaunchAsync(b.AppToken, OptionSet(b, DurB));
            var rg = Bugsee.RelaunchAsync(OptionSet(b, DurA));
            yield return WaitFor(() => lg.IsCompleted && rg.IsCompleted, WaitSeconds);
            yield return WaitFor(() => Bugsee.Status == BugseeStatus.Launched, WaitSeconds);
            yield return new WaitForSeconds(2f);
            Judged("Launch then Relaunch: both complete true", Done(lg, true) && Done(rg, true), Describe(lg) + " / " + Describe(rg));
            Judged("Launch then Relaunch: the relaunch's options apply last", ReadDuration() == DurA, "duration=" + ReadDuration());

            // f. Relaunch, Delete, Relaunch: the Delete runs between them, so the second Relaunch does not join the first; it is
            // refused because the Delete left the SDK stopped.
            yield return Reset(b, "facade delete");
            var d1 = Bugsee.RelaunchAsync(OptionSet(b, DurA));
            Bugsee.DeleteCollectedDataOnDevice();
            var d2 = Bugsee.RelaunchAsync(OptionSet(b, DurB));
            yield return WaitFor(() => d1.IsCompleted && d2.IsCompleted, WaitSeconds);
            yield return new WaitForSeconds(QuietSeconds);
            Judged("Relaunch, Delete, Relaunch: the first completes true, the second false", Done(d1, true) && Done(d2, false),
                Describe(d1) + " / " + Describe(d2));
            Judged("Relaunch, Delete, Relaunch: the SDK is left stopped by the Delete", Bugsee.Status == BugseeStatus.Stopped, "status=" + Bugsee.Status);

            // Leave the sample the way it started.
            yield return Reset(b, "end");
            Check("SDK launched at the end", Bugsee.Status == BugseeStatus.Launched, "status=" + Bugsee.Status);
        }

        static Dictionary<string, object> OptionSet(BugseeSampleBootstrap b, int duration)
        {
            var o = new Dictionary<string, object>(b.LastLaunchOptions) { [Options.Duration] = duration };
            return o;
        }

        static bool Done(Task<bool> t, bool expected) => t.IsCompleted && !t.IsFaulted && t.Result == expected;

        static string Describe(Task<bool> t) =>
            t == null ? "no task" : t.IsFaulted ? "faulted" : t.IsCompleted ? "result=" + t.Result : "pending";

        static void Raw(string name, Answers a, string expectation, bool matches)
        {
            Debug.Log("[Sample] lifecycle RAW " + name + " :: answers=[" + a.Joined() + "] expect(7.4.2)=\"" + expectation + "\" matches=" + matches);
            if (_judge) Check("raw SDK: " + name, matches, "answers=[" + a.Joined() + "] expected: " + expectation);
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
#endif

        // A version-dependent expectation: counted only on the SDK the wrapper models, otherwise logged as information.
        static void Judged(string name, bool ok, string detail)
        {
            if (_judge) Check(name, ok, detail);
            else Debug.Log("[Sample] lifecycle INFO " + name + " :: ok=" + ok + " " + detail);
        }

        static void Check(string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            Debug.Log("[Sample] lifecycle " + (ok ? "PASS " : "FAIL ") + name + " :: " + detail);
        }
    }
}
