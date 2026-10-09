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
    }
}
