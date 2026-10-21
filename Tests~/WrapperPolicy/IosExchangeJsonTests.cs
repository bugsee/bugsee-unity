using System.Collections.Generic;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Options;
using Bugsee.Platform.IOS;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The iOS filter payloads cross the native boundary as JSON written by <c>JsonUtility</c> (here the host-side
    /// stand-in in UnityEngineJsonStub.cs) and read back after <c>IosJsonNormalize</c> rewrites object maps into
    /// key/value arrays. These pin the shapes and the pure helpers around them without a device.
    /// </summary>
    public class IosExchangeJsonTests
    {
        // ---- IosJsonNormalize ----

        [Test]
        public void NormalizeNetwork_rewrites_the_headers_map_into_key_value_pairs()
        {
            var json = "{\"url\":\"http://a\",\"headers\":{\"Accept\":\"*/*\",\"X-A\":\"b\"},\"size\":3}";

            Assert.That(IosJsonNormalize.NormalizeNetwork(json), Is.EqualTo(
                "{\"url\":\"http://a\",\"headers\":[{\"key\":\"Accept\",\"value\":\"*/*\"},{\"key\":\"X-A\",\"value\":\"b\"}],\"size\":3}"));
        }

        [Test]
        public void Normalize_turns_an_empty_map_into_an_empty_array()
        {
            Assert.That(IosJsonNormalize.NormalizeNetwork("{\"headers\":{}}"), Is.EqualTo("{\"headers\":[]}"));
            Assert.That(IosJsonNormalize.NormalizeNetwork("{\"headers\":{ }}"), Is.EqualTo("{\"headers\":[]}"));
        }

        [Test]
        public void Normalize_returns_empty_object_for_null_or_empty_input()
        {
            Assert.That(IosJsonNormalize.NormalizeNetwork(null), Is.EqualTo("{}"));
        }

        [Test]
        public void Normalize_leaves_json_alone_when_the_field_is_missing_or_already_an_array()
        {
            Assert.That(IosJsonNormalize.NormalizeNetwork("{\"url\":\"x\"}"), Is.EqualTo("{\"url\":\"x\"}"));
            var arr = "{\"headers\":[{\"key\":\"a\",\"value\":\"b\"}]}";
            Assert.That(IosJsonNormalize.NormalizeNetwork(arr), Is.EqualTo(arr));
        }

        [Test]
        public void Normalize_quotes_scalar_values_and_keeps_null()
        {
            var json = "{\"headers\":{\"n\":42,\"b\":true,\"z\":null,\"s\":\"x\"}}";

            Assert.That(IosJsonNormalize.NormalizeNetwork(json), Is.EqualTo(
                "{\"headers\":[{\"key\":\"n\",\"value\":\"42\"},{\"key\":\"b\",\"value\":\"true\"},{\"key\":\"z\",\"value\":null},{\"key\":\"s\",\"value\":\"x\"}]}"));
        }

        [Test]
        public void Normalize_keeps_escaped_quotes_and_braces_inside_string_values()
        {
            var json = "{\"headers\":{\"k\":\"a \\\"q\\\" } b\"},\"size\":1}";

            Assert.That(IosJsonNormalize.NormalizeNetwork(json), Is.EqualTo(
                "{\"headers\":[{\"key\":\"k\",\"value\":\"a \\\"q\\\" } b\"}],\"size\":1}"));
        }

        [Test]
        public void Normalize_keeps_the_original_json_when_a_value_is_a_container()
        {
            var json = "{\"headers\":{\"a\":{\"nested\":1},\"b\":\"x\"}}";
            Assert.That(IosJsonNormalize.NormalizeNetwork(json), Is.EqualTo(json));

            json = "{\"headers\":{\"list\":[1,2]}}";
            Assert.That(IosJsonNormalize.NormalizeNetwork(json), Is.EqualTo(json));
        }

        // ---- network event ----

        static IosNetworkEvent Network(IosNetworkDto dto = null) => new IosNetworkEvent(dto ?? new IosNetworkDto());

        // Issue 131: the filter result carries only what the filter set (JsonUtility would write every null string as "").
        [Test]
        public void Network_ToResultJson_of_an_untouched_event_is_just_the_stage()
        {
            var ev = Network(new IosNetworkDto
            {
                id = "7", url = "http://a/b", method = "GET", body = "{}", size = 12, responseCode = 204,
                statusText = "No Content", stage = "complete", timestampMs = 1700000000123,
                headers = new[] { new IosKv { key = "A", value = "1" } },
            });

            Assert.That(ev.ToResultJson(), Is.EqualTo("{\"stage\":\"complete\"}"));
        }

        [Test]
        public void Network_ToResultJson_writes_the_fields_a_setter_was_called_on_and_headers_as_an_object()
        {
            var ev = Network(new IosNetworkDto
            {
                url = "http://a/b", stage = "complete", headers = new[] { new IosKv { key = "A", value = "1" } },
            });
            ev.ResponseCode = 200;
            ev.Body = "";
            ev.Url = null;
            ev.Headers["B"] = "2";

            var json = ev.ToResultJson();

            Assert.That(json, Does.Contain("\"responseCode\":200"));
            Assert.That(json, Does.Contain("\"body\":\"\""));
            Assert.That(json, Does.Contain("\"url\":null"));
            Assert.That(json, Does.Contain("\"headers\":{\"A\":\"1\",\"B\":\"2\"}"));
            Assert.That(json, Does.Not.Contain("error"));
            Assert.That(json, Does.Not.Contain("size"));
        }

        [Test]
        public void Network_ToResultJson_notices_a_header_edited_in_place_and_each_setter()
        {
            var ev = Network(new IosNetworkDto { headers = new[] { new IosKv { key = "Authorization", value = "x" } } });
            ev.Headers.Remove("Authorization");
            Assert.That(ev.ToResultJson(), Does.Contain("\"headers\":{}"));

            ev = Network();
            ev.Size = 1; ev.StatusText = "s"; ev.ErrorShortMessage = "n"; ev.ErrorDescription = "d";
            var json = ev.ToResultJson();
            foreach (var key in new[] { "size", "statusText", "errorShortMessage", "errorDescription" })
                Assert.That(json, Does.Contain("\"" + key + "\":"), key);
        }

        [Test]
        public void Network_ToReplacementJson_writes_every_field_so_nothing_survives_from_the_original()
        {
            var ev = Network(new IosNetworkDto { method = "GET", mechanism = "m" });
            ev.Url = "http://a/clean";

            var json = ev.ToReplacementJson("complete");

            Assert.That(json, Does.Contain("\"url\":\"http://a/clean\""));
            Assert.That(json, Does.Contain("\"body\":null"));
            Assert.That(json, Does.Contain("\"errorShortMessage\":null"));
            Assert.That(json, Does.Contain("\"headers\":{}"));
            Assert.That(json, Does.Not.Contain("timestampMs"));
        }

        [Test]
        public void Network_ToChannelJson_overrides_stage_and_stamps_a_missing_time_without_mutating_the_event()
        {
            var ev = Network(new IosNetworkDto { stage = "RequestCompleted" });

            var json = ev.ToChannelJson("before", 1234);

            Assert.That(json, Does.Contain("\"stage\":\"before\""));
            Assert.That(json, Does.Contain("\"timestampMs\":1234"));
            // The event itself is unchanged: the next serialization starts from the original values.
            Assert.That(ev.ToResultJson(), Is.EqualTo("{\"stage\":\"RequestCompleted\"}"));
        }

        [Test]
        public void Network_ToChannelJson_keeps_the_time_the_event_already_has()
        {
            var ev = Network(new IosNetworkDto { timestampMs = 99 });

            Assert.That(ev.ToChannelJson("complete", 5000), Does.Contain("\"timestampMs\":99"));
            Assert.That(ev.ToChannelJson("complete", 0), Does.Contain("\"timestampMs\":99"));
        }

        [Test]
        public void Network_ToChannelJson_does_not_invent_a_time_when_none_is_supplied()
        {
            Assert.That(Network().ToChannelJson("complete", 0), Does.Not.Contain("timestampMs"));
        }

        [TestCase(NetworkEventStage.RequestStarted, "before")]
        [TestCase(NetworkEventStage.RequestCompleted, "complete")]
        [TestCase(NetworkEventStage.RequestErrored, "error")]
        [TestCase(NetworkEventStage.RequestAborted, "cancel")]
        [TestCase(NetworkEventStage.Redirect, null)]
        [TestCase(NetworkEventStage.WebSocket, null)]
        [TestCase(NetworkEventStage.RequestTimingsReceived, null)]
        public void ToNativeStage_maps_only_stages_the_sdk_has(NetworkEventStage stage, string expected)
        {
            Assert.That(IosNetworkEvent.ToNativeStage(stage), Is.EqualTo(expected));
        }

        [TestCase(null, NetworkEventStage.RequestCompleted)]
        [TestCase("", NetworkEventStage.RequestCompleted)]
        [TestCase("before", NetworkEventStage.RequestStarted)]
        [TestCase("RequestStarted", NetworkEventStage.RequestStarted)]
        [TestCase("begin", NetworkEventStage.RequestStarted)]
        [TestCase("cancel", NetworkEventStage.RequestAborted)]
        [TestCase("Abort", NetworkEventStage.RequestAborted)]
        [TestCase("error", NetworkEventStage.RequestErrored)]
        [TestCase("redirect", NetworkEventStage.Redirect)]
        [TestCase("websocket", NetworkEventStage.WebSocket)]
        [TestCase("timings", NetworkEventStage.RequestTimingsReceived)]
        [TestCase("complete", NetworkEventStage.RequestCompleted)]
        [TestCase("something else", NetworkEventStage.RequestCompleted)]
        public void Stage_parses_native_and_managed_spellings(string native, NetworkEventStage expected)
        {
            Assert.That(Network(new IosNetworkDto { stage = native }).Stage, Is.EqualTo(expected));
        }

        [Test]
        public void FromKv_skips_empty_keys_and_nulls_and_last_duplicate_wins()
        {
            var map = IosNetworkEvent.FromKv(new[]
            {
                new IosKv { key = "a", value = "1" },
                null,
                new IosKv { key = "", value = "x" },
                new IosKv { key = "a", value = "2" },
            });

            Assert.That(map, Is.EqualTo(new Dictionary<string, string> { { "a", "2" } }));
            Assert.That(IosNetworkEvent.FromKv(null), Is.Empty);
        }

        [Test]
        public void ToKv_round_trips_and_handles_empty_input()
        {
            Assert.That(IosNetworkEvent.ToKv(null), Is.Empty);
            Assert.That(IosNetworkEvent.ToKv(new Dictionary<string, string>()), Is.Empty);

            var pairs = IosNetworkEvent.ToKv(new Dictionary<string, string> { { "a", "1" }, { "b", null } });

            Assert.That(pairs.Length, Is.EqualTo(2));
            Assert.That(pairs[0].key, Is.EqualTo("a"));
            Assert.That(pairs[1].value, Is.Null);
        }

        [Test]
        public void Headers_setter_copies_the_map_and_null_clears_it()
        {
            var ev = Network();
            var source = new Dictionary<string, string> { { "a", "1" } };
            ev.Headers = source;
            source["a"] = "changed";
            Assert.That(ev.Headers["a"], Is.EqualTo("1"));

            ev.Headers = null;
            Assert.That(ev.Headers, Is.Empty);
        }

        // ---- log event ----

        [Test]
        public void Log_event_round_trips_message_and_level()
        {
            var ev = new IosLogEvent(new IosLogDto { message = "hi \"there\"\n", level = (int)LogLevel.Warning });

            Assert.That(ev.Message, Is.EqualTo("hi \"there\"\n"));
            Assert.That(ev.Level, Is.EqualTo(LogLevel.Warning));

            ev.Message = "changed";
            ev.Level = LogLevel.Error;
            Assert.That(ev.ToResultJson(), Is.EqualTo("{\"message\":\"changed\",\"level\":" + (int)LogLevel.Error + "}"));
        }

        [Test]
        public void Log_event_escapes_control_characters()
        {
            var ev = new IosLogEvent(new IosLogDto());
            ev.Message = "a\tb\\c";
            var json = ev.ToResultJson();

            Assert.That(json, Does.Contain("\"message\":\"a\\tb\\\\c\""));
        }

        // Issue 160: only what the filter set goes back (JsonUtility wrote every null string as "").
        [Test]
        public void Log_event_result_of_an_untouched_event_is_empty()
        {
            var ev = new IosLogEvent(new IosLogDto { message = null, level = (int)LogLevel.Warning });

            Assert.That(ev.ToResultJson(), Is.EqualTo("{}"));
        }

        [Test]
        public void Log_event_result_keeps_null_and_empty_message_distinct_and_one_field_alone()
        {
            var ev = new IosLogEvent(new IosLogDto { message = "m", level = 3 });
            ev.Message = null;
            Assert.That(ev.ToResultJson(), Is.EqualTo("{\"message\":null}"));
            ev.Message = "";
            Assert.That(ev.ToResultJson(), Is.EqualTo("{\"message\":\"\"}"));
        }

        [Test]
        public void Breadcrumb_result_of_an_untouched_crumb_is_empty()
        {
            var crumb = new IosBreadcrumb(new IosBreadcrumbDto { timestamp = 5, level = 2 }, null);

            Assert.That(crumb.ToResultJson(), Is.EqualTo("{}"));
        }

        [Test]
        public void Breadcrumb_result_carries_only_the_touched_fields()
        {
            var crumb = new IosBreadcrumb(new IosBreadcrumbDto { category = "c", message = "m", type = "t", level = 2 }, null);
            crumb.Category = null;
            crumb.Message = "";
            crumb.Level = null;

            Assert.That(crumb.ToResultJson(), Is.EqualTo("{\"category\":null,\"message\":\"\",\"level\":0}"));

            crumb = new IosBreadcrumb(new IosBreadcrumbDto(), null);
            crumb.Type = "x";
            crumb.Level = LogLevel.Error;
            Assert.That(crumb.ToResultJson(), Is.EqualTo("{\"type\":\"x\",\"level\":" + (int)LogLevel.Error + "}"));
        }

        [Test]
        public void Breadcrumb_result_adds_touched_data_after_the_fields()
        {
            var crumb = new IosBreadcrumb(new IosBreadcrumbDto(), null);
            crumb.Message = "m";
            crumb.Data["k"] = 1;

            Assert.That(crumb.ToResultJson(), Is.EqualTo("{\"message\":\"m\",\"data\":{\"k\":1}}"));
        }

        [Test]
        public void Log_and_breadcrumb_results_do_not_serialize_the_dto_with_JsonUtility()
        {
            var dir = NUnit.Framework.TestContext.CurrentContext.TestDirectory;
            while (!System.IO.File.Exists(System.IO.Path.Combine(dir, "package.json"))) dir = System.IO.Directory.GetParent(dir).FullName;
            var src = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "Runtime/Platform/IOS/IosExchangeEvents.cs"));
            Assert.That(src, Does.Not.Contain("JsonUtility.ToJson(_dto)"));
        }

        [Test]
        public void Replacement_results_write_every_field()
        {
            var log = new IosLogEvent(new IosLogDto { message = "m", level = 3 });
            Assert.That(log.ToReplacementJson(), Is.EqualTo("{\"message\":\"m\",\"level\":3}"));

            var crumb = new IosBreadcrumb(new IosBreadcrumbDto { category = "c", type = "t", level = 2 }, null);
            Assert.That(crumb.ToReplacementJson(), Is.EqualTo(
                "{\"category\":\"c\",\"message\":null,\"type\":\"t\",\"level\":2,\"data\":{}}"));
        }
    }
}
