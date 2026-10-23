using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ProguardRulesTests
    {
        const string Unity = "-keep class com.unity3d.player.* { *; }\n-ignorewarnings\n";
        const string Rules = "-keep class a.B { *; }\n-keep class c.D { *; }\n";

        static string FindRepoRoot()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json"))) return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }
            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        static string RepoFile(string relative) => Path.Combine(FindRepoRoot(), relative);

        static string RulesFile() => File.ReadAllText(RepoFile("Plugins/Android/bugsee-unity-wrapper.proguard"));

        [Test]
        public void Apply_appends_one_marked_block_and_keeps_unity_rules()
        {
            string result = ProguardRules.Apply(Unity, Rules);
            Assert.That(result, Does.StartWith(Unity));
            Assert.That(result, Does.Contain(Rules));
            Assert.That(Regex.Matches(result, Regex.Escape(ProguardRules.BeginMarker)).Count, Is.EqualTo(1));
        }

        [Test]
        public void Apply_is_idempotent()
        {
            string once = ProguardRules.Apply(Unity, Rules);
            Assert.That(ProguardRules.Apply(once, Rules), Is.EqualTo(once));
        }

        [Test]
        public void Apply_replaces_the_block_when_the_rules_changed()
        {
            string first = ProguardRules.Apply(Unity + "# tail\n", "-keep class old.X { *; }\n");
            string second = ProguardRules.Apply(first, Rules);
            Assert.That(second, Does.Not.Contain("old.X"));
            Assert.That(second, Does.Contain("a.B"));
            Assert.That(second, Does.StartWith(Unity));
            Assert.That(second, Is.EqualTo(Unity + "# tail\n" + ProguardRules.BeginMarker + "\n" + Rules + ProguardRules.EndMarker + "\n"));
            Assert.That(Regex.Matches(second, Regex.Escape(ProguardRules.BeginMarker)).Count, Is.EqualTo(1));
        }

        [Test]
        public void Apply_keeps_the_rules_after_a_block_whose_end_marker_is_missing()
        {
            string broken = Unity + ProguardRules.BeginMarker + "\n-keep class old.X { *; }\n-dontwarn user.Rule\n";
            string result = ProguardRules.Apply(broken, Rules);
            Assert.That(result, Does.Contain("-dontwarn user.Rule"));
            Assert.That(result, Does.Contain("-keep class old.X { *; }"));
            Assert.That(result, Does.Contain(Rules + ProguardRules.EndMarker));
            Assert.That(Regex.Matches(result, Regex.Escape(ProguardRules.BeginMarker)).Count, Is.EqualTo(1));
            Assert.That(ProguardRules.Apply(result, Rules), Is.EqualTo(result));
        }

        [Test]
        public void DeclaresConsumerRules_reads_groovy_and_kotlin_and_rejects_absence()
        {
            Assert.That(ProguardRules.DeclaresConsumerRules("defaultConfig {\n    consumerProguardFiles 'proguard-unity.txt'\n}"), Is.True);
            Assert.That(ProguardRules.DeclaresConsumerRules("consumerProguardFiles(\"proguard-unity.txt\")"), Is.True);
            Assert.That(ProguardRules.DeclaresConsumerRules("defaultConfig {\n    minSdkVersion 22\n}"), Is.False);
            Assert.That(ProguardRules.DeclaresConsumerRules("// consumerProguardFiles other.txt\nproguard-unity.txt"), Is.False);
            Assert.That(ProguardRules.DeclaresConsumerRules(null), Is.False);
        }

        [Test]
        public void Apply_handles_an_empty_file_and_a_missing_final_newline()
        {
            Assert.That(ProguardRules.Apply("", Rules), Does.StartWith(ProguardRules.BeginMarker));
            Assert.That(ProguardRules.Apply("-dontwarn x", Rules), Does.StartWith("-dontwarn x\n" + ProguardRules.BeginMarker));
        }

        [Test]
        public void Apply_keeps_crlf_files_crlf()
        {
            string result = ProguardRules.Apply("-dontwarn x\r\n", Rules);
            Assert.That(Regex.IsMatch(result, "(?<!\r)\n"), Is.False);
            Assert.That(ProguardRules.Apply(result, Rules), Is.EqualTo(result));
        }

        static string CodeOnly(string csharp) =>
            Regex.Replace(csharp, @"(^|\s)//[^\n]*", "$1");

        // UnityWrapper and UnityManagedException are looked up by name over JNI (dotted or slashed): -keepnames would
        // still let R8 remove them, so a full -keep is required.
        [Test]
        public void Every_unity_wrapper_class_named_in_csharp_has_a_keep_rule()
        {
            var named = new HashSet<string>();
            foreach (string file in Directory.GetFiles(RepoFile("Runtime"), "*.cs", SearchOption.AllDirectories))
                foreach (Match m in Regex.Matches(CodeOnly(File.ReadAllText(file)), "\"(com[./]bugsee[./]unity[./][A-Za-z0-9_$]+)\""))
                    named.Add(m.Groups[1].Value.Replace('/', '.'));

            Assert.That(named, Is.Not.Empty);
            string rules = RulesFile();
            foreach (string cls in named)
                Assert.That(Regex.IsMatch(rules, @"^-keep\s+class\s+" + Regex.Escape(cls) + @"\s*\{", RegexOptions.Multiline),
                    Is.True, cls + " is looked up over JNI but R8 is not told to keep it (-keepnames does not stop removal)");
        }

        static IEnumerable<string> RuntimeSources() =>
            Directory.GetFiles(RepoFile("Runtime"), "*.cs", SearchOption.AllDirectories).Select(f => CodeOnly(File.ReadAllText(f)));

        // Any C# call on a local bound to the UnityWrapper class, in any Runtime file, must hit a public static
        // method (the rules keep exactly those: R8 removes unlisted static methods that no Java code calls).
        [Test]
        public void Every_method_called_on_the_wrapper_over_jni_is_public_static_and_kept()
        {
            string rules = RulesFile();
            Assert.That(Regex.IsMatch(rules, @"-keep\s+class\s+com\.bugsee\.unity\.UnityWrapper\s*\{\s*public\s+static\s+<methods>;\s*\}"), Is.True);

            string java = File.ReadAllText(RepoFile("Plugins/Android/UnityWrapperProvider.java"));
            var called = new HashSet<string>();
            foreach (string source in RuntimeSources())
                foreach (Match v in Regex.Matches(source, @"(\w+)\s*=\s*new\s+AndroidJavaClass\(\s*""com\.bugsee\.unity\.UnityWrapper""\s*\)"))
                    foreach (Match c in Regex.Matches(source, @"\b" + v.Groups[1].Value + @"\.(?:CallStatic|Call)(?:<[^>]+>)?\(\s*""(\w+)"""))
                        called.Add(c.Groups[1].Value);

            Assert.That(called, Is.Not.Empty);
            foreach (string method in called)
                Assert.That(Regex.IsMatch(java, @"^\s*public\s+static\s+\S+\s+" + method + @"\(", RegexOptions.Multiline), Is.True,
                    "UnityWrapper." + method + " is called over JNI but is not a public static method (not covered by the keep rule)");
        }

        // AndroidJavaProxy dispatches by interface method name, so a proxied interface must not be stripped or renamed.
        [Test]
        public void Every_interface_implemented_by_an_android_java_proxy_is_kept()
        {
            string proxies = string.Join("\n", RuntimeSources());
            var interfaces = Regex.Matches(proxies, @"base\(\s*""(com\.bugsee\.[A-Za-z0-9_.$]+)""")
                .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.That(interfaces, Is.Not.Empty);
            string rules = RulesFile();
            foreach (string cls in interfaces)
                Assert.That(Regex.IsMatch(rules, @"^-keep\s+interface\s+" + Regex.Escape(cls) + @"\s*\{\s*\*;\s*\}", RegexOptions.Multiline),
                    Is.True, cls + " is proxied from C# but not kept");
        }

        [Test]
        public void The_sdks_platform_classes_missing_from_an_older_compile_jar_do_not_stop_R8()
        {
            // Bugsee Android 7.4.2 references android.telephony.satellite.* (absent from android.jar below API 36), and its
            // consumer rules carry no -dontwarn, so a Unity 6 Minify build with Target API Level 35 fails in R8 (#255).
            string rules = RulesFile();
            foreach (string cls in new[] { "SatelliteManager", "SatelliteStateChangeListener" })
                Assert.That(Regex.IsMatch(rules, @"^-dontwarn\s+android\.telephony\.satellite\." + cls + @"\s*$", RegexOptions.Multiline),
                    Is.True, cls + " has no -dontwarn");
        }

        [Test]
        public void Wiring_only_the_editor_step_references_the_rules_file_and_policy()
        {
            string editor = File.ReadAllText(RepoFile("Editor/BugseeAndroidProguardRules.cs"));
            Assert.That(editor, Does.Contain("IPostGenerateGradleAndroidProject"));
            Assert.That(editor, Does.Contain("\"proguard-unity.txt\""));
            Assert.That(editor, Does.Contain("ProguardRules.Apply("));
            Assert.That(editor, Does.Contain("\"bugsee-unity-wrapper.proguard\""));
        }
    }
}
