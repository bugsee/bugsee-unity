using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>Pins AndroidReport to Java Report methods that exist in Bugsee Android 7.4.x.</summary>
    public class AndroidReportBindingSourceTests
    {
        static string Source() => File.ReadAllText(Path.Combine(RepoRoot(), "Runtime/Platform/Android/AndroidReport.cs"));

        static string RepoRoot()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        [Test]
        public void Report_reads_attributes_and_attachments_from_native()
        {
            var src = Source();
            Assert.That(src, Does.Contain("\"getAttributes\""));
            Assert.That(src, Does.Contain("\"getAttachments\""));
        }

        [Test]
        public void Attachments_keep_the_null_on_refusal_addAttachment_overloads()
        {
            var src = Source();
            Assert.That(src, Does.Contain("\"addAttachment\", file, attachmentName, mimeType, false"));
            Assert.That(src, Does.Contain("\"addAttachment\", data, attachmentName, mimeType"));
            Assert.That(src, Does.Not.Contain("\"createAndAddAttachment\""));
        }

        [Test]
        public void Fluent_attachment_setters_are_called_with_an_object_return_type()
        {
            var src = Source();
            Assert.That(src, Does.Not.Contain("_attachment.Call(\"setName\""));
            Assert.That(src, Does.Not.Contain("_attachment.Call(\"setFileName\""));
            Assert.That(src, Does.Not.Contain("_attachment.Call(\"setMimeType\""));
        }

        [Test]
        public void Android_network_event_maps_isOverride_and_ios_ignores_it()
        {
            string root = RepoRoot();
            string android = File.ReadAllText(Path.Combine(root, "Runtime/Platform/Android/AndroidExchangeEvents.cs"));
            Assert.That(android, Does.Contain("_native.Call<bool>(\"isOverride\")"));
            Assert.That(android, Does.Contain("_native.Call(\"setOverride\", value)"));

            string bridge = File.ReadAllText(Path.Combine(root, "Runtime/Platform/Android/AndroidBridge.cs"));
            Assert.That(bridge, Does.Contain("target.IsOverride = source.IsOverride;"));

            string ios = File.ReadAllText(Path.Combine(root, "Runtime/Platform/IOS/IosExchangeEvents.cs"));
            Assert.That(ios, Does.Contain("public bool IsOverride { get; set; }"));
            Assert.That(ios, Does.Not.Contain("_dto.isOverride"));

            string contract = File.ReadAllText(Path.Combine(root, "Runtime/Contracts/Exchange/INetworkEvent.cs"));
            Assert.That(contract, Does.Contain("bool IsOverride { get; set; }"));
        }

        [Test]
        public void Managed_exception_options_carry_only_what_the_sdk_reads()
        {
            // The SDK takes only the signature from the reason JSON (the worker takes the module UUID) and ignores other option keys (they only land in the
            // report's logging options), so the bridge must not send them as options.
            var bridge = File.ReadAllText(Path.Combine(RepoRoot(), "Runtime/Platform/Android/AndroidBridge.cs"));
            Assert.That(bridge, Does.Not.Contain("\"$$signature\""));
            Assert.That(bridge, Does.Not.Contain("\"moduleUUID\""));
        }

        [Test]
        public void Managed_exception_class_captures_no_java_stack()
        {
            // The SDK hashes the Java stack of the throwable into the client signature: capturing it would make the signature
            // depend on the calling thread.
            var java = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins/Android/UnityManagedException.java"));
            Assert.That(java, Does.Match(@"(?m)^\s*public\s+(synchronized\s+)?Throwable\s+fillInStackTrace\(\)"));
        }
    }
}
