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

        static readonly string[] Inputs =
        {
            "$(DWARF_DSYM_FOLDER_PATH)/$(DWARF_DSYM_FILE_NAME)/Contents/Resources/DWARF/$(EXECUTABLE_NAME)",
            "$(DWARF_DSYM_FOLDER_PATH)/UnityFramework.framework.dSYM/Contents/Resources/DWARF/UnityFramework",
        };

        const string InputsBlock =
            "\t\t\talwaysOutOfDate = 1;\n" +
            "\t\t\tinputPaths = (\n" +
            "\t\t\t\t\"$(DWARF_DSYM_FOLDER_PATH)/$(DWARF_DSYM_FILE_NAME)/Contents/Resources/DWARF/$(EXECUTABLE_NAME)\",\n" +
            "\t\t\t\t\"$(DWARF_DSYM_FOLDER_PATH)/UnityFramework.framework.dSYM/Contents/Resources/DWARF/UnityFramework\",\n" +
            "\t\t\t);\n";

        const string CurrentPhase =
            "\t\tF2CE4AE9812048D09DE3E73A /* ShellScript */ = {\n" +
            "\t\t\tisa = PBXShellScriptBuildPhase;\n" +
            "\t\t\tbuildActionMask = 2147483647;\n" +
            "\t\t\tfiles = (\n\t\t\t);\n" +
            "\t\t\tname = \"Bugsee Upload Symbols\";\n" +
            "\t\t\trunOnlyForDeploymentPostprocessing = 0;\n" +
            "\t\t\tshellPath = /bin/sh;\n" +
            "\t\t\t" + NewScriptLine + "\n" +
            "\t\t};\n";

        // Without inputs Xcode may run the phase before the app's dSYM is written (seen in an archive of the sample: the
        // phase starts within a fraction of a second of the task that writes it), and warns on every build.
        [Test]
        public void The_phase_gets_the_dsym_inputs_and_always_out_of_date_and_other_phases_are_untouched()
        {
            string before = Section(OtherPhase + CurrentPhase);
            string after = XcodeShellScriptPhase.SetInputs(before, PhaseName, Inputs);

            string expected = Section(OtherPhase + CurrentPhase.Replace(
                "\t\t\tisa = PBXShellScriptBuildPhase;\n", "\t\t\tisa = PBXShellScriptBuildPhase;\n" + InputsBlock));
            Assert.That(after, Is.EqualTo(expected));
            Assert.That(after, Does.Contain("inputPaths = ("));
            Assert.That(after.Split(new[] { "inputPaths" }, System.StringSplitOptions.None).Length, Is.EqualTo(2),
                "only the Bugsee phase gets inputs");
        }

        [Test]
        public void Setting_the_inputs_twice_changes_nothing_the_second_time()
        {
            string once = XcodeShellScriptPhase.SetInputs(Section(OtherPhase + CurrentPhase), PhaseName, Inputs);
            Assert.That(XcodeShellScriptPhase.SetInputs(once, PhaseName, Inputs), Is.EqualTo(once));
        }

        // An Append export reads the phase of the earlier export back: its inputs come off before the phase is re-added,
        // or PBXProject does not recognise it and adds a second one.
        [Test]
        public void Inputs_of_an_earlier_export_come_off_again()
        {
            string plain = Section(OtherPhase + CurrentPhase);
            string withInputs = XcodeShellScriptPhase.SetInputs(plain, PhaseName, Inputs);

            Assert.That(XcodeShellScriptPhase.SetInputs(withInputs, PhaseName, null), Is.EqualTo(plain));
            Assert.That(XcodeShellScriptPhase.SetInputs(withInputs, PhaseName, new string[0]), Is.EqualTo(plain));
            Assert.That(XcodeShellScriptPhase.SetInputs(plain, PhaseName, null), Is.EqualTo(plain));
        }

        [Test]
        public void Inputs_are_replaced_not_added_to()
        {
            string other = XcodeShellScriptPhase.SetInputs(Section(CurrentPhase), PhaseName, new[] { "$(A)/b" });
            string after = XcodeShellScriptPhase.SetInputs(other, PhaseName, Inputs);

            Assert.That(after, Is.EqualTo(Section(CurrentPhase.Replace(
                "\t\t\tisa = PBXShellScriptBuildPhase;\n", "\t\t\tisa = PBXShellScriptBuildPhase;\n" + InputsBlock))));
        }

        // The injector adds the inputs after the phase is written, turns the script sandbox off for the app target, and
        // the Run Script's own input list names the dSYM files.
        [Test]
        public void The_injector_declares_the_inputs_and_turns_user_script_sandboxing_off_for_the_target()
        {
            var source = File.ReadAllText(RepoFile("Editor/BugseeSymbolUpload.cs"));
            Assert.That(source, Does.Contain("project.SetBuildProperty(target, \"ENABLE_USER_SCRIPT_SANDBOXING\", \"NO\")"));
            Assert.That(source, Does.Contain("XcodeShellScriptPhase.SetInputs(File.ReadAllText(projectPath), phaseName, DsymInputPaths)"));
            Assert.That(source, Does.Contain("$(DWARF_DSYM_FOLDER_PATH)/$(DWARF_DSYM_FILE_NAME)/Contents/Resources/DWARF/$(EXECUTABLE_NAME)"));
            Assert.That(source, Does.Contain("$(DWARF_DSYM_FOLDER_PATH)/UnityFramework.framework.dSYM/Contents/Resources/DWARF/UnityFramework"));
            int strip = source.IndexOf("phaseName, null)");
            int add = source.IndexOf("project.AddShellScriptBuildPhase(target, phaseName");
            Assert.That(strip, Is.GreaterThan(0));
            Assert.That(add, Is.GreaterThan(strip), "an earlier export's inputs come off before the phase is added again");
        }

        // The export has no UnityFramework binary yet: one in the export folder is an earlier build's leftover.
        [Test]
        public void The_export_time_module_id_scan_reports_nothing_on_ios_and_a_stale_pointer_goes_at_every_export()
        {
            var identity = File.ReadAllText(RepoFile("Editor/BugseeIl2CppModuleIdentity.cs"));
            int platform = identity.IndexOf("report.summary.platform == BuildTarget.iOS || report.summary.platform == BuildTarget.tvOS");
            int scan = identity.IndexOf("CollectCandidateBinaries(root, candidates)");
            Assert.That(platform, Is.GreaterThan(0));
            Assert.That(scan, Is.GreaterThan(platform));

            var linemap = File.ReadAllText(RepoFile("Editor/BugseeIl2CppLinemapUpload.cs"));
            int delete = linemap.IndexOf("DeleteIosPendingLinemap(report);");
            int gate = linemap.IndexOf("GetEnvironmentVariable(\"BUGSEE_NO_IL2CPP_MAPPING\")");
            Assert.That(delete, Is.GreaterThan(0));
            Assert.That(delete, Is.LessThan(gate), "removed before any gate that can return");
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
