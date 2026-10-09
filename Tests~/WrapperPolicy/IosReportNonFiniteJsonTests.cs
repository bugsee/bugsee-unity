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
    }
}
