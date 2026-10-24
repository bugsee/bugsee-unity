using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class Il2CppMappingArgsTests
    {
        [TestCase(null, "--emit-source-mapping --emit-method-map")]
        [TestCase("", "--emit-source-mapping --emit-method-map")]
        [TestCase("--emit-source-mapping", "--emit-source-mapping --emit-method-map")]
        [TestCase("--emit-method-map", "--emit-method-map --emit-source-mapping")]
        [TestCase("--emit-null-checks", "--emit-null-checks --emit-source-mapping --emit-method-map")]
        [TestCase("--emit-null-checks ", "--emit-null-checks --emit-source-mapping --emit-method-map")]
        [TestCase("--emit-method-map --emit-source-mapping", "--emit-method-map --emit-source-mapping")]
        [TestCase("--emit-source-mapping=1  --emit-method-map", "--emit-source-mapping=1  --emit-method-map")]
        public void Missing_mapping_arguments_are_appended_and_nothing_else_changes(string existing, string expected)
        {
            Assert.That(Il2CppMappingArgs.Ensure(existing), Is.EqualTo(expected));
        }

        const string Both = "--emit-source-mapping --emit-method-map";

        [TestCase("    commandLineArgs.add(\"--emit-source-mapping --emit-method-map\")\n",
                  "    commandLineArgs.add(\"--emit-source-mapping\")\n    commandLineArgs.add(\"--emit-method-map\")\n")]
        [TestCase("    commandLineArgs.add(\"--emit-source-mapping --emit-method-map\")\r\n",
                  "    commandLineArgs.add(\"--emit-source-mapping\")\r\n    commandLineArgs.add(\"--emit-method-map\")\r\n")]
        [TestCase("\tcommandLineArgs.add('--emit-method-map --emit-null-checks --emit-source-mapping')",
                  "\tcommandLineArgs.add('--emit-method-map')\n\tcommandLineArgs.add('--emit-null-checks')\n\tcommandLineArgs.add('--emit-source-mapping')")]
        [TestCase("    commandLineArgs.add(\"--emit-source-mapping  --emit-method-map\")  \n",
                  "    commandLineArgs.add(\"--emit-source-mapping\")\n    commandLineArgs.add(\"--emit-method-map\")  \n")]
        [TestCase("    commandLineArgs.add(\"--emit-source-mapping --emit-method-map\");\n",
                  "    commandLineArgs.add(\"--emit-source-mapping\");\n    commandLineArgs.add(\"--emit-method-map\");\n")]
        [TestCase("    commandLineArgs.add(\"--emit-source-mapping --emit-method-map\") // mapping\n",
                  "    commandLineArgs.add(\"--emit-source-mapping\")\n    commandLineArgs.add(\"--emit-method-map\") // mapping\n")]
        [TestCase("    commandLineArgs.add(\"--emit-source-mapping --emit-method-map\"); // mapping\r\n",
                  "    commandLineArgs.add(\"--emit-source-mapping\");\r\n    commandLineArgs.add(\"--emit-method-map\"); // mapping\r\n")]
        [TestCase("    commandLineArgs.add('--emit-source-mapping --emit-method-map')\r\n",
                  "    commandLineArgs.add('--emit-source-mapping')\r\n    commandLineArgs.add('--emit-method-map')\r\n")]
        [TestCase("    commandLineArgs.add( \"--emit-source-mapping --emit-method-map\" )\n",
                  "    commandLineArgs.add(\"--emit-source-mapping\")\n    commandLineArgs.add(\"--emit-method-map\")\n")]
        public void Gradle_line_with_several_options_becomes_one_line_per_option(string gradle, string expected)
        {
            Assert.That(Il2CppMappingArgs.SplitGradleArguments(gradle), Is.EqualTo(expected));
        }

        [Test]
        public void Gradle_text_around_the_split_line_is_kept()
        {
            string before = "def x = 1\n    commandLineArgs.add(\"--generatedheaders\")\n    commandLineArgs.add(\"" + Both + "\")\n    commandLineArgs.add(\"--dotnetprofile=unityaot-linux\")\n}\n";
            string after = "def x = 1\n    commandLineArgs.add(\"--generatedheaders\")\n    commandLineArgs.add(\"--emit-source-mapping\")\n    commandLineArgs.add(\"--emit-method-map\")\n    commandLineArgs.add(\"--dotnetprofile=unityaot-linux\")\n}\n";
            Assert.That(Il2CppMappingArgs.SplitGradleArguments(before), Is.EqualTo(after));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("apply plugin: 'com.android.library'\n")]
        // already one argument per line
        [TestCase("    commandLineArgs.add(\"--emit-source-mapping\")\n    commandLineArgs.add(\"--emit-method-map\")\n")]
        // several options but none of ours: not touched
        [TestCase("    commandLineArgs.add(\"--emit-null-checks --stats\")\n")]
        // a value with a space after an option, or a path: not split
        [TestCase("    commandLineArgs.add(\"--emit-source-mapping --map-file=C:/my dir/x\")\n")]
        [TestCase("    commandLineArgs.add(\"--emit-source-mapping=a b\")\n")]
        // an interpolated or escaped value is not ours to parse
        [TestCase("    commandLineArgs.add(\"--emit-source-mapping ${foo}\")\n")]
        [TestCase("    commandLineArgs.add(\"--emit-source-mapping \\\"x y\\\"\")\n")]
        // not a plain add of a literal
        [TestCase("    commandLineArgs.add(flags)\n")]
        [TestCase("    commandLineArgs.add(\"--emit-source-mapping --emit-method-map\" + x)\n")]
        public void Gradle_text_that_needs_no_split_comes_back_unchanged(string gradle)
        {
            Assert.That(Il2CppMappingArgs.SplitGradleArguments(gradle), Is.EqualTo(gradle));
        }

        [Test]
        public void Splitting_twice_changes_nothing_more()
        {
            string once = Il2CppMappingArgs.SplitGradleArguments("    commandLineArgs.add(\"" + Both + "\")\n");
            Assert.That(Il2CppMappingArgs.SplitGradleArguments(once), Is.EqualTo(once));
        }

        // What the editor step puts in the environment variable is what Unity 6 writes as one Gradle argument; the split
        // must turn exactly that value (with a user's own option in front) into one argument per option.
        [Test]
        public void The_environment_value_the_package_builds_splits_into_its_own_options()
        {
            string value = Il2CppMappingArgs.EnvironmentValue(null, "--emit-null-checks");
            string gradle = "    commandLineArgs.add(\"" + value + "\")\n";
            Assert.That(Il2CppMappingArgs.SplitGradleArguments(gradle), Is.EqualTo(
                "    commandLineArgs.add(\"--emit-null-checks\")\n    commandLineArgs.add(\"--emit-source-mapping\")\n    commandLineArgs.add(\"--emit-method-map\")\n"));
        }

        [Test]
        public void The_editor_step_splits_the_exported_gradle_arguments()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!File.Exists(Path.Combine(dir, "package.json")))
            {
                var parent = Directory.GetParent(dir);
                if (parent == null) Assert.Fail("package.json not found above " + TestContext.CurrentContext.TestDirectory);
                dir = parent.FullName;
            }

            var source = File.ReadAllText(Path.Combine(dir, "Editor", "BugseeIl2CppMappingArgs.cs"));
            Assert.That(source, Does.Contain("IPostGenerateGradleAndroidProject"));
            Assert.That(source, Does.Contain("Il2CppMappingArgs.SplitGradleFile("));
            Assert.That(source, Does.Contain("BugseeAndroidMavenDependencies.FindGradleFile("));
        }

        // The line is a mapping line among several words, but not safe to split: reported by number, text unchanged.
        [TestCase("a\n    commandLineArgs.add(\"--emit-source-mapping --map-file=C:/my dir/x\")\n", 2)]
        [TestCase("    commandLineArgs.add(\"--emit-source-mapping --out=C:\\\\tmp\\\\x\")\r\n", 1)]
        [TestCase("    commandLineArgs.add(\"--emit-method-map --dir=${workingDir}/x\")\n", 1)]
        public void A_mapping_line_that_cannot_be_split_is_reported(string gradle, int line)
        {
            Assert.That(Il2CppMappingArgs.SplitGradleArguments(gradle, out int[] unsplit), Is.EqualTo(gradle));
            Assert.That(unsplit, Is.EqualTo(new[] { line }));
        }

        [TestCase("    commandLineArgs.add(\"--emit-source-mapping\")\n")]
        [TestCase("    commandLineArgs.add(\"--emit-null-checks --stats\")\n")]
        [TestCase("    commandLineArgs.add(\"--emit-source-mapping --emit-method-map\")\n")]
        public void Nothing_is_reported_for_one_argument_lines_other_options_or_a_split_line(string gradle)
        {
            Il2CppMappingArgs.SplitGradleArguments(gradle, out int[] unsplit);
            Assert.That(unsplit, Is.Empty);
        }

        static string FixturePath(params string[] parts) =>
            Path.Combine(new[] { TestContext.CurrentContext.TestDirectory, "Fixtures" }.Concat(parts).ToArray());

        // The real unityLibrary/build.gradle of a Unity 6000.0.84f1 Export Project (Windows, CRLF) with the line Unity writes.
        [Test]
        public void The_real_unity_6_export_file_has_the_two_options_on_separate_lines_afterwards()
        {
            string original = File.ReadAllText(FixturePath("Il2CppExport", "Unity6000.0.84f1", "unityLibrary.build.gradle"));
            Assert.That(original, Does.Contain("commandLineArgs.add(\"--emit-source-mapping --emit-method-map\")"));

            string fixedText = Il2CppMappingArgs.SplitGradleArguments(original, out int[] unsplit);

            Assert.That(unsplit, Is.Empty);
            Assert.That(fixedText, Does.Contain("    commandLineArgs.add(\"--emit-source-mapping\")\r\n    commandLineArgs.add(\"--emit-method-map\")\r\n"));
            Assert.That(fixedText, Does.Not.Contain("--emit-source-mapping --emit-method-map"));
            // Everything else is byte for byte as it was.
            Assert.That(fixedText.Replace("    commandLineArgs.add(\"--emit-source-mapping\")\r\n    commandLineArgs.add(\"--emit-method-map\")\r\n",
                "    commandLineArgs.add(\"--emit-source-mapping --emit-method-map\")\r\n"), Is.EqualTo(original));
        }

        [Test]
        public void The_file_step_rewrites_the_file_on_disk_and_keeps_its_line_endings()
        {
            string dir = Path.Combine(Path.GetTempPath(), "bugsee-split-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "build.gradle");
                string original = File.ReadAllText(FixturePath("Il2CppExport", "Unity6000.0.84f1", "unityLibrary.build.gradle"));
                File.WriteAllText(path, original);

                Assert.That(Il2CppMappingArgs.SplitGradleFile(path, out int[] unsplit), Is.True);
                Assert.That(unsplit, Is.Empty);

                byte[] bytes = File.ReadAllBytes(path);
                Assert.That(bytes.Take(3).ToArray(), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }), "no BOM is added");
                string written = File.ReadAllText(path);
                Assert.That(written, Does.Contain("    commandLineArgs.add(\"--emit-method-map\")\r\n"));
                // CRLF stays CRLF everywhere: no lone LF was introduced.
                Assert.That(System.Text.RegularExpressions.Regex.IsMatch(written, "(?<!\r)\n"), Is.False);

                // A second run changes nothing and does not touch the file.
                var stamp = File.GetLastWriteTimeUtc(path);
                Assert.That(Il2CppMappingArgs.SplitGradleFile(path, out _), Is.False);
                Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(stamp));
                Assert.That(File.ReadAllText(path), Is.EqualTo(written));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void The_file_step_keeps_a_file_without_a_final_newline_and_ignores_a_missing_file()
        {
            string dir = Path.Combine(Path.GetTempPath(), "bugsee-split-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "build.gradle.kts");
                File.WriteAllText(path, "x\n    commandLineArgs.add(\"--emit-source-mapping --emit-method-map\")");
                Assert.That(Il2CppMappingArgs.SplitGradleFile(path, out _), Is.True);
                Assert.That(File.ReadAllText(path), Is.EqualTo("x\n    commandLineArgs.add(\"--emit-source-mapping\")\n    commandLineArgs.add(\"--emit-method-map\")"));

                Assert.That(Il2CppMappingArgs.SplitGradleFile(Path.Combine(dir, "missing.gradle"), out int[] unsplit), Is.False);
                Assert.That(unsplit, Is.Empty);
                Assert.That(Il2CppMappingArgs.SplitGradleFile(null, out _), Is.False);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        public void Emission_follows_the_backend_and_the_opt_out_only(bool il2cpp, bool optOut, bool expected)
        {
            Assert.That(Il2CppMappingArgs.ShouldEmit(il2cpp, optOut), Is.EqualTo(expected));
        }

        // Emission must not depend on the upload configuration: a build without the token (or one a later CI job uploads) still writes the files.
        [Test]
        public void The_editor_step_does_not_look_at_the_token_or_the_explicit_mapping_path()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!File.Exists(Path.Combine(dir, "package.json")))
            {
                var parent = Directory.GetParent(dir);
                if (parent == null) Assert.Fail("package.json not found above " + TestContext.CurrentContext.TestDirectory);
                dir = parent.FullName;
            }
            var text = File.ReadAllText(Path.Combine(dir, "Editor", "BugseeIl2CppMappingArgs.cs"));
            Assert.That(text, Does.Contain("Il2CppMappingArgs.ShouldEmit("));
            Assert.That(text, Does.Not.Contain("HasAppToken"));
            Assert.That(text, Does.Not.Contain("BUGSEE_IL2CPP_MAPPING\""));
            Assert.That(text, Does.Not.Contain("BUGSEE_NO_SYMBOL_UPLOAD"));
            // The tracked ProjectSettings.asset is not edited for a build.
            Assert.That(text, Does.Not.Contain("SetAdditionalIl2CppArgs"));
        }

        [Test]
        public void A_flag_that_only_shares_a_prefix_does_not_count()
        {
            Assert.That(Il2CppMappingArgs.Ensure("--emit-method-map-extra"), Is.EqualTo("--emit-method-map-extra --emit-source-mapping --emit-method-map"));
        }

        [Test]
        public void Ensure_is_idempotent()
        {
            string once = Il2CppMappingArgs.Ensure("--emit-null-checks");

            Assert.That(Il2CppMappingArgs.Ensure(once), Is.EqualTo(once));
        }

        [TestCase(null, null, "--emit-source-mapping --emit-method-map")]
        [TestCase("", "", "--emit-source-mapping --emit-method-map")]
        [TestCase("--emit-null-checks", null, "--emit-source-mapping --emit-method-map")]
        [TestCase("--emit-source-mapping --emit-method-map", null, null)]
        [TestCase("--emit-source-mapping", "--emit-method-map", null)]
        [TestCase("--emit-source-mapping", null, "--emit-method-map")]
        [TestCase(null, "--foo", "--foo --emit-source-mapping --emit-method-map")]
        [TestCase(null, "--foo ", "--foo --emit-source-mapping --emit-method-map")]
        [TestCase("--emit-method-map", "--foo", "--foo --emit-source-mapping")]
        public void The_environment_value_adds_only_what_neither_Player_Settings_nor_the_environment_has(string playerArgs, string envArgs, string expected)
        {
            Assert.That(Il2CppMappingArgs.EnvironmentValue(playerArgs, envArgs), Is.EqualTo(expected));
        }
    }
}
