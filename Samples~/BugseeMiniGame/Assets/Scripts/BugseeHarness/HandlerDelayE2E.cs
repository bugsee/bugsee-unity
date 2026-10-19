using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Bugsee.Contracts.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Bugsee.Sample
{
    /// <summary>
    /// Report handler for checking what happens when the SDK stops waiting for it (issue #136).
    /// Android intent extras / iOS environment:
    ///   bugsee_handler_mode     = prompt | slow&lt;seconds&gt; | never | filter&lt;seconds&gt;
    ///                             (prompt: completion at once; slowN: after N s; never: the completion is never called;
    ///                              filterN: a log filter that blocks the main thread for N s on a probe line, so its reply is late)
    ///   bugsee_handler_report_s = seconds after launch at which a worker thread calls Bugsee.Upload
    ///                             (so the report is made while Unity's loop may be paused, e.g. the app in the background)
    /// Every step logs "[HandlerE2E] ... t=&lt;seconds since install&gt;" so the log shows when the handler really ran.
    /// </summary>
    sealed class HandlerDelayE2E : ReportHandlerBase
    {
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        readonly MonoBehaviour _host;
        readonly float _delaySeconds;

        HandlerDelayE2E(MonoBehaviour host, float delaySeconds)
        {
            _host = host;
            _delaySeconds = delaySeconds;
        }

        static readonly object LogGate = new object();
        static string _logPath; // set on the main thread: persistentDataPath cannot be read elsewhere

        // iOS has no logcat: the same lines also go to a file in the app's data container (read it with
        // `simctl get_app_container <udid> <bundle id> data`).
        static void Log(string line)
        {
            Debug.Log(line);
            try
            {
                lock (LogGate)
                {
                    if (_logPath != null) System.IO.File.AppendAllText(_logPath, line + "\n");
                }
            }
            catch (Exception) { }
        }

        static string T() => "t=" + Clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);

        public static void Install(MonoBehaviour host, string mode, string reportAfterSeconds)
        {
            _logPath = System.IO.Path.Combine(Application.persistentDataPath, "handler-e2e.log");
            try { System.IO.File.Delete(_logPath); } catch (Exception) { }
            // The wrapper's own warnings ("[Bugsee] ... skipped / abandoned") do not reach the iOS system log from C#.
            Application.logMessageReceivedThreaded += (condition, stackTrace, type) =>
            {
                if (condition == null || !condition.StartsWith("[Bugsee]", StringComparison.Ordinal)) return;
                try { lock (LogGate) System.IO.File.AppendAllText(_logPath, "[wrapper] " + condition + " " + T() + "\n"); }
                catch (Exception) { }
            };
            float delay = 0f;
            if (mode.StartsWith("slow", StringComparison.Ordinal))
                float.TryParse(mode.Substring(4), NumberStyles.Float, CultureInfo.InvariantCulture, out delay);
            else if (mode == "never")
                delay = -1f;
            else if (mode.StartsWith("filter", StringComparison.Ordinal))
            {
                float.TryParse(mode.Substring(6), NumberStyles.Float, CultureInfo.InvariantCulture, out var stall);
                Bugsee.SetLogEventFilter(ev =>
                {
                    if (ev.Message == null || !ev.Message.Contains("FILTERPROBE")) return ev;
                    Log("[HandlerE2E] filter-start " + T() + " blocking " + stall + "s");
                    Thread.Sleep((int)(stall * 1000));
                    ev.Message = "FILTERPROBE-FILTERED " + ev.Message;
                    Log("[HandlerE2E] filter-returning " + T());
                    return ev;
                });
                host.StartCoroutine(Probe());
            }
            Bugsee.SetReportHandler(new HandlerDelayE2E(host, delay));
            Log("[HandlerE2E] installed mode=" + mode + " delay=" + delay + " " + T());

            if (float.TryParse(reportAfterSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var at))
            {
                var touched = Bugsee.IsLaunched; // read only to initialise Bugsee's statics on the main thread, before the worker thread uses them
                new Thread(() =>
                {
                    Thread.Sleep((int)(at * 1000));
#if UNITY_ANDROID && !UNITY_EDITOR
                    AndroidJNI.AttachCurrentThread();
#endif
                    try
                    {
                        Log("[HandlerE2E] upload-called " + T());
                        Bugsee.Upload("handler-delay-e2e", "report made from a worker thread at t+" + at + "s");
                        Log("[HandlerE2E] upload-returned " + T());
                    }
                    catch (Exception ex) { Log("[HandlerE2E] upload threw " + ex); }
#if UNITY_ANDROID && !UNITY_EDITOR
                    finally { AndroidJNI.DetachCurrentThread(); }
#endif
                }) { IsBackground = true }.Start();
            }
        }

        public override void OnBeforeReportCreated(IReport report, bool isTerminating, Action completionCallback) =>
            Run("before", report, completionCallback);

        public override void OnAfterReportCreated(IReport report, bool isTerminating, Action completionCallback) =>
            Run("after", report, completionCallback);

        static IEnumerator Probe()
        {
            yield return new WaitForSeconds(5f);
            Log("[HandlerE2E] probe-line-logged " + T());
            Debug.Log("FILTERPROBE line");
            yield return new WaitForSeconds(2f);
            Debug.Log("FILTERPROBE second line");
        }

        void Run(string phase, IReport report, Action completion)
        {
            Log("[HandlerE2E] " + phase + "-start " + T() + " frame=" + Time.frameCount + " delay=" + _delaySeconds);
            if (phase == "before" && report != null)
                report.Summary = "[HandlerE2E-edit] " + (report.Summary ?? "");
            if (_delaySeconds < 0f)
                return; // never completes
            if (_delaySeconds == 0f)
            {
                completion?.Invoke();
                Log("[HandlerE2E] " + phase + "-done-called " + T());
            }
            else
            {
                _host.StartCoroutine(CompleteLater(phase, completion));
            }
        }

        IEnumerator CompleteLater(string phase, Action completion)
        {
            yield return new WaitForSecondsRealtime(_delaySeconds);
            completion?.Invoke();
            Log("[HandlerE2E] " + phase + "-done-called " + T());
        }
    }
}
