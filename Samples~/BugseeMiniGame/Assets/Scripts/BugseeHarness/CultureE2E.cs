using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Bugsee.Contracts.Exchange;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Device check for issue #143 (BUGSEE_E2E_SCENARIO=culture): runs under a decimal-comma culture (de-DE, then ru-RU) and sends
    /// floating point, decimal and bool values through every bridge path that turns a value into wire text: attributes (read back
    /// with GetAttribute), event parameters, a trace value, LogException fields and breadcrumb filter data. An attribute must read
    /// back as the double 1.5 (not "1,5") and the bool as a bool. What native does with event, trace and exception values is not
    /// readable from C#, so for those paths the run proves the call went through under that culture without an exception; the
    /// wire text itself is covered by Tests~/WrapperPolicy/CultureInvariantWireTests.cs.
    /// Logs <c>[Sample] culture PASS|FAIL name :: detail</c> and a final <c>done failures=N</c>.
    /// </summary>
    static class CultureE2E
    {
        static int _failures;
        static string _crumbData;

        public static IEnumerator Run(BugseeSampleBootstrap b)
        {
            _failures = 0;
            var saved = CultureInfo.CurrentCulture;
            Debug.Log("[Sample] culture e2e start launched=" + Bugsee.IsLaunched + " status=" + Bugsee.Status);
            foreach (var name in new[] { "de-DE", "ru-RU" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture;
                Debug.Log("[Sample] culture using " + name + " (1.5 formats as \"" + 1.5.ToString() + "\", true as \"" + true.ToString() + "\")");
                var body = Body(name);
                while (true)
                {
                    bool more;
                    try { more = body.MoveNext(); }
                    catch (Exception ex)
                    {
                        Check(name, "scenario completed without an exception", false, ex.GetType().Name + ": " + ex.Message);
                        break;
                    }
                    if (!more) break;
                    yield return body.Current;
                }
            }
            CultureInfo.CurrentCulture = saved;
            CultureInfo.DefaultThreadCurrentCulture = null;
            Debug.Log("[Sample] culture e2e done failures=" + _failures);
        }

        static IEnumerator Body(string culture)
        {
            var waited = 0f;
            while (!Bugsee.IsLaunched && waited < 60f) { waited += 0.5f; yield return new WaitForSeconds(0.5f); }

            Bugsee.SetAttribute("c_double", 1.5);
            Bugsee.SetAttribute("c_float", 2.5f);
            Bugsee.SetAttribute("c_decimal", 3.5m);
            Bugsee.SetAttribute("c_bool", true);
            Bugsee.SetAttribute("c_text", "1,5");
            yield return new WaitForSeconds(1f);
            Expect(culture, "double attribute", Bugsee.GetAttribute("c_double"), 1.5);
            Expect(culture, "float attribute", Bugsee.GetAttribute("c_float"), 2.5);
            Expect(culture, "decimal attribute", Bugsee.GetAttribute("c_decimal"), 3.5);
            Expect(culture, "bool attribute", Bugsee.GetAttribute("c_bool"), true);
            Expect(culture, "text attribute is untouched", Bugsee.GetAttribute("c_text"), "1,5");

            _crumbData = null;
            Bugsee.SetBreadcrumbFilter(crumb =>
            {
                if (crumb != null && crumb.Category == "culture-e2e")
                {
                    var data = new Dictionary<string, object> { { "d", 1.5 }, { "m", 2.5m }, { "b", true }, { "u", 7u }, { "t", DateTime.UtcNow } };
                    crumb.Data = data;
                    _crumbData = "seen";
                }
                return crumb;
            });

            var ok = true;
            try
            {
                Bugsee.Event("culture-e2e", new Dictionary<string, object> { { "d", 1.5 }, { "m", 2.5m }, { "b", true }, { "u", 7u }, { "when", DateTime.UtcNow } });
                Bugsee.Trace("culture-trace", 1.5m);
                Bugsee.Trace("culture-trace-d", 1.5);
                Bugsee.LogException(new InvalidOperationException("culture e2e " + culture),
                    new Dictionary<string, object> { { "d", 1.5 }, { "m", 2.5m }, { "b", true } });
                Bugsee.AddBreadcrumb("culture-e2e", "under " + culture, "info");
            }
            catch (Exception ex) { ok = false; Debug.Log("[Sample] culture exception " + ex); }
            Check(culture, "event, trace, exception and breadcrumb calls went through", ok, "no exception");
            yield return new WaitForSeconds(3f);
            Debug.Log("[Sample] culture INFO breadcrumb filter " + (_crumbData ?? "did not run (breadcrumb capture may be off)"));
            Bugsee.SetBreadcrumbFilter(null);
        }

        static void Expect(string culture, string name, object actual, object expected)
        {
            var ok = Equals(actual, expected);
            Check(culture, name, ok, "expected " + expected.GetType().Name + ":" + Convert.ToString(expected, CultureInfo.InvariantCulture) +
                " got " + (actual == null ? "null" : actual.GetType().Name + ":" + Convert.ToString(actual, CultureInfo.InvariantCulture)));
        }

        static void Check(string culture, string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            Debug.Log("[Sample] culture " + (ok ? "PASS " : "FAIL ") + culture + " " + name + " :: " + detail);
        }
    }
}
