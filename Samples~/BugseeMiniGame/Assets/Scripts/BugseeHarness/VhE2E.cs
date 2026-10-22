using System.Collections;
using Bugsee.Contracts.Options;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Device check for the managed view hierarchy (BUGSEE_E2E_SCENARIO=vh, issue #111): waits for the SDK to launch, calls
    /// <c>Bugsee.CaptureViewHierarchy()</c> so the native SDK asks the wrapper for <c>requestData("vh")</c>, and counts the
    /// debug-build line the wrapper logs when it builds a non-null tree ("[Bugsee] managed view hierarchy: N nodes ...").
    /// Logs <c>[Sample] vh PASS|FAIL name :: detail</c> and a final <c>done failures=N</c>. It proves the native SDK asked and the
    /// wrapper built a reply within the SDK's 500 ms window; it also adds a secure rectangle over the screen and checks the wrapper's "secured N" count rises, then falls to 0 once it is removed. It does not prove the reply reached a report, and the SDK keeps it
    /// byte for byte, so what the viewer shows is not observable here. Needs a development build (the reply is logged only
    /// there) and a launch with CaptureViewHierarchy on (the presets minimal and privacy turn it off: the scenario then stops).
    /// </summary>
    static class VhE2E
    {
        const string ErrorPrefix = "[Bugsee] The managed view hierarchy could not be read";
        const string ReplyPrefix = "[Bugsee] managed view hierarchy:";
        static int _replies;
        static int _nodes;
        static int _chars;
        static int _secured;
        const int WindowMs = 500; // the native SDK's wait for the reply
        static int _failures;
        static int _slowest; // longest call-to-log time in ms
        static float _calledAt;
        static string _error;

        public static IEnumerator Run(BugseeSampleBootstrap b)
        {
            _replies = 0; _nodes = 0; _chars = 0; _secured = -1; _failures = 0; _slowest = 0; _error = null;
            Debug.Log("[Sample] vh e2e start launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status + " dev=" + Debug.isDebugBuild);
            Application.logMessageReceived += OnLog;
            try
            {
                if (!Check("development build (the reply is logged only there)", Debug.isDebugBuild, "isDebugBuild=false"))
                    yield break;
                object enabled = null;
                Bugsee.GetLaunchOptions()?.TryGetValue(Options.CaptureViewHierarchy, out enabled);
                Debug.Log("[Sample] vh option CaptureViewHierarchy=" + (enabled ?? "(not reported)"));
                if (enabled != null && string.Equals(enabled.ToString(), "false", System.StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log("[Sample] vh SKIP CaptureViewHierarchy is off in this launch (preset minimal or privacy): the SDK never asks");
                    yield break;
                }
                for (var i = 0; i < 2; i++)
                {
                    var before = _replies;
                    _calledAt = Time.realtimeSinceStartup;
                    Bugsee.CaptureViewHierarchy();
                    var waited = 0f;
                    while (_replies == before && waited < 10f) { waited += Time.unscaledDeltaTime; yield return null; }
                    Check("capture " + (i + 1) + " answered with a tree", _replies > before, "replies=" + _replies + (_error != null ? " wrapper error: " + _error : " (no reply logged: the SDK did not ask, or the request was answered null)") + " waited=" + waited.ToString("0.0") + "s");
                    yield return new WaitForSecondsRealtime(1f);
                }
                Check("tree has nodes", _nodes > 1, "nodes=" + _nodes);
                Check("tree is non-empty JSON", _chars > 100, "chars=" + _chars);
                Check("capture call to reply stayed inside the SDK's 500 ms window", _slowest < WindowMs, "slowest=" + _slowest + " ms");
                Check("no node is secured while no secure rectangle is registered", _secured == 0, "secured=" + _secured);

                // Secure rectangle over the whole screen: every named node loses its name and component list (counted by the wrapper's log line).
                var rect = new RectInt(0, 0, Screen.width, Screen.height);
                Bugsee.AddSecureRectangle(rect);
                try
                {
                    yield return null;
                    var before = _replies;
                    Bugsee.CaptureViewHierarchy();
                    var waited = 0f;
                    while (_replies == before && waited < 10f) { waited += Time.unscaledDeltaTime; yield return null; }
                    Check("capture with a secure rectangle answered with a tree", _replies > before, "replies=" + _replies);
                    Check("nodes under the secure rectangle lost their name and components", _secured > 10, "secured=" + _secured);
                }
                finally
                {
                    Bugsee.RemoveSecureRectangle(rect);
                }

                yield return new WaitForSecondsRealtime(1f);
                {
                    var before = _replies;
                    Bugsee.CaptureViewHierarchy();
                    var waited = 0f;
                    while (_replies == before && waited < 10f) { waited += Time.unscaledDeltaTime; yield return null; }
                    Check("capture after removing the rectangle answered with a tree", _replies > before, "replies=" + _replies);
                    Check("nothing is secured once the rectangle is removed", _secured == 0, "secured=" + _secured);
                }
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                Debug.Log("[Sample] vh e2e done failures=" + _failures);
            }
        }

        static void OnLog(string condition, string stack, LogType type)
        {
            // Any tree built while this runs counts, also one for an SDK-initiated snapshot (unlikely in this idle scenario).
            if (condition != null && condition.StartsWith(ErrorPrefix)) { _error = condition; return; }
            if (condition == null || !condition.StartsWith(ReplyPrefix)) return;
            _replies++;
            // "...: 1500 nodes, 203441 chars, walk ..."
            var rest = condition.Substring(ReplyPrefix.Length).Trim().Split(' ');
            if (rest.Length > 3)
            {
                int.TryParse(rest[0], out _nodes);
                int.TryParse(rest[2], out _chars);
            }
            var at = condition.IndexOf(", secured ", System.StringComparison.Ordinal);
            if (at >= 0) int.TryParse(condition.Substring(at + 10).Trim(), out _secured);
            // Call to log, in ms: includes the main-thread queue delay and is a little long (the line is logged the frame after the reply).
            _slowest = System.Math.Max(_slowest, (int)((Time.realtimeSinceStartup - _calledAt) * 1000f));
        }

        static bool Check(string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            Debug.Log("[Sample] vh " + (ok ? "PASS " : "FAIL ") + name + (ok ? "" : " :: " + detail));
            return ok;
        }
    }
}
