using System.Collections.Generic;
using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    // Issue 131: Unity's JsonUtility writes a null string as "", so serializing a whole network event gave every
    // filtered request an empty error, body and url. The result now carries only what the filter changed.
    public class IosNetworkResultJsonTests
    {
        static IosNetworkFields Clean() => new IosNetworkFields
        {
            Url = "https://a.test/x",
            Body = null,
            Size = 12,
            ResponseCode = 200,
            Headers = new Dictionary<string, string> { { "Accept", "*/*" } },
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
            Assert.That(cls, Does.Contain("IosNetworkResultJson.ForNewEvent"));
            Assert.That(cls, Does.Not.Contain("JsonUtility.ToJson"));
        }

        [Test]
        public void Untouched_event_carries_no_error_body_or_url()
        {
            var before = Clean();
            var json = IosNetworkResultJson.ForFilter(before, before.Copy(), "complete");
            Assert.That(json, Is.EqualTo("{\"stage\":\"complete\"}"));
        }

        [Test]
        public void Changed_url_and_header_are_the_only_fields_written()
        {
            var before = Clean();
            var after = before.Copy();
            after.Url = "https://a.test/redacted";
            after.Headers["Authorization"] = "x";
            var json = IosNetworkResultJson.ForFilter(before, after, "complete");
            Assert.That(json, Is.EqualTo(
                "{\"stage\":\"complete\",\"url\":\"https://a.test/redacted\",\"headers\":{\"Accept\":\"*/*\",\"Authorization\":\"x\"}}"));
        }

        [Test]
        public void Deliberate_null_and_empty_still_apply()
        {
            var before = Clean();
            before.Body = "secret";
            before.ErrorShortMessage = "e";
            var after = before.Copy();
            after.Url = null;
            after.Body = "";
            after.ErrorShortMessage = null;
            var json = IosNetworkResultJson.ForFilter(before, after, "complete");
            Assert.That(json, Does.Contain("\"url\":null"));
            Assert.That(json, Does.Contain("\"body\":\"\""));
            Assert.That(json, Does.Contain("\"errorShortMessage\":null"));
            Assert.That(json, Does.Not.Contain("errorDescription"));
        }

        [Test]
        public void Cleared_headers_are_an_empty_object()
        {
            var before = Clean();
            var after = before.Copy();
            after.Headers.Clear();
            Assert.That(IosNetworkResultJson.ForFilter(before, after, "complete"), Does.Contain("\"headers\":{}"));
        }

        [Test]
        public void Changed_numbers_are_written()
        {
            var before = Clean();
            var after = before.Copy();
            after.ResponseCode = 500;
            after.Size = 0;
            var json = IosNetworkResultJson.ForFilter(before, after, "complete");
            Assert.That(json, Does.Contain("\"responseCode\":500"));
            Assert.That(json, Does.Contain("\"size\":0"));
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
            Assert.That(json, Does.Contain("\"headers\":{\"Accept\":\"*/*\"}"));
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
    }
}
