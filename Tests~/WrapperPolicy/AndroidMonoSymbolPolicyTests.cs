using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AndroidMonoSymbolPolicyTests
    {
        [TestCase("Mono2x", true)]
        [TestCase("mono2x", true)]
        [TestCase("IL2CPP", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void Detects_the_mono_backend(string backend, bool expected)
        {
            Assert.That(AndroidMonoSymbolPolicy.IsMono(backend), Is.EqualTo(expected));
        }

        [Test]
        public void Warns_once_for_mono()
        {
            bool warned = false;
            Assert.That(AndroidMonoSymbolPolicy.TakeWarning("Mono2x", ref warned), Does.Contain("IL2CPP"));
            Assert.That(warned, Is.True);
            Assert.That(AndroidMonoSymbolPolicy.TakeWarning("Mono2x", ref warned), Is.Null);
        }

        [Test]
        public void Warns_once_about_missing_file_line_for_release_mono()
        {
            bool warned = false;
            var text = AndroidMonoSymbolPolicy.TakeManagedLinesWarning("Mono2x", false, ref warned);
            Assert.That(text, Does.Contain("IL2CPP").And.Contain("Development Build").And.Contain("IL offsets"));
            Assert.That(warned, Is.True);
            Assert.That(AndroidMonoSymbolPolicy.TakeManagedLinesWarning("Mono2x", false, ref warned), Is.Null);
        }

        [TestCase("Mono2x", true)]
        [TestCase("IL2CPP", false)]
        [TestCase("IL2CPP", true)]
        [TestCase(null, false)]
        public void No_file_line_warning_for_development_mono_or_other_backends(string backend, bool development)
        {
            bool warned = false;
            Assert.That(AndroidMonoSymbolPolicy.TakeManagedLinesWarning(backend, development, ref warned), Is.Null);
            Assert.That(warned, Is.False);
        }

        [Test]
        public void Editor_reads_the_development_flag_from_the_build_report()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!File.Exists(Path.Combine(dir, "package.json"))) dir = Directory.GetParent(dir).FullName;
            var text = File.ReadAllText(Path.Combine(dir, "Editor", "BugseeAndroidMonoWarning.cs"));
            Assert.That(text, Does.Contain("report.summary.options & BuildOptions.Development"));
            Assert.That(text, Does.Contain("TakeManagedLinesWarning"));
        }

        [Test]
        public void Never_warns_for_il2cpp_and_leaves_the_flag_alone()
        {
            bool warned = false;
            Assert.That(AndroidMonoSymbolPolicy.TakeWarning("IL2CPP", ref warned), Is.Null);
            Assert.That(warned, Is.False);
        }

        [Test]
        public void Warning_names_the_real_libraries_and_does_not_overclaim()
        {
            Assert.That(AndroidMonoSymbolPolicy.Warning, Does.Contain("libmonobdwgc-2.0.so"));
            Assert.That(AndroidMonoSymbolPolicy.Warning, Does.Not.Contain("libmonobdwgc.so"));
            Assert.That(AndroidMonoSymbolPolicy.Warning, Does.Contain("inside the Mono runtime"));
        }

        [Test]
        public void Uses_the_backend_recorded_for_the_build()
        {
            var recorded = new PlayerNameSettings("out.apk", "1.0", 1, "Mono2x");
            bool warned = false;
            Assert.That(AndroidMonoSymbolPolicy.TakeWarning(recorded.Backend, ref warned), Is.Not.Null);
        }

        // The build-time warning must not depend on the symbol upload configuration, and must use the build's own
        // recorded settings rather than the live Player Settings.
        [Test]
        public void Editor_warning_is_independent_of_upload_config_and_uses_recorded_settings()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!File.Exists(Path.Combine(dir, "package.json"))) dir = Directory.GetParent(dir).FullName;
            var text = File.ReadAllText(Path.Combine(dir, "Editor", "BugseeAndroidMonoWarning.cs"));
            Assert.That(text, Does.Contain("BugseeBuildIdentity.AndroidNameSettings(report).Backend"));
            Assert.That(text, Does.Not.Contain("HasAppToken"));
            Assert.That(text, Does.Not.Contain("BUGSEE_NO_SYMBOL_UPLOAD"));
            Assert.That(text, Does.Not.Contain("GetScriptingBackend"));
        }
    }
}
