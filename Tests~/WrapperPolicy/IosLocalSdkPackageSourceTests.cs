using System.IO;
using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosLocalSdkPackageSourceTests
    {
        static string Read(params string[] relative)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "package.json")))
            {
                dir = Directory.GetParent(dir)?.FullName;
            }

            Assert.That(dir, Is.Not.Null.And.Not.Empty, "Could not find repo root (package.json)");
            return File.ReadAllText(Path.Combine(dir, Path.Combine(relative)));
        }

        // What Unity's PBXProject writes for a remote package linked into two targets.
        const string UnityRemote =
            "/* Begin PBXProject section */\n\t\t\tpackageReferences = (\n\t\t\t\t69C04F549EE201CC179B0684 /* XCRemoteSwiftPackageReference \"spm\" */,\n\t\t\t);\n/* End PBXProject section */\n" +
            "/* Begin XCRemoteSwiftPackageReference section */\n" +
            "\t\t69C04F549EE201CC179B0684 /* XCRemoteSwiftPackageReference \"spm\" */ = {\n\t\t\tisa = XCRemoteSwiftPackageReference;\n" +
            "\t\t\trepositoryURL = \"https://github.com/bugsee/spm.git\";\n\t\t\trequirement = {\n\t\t\t\tkind = exactVersion;\n\t\t\t\tversion = \"7.0.0-beta6\";\n\t\t\t};\n\t\t};\n" +
            "/* End XCRemoteSwiftPackageReference section */\n" +
            "/* Begin XCSwiftPackageProductDependency section */\n" +
            "\t\t72A742839A820990A8F88B84 /* Bugsee */ = {\n\t\t\tisa = XCSwiftPackageProductDependency;\n\t\t\tpackage = 69C04F549EE201CC179B0684 /* XCRemoteSwiftPackageReference \"spm\" */;\n\t\t\tproductName = Bugsee;\n\t\t};\n" +
            "/* End XCSwiftPackageProductDependency section */\n";

        const string SpmUrl = "https://github.com/bugsee/spm.git";

        [Test]
        public void Remote_reference_becomes_a_local_path_reference_with_the_link_left_alone()
        {
            string text = XcodeLocalPackage.FromRemote(UnityRemote, SpmUrl, "Bugsee");
            Assert.That(text, Does.Contain("isa = XCLocalSwiftPackageReference;\n\t\t\trelativePath = Bugsee;"));
            Assert.That(text, Does.Contain("69C04F549EE201CC179B0684 /* XCLocalSwiftPackageReference \"Bugsee\" */,"));
            Assert.That(text, Does.Not.Contain("XCRemoteSwiftPackageReference"));
            Assert.That(text, Does.Not.Contain("repositoryURL"));
            Assert.That(text, Does.Not.Contain("package = "));
            Assert.That(text, Does.Contain("productName = Bugsee;"));
        }

        [Test]
        public void Other_remote_packages_stay_remote()
        {
            string other =
                "\t\tAAAAAAAAAAAAAAAAAAAAAAAA /* XCRemoteSwiftPackageReference \"other\" */ = {\n\t\t\tisa = XCRemoteSwiftPackageReference;\n" +
                "\t\t\trepositoryURL = \"https://example.com/other.git\";\n\t\t\trequirement = {\n\t\t\t\tkind = exactVersion;\n\t\t\t\tversion = 1.0.0;\n\t\t\t};\n\t\t};\n";
            string text = XcodeLocalPackage.FromRemote(
                UnityRemote.Replace("/* Begin XCRemoteSwiftPackageReference section */\n", "/* Begin XCRemoteSwiftPackageReference section */\n" + other),
                SpmUrl, "Bugsee");
            Assert.That(text, Does.Contain("repositoryURL = \"https://example.com/other.git\""));
            Assert.That(text, Does.Contain("/* Begin XCRemoteSwiftPackageReference section */"));
            Assert.That(text, Does.Contain("relativePath = Bugsee;"));
            Assert.That(text, Does.Not.Contain(SpmUrl));
        }

        [Test]
        public void A_project_without_the_remote_reference_is_not_converted()
        {
            Assert.That(XcodeLocalPackage.FromRemote("/* Begin PBXProject section */\n", SpmUrl, "Bugsee"), Is.Null);
            Assert.That(XcodeLocalPackage.FromRemote(UnityRemote, "https://example.com/else.git", "Bugsee"), Is.Null);
        }

        // A project an earlier export linked once, remote (what Append mode leaves behind).
        const string Linked =
            "/* Begin PBXBuildFile section */\n" +
            "\t\t228B4264A0A8E7455A559FD4 /* Bugsee in Frameworks */ = {isa = PBXBuildFile; productRef = 72A742839A820990A8F88B84 /* Bugsee */; };\n" +
            "\t\tAAAAAAAAAAAAAAAAAAAAAAAA /* Other in Frameworks */ = {isa = PBXBuildFile; productRef = BBBBBBBBBBBBBBBBBBBBBBBB /* Other */; };\n" +
            "/* End PBXBuildFile section */\n" +
            "\t\t\tpackageProductDependencies = (\n\t\t\t\t72A742839A820990A8F88B84 /* Bugsee */,\n\t\t\t\tBBBBBBBBBBBBBBBBBBBBBBBB /* Other */,\n\t\t\t);\n" +
            "\t\t\tpackageReferences = (\n\t\t\t\t69C04F549EE201CC179B0684 /* XCRemoteSwiftPackageReference \"spm\" */,\n\t\t\t);\n" +
            "/* Begin XCRemoteSwiftPackageReference section */\n" +
            "\t\t69C04F549EE201CC179B0684 /* XCRemoteSwiftPackageReference \"spm\" */ = {\n\t\t\tisa = XCRemoteSwiftPackageReference;\n" +
            "\t\t\trepositoryURL = \"https://github.com/bugsee/spm.git\";\n\t\t\trequirement = {\n\t\t\t\tkind = exactVersion;\n\t\t\t\tversion = \"7.0.0-beta6\";\n\t\t\t};\n\t\t};\n" +
            "/* End XCRemoteSwiftPackageReference section */\n" +
            "/* Begin XCSwiftPackageProductDependency section */\n" +
            "\t\t72A742839A820990A8F88B84 /* Bugsee */ = {\n\t\t\tisa = XCSwiftPackageProductDependency;\n\t\t\tpackage = 69C04F549EE201CC179B0684 /* XCRemoteSwiftPackageReference \"spm\" */;\n\t\t\tproductName = Bugsee;\n\t\t};\n" +
            "\t\tBBBBBBBBBBBBBBBBBBBBBBBB /* Other */ = {\n\t\t\tisa = XCSwiftPackageProductDependency;\n\t\t\tproductName = Other;\n\t\t};\n" +
            "/* End XCSwiftPackageProductDependency section */\n";

        [Test]
        public void Removing_the_package_drops_every_bugsee_link_and_keeps_the_rest()
        {
            string text = XcodeLocalPackage.RemoveBugseePackage(Linked, SpmUrl, "Bugsee");
            Assert.That(text, Does.Not.Contain("Bugsee in Frameworks"));
            Assert.That(text, Does.Not.Contain("72A742839A820990A8F88B84"));
            Assert.That(text, Does.Not.Contain("69C04F549EE201CC179B0684"));
            Assert.That(text, Does.Not.Contain("XCRemoteSwiftPackageReference"));
            Assert.That(text, Does.Contain("Other in Frameworks"));
            Assert.That(text, Does.Contain("BBBBBBBBBBBBBBBBBBBBBBBB /* Other */,"));
            Assert.That(text, Does.Contain("productName = Other;"));
        }

        [Test]
        public void Linking_twice_through_remove_first_yields_one_reference()
        {
            string once = XcodeLocalPackage.FromRemote(Linked, SpmUrl, "Bugsee");
            string again = XcodeLocalPackage.RemoveBugseePackage(once, SpmUrl, "Bugsee");
            Assert.That(again, Does.Not.Contain("XCLocalSwiftPackageReference"));
            Assert.That(again, Does.Not.Contain("Bugsee in Frameworks"));
            Assert.That(XcodeLocalPackage.RemoveBugseePackage(again, SpmUrl, "Bugsee"), Is.EqualTo(again));
        }

        [Test]
        public void Every_remote_reference_to_the_repository_is_converted()
        {
            string second = Linked.Replace("69C04F549EE201CC179B0684", "69C04F549EE201CC179B0685");
            string both = Linked.Replace("/* Begin XCRemoteSwiftPackageReference section */\n",
                "/* Begin XCRemoteSwiftPackageReference section */\n" + second.Substring(
                    second.IndexOf("\t\t69C04F549EE201CC179B0685 /* XCRemote", System.StringComparison.Ordinal),
                    second.IndexOf("/* End XCRemote", System.StringComparison.Ordinal) - second.IndexOf("\t\t69C04F549EE201CC179B0685 /* XCRemote", System.StringComparison.Ordinal)));
            string text = XcodeLocalPackage.FromRemote(both, SpmUrl, "Bugsee");
            Assert.That(text, Does.Not.Contain("XCRemoteSwiftPackageReference"));
            Assert.That(System.Text.RegularExpressions.Regex.Matches(text, "isa = XCLocalSwiftPackageReference;").Count, Is.EqualTo(2));
            Assert.That(System.Text.RegularExpressions.Regex.Matches(text, "Begin XCLocalSwiftPackageReference section").Count, Is.EqualTo(1));
        }

        // The same project after Unity's PBXProject.WriteToFile: package references lose their trailing comments.
        const string RoundTripped =
            "/* Begin PBXBuildFile section */\n" +
            "\t\t228B4264A0A8E7455A559FD4 /* Bugsee in Frameworks */ = {isa = PBXBuildFile; productRef = 72A742839A820990A8F88B84 /* Bugsee */; };\n" +
            "\t\tCCCCCCCCCCCCCCCCCCCCCCCC /* BugseeSwiftUI in Frameworks */ = {isa = PBXBuildFile; productRef = DDDDDDDDDDDDDDDDDDDDDDDD /* BugseeSwiftUI */; };\n" +
            "/* End PBXBuildFile section */\n" +
            "\t\t\tpackageProductDependencies = (\n\t\t\t\t72A742839A820990A8F88B84 /* Bugsee */,\n\t\t\t\tDDDDDDDDDDDDDDDDDDDDDDDD /* BugseeSwiftUI */,\n\t\t\t);\n" +
            "\t\t\tpackageReferences = (\n\t\t\t\t493B4ABD96E30E264332E7A8,\n\t\t\t);\n" +
            "/* Begin XCLocalSwiftPackageReference section */\n" +
            "\t\t493B4ABD96E30E264332E7A8 = {\n\t\t\tisa = XCLocalSwiftPackageReference;\n\t\t\trelativePath = Bugsee;\n\t\t};\n" +
            "/* End XCLocalSwiftPackageReference section */\n\n" +
            "/* Begin XCSwiftPackageProductDependency section */\n" +
            "\t\t72A742839A820990A8F88B84 /* Bugsee */ = {\n\t\t\tisa = XCSwiftPackageProductDependency;\n\t\t\tproductName = Bugsee;\n\t\t};\n" +
            "\t\tDDDDDDDDDDDDDDDDDDDDDDDD /* BugseeSwiftUI */ = {\n\t\t\tisa = XCSwiftPackageProductDependency;\n\t\t\tpackage = 493B4ABD96E30E264332E7A8;\n\t\t\tproductName = BugseeSwiftUI;\n\t\t};\n" +
            "/* End XCSwiftPackageProductDependency section */\n";

        [Test]
        public void A_comment_less_local_reference_from_a_unity_rewrite_is_removed()
        {
            string text = XcodeLocalPackage.RemoveBugseePackage(RoundTripped, SpmUrl, "Bugsee");
            Assert.That(text, Does.Not.Contain("493B4ABD96E30E264332E7A8,"));
            Assert.That(text, Does.Not.Contain("XCLocalSwiftPackageReference"));
            Assert.That(text, Does.Not.Contain("productName = Bugsee;"));
            Assert.That(text, Does.Not.Contain("72A742839A820990A8F88B84"));
            // The user's BugseeSwiftUI product is theirs: it and its build file stay, to be re-pointed at the new reference.
            Assert.That(text, Does.Contain("productName = BugseeSwiftUI;"));
            Assert.That(text, Does.Contain("CCCCCCCCCCCCCCCCCCCCCCCC /* BugseeSwiftUI in Frameworks */"));
            Assert.That(text, Does.Contain("DDDDDDDDDDDDDDDDDDDDDDDD /* BugseeSwiftUI */,"));
        }

        [Test]
        public void The_users_other_products_follow_the_new_reference_in_both_modes()
        {
            string stripped = XcodeLocalPackage.RemoveBugseePackage(RoundTripped, SpmUrl, "Bugsee", out var removed);
            string added = XcodeLocalPackage.RepointProducts(stripped + UnityRemote, "69C04F549EE201CC179B0684", removed);
            Assert.That(added, Does.Contain("package = 69C04F549EE201CC179B0684;\n\t\t\tproductName = BugseeSwiftUI;"));
            Assert.That(added, Does.Not.Contain("493B4ABD96E30E264332E7A8"));
            // Remote mode keeps the link to the new reference; local mode drops the package key as Xcode writes it.
            string local = XcodeLocalPackage.FromRemote(added, SpmUrl, "Bugsee");
            Assert.That(local, Does.Contain("productName = BugseeSwiftUI;"));
            Assert.That(local, Does.Not.Contain("package = "));
            Assert.That(XcodeLocalPackage.RepointProducts(UnityRemote, "69C04F549EE201CC179B0684", removed), Is.EqualTo(UnityRemote));
        }

        [Test]
        public void A_users_own_package_providing_a_product_named_bugsee_is_untouched()
        {
            string fork =
                "/* Begin XCRemoteSwiftPackageReference section */\n" +
                "\t\tEEEEEEEEEEEEEEEEEEEEEEEE /* XCRemoteSwiftPackageReference \"spm-fork\" */ = {\n\t\t\tisa = XCRemoteSwiftPackageReference;\n" +
                "\t\t\trepositoryURL = \"https://github.com/me/spm.git\";\n\t\t\trequirement = {\n\t\t\t\tkind = branch;\n\t\t\t\tbranch = main;\n\t\t\t};\n\t\t};\n" +
                "/* End XCRemoteSwiftPackageReference section */\n" +
                "/* Begin XCSwiftPackageProductDependency section */\n" +
                "\t\tFFFFFFFFFFFFFFFFFFFFFFFF /* Bugsee */ = {\n\t\t\tisa = XCSwiftPackageProductDependency;\n\t\t\tpackage = EEEEEEEEEEEEEEEEEEEEEEEE /* XCRemoteSwiftPackageReference \"spm-fork\" */;\n\t\t\tproductName = Bugsee;\n\t\t};\n" +
                "/* End XCSwiftPackageProductDependency section */\n";
            string text = XcodeLocalPackage.RemoveBugseePackage(fork + Linked, SpmUrl, "Bugsee");
            Assert.That(text, Does.Contain("EEEEEEEEEEEEEEEEEEEEEEEE /* XCRemoteSwiftPackageReference \"spm-fork\" */ = {"));
            Assert.That(text, Does.Contain("FFFFFFFFFFFFFFFFFFFFFFFF /* Bugsee */ = {"));
            Assert.That(text, Does.Not.Contain("72A742839A820990A8F88B84"));
            // A local product with no package key is only ours when a local reference of ours was removed.
            Assert.That(XcodeLocalPackage.RemoveBugseePackage(
                "/* Begin XCSwiftPackageProductDependency section */\n\t\t72A742839A820990A8F88B84 /* Bugsee */ = {\n\t\t\tisa = XCSwiftPackageProductDependency;\n\t\t\tproductName = Bugsee;\n\t\t};\n/* End XCSwiftPackageProductDependency section */\n",
                SpmUrl, "Bugsee"), Does.Contain("productName = Bugsee"));
        }

        [Test]
        public void A_package_less_swiftui_product_of_a_local_package_follows_a_switch_to_remote()
        {
            string local = XcodeLocalPackage.RemoveBugseePackage(RoundTripped.Replace("\t\t\tpackage = 493B4ABD96E30E264332E7A8;\n", ""), SpmUrl, "Bugsee", out var removed);
            string added = XcodeLocalPackage.RepointProducts(local + UnityRemote, "69C04F549EE201CC179B0684", removed);
            Assert.That(added, Does.Contain("package = 69C04F549EE201CC179B0684;\n\t\t\tproductName = BugseeSwiftUI;"));
        }

        [Test]
        public void Remove_then_unity_add_then_convert_yields_one_local_reference()
        {
            // What the export does to a project an earlier export left with a comment-less local reference:
            // strip it, let Unity add the remote one, convert that.
            string stripped = XcodeLocalPackage.RemoveBugseePackage(RoundTripped, SpmUrl, "Bugsee", out var removed);
            string added = XcodeLocalPackage.RepointProducts(stripped + UnityRemote, "69C04F549EE201CC179B0684", removed);
            string converted = XcodeLocalPackage.FromRemote(added, SpmUrl, "Bugsee");
            Assert.That(System.Text.RegularExpressions.Regex.Matches(converted, "isa = XCLocalSwiftPackageReference;").Count, Is.EqualTo(1));
            Assert.That(converted, Does.Not.Contain("XCRemoteSwiftPackageReference"));
            Assert.That(converted, Does.Not.Contain("package = "));
        }

        [Test]
        public void Dropped_logexception_options_are_found_in_a_fixed_order()
        {
            var options = new System.Collections.Generic.Dictionary<string, object>
            {
                { "mergingRules", 1 }, { "skipFrames", 2 }, { "labels", 3 }, { "includeVideo", false },
            };
            Assert.That(ExceptionOptionDrops.PresentIn(options), Is.EqualTo(new[] { "labels", "includeVideo", "mergingRules" }));
            Assert.That(ExceptionOptionDrops.PresentIn(new System.Collections.Generic.Dictionary<string, object> { { "skipFrames", 1 } }), Is.Empty);
            Assert.That(ExceptionOptionDrops.PresentIn(null), Is.Empty);
        }

        [Test]
        public void The_export_runs_with_the_builds_own_options()
        {
            string text = Read("Editor", "BugseeIosSpmPostProcess.cs");
            Assert.That(text, Does.Contain("IPostprocessBuildWithReport"));
            Assert.That(text, Does.Contain("(report.summary.options & BuildOptions.Development) != 0"));
            // The checkbox is read only by the [Obsolete] forwarder for old callers, never by the export itself.
            Assert.That(System.Text.RegularExpressions.Regex.Matches(text, "EditorUserBuildSettings.development").Count, Is.EqualTo(1));
        }

        [Test]
        public void Release_placeholders_of_the_flip_patch_never_reach_a_tree_that_passes()
        {
            foreach (var file in new[] { new[] { "Tools~", "versions.env" }, new[] { "Tests~", "iOS", "run-tests.sh" }, new[] { "Runtime", "BugseePackageVersion.cs" } })
            {
                string text = Read(file);
                Assert.That(text, Does.Not.Contain("<commit of the bugsee/spm"), string.Join("/", file));
                Assert.That(text, Does.Not.Contain("<sha256 of"), string.Join("/", file));
            }
        }

        [Test]
        public void Export_removes_its_copied_package_only_when_it_made_it()
        {
            string text = Read("Editor", "BugseeIosSpmPostProcess.cs");
            Assert.That(text, Does.Contain("LocalPackageMarker = \".bugsee-local-package\""));
            Assert.That(text, Does.Contain("File.Exists(Path.Combine(packageFolder, LocalPackageMarker))"));
        }

        [Test]
        public void An_unrelated_product_with_a_foreign_package_id_is_not_adopted()
        {
            string foreign =
                "/* Begin XCSwiftPackageProductDependency section */\n" +
                "\t\t9999999999999999999999AA /* Other */ = {\n\t\t\tisa = XCSwiftPackageProductDependency;\n\t\t\tpackage = 8888888888888888888888BB;\n\t\t\tproductName = Other;\n\t\t};\n" +
                "/* End XCSwiftPackageProductDependency section */\n";
            string stripped = XcodeLocalPackage.RemoveBugseePackage(RoundTripped + foreign, SpmUrl, "Bugsee", out var removed);
            string added = XcodeLocalPackage.RepointProducts(stripped + UnityRemote, "69C04F549EE201CC179B0684", removed);
            Assert.That(added, Does.Contain("package = 8888888888888888888888BB;\n\t\t\tproductName = Other;"));
            Assert.That(added, Does.Contain("package = 69C04F549EE201CC179B0684;\n\t\t\tproductName = BugseeSwiftUI;"));
        }

        [Test]
        public void Export_replaces_only_a_package_folder_it_made_and_keeps_the_arming_flag_to_one_launch()
        {
            string editor = Read("Editor", "BugseeIosSpmPostProcess.cs");
            Assert.That(editor, Does.Contain("is not a copy of the Bugsee package made by an earlier export"));
            Assert.That(editor, Does.Contain("is the export's own '{LocalPackageFolderName}' folder"));
            int refusal = editor.IndexOf("is not a copy of the Bugsee package", System.StringComparison.Ordinal);
            Assert.That(refusal, Is.LessThan(editor.IndexOf("Directory.Delete(destPackageDir, true)", System.StringComparison.Ordinal)));
            // The folder's contents never prove it is ours: the marker goes in before any copy, so a failed copy stays ours.
            Assert.That(editor, Does.Not.Contain("Directory.Exists(Path.Combine(destPackageDir, \"Bugsee.xcframework\"))"));
            int marker = editor.IndexOf("File.WriteAllText(Path.Combine(destPackageDir, LocalPackageMarker)", System.StringComparison.Ordinal);
            Assert.That(marker, Is.GreaterThan(0));
            Assert.That(marker, Is.LessThan(editor.IndexOf("CopyPackagePreservingLinks(localPackageSource, destPackageDir);", System.StringComparison.Ordinal)));
            Assert.That(marker, Is.LessThan(editor.IndexOf("CopyDirectory(localPackageSource, destPackageDir);", System.StringComparison.Ordinal)));
            // Same directory under another spelling is found by probing, and a copy that leaves no package fails loudly.
            Assert.That(editor, Does.Contain(".bugsee-same-dir-probe-"));
            Assert.That(editor, Does.Contain("left no usable package"));
            string sample = Read("Samples~", "BugseeMiniGame", "Assets", "Scripts", "BugseeHarness", "BugseeSampleBootstrap.cs");
            Assert.That(sample, Does.Contain("_skipE2EArming = false;"));
        }

        [Test]
        public void Assemble_script_puts_the_stamped_files_back_on_exit()
        {
            string script = Read("Tools~", "scripts", "assemble-local-ios-sdk.sh");
            Assert.That(script, Does.Contain("trap 'restore_stamped; rm -rf \"${STAGE}\"' EXIT"));
            Assert.That(script.IndexOf("trap 'restore_stamped", System.StringComparison.Ordinal),
                Is.LessThan(script.IndexOf("> \"${COCOA}/BugseeDev/BugseeLib/BugseeLib/BGSSdkVersion.h\"", System.StringComparison.Ordinal)));
        }

        [Test]
        public void Local_package_env_var_replaces_the_remote_reference_only_when_set()
        {
            string text = Read("Editor", "BugseeIosSpmPostProcess.cs");
            Assert.That(text, Does.Contain("LocalPackageEnvVar = \"BUGSEE_IOS_SDK_LOCAL_PACKAGE\""));
            Assert.That(text, Does.Contain("if (localPackageSource == null && !UseRemoteSpm)"));
            Assert.That(text, Does.Contain("AddRemotePackageReferenceAtVersion(RemoteSpmUrl, RemoteSpmVersion)"));
            Assert.That(text, Does.Contain("XcodeLocalPackage.FromRemote(File.ReadAllText(projectPath), RemoteSpmUrl, LocalPackageFolderName)"));
            Assert.That(text, Does.Contain("XcodeLocalPackage.RemoveBugseePackage(existing, RemoteSpmUrl, LocalPackageFolderName, out var removedPackageIds)"));
            Assert.That(text, Does.Contain("LocalPackageAllowReleaseEnvVar = \"BUGSEE_IOS_SDK_LOCAL_PACKAGE_ALLOW_RELEASE\""));
            Assert.That(text, Does.Contain("if (!development &&"));
            Assert.That(text, Does.Contain("throw new BuildFailedException($\"{message} ({LocalPackageEnvVar})\");"));
        }

        [Test]
        public void Assemble_script_refuses_an_output_directory_it_does_not_own()
        {
            string script = Read("Tools~", "scripts", "assemble-local-ios-sdk.sh");
            int guard = script.IndexOf("! -f \"${OUT}/BUILD-INFO.txt\"", System.StringComparison.Ordinal);
            Assert.That(guard, Is.GreaterThan(0));
            Assert.That(guard, Is.LessThan(script.IndexOf("rm -rf \"${OUT:?}\"/*", System.StringComparison.Ordinal)));
            // The output may not be the checkout, inside its build directory (which the build deletes), or above it.
            int build = script.IndexOf("\"${OUT}\" == \"${COCOA}/build/\"*", System.StringComparison.Ordinal);
            Assert.That(build, Is.GreaterThan(0));
            Assert.That(build, Is.LessThan(script.IndexOf("rm -rf \"${COCOA}/build\"", System.StringComparison.Ordinal)));
            Assert.That(script, Does.Contain("it contains the bugsee-cocoa checkout"));
        }

        [Test]
        public void Bridge_tests_take_a_local_package_and_keep_the_pinned_zip_by_default()
        {
            string versions = Read("Tools~", "versions.env");
            string version = System.Text.RegularExpressions.Regex.Match(versions, "IOS_SDK_VERSION=\"([^\"]+)\"").Groups[1].Value;
            string script = Read("Tests~", "iOS", "run-tests.sh");
            Assert.That(script, Does.Contain("${1:-${BUGSEE_IOS_SDK_LOCAL_PACKAGE:-}}"));
            Assert.That(script, Does.Contain("Bugsee-" + version + ".zip"));
            Assert.That(script, Does.Contain("shasum -a 256 -c"));
        }

        [Test]
        public void Bridge_keeps_the_beta6_exception_api_behind_one_switch()
        {
            string bridge = Read("Plugins", "iOS", "BugseeUnityBridge.mm");
            Assert.That(bridge, Does.Contain("__has_include(<Bugsee/BugseeExceptionLoggingOptions.h>)"));
            Assert.That(bridge, Does.Contain("[Bugsee logException:finalName reason:finalReason options:BugseeExceptionOptionsFrom(optionsJson) completion:nil];"));
            Assert.That(bridge, Does.Contain("domain:finalName"));
            Assert.That(bridge, Does.Contain("skipFrames:BugseeExceptionSkipFrames(options)"));
            Assert.That(bridge, Does.Contain("[Bugsee deleteCollectedDataOnDevice:done];"));
            string callbacks = Read("Plugins", "iOS", "BugseeUnityCallbacks.mm");
            Assert.That(callbacks, Does.Not.Contain("addLabels"));
        }
    }
}
