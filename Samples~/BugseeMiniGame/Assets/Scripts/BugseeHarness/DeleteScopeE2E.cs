using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using Bugsee.Contracts.Reporting;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Device check for <c>Bugsee.DeleteCollectedDataOnDeviceAsync(DeleteScope)</c> (BUGSEE_E2E_SCENARIO=deletescope, issue #101).
    /// For every scope it makes data (log lines, an attribute, a breadcrumb, a draft report, a report whose upload fails because
    /// the SDK is relaunched against a dead endpoint, so its bundle waits for upload), counts the SDK's files on disk by kind
    /// (capture, report snapshots, report bundles), calls the delete and checks the task's result, the SDK's status and what is
    /// left on disk: the narrower scopes keep what the wider ones delete. It also checks the plain
    /// <c>DeleteCollectedDataOnDevice()</c> (still the widest wipe), the synchronous scoped overload, and deletes of two scopes
    /// requested at once (answered in request order, each with its own scope). Logs <c>[Sample] deletescope PASS|FAIL name :: detail</c>
    /// and a final <c>done failures=N</c>.
    /// </summary>
    static class DeleteScopeE2E
    {
        const string DeadEndpoint = "http://127.0.0.1:9";
        static int _failures;

        sealed class Census
        {
            public int Capture, Snapshots, Bundles, Other;
            public override string ToString() => "capture=" + Capture + " snapshots=" + Snapshots + " bundles=" + Bundles + " other=" + Other;
        }

        public static IEnumerator Run(BugseeSampleBootstrap b)
        {
            _failures = 0;
            Debug.Log("[Sample] deletescope e2e start launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status + " root=" + DataRoot());
            var body = Body(b);
            while (true)
            {
                bool more;
                try { more = body.MoveNext(); }
                catch (Exception ex)
                {
                    Check("scenario completed without an exception", false, ex.GetType().Name + ": " + ex);
                    break;
                }
                if (!more) break;
                yield return body.Current;
            }

            // Put the sample's own launch back, whatever happened.
            try { ReleaseHeld(); } catch { }
            if (!Bugsee.IsLaunched)
            {
                var restore = LaunchTask(b, false);
                yield return WaitFor(() => restore.IsCompleted, 40f);
                Check("restore launch succeeds", restore.IsCompleted && !restore.IsFaulted && restore.Result && Bugsee.IsLaunched, Describe(restore) + " status=" + Bugsee.Status);
            }
            Debug.Log("[Sample] deletescope e2e done failures=" + _failures + " launched=" + Bugsee.IsLaunched);
        }

        static IEnumerator Body(BugseeSampleBootstrap b)
        {
            // 0. A scope maps to one wire value; the default of the facade is the widest.
            Check("DeleteScope values", (int)DeleteScope.CaptureOnly == 0 && (int)DeleteScope.CaptureAndIntermediate == 1 && (int)DeleteScope.Everything == 2, "");

            // An undefined scope is refused before anything runs: it must never be read as the widest wipe.
            var statusBefore = Bugsee.Status;
            var refused = false;
            try { Bugsee.DeleteCollectedDataOnDeviceAsync((DeleteScope)5); } catch (ArgumentOutOfRangeException) { refused = true; }
            try { Bugsee.DeleteCollectedDataOnDevice((DeleteScope)(-1)); refused &= false; } catch (ArgumentOutOfRangeException) { }
            Check("an undefined scope throws ArgumentOutOfRangeException and stops nothing", refused && Bugsee.Status == statusBefore, "status=" + Bugsee.Status);

            var scopes = new[] { DeleteScope.CaptureOnly, DeleteScope.CaptureAndIntermediate, DeleteScope.Everything };
            foreach (var scope in scopes)
            {
                var name = scope.ToString();
                Census before = null;
                yield return Prepare(b, c => before = c, true);
                Task<bool> task = null;
                try { task = Bugsee.DeleteCollectedDataOnDeviceAsync(scope); }
                catch (Exception ex) { Check(name + " call did not throw", false, ex.ToString()); continue; }
                yield return WaitFor(() => task.IsCompleted, 40f);
                Check(name + " task completes true", task.IsCompleted && !task.IsFaulted && task.Result, Describe(task));
                yield return WaitFor(() => Bugsee.Status == BugseeStatus.Stopped, 15f);
                Check(name + " leaves the SDK stopped", Bugsee.Status == BugseeStatus.Stopped && !Bugsee.IsLaunched, "status=" + Bugsee.Status);
                var after = Count();
                Debug.Log("[Sample] deletescope " + name + " before: " + before + " | after: " + after);
                CheckEffect(name, scope, before, after);
            }

            // 1. The plain call is still the widest wipe.
            {
                Census before = null;
                yield return Prepare(b, c => before = c, true);
                Bugsee.DeleteCollectedDataOnDevice();
                // The plain call has no result, and the SDK clears its files on a background thread; a delete of the same scope after it is a
                // barrier (it shares the running wipe on iOS and queues behind it on Android), so the census never reads a folder being deleted.
                var barrier = Bugsee.DeleteCollectedDataOnDeviceAsync(DeleteScope.Everything);
                yield return WaitFor(() => barrier.IsCompleted, 60f);
                Check("plain: the barrier delete completes true", barrier.IsCompleted && !barrier.IsFaulted && barrier.Result, Describe(barrier));
                var after = Count();
                Debug.Log("[Sample] deletescope plain before: " + before + " | after: " + after);
                CheckEffect("plain DeleteCollectedDataOnDevice()", DeleteScope.Everything, before, after);
            }

            // 2. The default argument of the Task overload is the widest wipe too.
            {
                Census before = null;
                yield return Prepare(b, c => before = c, true);
                var task = Bugsee.DeleteCollectedDataOnDeviceAsync();
                yield return WaitFor(() => task.IsCompleted, 40f);
                var after = Count();
                Check("default Async task completes true", task.IsCompleted && !task.IsFaulted && task.Result, Describe(task));
                CheckEffect("default Async", DeleteScope.Everything, before, after);
            }

            // 3. The synchronous scoped overload has no result; its effect is the same.
            {
                Census before = null;
                yield return Prepare(b, c => before = c, true);
                Bugsee.DeleteCollectedDataOnDevice(DeleteScope.CaptureAndIntermediate);
                var barrier = Bugsee.DeleteCollectedDataOnDeviceAsync(DeleteScope.CaptureAndIntermediate);
                yield return WaitFor(() => barrier.IsCompleted, 60f);
                Check("sync scoped: the barrier delete completes true", barrier.IsCompleted && !barrier.IsFaulted && barrier.Result, Describe(barrier));
                var after = Count();
                Debug.Log("[Sample] deletescope sync scoped before: " + before + " | after: " + after);
                CheckEffect("sync scoped overload", DeleteScope.CaptureAndIntermediate, before, after);
            }

            // 4. Two deletes of different scopes at once, the SDK running: both answered true, in request order, the narrow one never
            // widened. The order is recorded on the main thread (the continuations run there).
            {
                yield return Prepare(b, null, false);
                var order = new List<string>();
                var main = TaskScheduler.FromCurrentSynchronizationContext();
                var narrow = Bugsee.DeleteCollectedDataOnDeviceAsync(DeleteScope.CaptureOnly);
                var wide = Bugsee.DeleteCollectedDataOnDeviceAsync(DeleteScope.Everything);
                narrow.ContinueWith(t => order.Add("narrow"), main);
                wide.ContinueWith(t => order.Add("wide"), main);
                yield return WaitFor(() => order.Count == 2, 120f);
                Check("two scopes at once: both complete true", narrow.IsCompleted && wide.IsCompleted && !narrow.IsFaulted && !wide.IsFaulted && narrow.Result && wide.Result,
                    "narrow " + Describe(narrow) + ", wide " + Describe(wide));
                Check("two scopes at once: the earlier request is answered first", order.Count == 2 && order[0] == "narrow", string.Join(",", order));
                CheckEffect("two scopes at once", DeleteScope.Everything, null, Count());
            }

            // 5. Two deletes of the same scope at once share one wipe and are both answered.
            {
                yield return Prepare(b, null, false);
                var one = Bugsee.DeleteCollectedDataOnDeviceAsync(DeleteScope.CaptureOnly);
                var two = Bugsee.DeleteCollectedDataOnDeviceAsync(DeleteScope.CaptureOnly);
                yield return WaitFor(() => one.IsCompleted && two.IsCompleted, 60f);
                Check("same scope twice: both complete true", one.IsCompleted && two.IsCompleted && !one.IsFaulted && !two.IsFaulted && one.Result && two.Result,
                    "one " + Describe(one) + ", two " + Describe(two));
            }

            // 6. A launch made right after the call waits for the wipe and the SDK runs again.
            {
                yield return Prepare(b, null, false);
                var del = Bugsee.DeleteCollectedDataOnDeviceAsync(DeleteScope.Everything);
                var launch = LaunchTask(b, false);
                yield return WaitFor(() => del.IsCompleted && launch.IsCompleted, 60f);
                Check("delete then launch: the delete is answered true", del.IsCompleted && !del.IsFaulted && del.Result, Describe(del));
                Check("delete then launch: the launch runs after it", launch.IsCompleted && !launch.IsFaulted && launch.Result && Bugsee.IsLaunched, Describe(launch) + " status=" + Bugsee.Status);
            }
        }

        // The SDK is launched against a dead endpoint, data is made, and the SDK is left running with its files on disk.
        static IEnumerator Prepare(BugseeSampleBootstrap b, Action<Census> done, bool stopForCensus)
        {
            ReleaseHeld();
            if (Bugsee.Status == BugseeStatus.Launched || Bugsee.Status == BugseeStatus.Launching)
            {
                var stop = Bugsee.StopAsync();
                yield return WaitFor(() => stop.IsCompleted, 30f);
            }
            var launch = LaunchTask(b, true);
            yield return WaitFor(() => launch.IsCompleted, 40f);
            Check("prepare: launch against a dead endpoint", launch.IsCompleted && !launch.IsFaulted && launch.Result && Bugsee.IsLaunched, Describe(launch) + " status=" + Bugsee.Status);
            yield return new WaitForSeconds(3f);

            Bugsee.Log("deletescope log line one");
            Bugsee.Log("deletescope log line two", LogLevel.Warning);
            Bugsee.SetAttribute("deletescope", "yes");
            Bugsee.AddBreadcrumb("deletescope", "a breadcrumb", "info");
            var draft = Bugsee.CreateReport(); // report snapshots: set aside, not bundled yet
            Bugsee.SetReportHandler(null);
            Bugsee.Upload("deletescope-report", "a report whose upload fails: its bundle waits for upload");
            // No census while the SDK runs: it creates and removes folders, and il2cpp's directory walk aborts the process when a folder
            // goes away under it (iOS). Fixed waits instead.
            yield return new WaitForSeconds(8f); // the failed upload leaves a bundle
            // A second report is held in the report handler: its snapshot (the data set aside for it) is on disk, but it is not bundled.
            Held.Clear();
            Bugsee.SetReportHandler(new HoldingHandler());
            Bugsee.Upload("deletescope-held", "a report held in the report handler: its snapshot is on disk, not bundled");
            yield return new WaitForSeconds(5f);
            Census census = null;
            if (stopForCensus)
            {
                var stop = Bugsee.StopAsync();
                yield return WaitFor(() => stop.IsCompleted, 30f);
                Check("prepare: the SDK stops before the census", stop.IsCompleted && !stop.IsFaulted && Bugsee.Status == BugseeStatus.Stopped, "status=" + Bugsee.Status);
                yield return new WaitForSeconds(1f);
                census = Count();
                Debug.Log("[Sample] deletescope prepared: " + census + " capture files: " + string.Join(" ; ", Files("capture", 6))
                    + " snapshot files: " + string.Join(" ; ", Files("snapshots", 6)) + " bundle files: " + string.Join(" ; ", Files("bundles", 6)));
            }
            Debug.Log("[Sample] deletescope draft report id=" + (draft != null ? draft.Id : "(null)"));
            done?.Invoke(census);
        }

        // Holds every report in its "before" stage, so the report's snapshot stays on disk without a bundle.
        static readonly List<Action> Held = new List<Action>();

        sealed class HoldingHandler : ReportHandlerBase
        {
            public override void OnBeforeReportCreated(IReport report, bool isTerminating, Action completionCallback)
            {
                lock (Held) Held.Add(completionCallback);
                Debug.Log("[Sample] deletescope handler holds a report");
            }
        }

        static void ReleaseHeld()
        {
            Action[] held;
            lock (Held) { held = Held.ToArray(); Held.Clear(); }
            Bugsee.SetReportHandler(null);
            foreach (var h in held) { try { h?.Invoke(); } catch (Exception ex) { Debug.Log("[Sample] deletescope release threw " + ex.Message); } }
        }

        static Task<bool> LaunchTask(BugseeSampleBootstrap b, bool deadEndpoint)
        {
            var options = b.LastLaunchOptions != null ? new Dictionary<string, object>(b.LastLaunchOptions) : new Dictionary<string, object>();
            if (deadEndpoint) options[InternalOptionKeys.Endpoint] = DeadEndpoint;
            return Bugsee.LaunchAsync(b.AppToken, options);
        }

        static void CheckEffect(string name, DeleteScope scope, Census before, Census after)
        {
            if (before != null)
            {
                Check(name + ": there was capture data to delete", before.Capture > 0, before.ToString());
                Check(name + ": there was a bundle waiting for upload", before.Bundles > 0, before.ToString());
                Check(name + ": there was a report snapshot to delete", before.Snapshots > 0, before.ToString());
            }
            if (after.Capture != 0) Debug.Log("[Sample] deletescope leftover capture files: " + string.Join(" ; ", Files("capture", 12)));
            Check(name + ": the capture is deleted", after.Capture == 0, after.ToString());
            if (scope == DeleteScope.CaptureOnly)
                Check(name + ": snapshots are kept", before != null ? after.Snapshots == before.Snapshots : after.Snapshots > 0, (before != null ? "before " + before.Snapshots + " " : "") + "after " + after.Snapshots);
            else
            {
                if (after.Snapshots != 0) Debug.Log("[Sample] deletescope leftover snapshot files: " + string.Join(" ; ", Files("snapshots", 12)));
                Check(name + ": snapshots are deleted", after.Snapshots == 0, after.ToString());
            }
            if (scope == DeleteScope.Everything)
                Check(name + ": bundles are deleted", after.Bundles == 0, after.ToString());
            else
                Check(name + ": bundles are kept", before != null ? after.Bundles == before.Bundles : after.Bundles > 0, (before != null ? "before " + before.Bundles + " " : "") + "after " + after.Bundles);
        }

        static string DataRoot()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var dir = activity.Call<AndroidJavaObject>("getFilesDir"))
                return dir.Call<string>("getAbsolutePath");
#else
            return Directory.GetParent(Application.persistentDataPath).FullName; // iOS: the app container
#endif
        }

        // Files of the SDK on disk by kind, found by the names of its folders (capture/generations, snapshots, bundles).
        static Census Count()
        {
            var census = new Census();
            var root = DataRoot();
            if (!Directory.Exists(root)) return census;
            foreach (var file in AllFiles(root))
            {
                var relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
                var segments = relative.Split(Path.DirectorySeparatorChar);
                var kind = Kind(segments);
                if (kind == null) continue;
                switch (kind)
                {
                    case "bundles": census.Bundles++; break;
                    case "snapshots": census.Snapshots++; break;
                    case "capture": census.Capture++; break;
                    default: census.Other++; break;
                }
            }
            return census;
        }

        // Every file under root; a folder the app may not read (the iOS container has some) is skipped. Called only while the SDK
        // is not deleting: il2cpp's directory enumeration aborts the process when a folder it reads is removed under it. On Android
        // the walk goes through java.io.File, which tolerates that.
#if UNITY_ANDROID && !UNITY_EDITOR
        static IEnumerable<string> AllFiles(string root)
        {
            var result = new List<string>();
            using (var dir = new AndroidJavaObject("java.io.File", root)) Walk(dir, result);
            return result;
        }

        static void Walk(AndroidJavaObject dir, List<string> result)
        {
            AndroidJavaObject[] children = null;
            try { children = dir.Call<AndroidJavaObject[]>("listFiles"); } catch (Exception) { }
            if (children == null) return;
            foreach (var child in children)
            {
                using (child)
                {
                    if (child.Call<bool>("isDirectory")) Walk(child, result);
                    else result.Add(child.Call<string>("getAbsolutePath"));
                }
            }
        }
#else
        static IEnumerable<string> AllFiles(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                string[] files = null, dirs = null;
                try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
                catch (Exception) { }
                if (files != null) foreach (var f in files) yield return f;
                if (dirs != null) foreach (var d in dirs) pending.Push(d);
            }
        }
#endif

        static List<string> Files(string kind, int max)
        {
            var list = new List<string>();
            var root = DataRoot();
            foreach (var file in AllFiles(root))
            {
                var relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
                if (Kind(relative.Split(Path.DirectorySeparatorChar)) != kind) continue;
                list.Add(relative + " (" + new FileInfo(file).Length + " B)");
                if (list.Count >= max) break;
            }
            return list;
        }

        static string Kind(string[] segments)
        {
            for (var i = 0; i < segments.Length - 1; i++) // the last segment is the file
            {
                var s = segments[i].ToLowerInvariant();
                if (s.StartsWith("unity") || s == "il2cpp" || s == "webview" || s == "cache" || s == "splashboard") return null; // not the SDK's
                if (s == "bundles") return "bundles";
                if (s == "snapshots" || s == "snapshots_shared") return "snapshots";
                // The recorded capture is the generations folder; the capture folder also holds the internal logs and the shared
                // snapshot index, which a delete leaves (they are the SDK's own working files).
                if (s == "generations") return "capture";
                if (s == "bugsee" || s == "detection" || s == "performance" || s == "relay" || s == "logs") return "other";
            }
            return null;
        }

        static string Describe(Task<bool> task)
        {
            if (task == null) return "task=(none)";
            if (task.IsFaulted) return "faulted: " + task.Exception.GetBaseException().Message;
            return task.IsCompleted ? "result=" + task.Result : "result=(pending)";
        }

        // pollSeconds > 0 checks the condition that rarely (a condition that reads the disk must not run all the time).
        static IEnumerator WaitFor(Func<bool> condition, float seconds, float pollSeconds = 0f)
        {
            var until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                if (condition()) yield break;
                if (pollSeconds > 0f) yield return new WaitForSeconds(pollSeconds);
                else yield return null;
            }
        }

        static void Check(string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            Debug.Log("[Sample] deletescope " + (ok ? "PASS " : "FAIL ") + name + " :: " + detail);
        }
    }
}
