using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Device check for the read APIs (BUGSEE_E2E_SCENARIO=readapis, issue #100): <c>GetHostLaunchOptions</c>,
    /// <c>GetAllAttributes</c> and <c>GetAllSecureRectangles</c>. It relaunches with a few known options and compares the host set with
    /// what was sent (and with <c>GetLaunchOptions</c>, which also holds SDK defaults), sets attributes of every type and compares the
    /// snapshot with them and with <c>GetAttribute</c>, and adds and removes secure rectangles. Logs
    /// <c>[Sample] readapis PASS|FAIL name :: detail</c> and a final <c>done failures=N</c>.
    /// </summary>
    static class ReadApisE2E
    {
        const float WaitSeconds = 40f;
        static int _failures;

        public static IEnumerator Run(BugseeSampleBootstrap b, string appToken)
        {
            _failures = 0;
            var original = b.LaunchPreset;
            Debug.Log("[Sample] readapis e2e start launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status);
            var body = Body(b, appToken);
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

            try { Bugsee.ClearAllAttributes(); Bugsee.RemoveAllSecureRectangles(); } catch { }
            Task<bool> restore = null;
            try { restore = b.RelaunchWithPreset(original); }
            catch (Exception ex) { Debug.Log("[Sample] readapis restore threw " + ex.Message); }
            yield return AwaitLaunched(restore);
            Check("restore relaunch succeeds", restore != null && restore.IsCompleted && !restore.IsFaulted && restore.Result && Bugsee.IsLaunched,
                "launched=" + Bugsee.IsLaunched);
            Debug.Log("[Sample] readapis e2e done failures=" + _failures + " launched=" + Bugsee.IsLaunched);
        }

        static IEnumerator Body(BugseeSampleBootstrap b, string appToken)
        {
            // 1. Host launch options: only what the app passed, replaced (not merged) by the next relaunch.
            var kept = new Dictionary<string, object>();
            foreach (var key in new[] { InternalOptionKeys.Endpoint, InternalOptionKeys.Debug, Options.CaptureVideo, Options.CaptureVideoMode })
                if (b.LastLaunchOptions != null && b.LastLaunchOptions.TryGetValue(key, out var v)) kept[key] = v;

            var first = new Dictionary<string, object>(kept)
            {
                [Options.CaptureBreadcrumbs] = false,
                [Options.CaptureNetwork] = false,
                [Options.CaptureNetworkBodySizeLimit] = 4321,
            };
            var task = Bugsee.RelaunchAsync(first);
            yield return AwaitLaunched(task);
            Check("relaunch with a known option set is accepted", task.IsCompleted && task.Result && Bugsee.IsLaunched, Describe(task));
            var host = Bugsee.GetHostLaunchOptions();
            var all = Bugsee.GetLaunchOptions();
            Check("host options are never null", host != null, host == null ? "null" : host.Count + " keys");
            bool ok = host != null;
            var wrong = new List<string>();
            foreach (var pair in first)
            {
                if (pair.Key.Contains("$$")) { ok &= !host.ContainsKey(pair.Key); continue; }
                // The video options are only sent to keep the sample running: the video mode goes as a number and comes back as its
                // name, and on iOS an option equal to its default (CaptureVideo true) is dropped from the host options.
                if (pair.Key == Options.CaptureVideoMode || pair.Key == Options.CaptureVideo) continue;
                bool same = host.TryGetValue(pair.Key, out var got) && Same(pair.Value, got);
                if (!same) wrong.Add(pair.Key + " sent " + pair.Value + " (" + pair.Value?.GetType().Name + ") got " + (got == null ? "(none)" : got + " (" + got.GetType().Name + ")"));
                ok &= same;
            }
            Check("host options hold every option sent (internal $$ keys left out)", ok, ok ? Dump(host) : string.Join("; ", wrong));
            Check("host options hold only what was sent", host != null && host.Keys.All(k => first.ContainsKey(k)), Dump(host));
            Check("the effective options hold more than the host options (the SDK defaults)", all != null && host != null && all.Count > host.Count,
                (all == null ? -1 : all.Count) + " vs " + (host == null ? -1 : host.Count));
            bool agree = host != null && all != null;
            if (agree) foreach (var pair in host) agree &= all.TryGetValue(pair.Key, out var av) && Same(pair.Value, av);
            Check("every host option reads the same in the effective options", agree, "");

            var second = new Dictionary<string, object>(kept) { [Options.CaptureBreadcrumbs] = true };
            task = Bugsee.RelaunchAsync(second);
            yield return AwaitLaunched(task);
            host = Bugsee.GetHostLaunchOptions();
            Check("the next relaunch replaces the host options, it does not merge",
                task.IsCompleted && task.Result && host.ContainsKey(Options.CaptureBreadcrumbs) && !host.ContainsKey(Options.CaptureNetwork)
                && !host.ContainsKey(Options.CaptureNetworkBodySizeLimit), Dump(host));
            try { ((IDictionary<string, object>)host).Clear(); Check("the host snapshot is read-only", false, "Clear succeeded"); }
            catch (NotSupportedException) { Check("the host snapshot is read-only", true, ""); }

            // After Stop the last applied configuration is still described.
            var stopped = new TaskCompletionSource<bool>();
            Bugsee.Stop(() => stopped.TrySetResult(true));
            yield return WaitFor(() => stopped.Task.IsCompleted, WaitSeconds);
            host = Bugsee.GetHostLaunchOptions();
            Check("after Stop the host options still describe the last configuration", stopped.Task.IsCompleted && host.ContainsKey(Options.CaptureBreadcrumbs), Dump(host));

            // A fresh Launch after the Stop (a Relaunch of a stopped SDK is not a launch), with the same options as before.
            Bugsee.Launch(appToken, second);
            yield return WaitFor(() => Bugsee.IsLaunched && Bugsee.Status == BugseeStatus.Launched, WaitSeconds);
            yield return null;

            // A relaunch with no options replaces the set with an empty one (the wrapper's own unity.* keys aside, iOS keeps them).
            task = Bugsee.RelaunchAsync(null);
            yield return AwaitLaunched(task);
            host = Bugsee.GetHostLaunchOptions();
            Check("a relaunch with no options leaves the host options empty",
                task.IsCompleted && task.Result && host.Keys.All(k => k.StartsWith("com.bugsee.option.unity.")), Dump(host));

            // 2. Attributes.
            Bugsee.ClearAllAttributes();
            Check("no attributes: an empty, non-null snapshot", Bugsee.GetAllAttributes() != null && Bugsee.GetAllAttributes().Count == 0,
                Dump(Bugsee.GetAllAttributes()));
            var set = new Dictionary<string, object>
            {
                ["ra_string"] = "hello",
                ["ra_numeric_text"] = "42",
                ["ra_int"] = 7,
                ["ra_long"] = 5000000000L,
                ["ra_double"] = 2.5,
                ["ra_float"] = 1.1f,
                ["ra_bool"] = true,
            };
            foreach (var pair in set) Bugsee.SetAttribute(pair.Key, pair.Value);
            yield return null;
            var attrs = Bugsee.GetAllAttributes();
            Check("every attribute set is in the snapshot with its value", set.All(p => attrs.TryGetValue(p.Key, out var g) && Same(p.Value, g)) && attrs.Count == set.Count, Dump(attrs));
            Check("string stays a string, bool a bool, number a long or double",
                attrs["ra_string"] is string && attrs["ra_numeric_text"] is string && attrs["ra_bool"] is bool
                && attrs["ra_int"] is long && attrs["ra_long"] is long && attrs["ra_double"] is double && attrs["ra_float"] is double,
                string.Join(",", attrs.Select(p => p.Key + ":" + (p.Value == null ? "null" : p.Value.GetType().Name))));
            // Not Same(): the type and the exact value must be the ones GetAttribute returns (int vs long, 1.1f as 1.1 and not 1.100000023841858).
            Check("each snapshot value has the type and value GetAttribute returns",
                attrs.All(p => { var one = Bugsee.GetAttribute(p.Key); return one != null && one.GetType() == p.Value.GetType() && one.Equals(p.Value); }),
                string.Join(",", attrs.Select(p => p.Key + "=" + p.Value + ":" + Bugsee.GetAttribute(p.Key))));
            Check("a float attribute reads as the double 1.1 (the shortest text, as on iOS)", attrs["ra_float"] is double d && d == 1.1, "" + attrs["ra_float"]);
            try { ((IDictionary<string, object>)attrs).Clear(); Check("the attribute snapshot is read-only", false, "Clear succeeded"); }
            catch (NotSupportedException) { Check("the attribute snapshot is read-only", true, ""); }
            Bugsee.ClearAttribute("ra_string");
            attrs = Bugsee.GetAllAttributes();
            Check("a cleared attribute leaves the snapshot", !attrs.ContainsKey("ra_string") && attrs.Count == set.Count - 1, Dump(attrs));
            var before = attrs;
            Bugsee.SetAttribute("ra_late", "x");
            Check("an earlier snapshot does not change", !before.ContainsKey("ra_late") && Bugsee.GetAllAttributes().ContainsKey("ra_late"), "");
            Bugsee.ClearAllAttributes();
            Check("ClearAllAttributes empties the snapshot", Bugsee.GetAllAttributes().Count == 0, Dump(Bugsee.GetAllAttributes()));

            // 3. Secure rectangles (answered by the wrapper's registry).
            Bugsee.RemoveAllSecureRectangles();
            Check("no rectangles: an empty array", Bugsee.GetAllSecureRectangles().Length == 0, "");
            var r1 = new RectInt(10, 20, 100, 50);
            var r2 = new RectInt(300, 400, 64, 64);
            Bugsee.AddSecureRectangle(r1);
            Bugsee.AddSecureRectangle(r2);
            Bugsee.AddSecureRectangle(r1);
            var rects = Bugsee.GetAllSecureRectangles();
            Check("both rectangles read back, a rectangle added twice once", rects.Length == 2 && rects.Contains(r1) && rects.Contains(r2), Dump(rects));
            Bugsee.RemoveSecureRectangle(r1);
            Check("a rectangle added twice survives one removal", Bugsee.GetAllSecureRectangles().Contains(r1), Dump(Bugsee.GetAllSecureRectangles()));
            Bugsee.RemoveSecureRectangle(r1);
            rects = Bugsee.GetAllSecureRectangles();
            Check("a rectangle leaves the list after as many removals as adds", rects.Length == 1 && rects[0].Equals(r2), Dump(rects));
            var screen = new Rect(5, 5, 40, 40);
            Bugsee.AddSecureScreenRectangle(screen);
            yield return null;
            yield return null;
            Check("a screen-space rectangle is not returned", Bugsee.GetAllSecureRectangles().Length == 1, Dump(Bugsee.GetAllSecureRectangles()));
            Bugsee.RemoveSecureScreenRectangle(screen);
            yield return null;
            Bugsee.RemoveAllSecureRectangles();
            Check("RemoveAllSecureRectangles empties the list", Bugsee.GetAllSecureRectangles().Length == 0, "");
        }

        static bool Same(object expected, object actual)
        {
            if (expected == null || actual == null) return expected == actual;
            if (expected is Enum en) return actual is string name ? string.Equals(name, en.ToString(), StringComparison.OrdinalIgnoreCase) : IsNumber(actual) && Convert.ToInt64(actual) == Convert.ToInt64(en);
            if (expected is string s) return actual is string a && a == s;
            if (expected is bool eb) return actual is bool ab ? ab == eb : IsNumber(actual) && Convert.ToDouble(actual) == (eb ? 1 : 0);
            if (!IsNumber(expected) || !IsNumber(actual)) return false;
            return Math.Abs(Convert.ToDouble(expected) - Convert.ToDouble(actual)) < 1e-4;
        }

        static bool IsNumber(object o) => o is sbyte || o is byte || o is short || o is ushort || o is int || o is uint || o is long || o is ulong || o is float || o is double;

        static string Dump(IReadOnlyDictionary<string, object> map) =>
            map == null ? "null" : string.Join(", ", map.Select(p => p.Key.Replace("com.bugsee.option.", "") + "=" + p.Value));

        static string Dump(RectInt[] rects) => string.Join(" ", rects.Select(r => r.ToString()));

        static string Describe(Task<bool> task) =>
            task.IsFaulted ? "faulted: " + task.Exception.GetBaseException().Message : task.IsCompleted ? "result=" + task.Result : "result=(pending)";

        static IEnumerator AwaitLaunched(Task<bool> task)
        {
            yield return WaitFor(() => task == null || task.IsCompleted, WaitSeconds);
            yield return WaitFor(() => Bugsee.IsLaunched && Bugsee.Status == BugseeStatus.Launched, WaitSeconds);
            yield return null;
            yield return null;
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
            Debug.Log("[Sample] readapis " + (ok ? "PASS " : "FAIL ") + name + " :: " + detail);
        }
    }
}
