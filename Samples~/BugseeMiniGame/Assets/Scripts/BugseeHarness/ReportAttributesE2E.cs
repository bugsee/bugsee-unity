using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Bugsee.Contracts.Reporting;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Scenarios "attrsnoop" and "attrsedit": a report handler installed with SetReportHandler runs on a report whose
    /// attributes include an int, a bool, a double, a string and a numeric-looking string (all set with
    /// Bugsee.SetAttribute). The noop handler touches nothing; the edit handler adds, changes and removes attributes.
    /// Every line is tagged "[ATTRS]" and carries each attribute with its managed type, so the log shows what the
    /// handler received (OnBefore) and what the SDK holds once the handler's result was applied (OnAfter, a second
    /// inspection point that reads the report back from native).
    /// </summary>
    public static class ReportAttributesE2E
    {
        public static void Run(MonoBehaviour host, bool edit, string marker)
        {
            host.StartCoroutine(Routine(edit, marker));
        }

        static IEnumerator Routine(bool edit, string marker)
        {
            // A call made while the SDK is still Launching can be lost: wait for it.
            var waited = 0f;
            while (!Bugsee.IsLaunched && waited < 60f)
            {
                waited += 0.5f;
                yield return new WaitForSeconds(0.5f);
            }
            Debug.Log("[ATTRS] " + (edit ? "edit" : "noop") + " launched=" + Bugsee.IsLaunched + " waited=" + waited.ToString("0.0", CultureInfo.InvariantCulture));

            Bugsee.SetAttribute("a_int", 42);
            Bugsee.SetAttribute("a_bool", true);
            Bugsee.SetAttribute("a_double", 1.5);
            Bugsee.SetAttribute("a_string", "hello");
            Bugsee.SetAttribute("a_numstr", "42");
            Bugsee.SetAttribute("a_whole", 2.0);
            Bugsee.SetReportHandler(new Probe(edit));
            yield return new WaitForSeconds(1f);
            Bugsee.Upload("attrs-" + (edit ? "edit-" : "noop-") + marker, "report attribute kinds, " + (edit ? "edit" : "noop") + " handler");
        }

        static string Describe(IReport report)
        {
            var keys = new List<string>(report.Attributes.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (var key in keys)
            {
                var value = report.Attributes[key];
                sb.Append(' ').Append(key).Append('=')
                    .Append(value == null ? "null" : value.GetType().Name + ":" + (value is System.Collections.IEnumerable && !(value is string) ? Items(value) : Convert.ToString(value, CultureInfo.InvariantCulture)));
            }
            return sb.ToString();
        }

        static string Items(object value)
        {
            var sb = new StringBuilder("[");
            foreach (var item in (System.Collections.IEnumerable)value) sb.Append(item).Append(',');
            return sb.Append(']').ToString();
        }

        static string ContainerCheck(object value, string json)
        {
            if (value is string text) return (text == json ? "PASS" : "FAIL") + " value=" + text;
            var flat = value is IEnumerable ? Flatten(value) : null;
            return (flat == json ? "PASS managed " : "FAIL ") + (value == null ? "null" : value.GetType().Name) + " flat=" + flat;
        }

        // Compact JSON of a managed list or map made of numbers, bools, strings and nested lists or maps.
        static string Flatten(object value)
        {
            if (value is string s) return "\"" + s + "\"";
            if (value is bool b) return b ? "true" : "false";
            if (value is IDictionary d)
            {
                var parts = new List<string>();
                foreach (DictionaryEntry e in d) parts.Add("\"" + e.Key + "\":" + Flatten(e.Value));
                return "{" + string.Join(",", parts.ToArray()) + "}";
            }
            if (value is IEnumerable l)
            {
                var parts = new List<string>();
                foreach (var item in l) parts.Add(Flatten(item));
                return "[" + string.Join(",", parts.ToArray()) + "]";
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        sealed class Probe : ReportHandlerBase
        {
            readonly bool _edit;

            public Probe(bool edit) { _edit = edit; }

            public override void OnBeforeReportCreated(IReport report, bool isTerminating, Action completionCallback)
            {
                Debug.Log("[ATTRS] before-in:" + Describe(report));
                if (_edit)
                {
                    report.SetAttribute("a_int", 43);
                    report.SetAttribute("added_double", 2.5);
                    report.SetAttribute("added_bool", false);
                    report.SetAttribute("added_long", 7L);
                    report.SetAttribute("added_whole", 3.0);
                    report.SetAttribute("added_list", new List<object> { 1, "a" });
                    report.SetAttribute("added_map", new Dictionary<string, object> { { "k", 1 }, { "n", new List<object> { true, "x" } } });
                    report.RemoveAttribute("a_numstr");
                    Debug.Log("[ATTRS] before-out:" + Describe(report));
                }
                completionCallback?.Invoke();
            }

            public override void OnAfterReportCreated(IReport report, bool isTerminating, Action completionCallback)
            {
                Debug.Log("[ATTRS] after:" + Describe(report));
                if (_edit)
                {
                    // Integers read back as long on both platforms. A container set by the handler is stored as JSON text
                    // on Android and as a native structure on iOS (read back as a managed container); either way it
                    // must carry the data, never be a type name.
                    Debug.Log("[ATTRS] check a_int long " + (report.GetAttribute("a_int") is long ? "PASS" : "FAIL"));
                    Debug.Log("[ATTRS] check added_long long " + (report.GetAttribute("added_long") is long ? "PASS" : "FAIL"));
                    Debug.Log("[ATTRS] check added_list json " + ContainerCheck(report.GetAttribute("added_list"), "[1,\"a\"]"));
                    Debug.Log("[ATTRS] check added_map json " + ContainerCheck(report.GetAttribute("added_map"), "{\"k\":1,\"n\":[true,\"x\"]}"));
                }
                completionCallback?.Invoke();
            }
        }
    }
}
