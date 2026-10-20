using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosBridgeJsonTests
    {
        static string RepoFile(string rel)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "package.json"))) dir = Path.GetDirectoryName(dir);
            return Path.Combine(dir ?? "", rel);
        }

        [Test]
        public void Header_with_escaped_backslash_then_n_is_not_turned_into_a_newline()
        {
            // The header value is the four characters  a \ n b  (a backslash followed by n), not a line feed.
            var kv = "[{\"key\":\"X\",\"value\":\"a\\\\nb\"}]";
            var json = "{\"headers\":" + kv + "}";
            var rewritten = IosBridgeJson.ToNativeMapJson(json, "headers");
            var headers = (Dictionary<string, object>)JsonRead.Object(rewritten)["headers"];
            Assert.That(headers["X"], Is.EqualTo("a\\nb"));
        }

        [Test]
        public void Header_value_keeps_tab_unicode_and_quote_escapes()
        {
            var kv = "[{\"key\":\"X\",\"value\":\"t\\tq\\\"u\\u00e9\\u0001\"}]";
            var rewritten = IosBridgeJson.ToNativeMapJson("{\"headers\":" + kv + "}", "headers");
            var headers = (Dictionary<string, object>)JsonRead.Object(rewritten)["headers"];
            Assert.That(headers["X"], Is.EqualTo("t\tq\"ué\u0001"));
        }

        [Test]
        public void Extract_string_field_is_null_for_null_absent_or_non_string()
        {
            Assert.That(IosBridgeJson.ExtractStringField("{\"value\":null}", "value"), Is.Null);
            Assert.That(IosBridgeJson.ExtractStringField("{\"key\":\"a\"}", "value"), Is.Null);
            Assert.That(IosBridgeJson.ExtractStringField("{\"value\":3}", "value"), Is.Null);
            Assert.That(IosBridgeJson.ExtractStringField("not json", "value"), Is.Null);
        }

        [Test]
        public void Malformed_array_keeps_the_original_json()
        {
            var json = "{\"headers\":[{\"key\":\"a\",\"value\":\"b\"} junk]}";
            Assert.That(IosBridgeJson.ToNativeMapJson(json, "headers"), Is.EqualTo(json));
        }

        [Test]
        public void Empty_array_becomes_an_empty_object()
        {
            Assert.That(IosBridgeJson.ToNativeMapJson("{\"headers\":[]}", "headers"), Is.EqualTo("{\"headers\":{}}"));
        }

        [Test]
        public void Breadcrumb_data_keeps_nested_values_and_scalar_kinds()
        {
            var json = "{\"category\":\"c\",\"data\":{\"n\":3,\"f\":1.5,\"b\":true,\"s\":\"x\\\\ny\","
                + "\"o\":{\"deep\":[1,\"two\",{\"k\":false}]}}}";
            var data = IosBridgeJson.ReadObjectField(json, "data");
            Assert.That(data["n"], Is.EqualTo(3L));
            Assert.That(data["b"], Is.EqualTo(true));
            Assert.That(data["s"], Is.EqualTo("x\\ny"));

            var result = IosBridgeJson.AppendObjectField("{\"category\":\"c\",\"level\":2}", "data", data);
            var back = (Dictionary<string, object>)JsonRead.Object(result)["data"];
            Assert.That(back["n"], Is.EqualTo(3L));
            Assert.That(back["f"], Is.EqualTo(1.5));
            Assert.That(back["b"], Is.EqualTo(true));
            Assert.That(back["s"], Is.EqualTo("x\\ny"));
            var deep = (List<object>)((Dictionary<string, object>)back["o"])["deep"];
            Assert.That(deep[0], Is.EqualTo(1L));
            Assert.That(((Dictionary<string, object>)deep[2])["k"], Is.EqualTo(false));
            Assert.That(JsonRead.Object(result)["level"], Is.EqualTo(2L));
        }

        [Test]
        public void Unreadable_breadcrumb_data_is_left_out_so_native_keeps_it()
        {
            var deep = new string('[', 40) + new string(']', 40);
            Assert.That(IosBridgeJson.ReadObjectField("{\"data\":{\"a\":" + deep + "}}", "data"), Is.Null);
            Assert.That(IosBridgeJson.ReadObjectField("{\"data\":{", "data"), Is.Null);
            Assert.That(IosBridgeJson.AppendObjectField("{\"category\":\"c\"}", "data", null),
                Is.EqualTo("{\"category\":\"c\"}"));
        }

        [Test]
        public void Untouched_breadcrumb_without_data_round_trips_as_an_empty_object()
        {
            var data = IosBridgeJson.ReadObjectField("{\"category\":\"c\"}", "data");
            Assert.That(data, Is.Empty);
            Assert.That(IosBridgeJson.AppendObjectField("{\"category\":\"c\"}", "data", data),
                Is.EqualTo("{\"category\":\"c\",\"data\":{}}"));
            Assert.That(IosBridgeJson.AppendObjectField("{}", "data", new Dictionary<string, object>()), Is.EqualTo("{\"data\":{}}"));
        }

        [Test]
        public void Native_breadcrumb_path_no_longer_flattens_data_through_key_value_pairs()
        {
            var events = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosExchangeEvents.cs"));
            var callbacks = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosNativeCallbacks.cs"));
            Assert.That(events, Does.Not.Contain("NormalizeBreadcrumb"));
            Assert.That(callbacks, Does.Contain("IosBridgeJson.ReadObjectField(json, \"data\")"));
            Assert.That(callbacks, Does.Not.Contain("\"data\")))"));
        }

        [Test]
        public void Breadcrumb_data_is_only_written_back_when_the_filter_touched_it()
        {
            var events = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosExchangeEvents.cs"));
            Assert.That(events, Does.Contain("_dataTouched ? _data : null"));
            Assert.That(System.Text.RegularExpressions.Regex.Matches(events, "_dataTouched = true;").Count, Is.EqualTo(2));
        }

        [Test]
        public void Managed_report_completion_never_throws_into_native_and_clears_the_token_atomically()
        {
            var bridge = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));
            var start = bridge.IndexOf("static void OnManagedReportCreateCompletion", System.StringComparison.Ordinal);
            var body = bridge.Substring(start, bridge.IndexOf("void HandleManagedReportCreateCompletion", start, System.StringComparison.Ordinal) - start);
            Assert.That(body, Does.Contain("try"));
            Assert.That(body, Does.Contain("catch (Exception"));
            Assert.That(bridge, Does.Contain("Interlocked.CompareExchange(ref _inFlightUploadToken"));
            Assert.That(bridge, Does.Not.Contain("_inFlightUploadToken = "));
        }

        [Test]
        public void LogException_handled_flag_marshals_as_one_byte_to_match_the_native_bool()
        {
            var bridge = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));
            Assert.That(bridge, Does.Contain("[MarshalAs(UnmanagedType.U1)] bool handled"));
        }
    }
}
