using System;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosCrashExtensionPolicyTests
    {
        static IosCrashExtensionInputs Inputs(Action<IosCrashExtensionInputs> tweak = null)
        {
            var i = new IosCrashExtensionInputs
            {
                Mode = IosCrashExtensionMode.Auto,
                XcodeVersionText = "Xcode 27.1\nBuild version 27A9275\n",
                AutomaticSigningWithTeam = true,
                Package = IosSdkPackageKind.Remote,
                BundleId = "com.example.game",
            };
            tweak?.Invoke(i);
            return i;
        }

        [Test]
        public void Auto_on_Xcode_27_with_automatic_signing_enables_with_the_derived_group()
        {
            var d = IosCrashExtensionPolicy.Decide(Inputs());
            Assert.That(d.Enabled, Is.True, d.Reason);
            Assert.That(d.AppGroup, Is.EqualTo("group.com.example.game"));
        }

        [TestCase("Xcode 26.4\nBuild version 17E1", false)]
        [TestCase("Xcode 26.99", false)]
        [TestCase("Xcode 27.0", true)]
        [TestCase("Xcode 27", true)]
        [TestCase("Xcode 27.1", true)]
        [TestCase("Xcode 28.0 beta", true)]
        [TestCase("garbage", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void Xcode_version_gates_the_extension(string xcode, bool enabled)
        {
            var d = IosCrashExtensionPolicy.Decide(Inputs(i => i.XcodeVersionText = xcode));
            Assert.That(d.Enabled, Is.EqualTo(enabled), d.Reason);
        }

        [Test]
        public void Old_or_unknown_Xcode_warns_only_when_the_extension_was_asked_for()
        {
            Assert.That(IosCrashExtensionPolicy.Decide(Inputs(i => i.XcodeVersionText = "Xcode 16.2")).IsWarning, Is.False);
            var asked = IosCrashExtensionPolicy.Decide(Inputs(i => { i.XcodeVersionText = "Xcode 16.2"; i.Mode = IosCrashExtensionMode.On; }));
            Assert.That(asked.Enabled, Is.False);
            Assert.That(asked.IsWarning, Is.True);
            Assert.That(asked.Reason, Does.Contain("Xcode 27"));
            Assert.That(IosCrashExtensionPolicy.Decide(Inputs(i => { i.XcodeVersionText = null; i.Mode = IosCrashExtensionMode.On; })).IsWarning, Is.True);
        }

        [Test]
        public void Off_wins_over_everything()
        {
            var d = IosCrashExtensionPolicy.Decide(Inputs(i => i.Mode = IosCrashExtensionMode.Off));
            Assert.That(d.Enabled, Is.False);
            Assert.That(d.IsWarning, Is.False);
        }

        [Test]
        public void Auto_needs_automatic_signing_and_On_does_not()
        {
            var auto = IosCrashExtensionPolicy.Decide(Inputs(i => i.AutomaticSigningWithTeam = false));
            Assert.That(auto.Enabled, Is.False);
            Assert.That(auto.IsWarning, Is.False);
            Assert.That(auto.Reason, Does.Contain(IosCrashExtensionPolicy.ModeEnvVar));
            Assert.That(IosCrashExtensionPolicy.Decide(Inputs(i => { i.AutomaticSigningWithTeam = false; i.Mode = IosCrashExtensionMode.On; })).Enabled, Is.True);
        }

        [Test]
        public void Package_without_the_product_skips_a_local_stub_but_not_the_published_package()
        {
            Assert.That(IosCrashExtensionPolicy.Decide(Inputs()).Enabled, Is.True);
            Assert.That(IosCrashExtensionPolicy.Decide(Inputs(i => i.Package = IosSdkPackageKind.LocalWithCrashExtension)).Enabled, Is.True);
            var stub = IosCrashExtensionPolicy.Decide(Inputs(i => i.Package = IosSdkPackageKind.LocalWithoutCrashExtension));
            Assert.That(stub.Enabled, Is.False);
            Assert.That(stub.Reason, Does.Contain("BugseeCrashExtension"));
        }

        [Test]
        public void An_existing_crash_reporter_extension_is_left_alone_with_a_warning()
        {
            var d = IosCrashExtensionPolicy.Decide(Inputs(i => i.ForeignCrashReporterExtension = true));
            Assert.That(d.Enabled, Is.False);
            Assert.That(d.IsWarning, Is.True);
            Assert.That(d.Reason, Does.Contain("only one"));
        }

        [Test]
        public void A_configured_group_beats_the_derived_one_and_a_bad_one_skips_with_a_warning()
        {
            Assert.That(IosCrashExtensionPolicy.Decide(Inputs(i => i.ConfiguredAppGroup = " group.shared.team ")).AppGroup, Is.EqualTo("group.shared.team"));
            var bad = IosCrashExtensionPolicy.Decide(Inputs(i => i.ConfiguredAppGroup = "shared"));
            Assert.That(bad.Enabled, Is.False);
            Assert.That(bad.IsWarning, Is.True);
            Assert.That(IosCrashExtensionPolicy.Decide(Inputs(i => i.BundleId = null)).Enabled, Is.False);
        }

        [TestCase("on", IosCrashExtensionMode.On)]
        [TestCase(" 1 ", IosCrashExtensionMode.On)]
        [TestCase("TRUE", IosCrashExtensionMode.On)]
        [TestCase("off", IosCrashExtensionMode.Off)]
        [TestCase("0", IosCrashExtensionMode.Off)]
        [TestCase("auto", IosCrashExtensionMode.Auto)]
        [TestCase("", IosCrashExtensionMode.Auto)]
        [TestCase(null, IosCrashExtensionMode.Auto)]
        [TestCase("maybe", IosCrashExtensionMode.Auto)]
        public void Mode_parses(string text, IosCrashExtensionMode expected) =>
            Assert.That(IosCrashExtensionPolicy.ParseMode(text), Is.EqualTo(expected));

        [TestCase("com.example.game", "group.com.example.game")]
        [TestCase("com.Example-Game.v2", "group.com.Example-Game.v2")]
        [TestCase("com.example.my_game", null)]
        [TestCase("com.example.game ", "group.com.example.game")]
        [TestCase("", null)]
        [TestCase("com..game", null)]
        [TestCase("com.exämple", null)]
        public void Group_id_is_derived_from_the_bundle_id(string bundle, string expected) =>
            Assert.That(IosCrashExtensionPolicy.DeriveGroupId(bundle), Is.EqualTo(expected));

        [Test]
        public void Group_id_validation()
        {
            Assert.That(IosCrashExtensionPolicy.GroupIdProblem("group.com.a"), Is.Null);
            Assert.That(IosCrashExtensionPolicy.GroupIdProblem("com.a"), Does.Contain("group."));
            Assert.That(IosCrashExtensionPolicy.GroupIdProblem("group."), Is.Not.Null);
            Assert.That(IosCrashExtensionPolicy.GroupIdProblem("group.a b"), Does.Contain("' '"));
            Assert.That(IosCrashExtensionPolicy.GroupIdProblem("group.a_b"), Is.Not.Null);
            Assert.That(IosCrashExtensionPolicy.GroupIdProblem("group.a."), Is.Not.Null);
            Assert.That(IosCrashExtensionPolicy.GroupIdProblem(""), Is.Not.Null);
            Assert.That(IosCrashExtensionPolicy.GroupIdProblem("group." + new string('a', 200)), Does.Contain("longer"));
        }

        [Test]
        public void Extension_bundle_id_is_prefixed_by_the_apps()
        {
            Assert.That(IosCrashExtensionPolicy.ExtensionBundleId("com.example.game"), Is.EqualTo("com.example.game.BugseeCrashReport"));
        }

        [Test]
        public void Swift_source_is_exactly_the_documented_two_lines()
        {
            Assert.That(IosCrashExtensionPolicy.SwiftSource(),
                Is.EqualTo("import BugseeCrashExtension\n\n@main struct CrashReport: BugseeCrashReporterExtension {}\n"));
            Assert.That(IosCrashExtensionPolicy.SwiftSource(), Does.Not.Contain("processCrashReport"));
        }

        [Test]
        public void Info_plist_names_the_extension_point_group_and_synced_versions()
        {
            string plist = IosCrashExtensionPolicy.InfoPlist("group.com.example.game", "My <Game>");
            Assert.That(plist, Does.Contain("<key>BugseeAppGroup</key>\n\t<string>group.com.example.game</string>"));
            Assert.That(plist, Does.Contain("<key>EXExtensionPointIdentifier</key>\n\t\t<string>com.apple.crash-reporter.extension</string>"));
            Assert.That(plist, Does.Contain("<string>$(MARKETING_VERSION)</string>"));
            Assert.That(plist, Does.Contain("<string>$(CURRENT_PROJECT_VERSION)</string>"));
            Assert.That(plist, Does.Contain("My &lt;Game&gt;"));
            Assert.That(plist, Does.Not.Contain("NSExtension"));
        }

        [Test]
        public void Entitlements_for_a_new_file_and_for_existing_ones()
        {
            string fresh = IosCrashExtensionPolicy.Entitlements("group.a.b");
            Assert.That(fresh, Does.Contain("<key>com.apple.security.application-groups</key>"));
            Assert.That(fresh, Does.Contain("<string>group.a.b</string>"));

            const string head = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\">\n";
            string other = head + "<dict>\n\t<key>aps-environment</key>\n\t<string>development</string>\n</dict>\n</plist>\n";
            string added = IosCrashExtensionPolicy.AddGroupToEntitlements(other, "group.a.b");
            Assert.That(added, Does.Contain("aps-environment"));
            Assert.That(added, Does.Contain("<string>group.a.b</string>"));
            Assert.That(IosCrashExtensionPolicy.AddGroupToEntitlements(added, "group.a.b"), Is.EqualTo(added), "idempotent");

            string more = IosCrashExtensionPolicy.AddGroupToEntitlements(added, "group.other");
            Assert.That(more, Does.Contain("group.a.b"));
            Assert.That(more, Does.Contain("group.other"));

            string empty = head + "<dict/>\n</plist>\n";
            Assert.That(IosCrashExtensionPolicy.AddGroupToEntitlements(empty, "group.a.b"), Does.Contain("<string>group.a.b</string>"));
            string emptyArray = head + "<dict>\n\t<key>com.apple.security.application-groups</key>\n\t<array/>\n</dict>\n</plist>\n";
            Assert.That(IosCrashExtensionPolicy.AddGroupToEntitlements(emptyArray, "group.a.b"), Does.Contain("<string>group.a.b</string>"));
            Assert.That(IosCrashExtensionPolicy.AddGroupToEntitlements("not a plist", "group.a.b"), Is.Null);
        }

        [Test]
        public void Version_comes_from_the_app_plist_literal_or_its_build_setting()
        {
            const string literal = "<key>CFBundleShortVersionString</key>\n<string>1.2.3</string>\n<key>CFBundleVersion</key>\n<string>45</string>";
            Assert.That(IosCrashExtensionPolicy.TryResolveAppVersion(literal, _ => null, out var v, out var b), Is.True);
            Assert.That((v, b), Is.EqualTo(("1.2.3", "45")));

            const string vars = "<key>CFBundleShortVersionString</key>\n<string>$(MARKETING_VERSION)</string>\n<key>CFBundleVersion</key>\n<string>${CURRENT_PROJECT_VERSION}</string>";
            Assert.That(IosCrashExtensionPolicy.TryResolveAppVersion(vars, n => n == "MARKETING_VERSION" ? "2.0" : "7", out v, out b), Is.True);
            Assert.That((v, b), Is.EqualTo(("2.0", "7")));
            Assert.That(IosCrashExtensionPolicy.TryResolveAppVersion(vars, _ => null, out _, out _), Is.False);
            Assert.That(IosCrashExtensionPolicy.TryResolveAppVersion(null, _ => null, out _, out _), Is.False);
        }

        [Test]
        public void Detects_a_crash_reporter_info_plist()
        {
            Assert.That(IosCrashExtensionPolicy.IsCrashReporterInfoPlist(IosCrashExtensionPolicy.InfoPlist("group.a", "x")), Is.True);
            Assert.That(IosCrashExtensionPolicy.IsCrashReporterInfoPlist("<plist/>"), Is.False);
            Assert.That(IosCrashExtensionPolicy.IsCrashReporterInfoPlist(null), Is.False);
        }
    }

    public class XcodeCrashExtensionProjectTests
    {
        const string App = "1D6058900D05DD3D006BFB54";
        const string Pkg = "D6A94630BE6E4397329CD968";

        // A trimmed Unity 2022.3 iOS project after the SPM step: app + UnityFramework + Tests targets, remote package.
        const string Unity =
"// !$*UTF8*$!\n{\n\tarchiveVersion = 1;\n\tclasses = {\n\t};\n\tobjectVersion = 46;\n\tobjects = {\n\n" +
"/* Begin PBXBuildFile section */\n" +
"\t\t187D458690E021C183A18696 /* Bugsee in Frameworks */ = {isa = PBXBuildFile; productRef = DF274CE6B57BB2973826E5D5 /* Bugsee */; };\n" +
"/* End PBXBuildFile section */\n\n" +
"/* Begin PBXContainerItemProxy section */\n" +
"\t\t5623C57517FDCB0800090B9E /* PBXContainerItemProxy */ = {\n\t\t\tisa = PBXContainerItemProxy;\n\t\t\tcontainerPortal = 29B97313FDCFA39411CA2CEA /* Project object */;\n\t\t\tproxyType = 1;\n\t\t\tremoteGlobalIDString = 1D6058900D05DD3D006BFB54;\n\t\t\tremoteInfo = \"Unity-iPhone\";\n\t\t};\n" +
"/* End PBXContainerItemProxy section */\n\n" +
"/* Begin PBXCopyFilesBuildPhase section */\n" +
"\t\t9D25ABAB213FB47800354C27 /* Embed Frameworks */ = {\n\t\t\tisa = PBXCopyFilesBuildPhase;\n\t\t\tbuildActionMask = 2147483647;\n\t\t\tdstPath = \"\";\n\t\t\tdstSubfolderSpec = 10;\n\t\t\tfiles = (\n\t\t\t);\n\t\t\tname = \"Embed Frameworks\";\n\t\t\trunOnlyForDeploymentPostprocessing = 0;\n\t\t};\n" +
"/* End PBXCopyFilesBuildPhase section */\n\n" +
"/* Begin PBXFileReference section */\n" +
"\t\t1D6058910D05DD3E006BFB54 /* BugseeMiniGame.app */ = {isa = PBXFileReference; explicitFileType = wrapper.application; includeInIndex = 0; path = BugseeMiniGame.app; sourceTree = BUILT_PRODUCTS_DIR; };\n" +
"/* End PBXFileReference section */\n\n" +
"/* Begin PBXFrameworksBuildPhase section */\n" +
"\t\t1D60588F0D05DD3D006BFB54 /* Frameworks */ = {\n\t\t\tisa = PBXFrameworksBuildPhase;\n\t\t\tbuildActionMask = 2147483647;\n\t\t\tfiles = (\n\t\t\t\t187D458690E021C183A18696 /* Bugsee in Frameworks */,\n\t\t\t);\n\t\t\trunOnlyForDeploymentPostprocessing = 0;\n\t\t};\n" +
"/* End PBXFrameworksBuildPhase section */\n\n" +
"/* Begin PBXGroup section */\n" +
"\t\t19C28FACFE9D520D11CA2CBB /* Products */ = {\n\t\t\tisa = PBXGroup;\n\t\t\tchildren = (\n\t\t\t\t1D6058910D05DD3E006BFB54 /* BugseeMiniGame.app */,\n\t\t\t);\n\t\t\tname = Products;\n\t\t\tsourceTree = \"<group>\";\n\t\t};\n" +
"\t\t29B97314FDCFA39411CA2CEA /* CustomTemplate */ = {\n\t\t\tisa = PBXGroup;\n\t\t\tchildren = (\n\t\t\t\t19C28FACFE9D520D11CA2CBB /* Products */,\n\t\t\t);\n\t\t\tname = CustomTemplate;\n\t\t\tsourceTree = \"<group>\";\n\t\t};\n" +
"/* End PBXGroup section */\n\n" +
"/* Begin PBXNativeTarget section */\n" +
"\t\t1D6058900D05DD3D006BFB54 /* Unity-iPhone */ = {\n\t\t\tisa = PBXNativeTarget;\n\t\t\tbuildConfigurationList = 1D6058960D05DD3E006BFB54 /* Build configuration list for PBXNativeTarget \"Unity-iPhone\" */;\n\t\t\tbuildPhases = (\n\t\t\t\t1D60588F0D05DD3D006BFB54 /* Frameworks */,\n\t\t\t\t9D25ABAB213FB47800354C27 /* Embed Frameworks */,\n\t\t\t);\n\t\t\tbuildRules = (\n\t\t\t);\n\t\t\tdependencies = (\n\t\t\t);\n\t\t\tname = \"Unity-iPhone\";\n\t\t\tpackageProductDependencies = (\n\t\t\t\t95864908A8AAC1C080E578AC /* Bugsee */,\n\t\t\t);\n\t\t\tproductName = \"iPhone-target\";\n\t\t\tproductReference = 1D6058910D05DD3E006BFB54 /* BugseeMiniGame.app */;\n\t\t\tproductType = \"com.apple.product-type.application\";\n\t\t};\n" +
"/* End PBXNativeTarget section */\n\n" +
"/* Begin PBXProject section */\n" +
"\t\t29B97313FDCFA39411CA2CEA /* Project object */ = {\n\t\t\tisa = PBXProject;\n\t\t\tbuildConfigurationList = C01FCF4E08A954540054247B /* Build configuration list for PBXProject \"Unity-iPhone\" */;\n\t\t\tmainGroup = 29B97314FDCFA39411CA2CEA /* CustomTemplate */;\n\t\t\tpackageReferences = (\n\t\t\t\tD6A94630BE6E4397329CD968 /* XCRemoteSwiftPackageReference \"spm\" */,\n\t\t\t);\n\t\t\tproductRefGroup = 19C28FACFE9D520D11CA2CBB /* Products */;\n\t\t\tprojectDirPath = \"\";\n\t\t\ttargets = (\n\t\t\t\t1D6058900D05DD3D006BFB54 /* Unity-iPhone */,\n\t\t\t);\n\t\t};\n" +
"/* End PBXProject section */\n\n" +
"/* Begin XCBuildConfiguration section */\n" +
"\t\t1D6058940D05DD3E006BFB54 /* Debug */ = {\n\t\t\tisa = XCBuildConfiguration;\n\t\t\tbuildSettings = {\n\t\t\t\tDEVELOPMENT_TEAM = ABCDE12345;\n\t\t\t\tINFOPLIST_FILE = Info.plist;\n\t\t\t\tIPHONEOS_DEPLOYMENT_TARGET = 15.0;\n\t\t\t\tPRODUCT_BUNDLE_IDENTIFIER = com.example.game;\n\t\t\t\tSDKROOT = iphoneos;\n\t\t\t\tSUPPORTED_PLATFORMS = iphoneos;\n\t\t\t\tTARGETED_DEVICE_FAMILY = \"1,2\";\n\t\t\t};\n\t\t\tname = Debug;\n\t\t};\n" +
"\t\t1D6058950D05DD3E006BFB54 /* Release */ = {\n\t\t\tisa = XCBuildConfiguration;\n\t\t\tbuildSettings = {\n\t\t\t\tDEVELOPMENT_TEAM = ABCDE12345;\n\t\t\t\tINFOPLIST_FILE = Info.plist;\n\t\t\t\tIPHONEOS_DEPLOYMENT_TARGET = 15.0;\n\t\t\t\tPRODUCT_BUNDLE_IDENTIFIER = com.example.game;\n\t\t\t\tSDKROOT = iphoneos;\n\t\t\t\tSUPPORTED_PLATFORMS = iphoneos;\n\t\t\t\tTARGETED_DEVICE_FAMILY = \"1,2\";\n\t\t\t};\n\t\t\tname = Release;\n\t\t};\n" +
"/* End XCBuildConfiguration section */\n\n" +
"/* Begin XCConfigurationList section */\n" +
"\t\t1D6058960D05DD3E006BFB54 /* Build configuration list for PBXNativeTarget \"Unity-iPhone\" */ = {\n\t\t\tisa = XCConfigurationList;\n\t\t\tbuildConfigurations = (\n\t\t\t\t1D6058940D05DD3E006BFB54 /* Debug */,\n\t\t\t\t1D6058950D05DD3E006BFB54 /* Release */,\n\t\t\t);\n\t\t\tdefaultConfigurationIsVisible = 0;\n\t\t\tdefaultConfigurationName = Release;\n\t\t};\n" +
"/* End XCConfigurationList section */\n\n" +
"/* Begin XCRemoteSwiftPackageReference section */\n" +
"\t\tD6A94630BE6E4397329CD968 /* XCRemoteSwiftPackageReference \"spm\" */ = {\n\t\t\tisa = XCRemoteSwiftPackageReference;\n\t\t\trepositoryURL = \"https://github.com/bugsee/spm.git\";\n\t\t\trequirement = {\n\t\t\t\tkind = exactVersion;\n\t\t\t\tversion = 7.0.1;\n\t\t\t};\n\t\t};\n" +
"/* End XCRemoteSwiftPackageReference section */\n\n" +
"/* Begin XCSwiftPackageProductDependency section */\n" +
"\t\t95864908A8AAC1C080E578AC /* Bugsee */ = {\n\t\t\tisa = XCSwiftPackageProductDependency;\n\t\t\tpackage = D6A94630BE6E4397329CD968 /* XCRemoteSwiftPackageReference \"spm\" */;\n\t\t\tproductName = Bugsee;\n\t\t};\n" +
"/* End XCSwiftPackageProductDependency section */\n\t};\n\trootObject = 29B97313FDCFA39411CA2CEA /* Project object */;\n}\n";

        static XcodeCrashExtensionSpec Spec(string entitlementsToSet = "Unity-iPhone.entitlements") => new XcodeCrashExtensionSpec
        {
            AppTargetGuid = App, AppBundleId = "com.example.game", Version = "1.2.3", Build = "45", AppEntitlementsPathToSet = entitlementsToSet,
        };

        static string Apply(string pbxproj = Unity, XcodeCrashExtensionSpec spec = null)
        {
            string result = XcodeCrashExtensionProject.Apply(pbxproj, spec ?? Spec(), out string error);
            Assert.That(result, Is.Not.Null, error);
            return result;
        }

        static string Block(string text, string guidOrName)
        {
            Match m = Regex.Match(text, @"^\t\t" + guidOrName + @"[^\n]*= \{\n.*?^\t\t\};\n", RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.That(m.Success, Is.True, guidOrName);
            return m.Value;
        }

        static string ExtensionTarget(string text)
        {
            Match m = Regex.Match(text, @"^\t\t[0-9A-F]{24} /\* BugseeCrashReport \*/ = \{\n\t\t\tisa = PBXNativeTarget;.*?^\t\t\};\n", RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.That(m.Success, Is.True, "extension target");
            return m.Value;
        }

        [Test]
        public void Adds_an_extensionkit_target_with_the_crash_extension_product_only_on_it()
        {
            string text = Apply();
            string target = ExtensionTarget(text);
            Assert.That(target, Does.Contain("productType = \"com.apple.product-type.extensionkit-extension\""));
            Assert.That(target, Does.Contain("BugseeCrashExtension */,"));
            Assert.That(target, Does.Not.Contain("productName = Bugsee;"));

            // One product dependency per product: the app's Bugsee, the extension's BugseeCrashExtension, both from the one package.
            var products = Regex.Matches(text, @"isa = XCSwiftPackageProductDependency;\n\t\t\tpackage = " + Pkg + @"[^\n]*\n\t\t\tproductName = (\w+);").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.That(products, Is.EquivalentTo(new[] { "Bugsee", "BugseeCrashExtension" }));

            // The app target still links Bugsee and not the extension product; its Frameworks phase is untouched.
            string app = Block(text, App);
            Assert.That(app, Does.Contain("95864908A8AAC1C080E578AC /* Bugsee */"));
            Assert.That(app, Does.Not.Contain("BugseeCrashExtension"));
            string appFrameworks = Block(text, "1D60588F0D05DD3D006BFB54");
            Assert.That(appFrameworks, Does.Not.Contain("BugseeCrashExtension"));

            // The extension's own Frameworks phase links the crash product and not Bugsee.
            string phases = target.Substring(target.IndexOf("buildPhases"));
            string fwId = Regex.Matches(phases, "[0-9A-F]{24} /\\* Frameworks \\*/").Cast<Match>().First().Value.Substring(0, 24);
            string fw = Block(text, fwId);
            Assert.That(fw, Does.Contain("BugseeCrashExtension in Frameworks"));
            Assert.That(fw, Does.Not.Contain("Bugsee in Frameworks"));
        }

        [Test]
        public void Embeds_the_appex_in_the_app_with_the_extensionkit_destination_and_depends_on_it()
        {
            string text = Apply();
            string app = Block(text, App);
            Assert.That(app, Does.Contain("Embed ExtensionKit Extensions */,"));
            Assert.That(app, Does.Contain("PBXTargetDependency */,"));
            Match phase = Regex.Match(text, @"isa = PBXCopyFilesBuildPhase;\n[^}]*?dstPath = ""\$\(EXTENSIONS_FOLDER_PATH\)"";\n\t\t\tdstSubfolderSpec = 16;\n[^}]*?name = ""Embed ExtensionKit Extensions"";");
            Assert.That(phase.Success, Is.True);
            Assert.That(text, Does.Contain("BugseeCrashReport.appex in Embed ExtensionKit Extensions */ = {isa = PBXBuildFile;"));
            Assert.That(text, Does.Contain("explicitFileType = \"wrapper.extensionkit-extension\""));
            Assert.That(text, Does.Contain("isa = PBXTargetDependency;"));
            Assert.That(Regex.Matches(text, "remoteInfo = BugseeCrashReport;").Count, Is.EqualTo(1));
        }

        [Test]
        public void Only_the_extension_gets_the_27_deployment_target_and_its_own_identity()
        {
            string text = Apply();
            Assert.That(Regex.Matches(text, "IPHONEOS_DEPLOYMENT_TARGET = 27.0;").Count, Is.EqualTo(2), "Debug and Release of the extension");
            Assert.That(Regex.Matches(text, "IPHONEOS_DEPLOYMENT_TARGET = 15.0;").Count, Is.EqualTo(2), "the app keeps its own");
            Assert.That(Regex.Matches(text, "PRODUCT_BUNDLE_IDENTIFIER = com.example.game.BugseeCrashReport;").Count, Is.EqualTo(2));
            Assert.That(Regex.Matches(text, "SWIFT_VERSION = \"?5.0\"?;").Count, Is.EqualTo(2));
            Assert.That(text, Does.Contain("CODE_SIGN_ENTITLEMENTS = \"?BugseeCrashReport/BugseeCrashReport.entitlements\"?;".Replace("\"?", "")));
            Assert.That(text, Does.Contain("INFOPLIST_FILE = BugseeCrashReport/Info.plist;"));
            Assert.That(text, Does.Contain("DEVELOPMENT_TEAM = ABCDE12345;"));
        }

        [Test]
        public void Versions_are_set_on_the_extension_and_the_app_gets_the_entitlements_file()
        {
            string text = Apply();
            Assert.That(Regex.Matches(text, "MARKETING_VERSION = 1.2.3;").Count, Is.EqualTo(2));
            Assert.That(Regex.Matches(text, "CURRENT_PROJECT_VERSION = 45;").Count, Is.EqualTo(2));
            Assert.That(Regex.Matches(text, "CODE_SIGN_ENTITLEMENTS = \"Unity-iPhone.entitlements\";").Count, Is.EqualTo(2), "both app configurations");
        }

        [Test]
        public void An_app_that_already_names_entitlements_is_not_given_another_setting()
        {
            string withEnt = Unity.Replace("INFOPLIST_FILE = Info.plist;", "CODE_SIGN_ENTITLEMENTS = \"Unity-iPhone/App.entitlements\";\n\t\t\t\tINFOPLIST_FILE = Info.plist;");
            Assert.That(XcodeCrashExtensionProject.AppEntitlementsPath(withEnt, App), Is.EqualTo("Unity-iPhone/App.entitlements"));
            Assert.That(XcodeCrashExtensionProject.AppEntitlementsPath(Unity, App), Is.Null);
            string text = Apply(withEnt, Spec(null));
            Assert.That(text, Does.Not.Contain("Unity-iPhone.entitlements"));
        }

        [Test]
        public void The_files_the_target_names_are_grouped_under_the_main_group_and_the_product_under_Products()
        {
            string text = Apply();
            string main = Block(text, "29B97314FDCFA39411CA2CEA");
            Assert.That(main, Does.Contain("/* BugseeCrashReport */,"));
            string products = Block(text, "19C28FACFE9D520D11CA2CBB");
            Assert.That(products, Does.Contain("BugseeCrashReport.appex */,"));
            string project = Block(text, "29B97313FDCFA39411CA2CEA");
            Assert.That(project, Does.Contain("/* BugseeCrashReport */,"));
            Assert.That(text, Does.Contain("lastKnownFileType = sourcecode.swift; path = CrashReport.swift;"));
        }

        [Test]
        public void Applying_the_same_input_twice_changes_nothing_the_second_time()
        {
            string once = Apply();
            Assert.That(XcodeCrashExtensionProject.Has(once), Is.True);
            Assert.That(XcodeCrashExtensionProject.Has(Unity), Is.False);
            Assert.That(Apply(once), Is.EqualTo(once));
            Assert.That(Apply(Apply(once)), Is.EqualTo(once));
        }

        [Test]
        public void An_append_export_refreshes_the_bundle_id_version_and_build_on_every_configuration()
        {
            string once = Apply();
            var changed = Spec();
            changed.AppBundleId = "com.example.renamed";
            changed.Version = "2.0.0";
            changed.Build = "99";
            string text = Apply(once.Replace("PRODUCT_BUNDLE_IDENTIFIER = com.example.game;", "PRODUCT_BUNDLE_IDENTIFIER = com.example.renamed;"), changed);
            Assert.That(Regex.Matches(text, "PRODUCT_BUNDLE_IDENTIFIER = com.example.renamed.BugseeCrashReport;").Count, Is.EqualTo(2));
            Assert.That(text, Does.Not.Contain("com.example.game.BugseeCrashReport"));
            Assert.That(Regex.Matches(text, "MARKETING_VERSION = 2.0.0;").Count, Is.EqualTo(2));
            Assert.That(Regex.Matches(text, "CURRENT_PROJECT_VERSION = 99;").Count, Is.EqualTo(2));
            Assert.That(text, Does.Not.Contain("MARKETING_VERSION = 1.2.3;"));
            // Nothing else was duplicated.
            Assert.That(Regex.Matches(text, "isa = PBXNativeTarget;").Count, Is.EqualTo(Regex.Matches(once, "isa = PBXNativeTarget;").Count));
            Assert.That(text.Length, Is.LessThan(once.Length + 40));
        }

        const string DevId = "PRODUCT_BUNDLE_IDENTIFIER = com.example.game;";

        // Debug keeps com.example.game; Release gets its own id.
        static string SplitIds(string id = "com.example.game.prod")
        {
            int release = Unity.IndexOf("name = Release;", StringComparison.Ordinal);
            int at = Unity.LastIndexOf(DevId, release, StringComparison.Ordinal);
            return Unity.Substring(0, at) + "PRODUCT_BUNDLE_IDENTIFIER = " + id + ";" + Unity.Substring(at + DevId.Length);
        }

        static string ExtensionConfig(string text, string cfgName)
        {
            string list = Block(text, Regex.Match(ExtensionTarget(text), @"buildConfigurationList = ([0-9A-F]{24})").Groups[1].Value);
            foreach (Match m in Regex.Matches(list, "[0-9A-F]{24}"))
            {
                string block = Block(text, m.Value);
                if (block.Contains("name = " + cfgName + ";")) return block;
            }
            Assert.Fail(cfgName);
            return null;
        }

        [Test]
        public void Each_extension_configuration_takes_the_id_of_the_app_configuration_with_its_name()
        {
            string text = Apply(SplitIds());
            Assert.That(ExtensionConfig(text, "Debug"), Does.Contain("PRODUCT_BUNDLE_IDENTIFIER = com.example.game.BugseeCrashReport;"));
            Assert.That(ExtensionConfig(text, "Release"), Does.Contain("PRODUCT_BUNDLE_IDENTIFIER = com.example.game.prod.BugseeCrashReport;"));
            Assert.That(Apply(text), Is.EqualTo(text), "a refresh keeps them");

            // A refresh follows a later change of one configuration only.
            string back = Apply(text.Replace("PRODUCT_BUNDLE_IDENTIFIER = com.example.game.prod;", "PRODUCT_BUNDLE_IDENTIFIER = com.example.live;"));
            Assert.That(ExtensionConfig(back, "Release"), Does.Contain("PRODUCT_BUNDLE_IDENTIFIER = com.example.live.BugseeCrashReport;"));
            Assert.That(ExtensionConfig(back, "Debug"), Does.Contain("PRODUCT_BUNDLE_IDENTIFIER = com.example.game.BugseeCrashReport;"));
        }

        [Test]
        public void A_configuration_with_a_build_variable_or_no_id_falls_back_to_the_spec_id()
        {
            string text = Apply(SplitIds("$(APP_ID)"));
            Assert.That(ExtensionConfig(text, "Release"), Does.Contain("PRODUCT_BUNDLE_IDENTIFIER = com.example.game.BugseeCrashReport;"));
            string none = Unity.Replace("\t\t\t\t" + DevId + "\n", "");
            Assert.That(Apply(none), Does.Contain("PRODUCT_BUNDLE_IDENTIFIER = com.example.game.BugseeCrashReport;"));
        }

        [Test]
        public void An_append_export_refreshes_the_team_and_drops_it_when_the_app_has_none()
        {
            string once = Apply();
            string newTeam = once.Replace("DEVELOPMENT_TEAM = ABCDE12345;\n\t\t\t\tINFOPLIST_FILE = Info.plist;", "DEVELOPMENT_TEAM = ZZZZZ99999;\n\t\t\t\tINFOPLIST_FILE = Info.plist;");
            string text = Apply(newTeam);
            Assert.That(Regex.Matches(text, "DEVELOPMENT_TEAM = ZZZZZ99999;").Count, Is.EqualTo(4), "app and extension, both configurations");
            Assert.That(text, Does.Not.Contain("ABCDE12345"));

            string noTeam = Apply(once.Replace("\t\t\t\tDEVELOPMENT_TEAM = ABCDE12345;\n\t\t\t\tINFOPLIST_FILE = Info.plist;", "\t\t\t\tINFOPLIST_FILE = Info.plist;"));
            Assert.That(noTeam, Does.Not.Contain("DEVELOPMENT_TEAM"));
        }

        [Test]
        public void The_signing_style_follows_the_app_and_release_gets_a_dsym()
        {
            Assert.That(Apply(), Does.Contain("CODE_SIGN_STYLE = Automatic;"));
            string manualAttr = Unity.Replace("\t\tCustomTemplate", "\t\tCustomTemplate").Replace(
                "\t\t\tisa = PBXProject;", "\t\t\tisa = PBXProject;\n\t\t\tattributes = {\n\t\t\t\tTargetAttributes = {\n\t\t\t\t\t" + App + " = {\n\t\t\t\t\t\tProvisioningStyle = Manual;\n\t\t\t\t\t};\n\t\t\t\t};\n\t\t\t};");
            string manual = Apply(manualAttr);
            Assert.That(Regex.Matches(manual, "CODE_SIGN_STYLE = Manual;").Count, Is.EqualTo(2));
            // And a refresh follows a switch of the app to automatic.
            Assert.That(Apply(manual.Replace("ProvisioningStyle = Manual;", "ProvisioningStyle = Automatic;")), Does.Not.Contain("CODE_SIGN_STYLE = Manual;"));

            string text = Apply();
            Assert.That(Block(text, ConfigId(text, "Release")), Does.Contain("DEBUG_INFORMATION_FORMAT = \"dwarf-with-dsym\";"));
            Assert.That(Block(text, ConfigId(text, "Debug")), Does.Contain("DEBUG_INFORMATION_FORMAT = dwarf;"));
        }

        static string ConfigId(string text, string name)
        {
            // The extension's configuration of that name (the app's has no DEBUG_INFORMATION_FORMAT in the fixture).
            foreach (Match m in Regex.Matches(text, @"^\t\t([0-9A-F]{24}) /\* " + name + @" \*/ = \{\n\t\t\tisa = XCBuildConfiguration;.*?^\t\t\};\n", RegexOptions.Multiline | RegexOptions.Singleline))
                if (m.Value.Contains("BugseeCrashReport")) return m.Groups[1].Value;
            Assert.Fail("config " + name);
            return null;
        }

        [Test]
        public void Remove_takes_the_whole_extension_out_and_leaves_the_app_as_it_was()
        {
            string with = Apply();
            string back = XcodeCrashExtensionProject.Remove(with, out string error);
            Assert.That(back, Is.Not.Null, error);
            Assert.That(XcodeCrashExtensionProject.Has(back), Is.False);
            foreach (string gone in new[] { "BugseeCrashReport", "BugseeCrashExtension", "Embed ExtensionKit Extensions", "extensionkit", "CrashReport.swift", "27.0" })
                Assert.That(back, Does.Not.Contain(gone), gone);
            string app = Block(back, App);
            Assert.That(app, Does.Contain("Embed Frameworks */,"));
            Assert.That(app, Does.Not.Contain("PBXTargetDependency"));
            Assert.That(app, Does.Contain("95864908A8AAC1C080E578AC /* Bugsee */"));
            Assert.That(back.Count(c => c == '{'), Is.EqualTo(back.Count(c => c == '}')));
            Assert.That(back.Count(c => c == '('), Is.EqualTo(back.Count(c => c == ')')));
            // The only thing left of the export is the entitlements setting it put on the app.
            Assert.That(back.Replace("\t\t\t\tCODE_SIGN_ENTITLEMENTS = \"Unity-iPhone.entitlements\";\n", ""), Is.EqualTo(Unity));
            // Off after On after Off: it can be added again.
            Assert.That(Apply(back), Is.EqualTo(with));
            Assert.That(XcodeCrashExtensionProject.Remove(Unity, out _), Is.EqualTo(Unity));
        }

        [Test]
        public void Remove_keeps_an_embed_phase_that_also_holds_another_extension()
        {
            string with = Apply();
            // Someone else's extension in the same phase.
            string other = "\t\tAAAAAAAAAAAAAAAAAAAAAAAA /* Other.appex in Embed ExtensionKit Extensions */ = {isa = PBXBuildFile; fileRef = BBBBBBBBBBBBBBBBBBBBBBBB; };\n";
            string shared = with.Replace("/* End PBXBuildFile section */", other + "/* End PBXBuildFile section */")
                .Replace("\t\t\tfiles = (\n\t\t\t\t", "\t\t\tfiles = (\n\t\t\t\tAAAAAAAAAAAAAAAAAAAAAAAA /* Other.appex in Embed ExtensionKit Extensions */,\n\t\t\t\t");
            string back = XcodeCrashExtensionProject.Remove(shared, out string error);
            Assert.That(back, Is.Not.Null, error);
            Assert.That(XcodeCrashExtensionProject.Has(back), Is.False);
        }

        [Test]
        public void Malformed_projects_are_not_thrown_on_by_remove()
        {
            Assert.DoesNotThrow(() => XcodeCrashExtensionProject.Remove("isa = PBXNativeTarget;\nname = BugseeCrashReport;", out _));
            Assert.That(XcodeCrashExtensionProject.Remove(null, out string e), Is.Null);
            Assert.That(e, Is.Not.Null);
        }

        [Test]
        public void Entitlements_are_read_per_configuration_and_a_missing_one_is_filled_in()
        {
            string perConfig = Unity.Replace("\t\t\t\tINFOPLIST_FILE = Info.plist;\n\t\t\t\tIPHONEOS_DEPLOYMENT_TARGET = 15.0;\n\t\t\t\tPRODUCT_BUNDLE_IDENTIFIER = com.example.game;\n\t\t\t\tSDKROOT = iphoneos;\n\t\t\t\tSUPPORTED_PLATFORMS = iphoneos;\n\t\t\t\tTARGETED_DEVICE_FAMILY = \"1,2\";\n\t\t\t};\n\t\t\tname = Debug;",
                "\t\t\t\tCODE_SIGN_ENTITLEMENTS = \"$(SRCROOT)/App/Debug.entitlements\";\n\t\t\t\tINFOPLIST_FILE = Info.plist;\n\t\t\t\tIPHONEOS_DEPLOYMENT_TARGET = 15.0;\n\t\t\t\tPRODUCT_BUNDLE_IDENTIFIER = com.example.game;\n\t\t\t\tSDKROOT = iphoneos;\n\t\t\t\tSUPPORTED_PLATFORMS = iphoneos;\n\t\t\t\tTARGETED_DEVICE_FAMILY = \"1,2\";\n\t\t\t};\n\t\t\tname = Debug;");
            Assert.That(XcodeCrashExtensionProject.AppEntitlementsPaths(perConfig, App), Is.EqualTo(new[] { "$(SRCROOT)/App/Debug.entitlements" }));
            // Release has none: it gets the Debug one's path, Debug keeps its own.
            string text = Apply(perConfig, Spec("$(SRCROOT)/App/Debug.entitlements"));
            Assert.That(Regex.Matches(text, @"CODE_SIGN_ENTITLEMENTS = ""\$\(SRCROOT\)/App/Debug.entitlements"";").Count, Is.EqualTo(2));
        }

        [Test]
        public void Project_relative_entitlements_paths()
        {
            Assert.That(IosCrashExtensionPolicy.ProjectRelativePath("$(SRCROOT)/App/A.entitlements"), Is.EqualTo("App/A.entitlements"));
            Assert.That(IosCrashExtensionPolicy.ProjectRelativePath("${PROJECT_DIR}/A.entitlements"), Is.EqualTo("A.entitlements"));
            Assert.That(IosCrashExtensionPolicy.ProjectRelativePath("Unity-iPhone.entitlements"), Is.EqualTo("Unity-iPhone.entitlements"));
            Assert.That(IosCrashExtensionPolicy.ProjectRelativePath("/abs/A.entitlements"), Is.EqualTo("/abs/A.entitlements"));
            Assert.That(IosCrashExtensionPolicy.ProjectRelativePath("$(TARGET_NAME)/A.entitlements"), Is.Null);
            Assert.That(IosCrashExtensionPolicy.ProjectRelativePath(""), Is.Null);
        }

        [Test]
        public void Group_removal_and_check_on_entitlements()
        {
            string both = IosCrashExtensionPolicy.AddGroupToEntitlements(IosCrashExtensionPolicy.Entitlements("group.a"), "group.b");
            Assert.That(IosCrashExtensionPolicy.EntitlementsHaveGroup(both, "group.a"), Is.True);
            Assert.That(IosCrashExtensionPolicy.EntitlementsHaveGroup(both, "group.c"), Is.False);
            string onlyB = IosCrashExtensionPolicy.RemoveGroupFromEntitlements(both, "group.a");
            Assert.That(IosCrashExtensionPolicy.EntitlementsHaveGroup(onlyB, "group.b"), Is.True);
            Assert.That(onlyB, Does.Not.Contain("group.a"));
            string none = IosCrashExtensionPolicy.RemoveGroupFromEntitlements(onlyB, "group.b");
            Assert.That(none, Does.Not.Contain("application-groups"));
            Assert.That(none, Does.Contain("<dict>"));
            Assert.That(IosCrashExtensionPolicy.RemoveGroupFromEntitlements("<plist/>", "group.x"), Is.EqualTo("<plist/>"));
            Assert.That(IosCrashExtensionPolicy.EntitlementsHaveGroup(null, "group.a"), Is.False);
        }

        [Test]
        public void Auto_is_off_in_batch_mode_unless_asked_for_with_the_variable()
        {
            var auto = IosCrashExtensionPolicy.Decide(InputsBatch(IosCrashExtensionMode.Auto));
            Assert.That(auto.Enabled, Is.False);
            Assert.That(auto.IsWarning, Is.False);
            Assert.That(auto.Reason, Does.Contain("batch mode").And.Contain(IosCrashExtensionPolicy.ModeEnvVar + "=1"));
            Assert.That(IosCrashExtensionPolicy.Decide(InputsBatch(IosCrashExtensionMode.On)).Enabled, Is.True);
            Assert.That(IosCrashExtensionPolicy.Decide(InputsBatch(IosCrashExtensionMode.Off)).Enabled, Is.False);
        }

        static IosCrashExtensionInputs InputsBatch(IosCrashExtensionMode mode) => new IosCrashExtensionInputs
        {
            Mode = mode, BatchMode = true, XcodeVersionText = "Xcode 27.1", AutomaticSigningWithTeam = true,
            Package = IosSdkPackageKind.Remote, BundleId = "com.example.game",
        };

        [Test]
        public void Generated_ids_are_unique_and_do_not_collide_with_the_project()
        {
            string text = Apply();
            var ids = Regex.Matches(text, @"^\t\t([0-9A-F]{24})\b[^\n]*= \{", RegexOptions.Multiline).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.That(ids, Is.Unique);
            Assert.That(Apply(), Is.EqualTo(text), "stable for the same input");
        }

        [Test]
        public void Brace_and_paren_balance_is_kept()
        {
            string text = Apply();
            Assert.That(text.Count(c => c == '{'), Is.EqualTo(text.Count(c => c == '}')));
            Assert.That(text.Count(c => c == '('), Is.EqualTo(text.Count(c => c == ')')));
        }

        [Test]
        public void A_local_package_without_a_package_key_still_links_the_product()
        {
            string local = Unity.Replace("\t\t\tpackage = D6A94630BE6E4397329CD968 /* XCRemoteSwiftPackageReference \"spm\" */;\n", "");
            string text = Apply(local);
            Assert.That(Regex.IsMatch(text, @"isa = XCSwiftPackageProductDependency;\n\t\t\tproductName = BugseeCrashExtension;"), Is.True);
        }

        [Test]
        public void Missing_sections_are_created()
        {
            string noProxy = Unity.Replace("/* Begin PBXContainerItemProxy section */", "/* Begin Gone section */").Replace("/* End PBXContainerItemProxy section */", "/* End Gone section */");
            string text = Apply(noProxy);
            Assert.That(text, Does.Contain("/* Begin PBXContainerItemProxy section */"));
            Assert.That(text, Does.Contain("/* Begin PBXSourcesBuildPhase section */"));
            Assert.That(text, Does.Contain("/* End PBXSourcesBuildPhase section */"));
        }

        [TestCase("")]
        [TestCase("not a project")]
        [TestCase("{ rootObject = 29B97313FDCFA39411CA2CEA; }")]
        public void A_malformed_project_returns_null_and_never_throws(string pbxproj)
        {
            string result = null;
            Assert.DoesNotThrow(() => result = XcodeCrashExtensionProject.Apply(pbxproj, Spec(), out _));
            Assert.That(result, Is.Null);
        }

        [Test]
        public void A_project_without_the_bugsee_product_or_the_app_target_is_refused_with_a_reason()
        {
            string noBugsee = Unity.Replace("productName = Bugsee;", "productName = Other;");
            Assert.That(XcodeCrashExtensionProject.Apply(noBugsee, Spec(), out string e1), Is.Null);
            Assert.That(e1, Does.Contain("Bugsee"));
            var wrong = Spec();
            wrong.AppTargetGuid = "FFFFFFFFFFFFFFFFFFFFFFFF";
            Assert.That(XcodeCrashExtensionProject.Apply(Unity, wrong, out string e2), Is.Null);
            Assert.That(e2, Is.Not.Empty);
            Assert.DoesNotThrow(() => XcodeCrashExtensionProject.Apply(null, Spec(), out _));
            Assert.DoesNotThrow(() => XcodeCrashExtensionProject.Apply(Unity, null, out _));
        }

        [Test]
        public void Reads_the_app_settings_and_spots_any_extensionkit_target()
        {
            Assert.That(XcodeCrashExtensionProject.AppBundleId(Unity, App), Is.EqualTo("com.example.game"));
            Assert.That(XcodeCrashExtensionProject.AppSetting(Unity, App, "TARGETED_DEVICE_FAMILY"), Is.EqualTo("1,2"));
            Assert.That(XcodeCrashExtensionProject.AppSetting(Unity, App, "NOPE"), Is.Null);
            Assert.That(XcodeCrashExtensionProject.AppBundleId("garbage", App), Is.Null);
            Assert.That(XcodeCrashExtensionProject.HasAnyExtensionKitTarget(Unity), Is.False);
            Assert.That(XcodeCrashExtensionProject.HasAnyExtensionKitTarget(Apply()), Is.True);
        }
    }
}
