using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class FeedbackOptInTests
    {
        static string Read(params string[] relative)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "package.json")))
                dir = Directory.GetParent(dir)?.FullName;
            Assert.That(dir, Is.Not.Null.And.Not.Empty, "Could not find repo root (package.json)");
            return File.ReadAllText(Path.Combine(dir, Path.Combine(relative)));
        }

        [TestCase("BUGSEE_FEEDBACK", null, true)]
        [TestCase("FOO;BUGSEE_FEEDBACK;BAR", "", true)]
        [TestCase(" FOO ; BUGSEE_FEEDBACK ", null, true)]
        [TestCase("", "1", true)]
        [TestCase(null, "true", true)]
        [TestCase(null, " TRUE ", true)]
        [TestCase("FOO;BAR", null, false)]
        [TestCase("BUGSEE_FEEDBACK_OFF;XBUGSEE_FEEDBACK", null, false)]
        [TestCase("bugsee_feedback", null, false)]
        [TestCase(null, null, false)]
        [TestCase("", "0", false)]
        [TestCase("", "no", false)]
        public void The_switch_is_the_define_or_the_environment_variable(string defines, string env, bool expected)
        {
            Assert.That(FeedbackOptIn.IsEnabled(defines, env), Is.EqualTo(expected));
        }

        [TestCase(7, 4, true)]
        [TestCase(7, 0, true)]
        [TestCase(8, 0, true)]
        [TestCase(8, 1, false)]
        [TestCase(8, 3, false)]
        [TestCase(8, 7, false)]
        [TestCase(9, 0, false)]
        public void The_feedback_aar_is_refused_on_an_agp_that_cannot_build_it(int major, int minor, bool refused)
        {
            string problem = FeedbackOptIn.AndroidAgpProblem(major, minor);
            Assert.That(problem != null, Is.EqualTo(refused));
            if (refused) Assert.That(problem, Does.Contain(major + "." + minor).And.Contain("8.1.1"));
        }

        [Test]
        public void The_android_feedback_aar_is_declared_when_asked_and_dropped_when_not()
        {
            var core = new[] { "bugsee-android", "bugsee-android-ndk" };
            var withFeedback = new[] { "bugsee-android", "bugsee-android-ndk", FeedbackOptIn.AndroidArtifact };
            const string gradle = "apply plugin: 'com.android.library'\n\ndependencies {\n    implementation 'x:y:1'\n}\n";

            string on = GradleMavenInjection.ApplyDependencies(gradle, "com.bugsee", withFeedback, "7.4.2").Text;
            Assert.That(on, Does.Contain("implementation 'com.bugsee:bugsee-android-feedback:7.4.2'"));
            Assert.That(on, Does.Contain("implementation 'com.bugsee:bugsee-android:7.4.2'"));

            // A later export with the switch off rewrites the marked region: the AAR is gone again.
            string off = GradleMavenInjection.ApplyDependencies(on, "com.bugsee", core, "7.4.2").Text;
            Assert.That(off, Does.Not.Contain("bugsee-android-feedback"));
            Assert.That(off, Does.Contain("implementation 'com.bugsee:bugsee-android-ndk:7.4.2'"));

            // And the same inputs are stable.
            var again = GradleMavenInjection.ApplyDependencies(on, "com.bugsee", withFeedback, "7.4.2");
            Assert.That(again.Status, Is.EqualTo(GradleMavenInjection.DependencyStatus.AlreadyInjected));
        }

        const string FeedbackUrl = "https://github.com/bugsee/feedback-spm.git";
        const string CoreUrl = "https://github.com/bugsee/spm.git";

        // Both packages linked, as Unity's PBXProject writes them.
        const string BothLinked =
            "/* Begin PBXBuildFile section */\n" +
            "\t\t228B4264A0A8E7455A559FD4 /* Bugsee in Frameworks */ = {isa = PBXBuildFile; productRef = 72A742839A820990A8F88B84 /* Bugsee */; };\n" +
            "\t\tCCCCCCCCCCCCCCCCCCCCCCCC /* BugseeFeedback in Frameworks */ = {isa = PBXBuildFile; productRef = DDDDDDDDDDDDDDDDDDDDDDDD /* BugseeFeedback */; };\n" +
            "/* End PBXBuildFile section */\n" +
            "\t\t\tpackageProductDependencies = (\n\t\t\t\t72A742839A820990A8F88B84 /* Bugsee */,\n\t\t\t\tDDDDDDDDDDDDDDDDDDDDDDDD /* BugseeFeedback */,\n\t\t\t);\n" +
            "\t\t\tpackageReferences = (\n\t\t\t\t69C04F549EE201CC179B0684 /* XCRemoteSwiftPackageReference \"spm\" */,\n\t\t\t\tEEEEEEEEEEEEEEEEEEEEEEEE /* XCRemoteSwiftPackageReference \"feedback-spm\" */,\n\t\t\t);\n" +
            "/* Begin XCRemoteSwiftPackageReference section */\n" +
            "\t\t69C04F549EE201CC179B0684 /* XCRemoteSwiftPackageReference \"spm\" */ = {\n\t\t\tisa = XCRemoteSwiftPackageReference;\n" +
            "\t\t\trepositoryURL = \"https://github.com/bugsee/spm.git\";\n\t\t\trequirement = {\n\t\t\t\tkind = exactVersion;\n\t\t\t\tversion = \"7.0.0-beta7\";\n\t\t\t};\n\t\t};\n" +
            "\t\tEEEEEEEEEEEEEEEEEEEEEEEE /* XCRemoteSwiftPackageReference \"feedback-spm\" */ = {\n\t\t\tisa = XCRemoteSwiftPackageReference;\n" +
            "\t\t\trepositoryURL = \"https://github.com/bugsee/feedback-spm.git\";\n\t\t\trequirement = {\n\t\t\t\tkind = exactVersion;\n\t\t\t\tversion = \"7.0.0-beta7\";\n\t\t\t};\n\t\t};\n" +
            "/* End XCRemoteSwiftPackageReference section */\n" +
            "/* Begin XCSwiftPackageProductDependency section */\n" +
            "\t\t72A742839A820990A8F88B84 /* Bugsee */ = {\n\t\t\tisa = XCSwiftPackageProductDependency;\n\t\t\tpackage = 69C04F549EE201CC179B0684 /* XCRemoteSwiftPackageReference \"spm\" */;\n\t\t\tproductName = Bugsee;\n\t\t};\n" +
            "\t\tDDDDDDDDDDDDDDDDDDDDDDDD /* BugseeFeedback */ = {\n\t\t\tisa = XCSwiftPackageProductDependency;\n\t\t\tpackage = EEEEEEEEEEEEEEEEEEEEEEEE /* XCRemoteSwiftPackageReference \"feedback-spm\" */;\n\t\t\tproductName = BugseeFeedback;\n\t\t};\n" +
            "/* End XCSwiftPackageProductDependency section */\n";

        [Test]
        public void An_earlier_exports_feedback_link_is_removed_and_the_core_link_stays()
        {
            string text = XcodeLocalPackage.RemovePackageProduct(BothLinked, FeedbackUrl, FeedbackOptIn.IosProduct);

            Assert.That(text, Does.Not.Contain("feedback-spm"));
            Assert.That(text, Does.Not.Contain("BugseeFeedback"));
            Assert.That(text, Does.Not.Contain("EEEEEEEEEEEEEEEEEEEEEEEE"));
            Assert.That(text, Does.Not.Contain("DDDDDDDDDDDDDDDDDDDDDDDD"));
            Assert.That(text, Does.Contain("Bugsee in Frameworks"));
            Assert.That(text, Does.Contain("69C04F549EE201CC179B0684 /* XCRemoteSwiftPackageReference \"spm\" */,"));
            Assert.That(text, Does.Contain("72A742839A820990A8F88B84 /* Bugsee */,"));
            Assert.That(XcodeLocalPackage.RemovePackageProduct(text, FeedbackUrl, FeedbackOptIn.IosProduct), Is.EqualTo(text));
        }

        [Test]
        public void Removing_the_core_package_leaves_the_feedback_link_alone()
        {
            string text = XcodeLocalPackage.RemoveBugseePackage(BothLinked, CoreUrl, "Bugsee");
            Assert.That(text, Does.Contain("feedback-spm"));
            Assert.That(text, Does.Contain("BugseeFeedback in Frameworks"));
            Assert.That(text, Does.Not.Contain("Bugsee in Frameworks */"));
        }

        [Test]
        public void The_bridges_say_so_instead_of_doing_nothing_silently()
        {
            string ios = Read("Runtime", "Platform", "IOS", "IOSBridge.cs");
            string android = Read("Runtime", "Platform", "Android", "AndroidBridge.cs");
            string editor = Read("Runtime", "Platform", "Editor", "EditorBridge.cs");
            string native = Read("Plugins", "iOS", "BugseeUnityBridge.mm");

            Assert.That(ios, Does.Contain("FeedbackOptIn.IosMissingMessage"));
            Assert.That(ios, Does.Contain("_bugsee_feedback_set_listener(OnReceived, OnSent)"));
            Assert.That(android, Does.Contain("FeedbackOptIn.AndroidMissingMessage"));
            Assert.That(editor, Does.Contain("FeedbackOptIn.EditorMessage"));
            // The old iOS bridge only logged; the call has to reach the package now.
            Assert.That(native, Does.Not.Contain("is not in the core 7.x SPM package"));
            Assert.That(native, Does.Contain("showFeedbackUI"));
            Assert.That(native, Does.Contain("BugseeFeedback.BugseeFeedbackListener"));
        }

        [Test]
        public void The_editor_steps_follow_the_one_switch()
        {
            string android = Read("Editor", "BugseeAndroidMavenDependencies.cs");
            string ios = Read("Editor", "BugseeIosSpmPostProcess.cs");
            foreach (string source in new[] { android, ios })
            {
                Assert.That(source, Does.Contain("FeedbackOptIn.IsEnabled("));
                Assert.That(source, Does.Contain("FeedbackOptIn.EnvVar"));
            }

            Assert.That(android, Does.Contain("FeedbackOptIn.AndroidArtifact"));
            Assert.That(ios, Does.Contain("FeedbackOptIn.IosPackageUrl"));
        }
    }
}
