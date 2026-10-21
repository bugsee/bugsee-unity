using System.Collections;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Scenario "videoperm" (tagged "[VIDPERM]"): calls Bugsee.ResetVideoCapturePermission twice and counts the
    /// warning the wrapper logs when the call does nothing. Expected: iOS one warning in total (logged once), Android
    /// none (the SDK acts on the call). Ends with "RESULT PASS" or "RESULT FAIL".
    /// </summary>
    public static class VideoPermissionE2E
    {
        const string Needle = "ResetVideoCapturePermission does nothing";
        static int _warnings;

        static void OnLog(string condition, string stack, LogType type)
        {
            if (type == LogType.Warning && condition != null && condition.Contains(Needle)) _warnings++;
        }

        public static void Run(MonoBehaviour host) => host.StartCoroutine(Steps());

        static IEnumerator Steps()
        {
            for (var i = 0; i < 120 && !Bugsee.IsLaunched; i++) yield return new WaitForSeconds(0.5f);
            Debug.Log("[VIDPERM] launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status);
            _warnings = 0;
            Application.logMessageReceived += OnLog;
            Bugsee.ResetVideoCapturePermission();
            yield return null;
            var afterFirst = _warnings;
            Bugsee.ResetVideoCapturePermission();
            yield return null;
            var afterSecond = _warnings;
            Application.logMessageReceived -= OnLog;
            var expected = Application.platform == RuntimePlatform.IPhonePlayer ? 1 : 0;
            Debug.Log("[VIDPERM] platform=" + Application.platform + " warnings after first=" + afterFirst
                + " after second=" + afterSecond + " expected=" + expected);
            if (expected == 1 && afterFirst == 0 && afterSecond == 0)
                Debug.Log("[VIDPERM] already warned before the scenario started (a Reset call earlier in this process); run it in a fresh process");
            Debug.Log("[VIDPERM] RESULT " + (Bugsee.IsLaunched && afterFirst == expected && afterSecond == expected ? "PASS" : "FAIL"));
        }
    }
}
