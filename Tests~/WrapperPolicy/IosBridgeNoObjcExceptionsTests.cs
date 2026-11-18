using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The Xcode project Unity generates sets GCC_ENABLE_OBJC_EXCEPTIONS = NO (2021.3, 2022.3, 6000.0), where @try, @catch,
    /// @throw and @finally are compile errors (@synchronized still compiles, and the bridge uses it). The bridge sources are compiled into every iOS export, so none
    /// of them may use these constructs. The iOS bridge test script compiles the bridge with -fno-objc-exceptions too, but only
    /// on macOS; this test fails on every platform.
    /// </summary>
    public class IosBridgeNoObjcExceptionsTests
    {
        static readonly Regex Forbidden = new Regex(@"@(try|catch|throw|finally)\b", RegexOptions.Compiled);

        static string RepoRoot()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        /// <summary>Source with comments and string literals blanked, so a comment explaining the rule does not trip it.</summary>
        // One pass, so a "/*" inside a string or a "//" comment cannot hide code; newlines are kept so line numbers stay right.
        static readonly Regex NonCode = new Regex(
            "/\\*.*?\\*/|//[^\\n]*|@?\"(?:[^\"\\\\\\n]|\\\\.)*\"|'(?:[^'\\\\\\n]|\\\\.)*'",
            RegexOptions.Singleline | RegexOptions.Compiled);

        internal static string CodeOnly(string source) =>
            NonCode.Replace(source, m => new string('\n', m.Value.Count(c => c == '\n')));

        static string[] BridgeSources() =>
            Directory.GetFiles(Path.Combine(RepoRoot(), "Plugins", "iOS"), "*.*", SearchOption.AllDirectories)
                .Where(f => new[] { ".mm", ".m", ".h", ".c", ".cpp" }.Contains(Path.GetExtension(f)))
                .ToArray();

        [Test]
        public void Bridge_sources_exist()
        {
            Assert.That(BridgeSources().Length, Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void Bridge_sources_use_no_objc_exception_constructs()
        {
            var hits = BridgeSources()
                .SelectMany(f => CodeOnly(File.ReadAllText(f)).Split('\n')
                    .Select((line, i) => (f, line, n: i + 1))
                    .Where(t => Forbidden.IsMatch(t.line))
                    .Select(t => $"{Path.GetFileName(t.f)}:{t.n}: {t.line.Trim()}"))
                .ToArray();
            Assert.That(hits, Is.Empty,
                "Unity's Xcode project builds with Objective-C exceptions disabled; guard inputs instead of using @try/@catch:\n" +
                string.Join("\n", hits));
        }

        [Test]
        public void Detector_flags_code_and_ignores_comments_and_strings()
        {
            Assert.That(Forbidden.IsMatch(CodeOnly("void f() { @try { g(); } @catch (NSException *e) {} }")), Is.True);
            Assert.That(Forbidden.IsMatch(CodeOnly("@synchronized (self) { }")), Is.False, "@synchronized compiles with exceptions disabled");
            Assert.That(Forbidden.IsMatch(CodeOnly("// no @try here\n/* @throw */ NSLog(@\"@catch\");")), Is.False);
        }
    }
}
