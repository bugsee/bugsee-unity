using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosSymbolUploadPhaseTests
    {
        const string PhaseName = "Bugsee Upload Symbols";
        const string NewScript =
            "/bin/sh \"${PROJECT_DIR}/BugseeScripts/upload-symbols.sh\" || echo \"warning: Bugsee: symbol upload script failed (the build continues).\"\n";

        // As Unity 2021.3 wrote them in an export of the sample: another tool's phase, then the phase of the earlier
        // package version (bare path, fails with "Permission denied").
        const string OtherPhase =
            "\t\tCA8A3E04DB9B195EABF139B7 /* ShellScript */ = {\n" +
            "\t\t\tisa = PBXShellScriptBuildPhase;\n" +
            "\t\t\tbuildActionMask = 2147483647;\n" +
            "\t\t\tfiles = (\n\t\t\t);\n" +
            "\t\t\tname = \"[CP] Check Pods Manifest.lock\";\n" +
            "\t\t\trunOnlyForDeploymentPostprocessing = 0;\n" +
            "\t\t\tshellPath = /bin/sh;\n" +
            "\t\t\tshellScript = \"diff \\\"${PODS_PODFILE_DIR_PATH}/Podfile.lock\\\" \\\"${PODS_ROOT}/Manifest.lock\\\" > /dev/null\\nexit 1\\n\";\n" +
            "\t\t\tshowEnvVarsInLog = 0;\n" +
            "\t\t};\n";

        const string OldPhase =
            "\t\tF2CE4AE9812048D09DE3E73A /* ShellScript */ = {\n" +
            "\t\t\tisa = PBXShellScriptBuildPhase;\n" +
            "\t\t\tbuildActionMask = 2147483647;\n" +
            "\t\t\tfiles = (\n\t\t\t);\n" +
            "\t\t\tname = \"Bugsee Upload Symbols\";\n" +
            "\t\t\trunOnlyForDeploymentPostprocessing = 0;\n" +
            "\t\t\tshellPath = /bin/sh;\n" +
            "\t\t\tshellScript = \"\\\"${PROJECT_DIR}/BugseeScripts/upload-symbols.sh\\\"\\n\";\n" +
            "\t\t};\n";

        // The same phase as Unity writes it after adding it with NewScript to a fresh export.
        const string NewScriptLine =
            "shellScript = \"/bin/sh \\\"${PROJECT_DIR}/BugseeScripts/upload-symbols.sh\\\" || echo \\\"warning: Bugsee: symbol upload script failed (the build continues).\\\"\\n\";";

        static string Section(string phases) =>
            "/* Begin PBXShellScriptBuildPhase section */\n" + phases + "/* End PBXShellScriptBuildPhase section */\n\n" +
            "/* Begin PBXSourcesBuildPhase section */\n" +
            "\t\t1D60588E0D05DD3D006BFB54 /* Sources */ = {\n" +
            "\t\t\tisa = PBXSourcesBuildPhase;\n" +
            "\t\t};\n";

        static string RepoFile(string relative)
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json"))) dir = dir.Parent;
            Assert.That(dir, Is.Not.Null, "repo root (package.json) not found");
            return Path.Combine(dir.FullName, relative);
        }

        // The script is written from C# and so has no executable bit. Run directly, Xcode fails every iOS
        // build with "Permission denied" - observed on the first simulator build of the sample.
        [Test]
        public void The_run_script_phase_invokes_the_script_through_sh_and_cannot_fail_the_build()
        {
            var source = File.ReadAllText(RepoFile("Editor/BugseeSymbolUpload.cs"));
            Assert.That(source, Does.Contain("/bin/sh \\\"${PROJECT_DIR}/BugseeScripts/upload-symbols.sh\\\" ||"));
            Assert.That(source, Does.Not.Contain("\"\\\"${PROJECT_DIR}/BugseeScripts/upload-symbols.sh\\\"\\n\""),
                "the bare path needs an executable bit the written file does not have");
        }

        // Unity's "Append" export keeps the earlier phase; AddShellScriptBuildPhase would add a second one behind it.
        [Test]
        public void The_injector_updates_an_existing_phase_before_adding_one()
        {
            var source = File.ReadAllText(RepoFile("Editor/BugseeSymbolUpload.cs"));
            int rewrite = source.IndexOf("XcodeShellScriptPhase.RewriteScript(project.WriteToString(), phaseName, shellScript)");
            int add = source.IndexOf("project.AddShellScriptBuildPhase(target, phaseName, \"/bin/sh\", shellScript)");
            Assert.That(rewrite, Is.GreaterThan(0));
            Assert.That(add, Is.GreaterThan(rewrite));
        }

        [Test]
        public void The_phase_of_an_earlier_version_gets_the_new_script_and_nothing_else_changes()
        {
            string before = Section(OtherPhase + OldPhase);
            string after = XcodeShellScriptPhase.RewriteScript(before, PhaseName, NewScript);

            string expected = Section(OtherPhase + OldPhase.Replace(
                "shellScript = \"\\\"${PROJECT_DIR}/BugseeScripts/upload-symbols.sh\\\"\\n\";", NewScriptLine));
            Assert.That(after, Is.Not.EqualTo(before));
            Assert.That(after, Is.EqualTo(expected));
        }

        [Test]
        public void A_current_phase_or_a_project_without_one_is_left_unchanged()
        {
            string current = Section(OtherPhase + OldPhase.Replace(
                "shellScript = \"\\\"${PROJECT_DIR}/BugseeScripts/upload-symbols.sh\\\"\\n\";", NewScriptLine));
            Assert.That(XcodeShellScriptPhase.RewriteScript(current, PhaseName, NewScript), Is.EqualTo(current));

            string none = Section(OtherPhase);
            Assert.That(XcodeShellScriptPhase.RewriteScript(none, PhaseName, NewScript), Is.EqualTo(none));
        }
    }
}
