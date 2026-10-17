using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// Which Android Gradle plugin lines get the Bugsee Gradle plugin (8.6+), the build id asset task (7.4.x and 8.0 to 8.5) or
    /// neither (everything in between, and unknown versions). The AGP each Unity line pins, from Unity's "Unity and Gradle
    /// version compatibility" manual page (checked 2026-10-17) and, for 2021.3, 2022.3 and 6000.0.84f1, the editors'
    /// own templates: 2021.3.x and 2022.3.x 7.4.2, 6000.0.1-44 8.3.0 (Gradle 8.4), 6000.0.45-60 8.7.2 (8.11),
    /// 6000.0.61-78 8.10.0 (8.13), 6000.0.79-84 9.0.0 (9.1.0), 6000.0.85+ 9.1.1 (9.3.1). Only 7.4.2 and 9.0.0 have
    /// real Unity-generated fixtures; the other roots below are the real Unity 6 root with the version changed. The
    /// build id block was also run in real Gradle builds of a 2022.3.62f3 export on AGP 8.0.2 / Gradle 8.0.2, 8.3.0 / 8.4
    /// and 8.5.0 / 8.7 (APK carried the mapping's id and every game asset), which is why 8.0 to 8.5 get it (a Unity 6000.0.1-44 editor build, AGP 8.3.0, was not run: that editor is not installed).
    /// </summary>
    public class GradleAgpGateTests
    {
        static string RealRoot() => File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "Fixtures", "Unity6000.0.84f1-default", "root.build.gradle"));

        static string RootWith(string agp) => RealRoot().Replace("9.0.0", agp);

        [TestCase("7.4.2", false, true)]
        [TestCase("7.4.0", false, true)]
        [TestCase("7.3.1", false, false)]
        [TestCase("7.0.4", false, false)]
        [TestCase("4.0.1", false, false)]
        [TestCase("8.0.0", false, true)]
        [TestCase("8.0.2", false, true)]
        [TestCase("8.1.1", false, true)]
        [TestCase("8.1.4", false, true)]
        [TestCase("8.2.2", false, true)]
        [TestCase("8.3.0", false, true)]
        [TestCase("8.3.2", false, true)]
        [TestCase("8.4.2", false, true)]
        [TestCase("8.5.0", false, true)]
        [TestCase("8.5.2", false, true)]
        [TestCase("8.6.0", true, false)]
        [TestCase("8.7.2", true, false)]
        [TestCase("8.10.0", true, false)]
        [TestCase("9.0.0", true, false)]
        [TestCase("9.1.1", true, false)]
        [TestCase("8.8.0-alpha01", true, false)]
        public void Groovy_plugins_block_gates(string agp, bool plugin, bool buildIdBlock)
        {
            string root = RootWith(agp);

            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(root), Is.EqualTo(plugin));
            Assert.That(GradleTemplateDefaults.AgpSupportsBuildIdBlock(root), Is.EqualTo(buildIdBlock));
        }

        [TestCase("8.3.0", false)]
        [TestCase("8.5.2", false)]
        [TestCase("8.6.0", true)]
        [TestCase("9.0.0", true)]
        public void Kotlin_plugins_block_gates(string agp, bool plugin)
        {
            string root = "plugins {\n    id(\"com.android.application\") version \"" + agp + "\" apply false\n    id(\"com.android.library\") version \"" + agp + "\" apply false\n}\n";

            Assert.That(GradleTemplateDefaults.TryGetAgpVersion(root, out int major, out int minor), Is.True);
            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(root), Is.EqualTo(plugin));
            Assert.That(major, Is.EqualTo(int.Parse(agp.Split('.')[0])));
            Assert.That(minor, Is.EqualTo(int.Parse(agp.Split('.')[1])));
        }

        [TestCase("classpath 'com.android.tools.build:gradle:8.3.0'", false)]
        [TestCase("classpath(\"com.android.tools.build:gradle:8.3.0\")", false)]
        [TestCase("classpath 'com.android.tools.build:gradle:8.7.2'", true)]
        [TestCase("classpath(\"com.android.tools.build:gradle:9.0.0\")", true)]
        public void Buildscript_classpath_gates_in_either_dsl(string line, bool plugin)
        {
            string root = "buildscript {\n    dependencies {\n        " + line + "\n    }\n}\n";

            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(root), Is.EqualTo(plugin));
        }

        [Test]
        public void A_version_the_build_file_does_not_state_is_unknown_so_nothing_is_applied()
        {
            string computed = "plugins {\n    id 'com.android.application' version agpVersion apply false\n}\n";

            Assert.That(GradleTemplateDefaults.TryGetAgpVersion(computed, out _, out _), Is.False);
            Assert.That(GradleTemplateDefaults.AgpMeetsBugseePlugin(computed), Is.False);
            Assert.That(GradleTemplateDefaults.AgpSupportsBuildIdBlock(computed), Is.False);
        }

        [Test]
        public void Release_debug_symbols_use_the_dsl_of_the_launcher()
        {
            string groovy = "android {\n    buildTypes {\n        release {\n            minifyEnabled false\n        }\n    }\n}\n";
            string kotlin = "android {\n    buildTypes {\n        release {\n            isMinifyEnabled = false\n        }\n    }\n}\n";
            string getByName = kotlin.Replace("release {", "getByName(\"release\") {");

            string g = GradleTemplateDefaults.EnsureReleaseDebugSymbols(groovy, GradleMavenInjection.GradleDsl.Groovy);
            string k = GradleTemplateDefaults.EnsureReleaseDebugSymbols(kotlin, GradleMavenInjection.GradleDsl.Kotlin);
            string n = GradleTemplateDefaults.EnsureReleaseDebugSymbols(getByName, GradleMavenInjection.GradleDsl.Kotlin);

            Assert.That(g, Does.Contain("debugSymbolLevel 'FULL'"));
            Assert.That(k, Does.Contain("            ndk {\n                debugSymbolLevel = \"FULL\"\n            }\n"));
            Assert.That(k, Does.Not.Contain("'"), "a Groovy quote is a character literal in Kotlin");
            Assert.That(n, Does.Contain("debugSymbolLevel = \"FULL\""));
            Assert.That(GradleTemplateDefaults.EnsureReleaseDebugSymbols(k, GradleMavenInjection.GradleDsl.Kotlin), Is.EqualTo(k));
            Assert.That(GradleTemplateDefaults.EnsureReleaseDebugSymbols(groovy), Is.EqualTo(g));
        }
    }
}
