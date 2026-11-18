using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The order of the app target's build phases when the "Bugsee Upload Symbols" Run Script phase and the Crash Report
    /// Extension's "Embed ExtensionKit Extensions" phase are both there. With the script first, xcodebuild failed with
    /// "Cycle inside Unity-iPhone": the script's dSYM input waits for the app's Info.plist, which waits for the embedded
    /// extension to be copied, which waited for the script (a later phase). The script has to be the last phase.
    /// </summary>
    public class IosSymbolUploadPhaseOrderTests
    {
        const string UploadGuid = "F2CE4AE9812048D09DE3E73A";
        const string Upload = "Bugsee Upload Symbols";
        const string Embed = "Embed ExtensionKit Extensions";

        const string UploadObject =
            "/* Begin PBXShellScriptBuildPhase section */\n" +
            "\t\t" + UploadGuid + " /* ShellScript */ = {\n" +
            "\t\t\tisa = PBXShellScriptBuildPhase;\n" +
            "\t\t\talwaysOutOfDate = 1;\n" +
            "\t\t\tbuildActionMask = 2147483647;\n" +
            "\t\t\tfiles = (\n\t\t\t);\n" +
            "\t\t\tname = \"Bugsee Upload Symbols\";\n" +
            "\t\t\trunOnlyForDeploymentPostprocessing = 0;\n" +
            "\t\t\tshellPath = /bin/sh;\n" +
            "\t\t\tshellScript = \"/bin/sh \\\"${PROJECT_DIR}/BugseeScripts/upload-symbols.sh\\\"\\n\";\n" +
            "\t\t};\n" +
            "/* End PBXShellScriptBuildPhase section */\n\n";

        const string AppPhases =
            "\t\t\t\t1D60588F0D05DD3D006BFB54 /* Frameworks */,\n\t\t\t\t9D25ABAB213FB47800354C27 /* Embed Frameworks */,\n";

        // The fixture of the extension tests with the upload phase after "Embed Frameworks", where Unity's export puts it.
        static string Fixture(bool upload)
        {
            string unity = XcodeCrashExtensionProjectTests.Unity;
            if (!upload) return unity;
            return unity.Replace("/* Begin PBXProject section */", UploadObject + "/* Begin PBXProject section */")
                        .Replace(AppPhases, AppPhases + "\t\t\t\t" + UploadGuid + " /* ShellScript */,\n");
        }

        static XcodeCrashExtensionSpec Spec() => new XcodeCrashExtensionSpec
        {
            AppTargetGuid = "1D6058900D05DD3D006BFB54", AppBundleId = "com.example.game", Version = "1.2.3", Build = "45",
            AppEntitlementsPathToSet = "Unity-iPhone.entitlements",
        };

        static string WithExtension(string text)
        {
            string result = XcodeCrashExtensionProject.Apply(text, Spec(), out string error);
            Assert.That(result, Is.Not.Null, error);
            return result;
        }

        // The app target's buildPhases, by the name of each phase (the upload phase is a "ShellScript" in the list).
        static List<string> AppPhaseOrder(string text)
        {
            Match app = Regex.Match(text, @"^\t\t1D6058900D05DD3D006BFB54 /\* Unity-iPhone \*/ = \{.*?^\t\t\};\n", RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.That(app.Success, Is.True, "app target");
            Match list = Regex.Match(app.Value, @"buildPhases = \((.*?)\);", RegexOptions.Singleline);
            Assert.That(list.Success, Is.True, "buildPhases");
            var order = new List<string>();
            foreach (Match m in Regex.Matches(list.Groups[1].Value, @"([0-9A-F]{24}) /\* ([^*]*?) \*/"))
            {
                order.Add(m.Groups[1].Value == UploadGuid ? Upload : m.Groups[2].Value);
            }

            return order;
        }

        [Test]
        public void Upload_off_extension_off_leaves_the_phases_as_Unity_wrote_them()
        {
            Assert.That(AppPhaseOrder(Fixture(false)), Is.EqualTo(new[] { "Frameworks", "Embed Frameworks" }));
        }

        [Test]
        public void Upload_on_extension_off_has_the_upload_last()
        {
            Assert.That(AppPhaseOrder(Fixture(true)), Is.EqualTo(new[] { "Frameworks", "Embed Frameworks", Upload }));
            Assert.That(XcodeShellScriptPhase.MoveToEnd(Fixture(true), Upload), Is.EqualTo(Fixture(true)), "already last: no change");
        }

        [Test]
        public void Upload_off_extension_on_has_the_embed_phase_last()
        {
            Assert.That(AppPhaseOrder(WithExtension(Fixture(false))), Is.EqualTo(new[] { "Frameworks", "Embed Frameworks", Embed }));
        }

        [Test]
        public void Upload_on_extension_on_has_the_upload_after_the_embed_phase()
        {
            Assert.That(AppPhaseOrder(WithExtension(Fixture(true))), Is.EqualTo(new[] { "Frameworks", "Embed Frameworks", Embed, Upload }));
        }

        [Test]
        public void Upload_added_after_the_extension_goes_after_it_too()
        {
            // The other order of the two post-processes (the upload step run again on an Append export that has both).
            string extensionOnly = WithExtension(Fixture(false));
            string withUpload = extensionOnly.Replace("\t\t\t\t849D05CC0FF4FCB40F10E3FD", "\t\t\t\t" + UploadGuid + " /* ShellScript */,\n\t\t\t\t849D05CC0FF4FCB40F10E3FD");
            if (withUpload == extensionOnly)
            {
                // The embed phase's guid is generated: put the upload before the last list entry whatever it is.
                withUpload = Regex.Replace(extensionOnly, @"(\t\t\t\t[0-9A-F]{24} /\* " + Embed + @" \*/,\n)(\t\t\t\);)",
                    "\t\t\t\t" + UploadGuid + " /* ShellScript */,\n$1$2");
            }

            withUpload = withUpload.Replace("/* Begin PBXProject section */", UploadObject + "/* Begin PBXProject section */");
            Assert.That(AppPhaseOrder(withUpload), Is.EqualTo(new[] { "Frameworks", "Embed Frameworks", Upload, Embed }), "set up: the broken order");
            Assert.That(AppPhaseOrder(XcodeShellScriptPhase.MoveToEnd(withUpload, Upload)), Is.EqualTo(new[] { "Frameworks", "Embed Frameworks", Embed, Upload }));
        }

        [Test]
        public void Refreshing_an_export_that_has_the_broken_order_repairs_it()
        {
            string good = WithExtension(Fixture(true));
            Assert.That(XcodeShellScriptPhase.MoveToEnd(good, "no such phase"), Is.EqualTo(good), "a name nothing has changes nothing");
            // The earlier package version's export: upload first, then the embed phase.
            string swapped = Regex.Replace(good, @"(\t\t\t\t[0-9A-F]{24} /\* " + Embed + @" \*/,\n)(\t\t\t\t" + UploadGuid + @" /\* ShellScript \*/,\n)", "$2$1");
            Assert.That(swapped, Is.Not.EqualTo(good));
            Assert.That(AppPhaseOrder(swapped), Is.EqualTo(new[] { "Frameworks", "Embed Frameworks", Upload, Embed }));
            Assert.That(WithExtension(swapped), Is.EqualTo(good));
        }

        [Test]
        public void Running_the_steps_again_changes_nothing()
        {
            string once = WithExtension(Fixture(true));
            Assert.That(WithExtension(once), Is.EqualTo(once));
            Assert.That(XcodeShellScriptPhase.MoveToEnd(once, Upload), Is.EqualTo(once));
            Assert.That(AppPhaseOrder(WithExtension(WithExtension(once))), Is.EqualTo(new[] { "Frameworks", "Embed Frameworks", Embed, Upload }));
        }

        [Test]
        public void Removing_the_extension_and_adding_it_again_ends_with_the_same_order()
        {
            string once = WithExtension(Fixture(true));
            string removed = XcodeCrashExtensionProject.Remove(once, out string error);
            Assert.That(removed, Is.Not.Null, error);
            Assert.That(AppPhaseOrder(removed), Is.EqualTo(new[] { "Frameworks", "Embed Frameworks", Upload }));
            Assert.That(AppPhaseOrder(WithExtension(removed)), Is.EqualTo(new[] { "Frameworks", "Embed Frameworks", Embed, Upload }));
        }
    }
}
