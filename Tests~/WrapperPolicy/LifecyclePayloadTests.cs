using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class LifecyclePayloadTests
    {
        static string FindRepoRoot()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json"))) return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }

            Assert.Fail("Could not find repo root (package.json)");
            return null;
        }

        [Test]
        public void No_payload_is_null()
        {
            Assert.That(LifecyclePayload.Decode(null), Is.Null);
            Assert.That(LifecyclePayload.Decode(""), Is.Null);
        }

        [Test]
        public void String_literal_is_a_string()
        {
            Assert.That(LifecyclePayload.Decode("\"snap-1\""), Is.EqualTo("snap-1"));
            Assert.That(LifecyclePayload.Decode("\"a \\\"q\\\" \\u00e9\""), Is.EqualTo("a \"q\" \u00e9"));
        }

        [Test]
        public void Object_is_a_dictionary_like_Android()
        {
            var data = LifecyclePayload.Decode("{\"id\":\"x\",\"n\":3,\"ok\":true,\"inner\":{\"a\":1}}");
            Assert.That(data, Is.InstanceOf<Dictionary<string, object>>());
            var map = (Dictionary<string, object>)data;
            Assert.That(map["id"], Is.EqualTo("x"));
            Assert.That(map["n"], Is.EqualTo(3L));
            Assert.That(map["ok"], Is.EqualTo(true));
            Assert.That(map["inner"], Is.InstanceOf<Dictionary<string, object>>());
        }

        [Test]
        public void Dictionary_with_a_single_value_key_stays_a_dictionary()
        {
            var data = LifecyclePayload.Decode("{\"value\":\"x\"}");
            Assert.That(data, Is.InstanceOf<Dictionary<string, object>>());
            Assert.That(((Dictionary<string, object>)data)["value"], Is.EqualTo("x"));
        }

        [Test]
        public void Anything_else_passes_through_as_text()
        {
            Assert.That(LifecyclePayload.Decode("[1,2]"), Is.EqualTo("[1,2]"));
            Assert.That(LifecyclePayload.Decode("{broken"), Is.EqualTo("{broken"));
            Assert.That(LifecyclePayload.Decode("\"unterminated"), Is.EqualTo("\"unterminated"));
        }

        [Test]
        public void Ios_bridge_decodes_with_the_tested_policy_and_relaunch_registers_the_wrapper()
        {
            string root = FindRepoRoot();
            string callbacks = File.ReadAllText(Path.Combine(root, "Runtime", "Platform", "IOS", "IosNativeCallbacks.cs"));
            Assert.That(callbacks, Does.Contain("LifecyclePayload.Decode(dataJson)"));
            Assert.That(callbacks, Does.Not.Contain("StringBox"));

            string bridge = File.ReadAllText(Path.Combine(root, "Runtime", "Platform", "IOS", "IOSBridge.cs"));
            int relaunch = bridge.IndexOf("void RelaunchNow(", System.StringComparison.Ordinal);
            Assert.That(relaunch, Is.GreaterThan(0));
            int next = bridge.IndexOf("public void Stop(", relaunch, System.StringComparison.Ordinal);
            Assert.That(bridge.Substring(relaunch, next - relaunch), Does.Contain("EnsureWrapperRegistered();"));
        }

        [Test]
        public void Ios_native_wrapper_holds_early_lifecycle_events_until_the_callback_registers()
        {
            string text = File.ReadAllText(Path.Combine(FindRepoRoot(), "Plugins", "iOS", "BugseeUnityCallbacks.mm"));
            Assert.That(text, Does.Contain("gEarlyLifecycle"));
            int handler = text.IndexOf("- (void)onLifecycleEvent:", System.StringComparison.Ordinal);
            int end = text.IndexOf("- (void)onBeforeReportCreated", handler, System.StringComparison.Ordinal);
            string body = text.Substring(handler, end - handler);
            Assert.That(body, Does.Contain("gEarlyLifecycle"));
            Assert.That(body.IndexOf("if (!gLifecycleCb || !eventType) return;", System.StringComparison.Ordinal), Is.LessThan(0));
        }
    }
}
