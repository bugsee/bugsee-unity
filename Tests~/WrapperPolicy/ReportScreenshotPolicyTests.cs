using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ReportScreenshotPolicyTests
    {
        static readonly byte[] Png = Header(16, 16);

        // Signature + an IHDR chunk header with the given size (the rest of a PNG is not read).
        static byte[] Header(uint width, uint height)
        {
            var d = new byte[33];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(d, 0);
            for (var i = 0; i < 4; i++)
            {
                d[16 + i] = (byte)(width >> (24 - 8 * i));
                d[20 + i] = (byte)(height >> (24 - 8 * i));
            }
            return d;
        }

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
        public void The_declared_size_is_read_from_the_ihdr_chunk()
        {
            Assert.That(ReportScreenshotPolicy.TryReadSize(Header(640, 480), out var w, out var h), Is.True);
            Assert.That(new[] { w, h }, Is.EqualTo(new[] { 640, 480 }));
            Assert.That(ReportScreenshotPolicy.TryReadSize(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, out _, out _), Is.False, "no IHDR");
            var notHeader = Header(1, 1);
            notHeader[12] = (byte)'X';
            Assert.That(ReportScreenshotPolicy.TryReadSize(notHeader, out _, out _), Is.False, "first chunk is not IHDR");
            Assert.That(ReportScreenshotPolicy.TryReadSize(Header(uint.MaxValue, 1), out var big, out _), Is.True);
            Assert.That(big, Is.EqualTo(int.MaxValue));
        }

        [Test]
        public void A_png_that_declares_a_huge_or_empty_canvas_is_rejected_before_any_decode()
        {
            Assert.That(ReportScreenshotPolicy.RejectReason(Header(0, 10)), Does.Contain("zero"));
            Assert.That(ReportScreenshotPolicy.RejectReason(Header(10, 0)), Does.Contain("zero"));
            Assert.That(ReportScreenshotPolicy.RejectReason(Header(8193, 10)), Does.Contain("8193x10"));
            Assert.That(ReportScreenshotPolicy.RejectReason(Header(10, 8193)), Does.Contain("limit"));
            Assert.That(ReportScreenshotPolicy.RejectReason(Header(uint.MaxValue, uint.MaxValue)), Does.Contain("limit"));
            Assert.That(ReportScreenshotPolicy.RejectReason(Header(8192, 8192)), Does.Contain("limit"), "67 MP over the total");
            Assert.That(ReportScreenshotPolicy.RejectReason(Header(8192, 4096)), Is.Null, "exactly the pixel limit");
            Assert.That(ReportScreenshotPolicy.RejectReason(Header(1170, 2532)), Is.Null, "a phone screen");
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
