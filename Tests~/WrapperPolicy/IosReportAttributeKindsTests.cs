using System.Collections.Generic;
using System.IO;
using Bugsee.Platform.IOS;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosReportAttributeKindsTests
    {
        const string Native =
            "{\"id\":\"r1\",\"summary\":\"s\",\"attributes\":{\"i\":42,\"b\":true,\"d\":1.5,\"s\":\"x\",\"str42\":\"42\","
            + "\"o\":{\"k\":[1,\"two\",{\"z\":false}]},\"n\":null}}";

        static IosReport FromNative(string json = Native) =>
            new IosReport(new IosReportDto(), IosReport.ReadAttributes(json));

        static Dictionary<string, object> Result(IosReport report) => JsonRead.Object(report.ToResultJson());

        [Test]
        public void Attributes_keep_their_kinds_when_read_from_native()
        {
            var report = FromNative();
            Assert.That(report.GetAttribute("i"), Is.EqualTo(42L));
            Assert.That(report.GetAttribute("b"), Is.EqualTo(true));
            Assert.That(report.GetAttribute("d"), Is.EqualTo(1.5));
            Assert.That(report.GetAttribute("s"), Is.EqualTo("x"));
            Assert.That(report.GetAttribute("str42"), Is.EqualTo("42"));
            Assert.That(report.GetAttribute("o"), Is.InstanceOf<Dictionary<string, object>>());
            Assert.That(report.Attributes.ContainsKey("n"), Is.True);
        }

        [Test]
        public void A_handler_that_touches_nothing_sends_no_attributes_at_all()
        {
            var json = FromNative().ToResultJson();
            Assert.That(json, Does.Not.Contain("\"attributes\""));
            Assert.That(json, Does.Not.Contain("attributesReplaceAll"));
            Assert.That(json, Does.Not.Contain("attributeRemovals"));
        }

        [Test]
        public void Only_the_attributes_the_handler_set_go_back_and_they_keep_their_kind()
        {
            var report = FromNative();
            report.SetAttribute("added", 7);
            report.SetAttribute("b", false);
            report.SetAttribute("ratio", 2.25);

            var result = Result(report);
            var attrs = (Dictionary<string, object>)result["attributes"];
            Assert.That(attrs.Keys, Is.EquivalentTo(new[] { "added", "b", "ratio" }));
            Assert.That(attrs["added"], Is.EqualTo(7L));
            Assert.That(attrs["b"], Is.EqualTo(false));
            Assert.That(attrs["ratio"], Is.EqualTo(2.25));
            Assert.That(result.ContainsKey("attributesReplaceAll"), Is.False);
        }

        [Test]
        public void A_container_attribute_the_handler_sets_goes_back_as_json_not_a_type_name()
        {
            var report = FromNative();
            report.SetAttribute("list", new List<object> { 1, "a", true });
            report.SetAttribute("map", new Dictionary<string, object> { { "k", 1.5 } });

            var attrs = (Dictionary<string, object>)Result(report)["attributes"];
            var list = (List<object>)attrs["list"];
            Assert.That(list, Is.EqualTo(new object[] { 1L, "a", true }));
            Assert.That(((Dictionary<string, object>)attrs["map"])["k"], Is.EqualTo(1.5));
        }

        [Test]
        public void Removing_a_native_attribute_sends_a_removal_and_nothing_else()
        {
            var report = FromNative();
            report.RemoveAttribute("s");

            var result = Result(report);
            Assert.That((List<object>)result["attributeRemovals"], Is.EqualTo(new object[] { "s" }));
            Assert.That(result.ContainsKey("attributes"), Is.False);
            Assert.That(result.ContainsKey("attributesReplaceAll"), Is.False);
        }

        [Test]
        public void Setting_a_removed_name_again_is_a_set_not_a_removal()
        {
            var report = FromNative();
            report.RemoveAttribute("s");
            report.SetAttribute("s", "y");
            var result = Result(report);
            Assert.That(result.ContainsKey("attributeRemovals"), Is.False);
            Assert.That(((Dictionary<string, object>)result["attributes"])["s"], Is.EqualTo("y"));

            report.RemoveAttribute("s");
            result = Result(report);
            Assert.That(result.ContainsKey("attributes"), Is.False);
            Assert.That((List<object>)result["attributeRemovals"], Is.EqualTo(new object[] { "s" }));
        }

        [Test]
        public void Clear_all_replaces_the_set_with_what_the_handler_then_added()
        {
            var report = FromNative();
            report.ClearAllAttributes();
            report.SetAttribute("only", 1);

            var result = Result(report);
            var attrs = (Dictionary<string, object>)result["attributes"];
            Assert.That(attrs.Keys, Is.EquivalentTo(new[] { "only" }));
            Assert.That(attrs["only"], Is.EqualTo(1L));
            Assert.That(result["attributesReplaceAll"], Is.EqualTo(true));
        }

        [Test]
        public void Attributes_that_cannot_be_read_leave_a_handler_that_touches_nothing_without_attributes()
        {
            Assert.That(IosReport.ReadAttributes("{\"attributes\":{\"a\":"), Is.Empty);
            Assert.That(IosReport.ReadAttributes(null), Is.Empty);
            Assert.That(IosReport.ReadAttributes("{\"id\":\"r\"}"), Is.Empty);
            var json = new IosReport(new IosReportDto(), IosReport.ReadAttributes("{\"attributes\":{\"a\":")).ToResultJson();
            Assert.That(json, Does.Not.Contain("attributes"));
        }

        [Test]
        public void The_native_report_path_reads_attributes_with_their_kinds()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "package.json"))) dir = Path.GetDirectoryName(dir);
            var callbacks = File.ReadAllText(Path.Combine(dir ?? "", "Runtime/Platform/IOS/IosNativeCallbacks.cs"));
            var events = File.ReadAllText(Path.Combine(dir ?? "", "Runtime/Platform/IOS/IosExchangeEvents.cs"));
            Assert.That(callbacks, Does.Contain("IosReport.ReadAttributes(json)"));
            Assert.That(callbacks, Does.Not.Contain("NormalizeReport"));
            Assert.That(events, Does.Not.Contain("NormalizeReport"));
        }
    }
}
