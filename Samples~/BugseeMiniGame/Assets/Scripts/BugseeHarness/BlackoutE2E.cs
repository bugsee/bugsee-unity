using System.Collections;
using System.Collections.Generic;
using Bugsee.Contracts.Lifecycle;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Scenario "blackout": logs Bugsee.IsBlackout, tagged "[BLACKOUT]", after StartBlackout, EndBlackout,
    /// StartBlackout + Relaunch, StartBlackout + Stop and a fresh Launch, so it can be compared with what the SDK
    /// reports (IsBlackout reads the SDK on both platforms; it is not a flag kept by the wrapper).
    /// </summary>
    public static class BlackoutE2E
    {
        static void Log(string step) =>
            Debug.Log("[BLACKOUT] step=" + step + " IsBlackout=" + Bugsee.IsBlackout + " status=" + Bugsee.Status);

        static IEnumerator WaitStatus(BugseeStatus wanted, float seconds)
        {
            var until = Time.realtimeSinceStartup + seconds;
            while (Bugsee.Status != wanted && Time.realtimeSinceStartup < until) yield return null;
        }

        public static void Run(MonoBehaviour host, string appToken, Dictionary<string, object> launchOptions) =>
            host.StartCoroutine(Steps(appToken, launchOptions));

        static IEnumerator Steps(string appToken, Dictionary<string, object> launchOptions)
        {
            yield return WaitStatus(BugseeStatus.Launched, 60f);
            Log("0-launched");

            Bugsee.StartBlackout();
            Log("1-after-StartBlackout");
            Bugsee.EndBlackout();
            Log("2-after-EndBlackout");

            Bugsee.StartBlackout();
            Bugsee.Relaunch();
            yield return new WaitForSeconds(1f);
            yield return WaitStatus(BugseeStatus.Launched, 60f);
            Log("3-after-StartBlackout+Relaunch");
            Bugsee.EndBlackout();
            Log("4-after-EndBlackout");

            Bugsee.StartBlackout();
            Log("5-after-StartBlackout");
            Bugsee.Stop();
            yield return WaitStatus(BugseeStatus.Stopped, 60f);
            Log("6-after-StartBlackout+Stop");

            Bugsee.Launch(appToken, launchOptions);
            yield return WaitStatus(BugseeStatus.Launched, 60f);
            Log("7-after-fresh-Launch");
            Bugsee.EndBlackout();
            Log("8-after-EndBlackout");
            Debug.Log("[BLACKOUT] done");
        }
    }
}
