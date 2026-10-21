using System.Collections;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Device check for the managed view hierarchy (BUGSEE_E2E_SCENARIO=vh, issue #111): waits for the SDK to launch, calls
    /// <c>Bugsee.CaptureViewHierarchy()</c> so the native SDK asks the wrapper for <c>requestData("vh")</c>, and counts the
    /// debug-build line the wrapper logs when it answers with a non-null tree ("[Bugsee] managed view hierarchy: N nodes ...").
    /// Logs <c>[Sample] vh PASS|FAIL name :: detail</c> and a final <c>done failures=N</c>. It proves the native SDK asked and the
    /// wrapper replied with a tree; the SDK keeps the reply byte for byte, so what the report viewer shows is not observable here.
    /// Needs a development build (the reply is logged only there).
    /// </summary>
    static class VhE2E
    {
        const string ReplyPrefix = "[Bugsee] managed view hierarchy:";
        static int _replies;
        static int _nodes;
        static int _chars;
        static int _failures;

        public static IEnumerator Run(BugseeSampleBootstrap b)
        {
            _replies = 0; _nodes = 0; _chars = 0; _failures = 0;
            Debug.Log("[Sample] vh e2e start launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status + " dev=" + Debug.isDebugBuild);
            Application.logMessageReceived += OnLog;
            try
            {
                Check("development build (the reply is logged only there)", Debug.isDebugBuild, "isDebugBuild=false");
                for (var i = 0; i < 2; i++)
                {
                    var before = _replies;
                    Bugsee.CaptureViewHierarchy();
                    var waited = 0f;
                    while (_replies == before && waited < 10f) { waited += Time.unscaledDeltaTime; yield return null; }
                    Check("capture " + (i + 1) + " answered with a tree", _replies > before, "replies=" + _replies + " waited=" + waited.ToString("0.0") + "s");
                    yield return new WaitForSecondsRealtime(1f);
                }
                Check("tree has nodes", _nodes > 1, "nodes=" + _nodes);
                Check("tree is non-empty JSON", _chars > 100, "chars=" + _chars);
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
            }

            Debug.Log("[Sample] vh e2e done failures=" + _failures);
        }

        static void OnLog(string condition, string stack, LogType type)
        {
            if (condition == null || !condition.StartsWith(ReplyPrefix)) return;
            _replies++;
            // "...: 1500 nodes, 203441 chars, walk ..."
            var rest = condition.Substring(ReplyPrefix.Length).Trim().Split(' ');
            if (rest.Length > 3)
            {
                int.TryParse(rest[0], out _nodes);
                int.TryParse(rest[2], out _chars);
            }
        }

        static void Check(string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            Debug.Log("[Sample] vh " + (ok ? "PASS " : "FAIL ") + name + (ok ? "" : " :: " + detail));
        }
    }
}
