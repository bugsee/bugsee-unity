using System;
using System.IO;
using Bugsee.Platform.IOS;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosReportReleaseTests
    {
        [Test]
        public void A_file_attached_after_the_report_was_released_is_not_copied()
        {
            var source = Path.GetTempFileName();
            var dir = Path.Combine(Path.GetTempPath(), "bugsee-unity-report-attachments");
            try
            {
                File.WriteAllText(source, "x");
                var report = new IosReport(new IosReportDto());
                var before = Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0;

                Assert.That(report.AddAttachmentFile(source, "a", "text/plain"), Is.Not.Null, "before the release it is attached");
                report.ReleaseSnapshotFiles();
                var after = Directory.GetFiles(dir).Length;
                Assert.That(after, Is.EqualTo(before), "the release deleted the copy");

                Assert.That(report.AddAttachmentFile(source, "b", "text/plain"), Is.Null,
                    "a handler that is still running after the abandon must not leave a copy nothing deletes");
                Assert.That(Directory.GetFiles(dir).Length, Is.EqualTo(before));
            }
            finally
            {
                File.Delete(source);
            }
        }
    }
}
