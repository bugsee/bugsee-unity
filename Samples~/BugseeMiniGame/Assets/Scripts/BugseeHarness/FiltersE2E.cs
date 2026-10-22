using System;
using System.Collections;
using Bugsee.Contracts.Options;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Device check for the sample filters (BUGSEE_E2E_SCENARIO=filters, issue #117): turns the Filters bubble on, sends the
    /// events the Exchange and Log bubbles send, and waits for each filter's counter to move; with the filters off nothing
    /// may be redacted or dropped. Logs <c>[Sample] filters PASS|FAIL name :: detail</c> and a final <c>done failures=N</c>.
    /// The SDK adds events of its own, so the checks use at-least counts.
    /// </summary>
    static class FiltersE2E
    {
        const float WaitSeconds = 15f;
        static int _failures;

        public static IEnumerator Run(BugseeSampleBootstrap b)
        {
            _failures = 0;
            var c = b.Catalog;
            var wasOn = SampleFilters.NetworkRedactEnabled;
            Debug.Log("[Sample] filters e2e start launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status);
            var body = Body(b, c);
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
            try { b.ApplyFilters(wasOn); } catch { }
            Debug.Log("[Sample] filters e2e done failures=" + _failures + " " + SampleFilters.Summary);
        }

        static IEnumerator Body(BugseeSampleBootstrap b, BugseeActionCatalog c)
        {
            var x = c.Interactions;

            // 1. Filters on: every kind of event goes through its filter.
            b.ApplyFilters(true);
            SampleFilters.ResetCounters();
            SampleFilters.BreadcrumbMarker = "e2e"; // the SDK adds breadcrumbs of its own (category "http" for a network event)
            SampleFilters.LogMarker = "e2e filters debug";
            c.Run(BugseeDemoAction.NetworkEvent); // a started and a completed event each
            c.Run(BugseeDemoAction.NetworkEvent);
            x.AddBreadcrumb("e2e", "filters on", "info");
            x.AddBreadcrumb("e2e", "filters on again", "info");
            var androidDebug = LogAndroidDebug("e2e filters debug");
            // Bugsee.Log(message, level) skips the log filter on both platforms (the native default); Bugsee.Log(message, level, true)
            // goes through it (#190). Both lines are Debug, which the sample's filter drops. The log filter also sees the captured
            // logs: the Unity log, and on Android the logcat line written above. Which Debug lines it drops elsewhere depends on what
            // the SDK happens to log, so only Android has a deterministic one.
            Bugsee.Log(SampleFilters.LogDefaultText, LogLevel.Debug);
            Bugsee.Log(SampleFilters.LogOptInText, LogLevel.Debug, true);
            yield return WaitFor(() => SampleFilters.NetworkRedacted >= 4 && SampleFilters.BreadcrumbMarkerDropped >= 2
                && SampleFilters.LogOptInDropped >= 1 && SampleFilters.LogSeen >= 1 && (!androidDebug || SampleFilters.LogMarkerDropped >= 1), WaitSeconds);
            yield return new WaitForSeconds(1f); // a line that wrongly reached the filter would have by now
            Check("Log(requiresFiltering: true) reaches the log filter", SampleFilters.LogOptInSeen >= 1, SampleFilters.Summary + " optin seen=" + SampleFilters.LogOptInSeen);
            Check("Log(requiresFiltering: true) is dropped by the filter", SampleFilters.LogOptInDropped >= 1, SampleFilters.Summary + " optin dropped=" + SampleFilters.LogOptInDropped);
            Check("Log without requiresFiltering skips the log filter", SampleFilters.LogDefaultSeen == 0, SampleFilters.Summary + " default seen=" + SampleFilters.LogDefaultSeen);
            Check("network filter saw all four events", SampleFilters.NetworkSeen >= 4, SampleFilters.Summary);
            Check("network filter redacted them", SampleFilters.NetworkRedacted >= 4, SampleFilters.Summary);
            Check("breadcrumb filter dropped both of the scenario's breadcrumbs", SampleFilters.BreadcrumbMarkerDropped >= 2,
                SampleFilters.Summary + " marker=" + SampleFilters.BreadcrumbMarkerDropped);
            Check("log filter ran", SampleFilters.LogSeen >= 1, SampleFilters.Summary);
            if (androidDebug)
                Check("log filter dropped the Debug logcat line written by the scenario", SampleFilters.LogMarkerDropped >= 1,
                    SampleFilters.Summary + " last dropped=" + SampleFilters.LastLogDropped);
            else
                Debug.Log("[Sample] filters INFO log drops on this platform depend on Debug lines the SDK captures: " + SampleFilters.Summary + " last dropped=" + SampleFilters.LastLogDropped);
            Check("the HUD line shows the counters", b.FilterLine.StartsWith("flt on n"), b.FilterLine);
            SampleFilters.BreadcrumbMarker = null;
            SampleFilters.LogMarker = null;

            // 2. Filters off: the SDK must stop calling the filters at all (the sample's own flags are cleared too, so redacted and
            // dropped staying 0 would prove nothing; the seen counters do). Events already in flight may still reach an old filter, so
            // the counters are reset after a settle period.
            b.ApplyFilters(false);
            yield return new WaitForSeconds(2f);
            SampleFilters.ResetCounters();
            c.Run(BugseeDemoAction.NetworkEvent);
            x.AddBreadcrumb("e2e", "filters off", "info");
            LogAndroidDebug("e2e filters off debug");
            Bugsee.Log(SampleFilters.LogOptInText, LogLevel.Debug, true); // the opt-in path must not call a removed filter either
            yield return new WaitForSeconds(3f);
            Check("with the filters off the SDK calls no filter", SampleFilters.NetworkSeen == 0 && SampleFilters.BreadcrumbSeen == 0 && SampleFilters.LogSeen == 0,
                SampleFilters.Summary);
            Check("with the filters off nothing is redacted or dropped",
                SampleFilters.NetworkRedacted == 0 && SampleFilters.BreadcrumbDropped == 0 && SampleFilters.LogDropped == 0, SampleFilters.Summary);

            // 3. Filters on but nothing dropped or redacted (issue #160): a breadcrumb the filter leaves alone and one whose message it
            // edits go back to the SDK as a result that carries only the touched fields (JsonUtility wrote every untouched null string
            // as ""). The SDK has no read-back, so this checks the filter ran and the result was accepted without an exception.
            b.ApplyFilters(true);
            SampleFilters.NetworkRedactEnabled = false;
            SampleFilters.LogDropDebugEnabled = false;
            SampleFilters.BreadcrumbDropEnabled = false;
            SampleFilters.ResetCounters();
            SampleFilters.BreadcrumbEditMarker = "e2e-edit";
            try
            {
                x.AddBreadcrumb("e2e-pass", "left alone", "info");
                x.AddBreadcrumb("e2e-edit", "to be edited", "info");
                yield return WaitFor(() => SampleFilters.PassSeen >= 1 && SampleFilters.EditApplied >= 1, WaitSeconds);
                Check("breadcrumb filter saw the untouched breadcrumb", SampleFilters.PassSeen >= 1, SampleFilters.Summary + " pass=" + SampleFilters.PassSeen);
                Check("breadcrumb filter edited the message of the marked breadcrumb", SampleFilters.EditApplied >= 1,
                    SampleFilters.Summary + " edited=" + SampleFilters.EditApplied);
            }
            finally
            {
                SampleFilters.BreadcrumbEditMarker = null;
            }
        }

        /// <summary>Writes a Debug line to logcat (Android only), which the SDK captures and passes through the log filter.</summary>
        static bool LogAndroidDebug(string message)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var log = new AndroidJavaClass("android.util.Log"))
                    log.CallStatic<int>("d", "BugseeE2E", message);
                return true;
            }
            catch (Exception ex)
            {
                Debug.Log("[Sample] filters INFO android.util.Log.d failed: " + ex.Message);
            }
#endif
            return false;
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
            Debug.Log("[Sample] filters " + (ok ? "PASS " : "FAIL ") + name + " :: " + detail);
        }
    }
}
