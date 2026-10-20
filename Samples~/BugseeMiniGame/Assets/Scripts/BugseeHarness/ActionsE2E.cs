using System.Collections.Generic;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Device check for the action catalog (BUGSEE_E2E_SCENARIO=actions): invokes the same
    /// BugseeActionCatalog.Run items the bubbles call and the station selection path E/Space uses,
    /// logging <c>[Sample] ...</c> lines with PASS/FAIL.
    /// </summary>
    static class ActionsE2E
    {
        static int _failures;

        public static void Run(BugseeSampleBootstrap b)
        {
            var c = b.Catalog;
            _failures = 0;
            Debug.Log("[Sample] actions e2e start launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status);
            var savedKey = BugseeActionCatalog.AttributeKey;
            GameObject hill = null;
            try
            {
                Body(c, out hill);
            }
            catch (System.Exception ex)
            {
                Check("scenario completed without an exception", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (hill != null) Object.Destroy(hill);
                try { Bugsee.EndBlackout(); } catch { }
                BugseeActionCatalog.AttributeKey = savedKey;
            }

            Debug.Log("[Sample] actions e2e done failures=" + _failures);
        }

        static void Body(BugseeActionCatalog c, out GameObject hill)
        {
            hill = null;

            // 1. One attribute key shared by Set / Get / Clear.
            var key = "e2e.key";
            Bugsee.SetAttribute(key, "e2e-value");
            BugseeActionCatalog.AttributeKey = key; // what the Set Attr form does on apply
            c.Run(BugseeDemoAction.GetAttribute);
            Check("GetAttribute after set", c.LastStatus == "GetAttribute " + key + "=e2e-value", c.LastStatus);
            c.Run(BugseeDemoAction.ClearAttribute);
            Check("ClearAttribute cleared the set key", Bugsee.GetAttribute(key) == null, "value=" + Bugsee.GetAttribute(key));

            // 2. Clearing the identity keeps attributes.
            Bugsee.SetAttribute(key, "survives");
            Bugsee.SetUserIdentifier("e2e-user");
            c.Run(BugseeDemoAction.ClearIdentity);
            Check("identity cleared", string.IsNullOrEmpty(Bugsee.GetUserIdentifier()), c.LastStatus);
            var kept = Bugsee.GetAttribute(key); // object: compare by value, not by reference
            Check("attribute survives ClearIdentity", "survives".Equals(kept), "value=" + (kept ?? "(null)"));
            Bugsee.ClearAttribute(key);

            // 3. Pause/Resume alternates and the state is visible (start from a known state).
            Bugsee.EndBlackout();
            c.Run(BugseeDemoAction.ObsoletePauseResume);
            var s1 = c.LastStatus; var bo1 = Bugsee.IsBlackout;
            c.Run(BugseeDemoAction.ObsoletePauseResume);
            var s2 = c.LastStatus; var bo2 = Bugsee.IsBlackout;
            Check("Pause then Resume", bo1 && !bo2 && s1.StartsWith("Pause()") && s2.StartsWith("Resume()"), "bo1=" + bo1 + " bo2=" + bo2 + " :: " + s1 + " | " + s2);

            // 4. Hazard path: counter in the message, status is the LogException result.
            c.Run(BugseeDemoAction.LogException);
            var h1 = c.LastStatus;
            c.Run(BugseeDemoAction.LogException);
            var h2 = c.LastStatus;
            Check("LogException status carries a distinct counter", h1 != h2 && h1.StartsWith("LogException #"), h1 + " | " + h2);

            // 5. E/Space selection through the real station code.
            var go = new GameObject("E2EHill");
            hill = go;
            var st = go.AddComponent<BugseeHillStation>();
            st.Configure(new BugseeActionGroup("E2E", Color.white,
                BugseeDemoAction.GetIdentity, BugseeDemoAction.GetAttribute, BugseeDemoAction.LogException), new List<Transform>());
            Bugsee.SetAttribute(key, "sel");
            st.ActivateSelected();
            var e0 = c.LastStatus;
            st.SelectNext(); st.ActivateSelected();
            var e1 = c.LastStatus;
            st.SelectNext(); st.ActivateSelected();
            var e2 = c.LastStatus;
            st.SelectNext();
            Check("selection runs 1st, 2nd, 3rd action then wraps",
                e0.StartsWith("GetUserIdentifier=") && e1.StartsWith("GetAttribute ") && e2.StartsWith("LogException #") && st.SelectedIndex == 0,
                e0 + " | " + e1 + " | " + e2 + " | idx=" + st.SelectedIndex);
            Bugsee.ClearAttribute(key);
        }

        static void Check(string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            Debug.Log("[Sample] actions " + (ok ? "PASS " : "FAIL ") + name + " :: " + detail);
        }
    }
}
