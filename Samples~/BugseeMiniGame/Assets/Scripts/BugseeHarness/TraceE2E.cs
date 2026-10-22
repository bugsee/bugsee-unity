using System.Collections.Generic;
using UnityEngine;
using Bugsee.WrapperPolicy;

namespace Bugsee.Sample
{
    /// <summary>
    /// Device check for Trace values (BUGSEE_E2E_SCENARIO=trace, issue #147). Each value goes through the real
    /// <c>Bugsee.Trace</c>, and <see cref="TraceProbe"/> reports what the platform bridge handed to the native SDK:
    /// on iOS the JSON string, on Android the boxed Java value (<c>class:text</c>). A map, a list and an array are the same JSON
    /// on both (a structure on iOS, a string on Android); a list too long for the cap keeps whole leading entries and stays valid
    /// JSON, with no lone surrogate when an emoji sits at the cap. The SDK itself cannot be read back from C#.
    /// </summary>
    static class TraceE2E
    {
        static int _failures;
        static bool _ios;

        public static void Run()
        {
            _failures = 0;
            _ios = Application.platform == RuntimePlatform.IPhonePlayer;
            Debug.Log("[Sample] trace e2e start launched=" + Bugsee.IsLaunched + " platform=" + Application.platform);
            if (!Bugsee.IsLaunched) Check("SDK launched", false, "status=" + Bugsee.Status);
            TraceProbe.Enabled = true;
            try
            {
                Expect("map", new Dictionary<string, object> { { "k", 1 }, { "n", new List<object> { true, "x" } } },
                    "{\"k\":1,\"n\":[true,\"x\"]}", true);
                Expect("list", new List<object> { 1, "a", 2.5 }, "[1,\"a\",2.5]", true);
                Expect("array", new[] { "p", "q" }, "[\"p\",\"q\"]", true);

                var emoji = new List<object> { new string('a', 8185), "\U0001F600\U0001F600" };
                var wire = Send("emoji-at-cap", emoji);
                var json = _ios ? wire : Strip(wire, "java.lang.String:");
                Check("emoji list cut is valid JSON within the cap", json != null && json.Length <= 8192 && json.StartsWith("[\"aaaa")
                      && json.EndsWith("\"]") && !HasLoneSurrogate(json), "len=" + (json?.Length ?? -1));

                var big = new List<object>();
                for (var i = 0; i < 5000; i++) big.Add("abcdefgh");
                wire = Send("big", big);
                json = _ios ? wire : Strip(wire, "java.lang.String:");
                Check("big list keeps 744 whole entries", json != null && json.Length <= 8192 && json.EndsWith("\"abcdefgh\"]")
                      && CountOf(json, "abcdefgh") == 744, "len=" + (json?.Length ?? -1));

                Expect("string", "plain", _ios ? "\"plain\"" : "java.lang.String:plain", false);
                Expect("int", 42, _ios ? "42" : "java.lang.Integer:42", false);
                Expect("float1", 1.0f, _ios ? "1.0" : "java.lang.Double:1.0", false);
                Expect("bytes", new byte[] { 1, 255 }, _ios ? "\"Af8=\"" : "java.lang.String:Af8=", false);
            }
            catch (System.Exception ex)
            {
                Check("scenario completed without an exception", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally { TraceProbe.Enabled = false; }

            Debug.Log("[Sample] trace e2e done failures=" + _failures);
        }

        static string Send(string name, object value)
        {
            TraceProbe.Last = "(not set)";
            Bugsee.Trace("e2e.trace." + name, value);
            var wire = TraceProbe.Last;
            Debug.Log("[TRACE] wire " + name + " " + (wire != null && wire.Length > 120 ? wire.Substring(0, 120) + "... len=" + wire.Length : wire));
            return wire;
        }

        static void Expect(string name, object value, string expected, bool container)
        {
            var wire = Send(name, value);
            var want = container && !_ios ? "java.lang.String:" + expected : expected;
            Check("Trace " + name, wire == want, "got=" + wire + " want=" + want);
        }

        static string Strip(string s, string prefix) => s != null && s.StartsWith(prefix) ? s.Substring(prefix.Length) : null;

        static int CountOf(string s, string part)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(part, i, System.StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
            return n;
        }

        static bool HasLoneSurrogate(string s)
        {
            for (var i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i])) { if (i + 1 >= s.Length || !char.IsLowSurrogate(s[++i])) return true; }
                else if (char.IsLowSurrogate(s[i])) return true;
            }
            return false;
        }

        static void Check(string what, bool ok, string detail)
        {
            if (!ok) _failures++;
            Debug.Log("[Sample] trace " + (ok ? "PASS " : "FAIL ") + what + " :: " + detail);
        }
    }
}
