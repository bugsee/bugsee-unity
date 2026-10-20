using System;
using System.IO;
using System.Linq;
using System.Text;
using Bugsee.Contracts.Options;
using Bugsee.Platform.IOS;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The report contract (specs sdk/report-contract, "Attachments"): empty bytes are a valid attachment, and a
    /// missing MIME type is unset rather than an invented one.
    /// </summary>
    public class AttachmentSpecTests
    {
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

        static string Source(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

        [Test]
        public void Ios_empty_bytes_are_a_valid_attachment_and_travel_as_empty_base64()
        {
            var report = new IosReport(new IosReportDto());
            var att = report.AddAttachmentBytes(new byte[0], "empty.bin", "application/x-empty");

            Assert.That(att, Is.Not.Null);
            Assert.That(report.Attachments.Count, Is.EqualTo(1));
            Assert.That(report.ToResultJson(), Does.Contain("\"dataBase64\":\"\""));
        }

        [Test]
        public void Ios_null_bytes_are_still_refused()
        {
            var report = new IosReport(new IosReportDto());
            Assert.That(report.AddAttachmentBytes(null, "x", "text/plain"), Is.Null);
        }

        [Test]
        public void Ios_missing_mime_type_is_not_invented()
        {
            var report = new IosReport(new IosReportDto());
            var att = report.AddAttachmentBytes(Encoding.UTF8.GetBytes("hello"), "log", null);

            Assert.That(att.MimeType, Is.Null);
            string json = report.ToResultJson();
            Assert.That(json, Does.Not.Contain("mimeType"));
            Assert.That(json, Does.Not.Contain("octet-stream"));
        }

        [Test]
        public void Ios_blank_mime_type_is_unset_too_and_a_given_one_is_kept()
        {
            var report = new IosReport(new IosReportDto());
            Assert.That(report.AddAttachmentBytes(new byte[] { 1 }, "a", "").MimeType, Is.Null);
            Assert.That(report.AddAttachmentBytes(new byte[] { 1 }, "b", "image/png").MimeType, Is.EqualTo("image/png"));
            Assert.That(report.ToResultJson(), Does.Contain("\"mimeType\":\"image/png\""));
        }

        [Test]
        public void Ios_file_attachment_without_mime_type_is_not_given_one()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dat");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            try
            {
                var report = new IosReport(new IosReportDto());
                var att = report.AddAttachmentFile(path, "data", null);

                Assert.That(att, Is.Not.Null);
                Assert.That(att.MimeType, Is.Null);
                Assert.That(report.ToResultJson(), Does.Not.Contain("octet-stream"));
                report.ClearAttachments();
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void Ios_set_data_on_an_attachment_does_not_invent_or_override_a_mime_type()
        {
            var report = new IosReport(new IosReportDto());
            var unset = report.AddAttachmentBytes(new byte[] { 1 }, "a", null);
            unset.SetData(new byte[] { 0xFF, 0xFE, 0x00, 0x80 });
            Assert.That(unset.MimeType, Is.Null);

            var text = report.AddAttachmentBytes(new byte[] { 1 }, "b", "text/plain");
            text.SetData(new byte[] { 0xFF, 0xFE, 0x00, 0x80 });
            Assert.That(text.MimeType, Is.EqualTo("text/plain"));
            Assert.That(report.ToResultJson(), Does.Not.Contain("octet-stream"));
        }

        [Test]
        public void Ios_native_bridge_accepts_empty_data_and_invents_no_mime_type()
        {
            string callbacks = Source("Plugins/iOS/BugseeUnityCallbacks.mm");
            string bridge = Source("Plugins/iOS/BugseeUnityBridge.mm");

            Assert.That(Source("Runtime/Platform/IOS/IosReport.cs"), Does.Not.Contain("application/octet-stream"));
            Assert.That(Source("Runtime/Platform/IOS/IosReport.cs"), Does.Not.Contain("\"text/plain\""));
            Assert.That(callbacks, Does.Not.Contain("application/octet-stream"));
            Assert.That(bridge, Does.Not.Contain("application/octet-stream"));
            Assert.That(callbacks, Does.Not.Contain("[(NSString *)b64 length] > 0"));
            Assert.That(bridge, Does.Not.Contain("[(NSString *)b64 length] > 0"));
        }

        [Test]
        public void Android_managed_attachments_keep_empty_bytes_and_no_invented_mime_type()
        {
            string android = Source("Runtime/Platform/Android/AndroidReport.cs");

            Assert.That(android, Does.Not.Contain("application/octet-stream"));
            foreach (var part in android.Split(new[] { "public IAttachment AddAttachmentBytes(" }, StringSplitOptions.None).Skip(1))
                Assert.That(part.Substring(0, 160), Does.Not.Contain("data.Length == 0"));
            Assert.That(android, Does.Not.Contain("payload.Length > 0"));
            Assert.That(android, Does.Not.Contain("\"text/plain\""));
            // A live attachment's SetData(empty) truncates through the stream instead of returning early.
            Assert.That(android, Does.Not.Contain("if (data == null || data.Length == 0) return;"));
        }

        [Test]
        public void Unobserved_task_capture_is_an_option_that_defaults_on()
        {
            Assert.That(Options.CaptureUnobservedTaskExceptions,
                Is.EqualTo("com.bugsee.option.unity.capture-unobserved-task-exceptions"));

            string pipeline = Source("Runtime/Internal/ExceptionPipeline.cs");
            Assert.That(pipeline, Does.Contain("GetBoolOption(launchOptions, Options.CaptureUnobservedTaskExceptions, true)"));
            // A Relaunch calls Install again while the hooks exist: the subscription is re-synced, not skipped.
            int existing = pipeline.IndexOf("if (_instance != null)", pipeline.IndexOf("public static void Install(", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.That(pipeline.Substring(existing, 400), Does.Contain("SyncUnobservedTaskHandler(launchOptions)"));
            Assert.That(pipeline, Does.Contain("TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;"));
            Assert.That(Source("Runtime/Contracts/Options/BugseeLaunchOptions.cs"), Does.Contain("Options.CaptureUnobservedTaskExceptions"));
            Assert.That(Source("Runtime/Contracts/Options/OptionsBuilder.cs"), Does.Contain("Options.CaptureUnobservedTaskExceptions"));
        }
    }
}
