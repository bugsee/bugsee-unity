using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ReportScreenshotPolicyTests
    {
        static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };

        static string RepoFile(string relative)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return Path.Combine(dir, relative);
                dir = Directory.GetParent(dir)?.FullName;
            }
            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        [Test]
        public void A_png_signature_is_recognised_and_anything_else_is_not()
        {
            Assert.That(ReportScreenshotPolicy.IsPng(Png), Is.True);
            Assert.That(ReportScreenshotPolicy.IsPng(null), Is.False);
            Assert.That(ReportScreenshotPolicy.IsPng(new byte[0]), Is.False);
            Assert.That(ReportScreenshotPolicy.IsPng(new byte[] { 0x89, 0x50, 0x4E }), Is.False, "too short");
            Assert.That(ReportScreenshotPolicy.IsPng(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0 }), Is.False, "a JPEG");
        }

        [Test]
        public void Null_means_remove_and_is_never_rejected()
        {
            Assert.That(ReportScreenshotPolicy.RejectReason(null), Is.Null);
        }

        [Test]
        public void A_png_is_accepted_and_a_non_png_is_rejected_with_a_reason()
        {
            Assert.That(ReportScreenshotPolicy.RejectReason(Png), Is.Null);
            Assert.That(ReportScreenshotPolicy.RejectReason(new byte[] { 1, 2, 3 }), Does.Contain("not a PNG"));
            Assert.That(ReportScreenshotPolicy.RejectReason(new byte[0]), Does.Contain("not a PNG"));
        }

        [Test]
        public void A_png_over_the_limit_is_rejected_with_the_limit_in_the_reason()
        {
            var big = new byte[ReportScreenshotPolicy.MaxPngBytes + 1];
            Array.Copy(Png, big, Png.Length);
            Assert.That(ReportScreenshotPolicy.RejectReason(big), Does.Contain("33554432"));

            var atLimit = new byte[ReportScreenshotPolicy.MaxPngBytes];
            Array.Copy(Png, atLimit, Png.Length);
            Assert.That(ReportScreenshotPolicy.RejectReason(atLimit), Is.Null);
        }

        [Test]
        public void Display_ids_parse_ascending_distinct_and_tolerate_bad_text()
        {
            Assert.That(ReportScreenshotPolicy.ParseDisplayIds("[0,1]"), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(ReportScreenshotPolicy.ParseDisplayIds(" [ 2 , 0 , 2 ] "), Is.EqualTo(new[] { 0, 2 }));
            Assert.That(ReportScreenshotPolicy.ParseDisplayIds("[]"), Is.Empty);
            Assert.That(ReportScreenshotPolicy.ParseDisplayIds("[1,x,3]"), Is.EqualTo(new[] { 1, 3 }));
            Assert.That(ReportScreenshotPolicy.ParseDisplayIds(null), Is.Empty);
            Assert.That(ReportScreenshotPolicy.ParseDisplayIds("nope"), Is.Empty);
        }

        [Test]
        public void The_unsupported_messages_say_what_to_do()
        {
            Assert.That(ReportScreenshotPolicy.SnapshotReportMessage, Does.Contain("SetReportHandler"));
            Assert.That(ReportScreenshotPolicy.ClosedMessage, Does.Contain("report handler"));
            Assert.That(ReportScreenshotPolicy.TooLargeToReadMessage(1, 40000000), Does.Contain("GetScreenshotPng(1)"));
        }

        // Source checks: the platform code is not compiled here, so what it must do is asserted on the text.

        [Test]
        public void Both_android_reports_and_both_ios_reports_implement_the_four_members()
        {
            foreach (var file in new[] { "Runtime/Platform/Android/AndroidReport.cs", "Runtime/Platform/IOS/IosReport.cs", "Runtime/Platform/IOS/IOSBridge.cs" })
            {
                var src = File.ReadAllText(RepoFile(file));
                foreach (var member in new[] { "ScreenshotDisplayIds", "GetScreenshotPng(int displayId)", "SetScreenshotPng(int displayId, byte[] png)", "IsScreenshotChanged" })
                    Assert.That(src, Does.Contain(member), file + " lacks " + member);
            }
            var android = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidReport.cs"));
            Assert.That(android.Split(new[] { "bool SetScreenshotPng(" }, StringSplitOptions.None).Length - 1, Is.EqualTo(2), "AndroidReport and AndroidManagedReport");
        }

        [Test]
        public void A_snapshot_report_says_so_instead_of_dropping_the_set_silently()
        {
            var android = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidReport.cs"));
            var managed = android.Substring(android.IndexOf("sealed class AndroidManagedReport", StringComparison.Ordinal));
            Assert.That(managed, Does.Contain("AndroidReportScreenshots.SnapshotNotice()"));
            var ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosReportScreenshots.cs"));
            Assert.That(ios, Does.Contain("ReportScreenshotPolicy.SnapshotReportMessage"));
            Assert.That(ios, Does.Contain("requestId == 0"));
        }

        [Test]
        public void The_android_set_checks_the_gate_and_the_policy_and_removes_through_the_java_helper()
        {
            var src = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidReportScreenshots.cs"));
            Assert.That(src, Does.Contain("ReportScreenshotPolicy.RejectReason(png)"));
            Assert.That(src, Does.Contain("gate != null && !gate.IsOpen"));
            Assert.That(src, Does.Contain("setReportScreenshotPng"), "a null (remove) goes through the Java helper, JNI cannot name the Bitmap overload with null");
            Assert.That(File.ReadAllText(RepoFile("Plugins/Android/UnityWrapperProvider.java")), Does.Contain("public static int setReportScreenshotPng(Report report, int displayId, byte[] png)")
                .And.Contain("public static byte[] getReportScreenshotPng(Report report, int displayId)"));
        }

        [Test]
        public void The_ios_handler_report_is_bound_to_its_request_and_the_exports_are_declared_once()
        {
            var callbacks = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosNativeCallbacks.cs"));
            Assert.That(callbacks, Does.Contain("new IosReport(dto, attributes, requestId)"));
            var src = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosReportScreenshots.cs"));
            foreach (var export in new[] { "_bugsee_report_screenshot_ids", "_bugsee_report_screenshot_png", "_bugsee_report_set_screenshot", "_bugsee_report_screenshot_changed" })
                Assert.That(src, Does.Contain("static extern").And.Contain(export));
        }

        [Test]
        public void The_interface_documents_privacy_and_the_changelog_and_docs_mention_the_api()
        {
            Assert.That(File.ReadAllText(RepoFile("Runtime/Contracts/Reporting/IReport.cs")), Does.Contain("PRIVACY"));
            Assert.That(File.ReadAllText(RepoFile("Documentation~/index.md")), Does.Contain("## Report screenshots"));
            Assert.That(File.ReadAllText(RepoFile("CHANGELOG.md")), Does.Contain("`bool SetScreenshotPng(int displayId, byte[] png)`"));
        }
    }
}
