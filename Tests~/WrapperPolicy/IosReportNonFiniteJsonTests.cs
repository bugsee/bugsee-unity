using System.Text;
using Bugsee.Contracts.Options;
using Bugsee.Platform.IOS;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosReportNonFiniteJsonTests
    {
        [Test]
        public void ToResultJson_writes_null_for_non_finite_attribute_numbers()
        {
            var report = new IosReport(new IosReportDto());
            report.SetAttribute("nan", double.NaN);
            report.SetAttribute("inf", double.PositiveInfinity);
            report.SetAttribute("ninf", double.NegativeInfinity);
            report.SetAttribute("fnan", float.NaN);
            report.SetAttribute("finf", float.PositiveInfinity);
            report.SetAttribute("ok", 42.5);

            string json = report.ToResultJson();

            Assert.That(json, Does.Contain("\"nan\":null"));
            Assert.That(json, Does.Contain("\"inf\":null"));
            Assert.That(json, Does.Contain("\"ninf\":null"));
            Assert.That(json, Does.Contain("\"fnan\":null"));
            Assert.That(json, Does.Contain("\"finf\":null"));
            Assert.That(json, Does.Contain("\"ok\":42.5"));
            Assert.That(json, Does.Not.Contain("NaN"));
            Assert.That(json, Does.Not.Contain("Infinity"));
        }

        [Test]
        public void ToResultJson_omits_severity_when_unset_zero()
        {
            var report = new IosReport(new IosReportDto());
            string json = report.ToResultJson();

            Assert.That(json, Does.Not.Contain("\"severity\""));

            report.Severity = IssueSeverity.High;
            json = report.ToResultJson();
            Assert.That(json, Does.Contain("\"severity\":"));
            Assert.That(json, Does.Contain("\"severity\":" + (int)IssueSeverity.High));
        }

        [Test]
        public void ToResultJson_overlays_create_report_attributes_without_replace_all()
        {
            var report = new IosReport(new IosReportDto());
            report.SetAttribute("cart_id", "abc");
            string json = report.ToResultJson();

            Assert.That(json, Does.Contain("\"cart_id\":\"abc\""));
            Assert.That(json, Does.Not.Contain("attributesReplaceAll"));

            report.RemoveAttribute("cart_id");
            json = report.ToResultJson();
            Assert.That(json, Does.Contain("\"attributeRemovals\":[\"cart_id\"]"));
            Assert.That(json, Does.Not.Contain("attributesReplaceAll"));

            report.ClearAllAttributes();
            json = report.ToResultJson();
            Assert.That(json, Does.Contain("attributesReplaceAll"));
        }

        [Test]
        public void ToResultJson_appends_attachments_without_replace_all_until_clear()
        {
            var report = new IosReport(new IosReportDto());
            var att = report.AddAttachmentBytes(Encoding.UTF8.GetBytes("hello"), "log.txt", "text/plain");
            Assert.That(att, Is.Not.Null);
            string json = report.ToResultJson();

            Assert.That(json, Does.Contain("\"attachments\":["));
            Assert.That(json, Does.Not.Contain("attachmentsReplaceAll"));

            report.ClearAttachments();
            json = report.ToResultJson();
            Assert.That(json, Does.Contain("\"attachments\":[]"));
            Assert.That(json, Does.Contain("attachmentsReplaceAll"));
        }
    }
}
