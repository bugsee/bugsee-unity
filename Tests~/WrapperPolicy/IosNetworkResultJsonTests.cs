using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    // Issue 131: Unity's JsonUtility writes a null string as "", so serializing a whole network event gave every
    // filtered request an empty error, body and url. The result now carries only what the filter set.
    public class IosNetworkResultJsonTests
    {
        static IosNetworkFields Clean() => new IosNetworkFields
        {
            Url = "https://a.test/x",
            Body = "",
            Size = 12,
            ResponseCode = 200,
            Headers = new Dictionary<string, string> { { "Accept", "*/*" }, { "Authorization", "Bearer abc" } },
        };

        // The managed event type is Unity-only and cannot run here: pin that its results go through the policy, not
        // JsonUtility.ToJson(dto), which is what wrote "" for every null string.
        [Test]
        public void Ios_network_event_results_do_not_serialize_the_dto_with_JsonUtility()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!System.IO.File.Exists(System.IO.Path.Combine(dir, "package.json"))) dir = System.IO.Directory.GetParent(dir).FullName;
            var src = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "Runtime/Platform/IOS/IosExchangeEvents.cs"));
            var start = src.IndexOf("sealed class IosNetworkEvent", System.StringComparison.Ordinal);
            var end = src.IndexOf("sealed class IosLogEvent", System.StringComparison.Ordinal);
            var cls = src.Substring(start, end - start);
            Assert.That(cls, Does.Contain("IosNetworkResultJson.ForFilter"));
            Assert.That(cls, Does.Contain("IosNetworkResultJson.ForReplacement"));
            Assert.That(cls, Does.Contain("IosNetworkResultJson.ForNewEvent"));
            Assert.That(cls, Does.Not.Contain("JsonUtility.ToJson"));
            var callbacks = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "Runtime/Platform/IOS/IosNativeCallbacks.cs"));
            Assert.That(callbacks, Does.Contain("ToReplacementJson"), "a replacement returned from a filter is not a channel event");
        }

        [Test]
        public void Untouched_event_carries_no_error_body_or_url()
        {
            Assert.That(IosNetworkResultJson.ForFilter(Clean(), IosNetworkField.None, "complete"), Is.EqualTo("{\"stage\":\"complete\"}"));
        }

        [Test]
        public void Only_set_fields_are_written()
        {
            var now = Clean();
            now.Url = "https://a.test/redacted";
            now.Headers["Authorization"] = "x";
            var json = IosNetworkResultJson.ForFilter(now, IosNetworkField.Url | IosNetworkField.Headers, "complete");
            Assert.That(json, Is.EqualTo(
                "{\"stage\":\"complete\",\"url\":\"https://a.test/redacted\",\"headers\":{\"Accept\":\"*/*\",\"Authorization\":\"x\"}}"));
        }

        [Test]
        public void Setting_an_empty_body_is_written_even_when_it_reads_the_same_as_before()
        {
            // Native sends a body that is not text (binary, gzip) as null, which JsonUtility reads as "": the filter's
            // `Body = ""` redaction must still reach native.
            var now = Clean();
            now.Body = "";
            var json = IosNetworkResultJson.ForFilter(now, IosNetworkField.Body, "complete");
            Assert.That(json, Does.Contain("\"body\":\"\""));
        }

        [Test]
        public void Deliberate_null_and_empty_still_apply()
        {
            var now = Clean();
            now.Url = null;
            now.Body = "";
            now.ErrorShortMessage = null;
            now.ErrorDescription = "";
            var json = IosNetworkResultJson.ForFilter(now,
                IosNetworkField.Url | IosNetworkField.Body | IosNetworkField.ErrorShortMessage | IosNetworkField.ErrorDescription, "complete");
            Assert.That(json, Does.Contain("\"url\":null"));
            Assert.That(json, Does.Contain("\"body\":\"\""));
            Assert.That(json, Does.Contain("\"errorShortMessage\":null"));
            Assert.That(json, Does.Contain("\"errorDescription\":\"\""));
        }

        [Test]
        public void Cleared_headers_are_an_empty_object_and_null_values_are_empty_strings()
        {
            var now = Clean();
            now.Headers.Clear();
            Assert.That(IosNetworkResultJson.ForFilter(now, IosNetworkField.Headers, "complete"), Does.Contain("\"headers\":{}"));
            now.Headers["A"] = null;
            Assert.That(IosNetworkResultJson.ForFilter(now, IosNetworkField.Headers, "complete"), Does.Contain("\"headers\":{\"A\":\"\"}"));
        }

        [Test]
        public void Same_headers_compares_by_content()
        {
            Assert.That(IosNetworkResultJson.SameHeaders(Clean().Headers, Clean().Headers), Is.True);
            var other = Clean();
            other.Headers["Authorization"] = "x";
            Assert.That(IosNetworkResultJson.SameHeaders(Clean().Headers, other.Headers), Is.False);
            Assert.That(IosNetworkResultJson.SameHeaders(null, new Dictionary<string, string>()), Is.True);
        }

        [Test]
        public void Replacement_writes_every_field_so_nothing_survives_from_the_original()
        {
            // A replacement the filter returned is applied onto the ORIGINAL native event, which keeps any key left out.
            var f = new IosNetworkFields { Url = "https://a.test/clean", Body = null, ErrorShortMessage = null, ErrorDescription = "", Size = 0, ResponseCode = 200 };
            var json = IosNetworkResultJson.ForReplacement(f, "GET", "NSURLSession", "complete");
            Assert.That(json, Does.Contain("\"url\":\"https://a.test/clean\""));
            Assert.That(json, Does.Contain("\"body\":null"));
            Assert.That(json, Does.Contain("\"errorShortMessage\":null"));
            Assert.That(json, Does.Contain("\"errorDescription\":null"));
            Assert.That(json, Does.Contain("\"headers\":{}"));
            Assert.That(json, Does.Contain("\"stage\":\"complete\""));
            Assert.That(json, Does.Not.Contain("timestampMs"));
        }

        [Test]
        public void Replacement_keeps_what_it_carries()
        {
            var f = new IosNetworkFields
            {
                Url = null, Body = "b", ErrorShortMessage = "boom", Headers = new Dictionary<string, string> { { "H", "v" } }, Size = 7, ResponseCode = 500,
            };
            var json = IosNetworkResultJson.ForReplacement(f, "POST", null, "error");
            Assert.That(json, Does.Contain("\"url\":null"));
            Assert.That(json, Does.Contain("\"body\":\"b\""));
            Assert.That(json, Does.Contain("\"errorShortMessage\":\"boom\""));
            Assert.That(json, Does.Contain("\"headers\":{\"H\":\"v\"}"));
            Assert.That(json, Does.Contain("\"responseCode\":500"));
            Assert.That(json, Does.Not.Contain("mechanism"));
        }

        [Test]
        public void New_event_omits_empty_error_and_body()
        {
            var f = Clean();
            f.Body = "";
            f.ErrorShortMessage = "";
            f.ErrorDescription = null;
            var json = IosNetworkResultJson.ForNewEvent(f, "id1", "GET", "", "complete", 5);
            Assert.That(json, Does.Not.Contain("error"));
            Assert.That(json, Does.Not.Contain("body"));
            Assert.That(json, Does.Not.Contain("mechanism"));
            Assert.That(json, Does.Contain("\"url\":\"https://a.test/x\""));
            Assert.That(json, Does.Contain("\"timestampMs\":5"));
        }

        [Test]
        public void New_event_keeps_a_real_error()
        {
            var f = Clean();
            f.ErrorShortMessage = "boom";
            f.ErrorDescription = "bad \"thing\"";
            var json = IosNetworkResultJson.ForNewEvent(f, "id1", "GET", null, "error", 0);
            Assert.That(json, Does.Contain("\"errorShortMessage\":\"boom\""));
            Assert.That(json, Does.Contain("\"errorDescription\":\"bad \\\"thing\\\"\""));
            Assert.That(json, Does.Not.Contain("timestampMs"));
        }

        [TestCase("de-DE")]
        [TestCase("ar-SA")]
        [TestCase("fa-IR")]
        [TestCase("sv-SE")]
        public void Numbers_are_culture_invariant(string culture)
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);
                var f = new IosNetworkFields { Size = -1234567, ResponseCode = 200 };
                var all = IosNetworkField.Size | IosNetworkField.ResponseCode;
                Assert.That(IosNetworkResultJson.ForFilter(f, all, "complete"), Does.Contain("\"size\":-1234567"));
                Assert.That(IosNetworkResultJson.ForReplacement(f, null, null, "complete"), Does.Contain("\"size\":-1234567"));
                Assert.That(IosNetworkResultJson.ForNewEvent(f, null, null, null, "complete", 1700000000000), Does.Contain("\"size\":-1234567"));
                Assert.That(IosNetworkResultJson.ForNewEvent(f, null, null, null, "complete", 1700000000000), Does.Contain("\"timestampMs\":1700000000000"));
            }
            finally { Thread.CurrentThread.CurrentCulture = saved; }
        }
    }
}
