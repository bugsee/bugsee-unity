using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// bugsee-unity#190: Bugsee.Log(message, level, requiresFiltering) reaches both native opt-in overloads, and the
    /// two-argument Log keeps the native default (skips the log filter).
    /// </summary>
    public class LogRequiresFilteringSourceTests
    {
        static string RepoFile(string relative)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return Path.Combine(dir, relative);
                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        static string Read(string relative) => File.ReadAllText(RepoFile(relative));

        [Test]
        public void Public_log_keeps_the_filter_skipping_default_and_adds_the_opt_in()
        {
            var api = Read("Runtime/Bugsee.cs");
            Assert.That(Regex.IsMatch(api, @"public static void Log\(string message, LogLevel level = LogLevel\.Info\) =>\s*Bridge\.Log\(message, level, false\);"));
            Assert.That(Regex.IsMatch(api, @"public static void Log\(string message, LogLevel level, bool requiresFiltering\) =>\s*Bridge\.Log\(message, level, requiresFiltering\);"));
            Assert.That(Read("Runtime/Platform/IBugseeNativeBridge.cs"), Does.Contain("void Log(string message, LogLevel level, bool requiresFiltering);"));
            Assert.That(Read("Runtime/Platform/Editor/EditorBridge.cs"), Does.Contain("public void Log(string message, LogLevel level, bool requiresFiltering)"));
        }

        [Test]
        public void Android_passes_the_flag_to_the_three_argument_native_log()
        {
            var android = Read("Runtime/Platform/Android/AndroidBridge.cs");
            Assert.That(android, Does.Contain("public void Log(string message, LogLevel level, bool requiresFiltering)"));
            Assert.That(android, Does.Contain("if (message == null) return;"));
            Assert.That(android, Does.Contain("_bugsee.CallStatic(\"log\", message, ll, requiresFiltering);"));
        }

        [Test]
        public void Ios_passes_the_flag_through_the_export_to_the_native_overload()
        {
            var cs = Read("Runtime/Platform/IOS/IOSBridge.cs");
            Assert.That(cs, Does.Contain("_bugsee_log(message, (int)level, requiresFiltering ? 1 : 0);"));
            Assert.That(cs, Does.Contain("static extern void _bugsee_log(string message, int level, int requiresFiltering);"));
            var mm = Read("Plugins/iOS/BugseeUnityBridge.mm");
            Assert.That(mm, Does.Contain("void _bugsee_log(const char *message, int level, int requiresFiltering)"));
            Assert.That(mm, Does.Contain("[Bugsee log:BugseeNSString(message) level:(BugseeLogLevel)level requiresFiltering:requiresFiltering != 0];"));
            Assert.That(Read("Tests~/iOS/BridgeExportTests.mm"), Does.Contain("_bugsee_log(\"hello\", 3, 1);"));
        }

        [Test]
        public void Documentation_no_longer_says_the_opt_in_is_missing()
        {
            Assert.That(Read("Documentation~/index.md"), Does.Not.Contain("does not expose it yet (#190)"));
            Assert.That(Read("Runtime/Bugsee.cs"), Does.Not.Contain("does not expose the opt-in"));
            Assert.That(Read("Samples~/BugseeMiniGame/README.md"), Does.Not.Contain("does not expose the opt-in"));
        }
    }
}
