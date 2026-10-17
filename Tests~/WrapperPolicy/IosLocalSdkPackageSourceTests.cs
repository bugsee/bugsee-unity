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

        [Test]
        public void Local_package_env_var_replaces_the_remote_reference_only_when_set()
        {
            string text = Read("Editor", "BugseeIosSpmPostProcess.cs");
            Assert.That(text, Does.Contain("LocalPackageEnvVar = \"BUGSEE_IOS_SDK_LOCAL_PACKAGE\""));
            Assert.That(text, Does.Contain("if (localPackageSource == null && !UseRemoteSpm)"));
            Assert.That(text, Does.Contain("AddRemotePackageReferenceAtVersion(RemoteSpmUrl, RemoteSpmVersion)"));
            Assert.That(text, Does.Contain("XcodeLocalPackage.FromRemote(File.ReadAllText(projectPath), RemoteSpmUrl, LocalPackageFolderName)"));
            Assert.That(text, Does.Contain("XcodeLocalPackage.RemoveBugseePackage(existing, RemoteSpmUrl, LocalPackageFolderName)"));
            Assert.That(text, Does.Contain("LocalPackageAllowReleaseEnvVar = \"BUGSEE_IOS_SDK_LOCAL_PACKAGE_ALLOW_RELEASE\""));
            Assert.That(text, Does.Contain("!EditorUserBuildSettings.development"));
            Assert.That(text, Does.Contain("throw new BuildFailedException($\"{message} ({LocalPackageEnvVar})\");"));
        }

        [Test]
        public void Assemble_script_refuses_an_output_directory_it_does_not_own()
        {
            string script = Read("Tools~", "scripts", "assemble-local-ios-sdk.sh");
            int guard = script.IndexOf("! -f \"${OUT}/BUILD-INFO.txt\"", System.StringComparison.Ordinal);
            Assert.That(guard, Is.GreaterThan(0));
            Assert.That(guard, Is.LessThan(script.IndexOf("rm -rf \"${OUT:?}\"/*", System.StringComparison.Ordinal)));
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
