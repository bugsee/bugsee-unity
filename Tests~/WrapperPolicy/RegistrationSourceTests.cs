using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class RegistrationSourceTests
    {
        static string FindRepoRoot()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return dir;
                var parent = Directory.GetParent(dir);
                dir = parent?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        static string RepoFile(string relative)
        {
            return Path.GetFullPath(Path.Combine(FindRepoRoot(), relative));
        }

        [Test]
        public void AndroidBridge_source_does_not_call_setWrapper()
        {
            string text = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            Assert.That(text, Does.Not.Contain("\"setWrapper\""));
        }

        [Test]
        public void Provider_class_name_survives_the_gradle_plugin_strip()
        {
            string java = File.ReadAllText(RepoFile("Plugins/Android/UnityWrapperProvider.java"));
            Assert.That(Regex.IsMatch(java, @"class\s+Bugsee[A-Za-z0-9_]+InitProvider\b"), Is.False);
            Assert.That(java, Does.Contain("class UnityWrapperProvider"));
            Assert.That(java, Does.Contain("initOrder"));
            Assert.That(java, Does.Contain("Bugsee.setWrapper"));
        }

        // Unity 2021.3 compiles the provider's Java into the APK but does not merge a package's
        // loose Plugins/Android/AndroidManifest.xml, so without this step the provider is absent
        // and early registration silently never happens. Verified against a real APK build.
        [Test]
        public void Provider_is_injected_into_the_generated_gradle_manifest()
        {
            string injector = File.ReadAllText(RepoFile("Editor/BugseeAndroidWrapperManifest.cs"));
            Assert.That(injector, Does.Contain("IPostGenerateGradleAndroidProject"));
            // A platform guard would drop the callback when the active build target is not Android
            // at compile time; the interface exists for every target.
            Assert.That(injector, Does.Not.Contain("#if UNITY_ANDROID"));
            Assert.That(injector, Does.Contain("\"src\", \"main\""));
            Assert.That(injector, Does.Contain("\"AndroidManifest.xml\""));
            Assert.That(injector, Does.Contain("\"provider\""));

            string template = File.ReadAllText(RepoFile("Plugins/Android/AndroidManifest.xml"));
            Assert.That(template, Does.Contain("com.bugsee.unity.UnityWrapperProvider"));
            Assert.That(template, Does.Contain("android:initOrder=\"200\""));
        }
    }
}
