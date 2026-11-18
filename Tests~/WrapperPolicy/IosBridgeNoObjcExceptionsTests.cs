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
    /// of them may use these constructs. The iOS bridge test script compiles the two bridge files with -fno-objc-exceptions too (macOS only), which is the
    /// authoritative check: this text scan runs on Linux CI and is a cheap early guard, not a parser. It does not see
    /// @try spelled through a macro, behind a raw string containing a quote, or between C++14 digit separators.
    /// </summary>
    public class IosBridgeNoObjcExceptionsTests
    {
        static readonly Regex Forbidden = new Regex(@"@\s*(try|catch|throw|finally)\b", RegexOptions.Compiled);

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
        // One pass, so a "/*" inside a string or a "//" comment cannot hide code; newlines are kept so line numbers stay right
        // (except that each backslash line continuation is joined first, so lines after one report a number one lower).
        static readonly Regex NonCode = new Regex(
            "/\\*.*?\\*/|//[^\\n]*|@?\"(?:[^\"\\\\\\n]|\\\\.)*\"|'(?:[^'\\\\\\n]|\\\\.)*'",
            RegexOptions.Singleline | RegexOptions.Compiled);

        internal static string CodeOnly(string source) =>
            NonCode.Replace(Regex.Replace(source, @"\\\r?\n", ""), m => " " + new string('\n', m.Value.Count(c => c == '\n')));

        /// <summary>The scan the file test runs on each bridge source: one "name:line: token" per forbidden construct.</summary>
        internal static string[] Violations(string name, string source)
        {
            var code = CodeOnly(source);
            return Forbidden.Matches(code).Cast<Match>()
                .Select(m => $"{name}:{code.Take(m.Index).Count(c => c == '\n') + 1}: {m.Value}").ToArray();
        }

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
            var hits = BridgeSources().SelectMany(f => Violations(Path.GetFileName(f), File.ReadAllText(f))).ToArray();
            Assert.That(hits, Is.Empty,
                "Unity's Xcode project builds with Objective-C exceptions disabled; guard inputs instead of using @try/@catch:\n" +
                string.Join("\n", hits));
        }

        [Test]
        public void Detector_flags_code_and_ignores_comments_and_strings()
        {
            Assert.That(Forbidden.IsMatch(CodeOnly("void f() { @try { g(); } @catch (NSException *e) {} }")), Is.True);
            Assert.That(Forbidden.IsMatch(CodeOnly("@ try { }")), Is.True);
            Assert.That(Forbidden.IsMatch(CodeOnly("@throw/**/e;")), Is.True, "a comment is whitespace, not nothing");
            Assert.That(Violations("x.mm", "int a;\nint b;\n@\nthrow e;"), Is.EqualTo(new[] { "x.mm:3: @\nthrow" }), "keyword on the next line, reported at the @");
            Assert.That(Violations("x.mm", "/* a\nb */ @try {}"), Is.EqualTo(new[] { "x.mm:2: @try" }));
            Assert.That(Violations("x.mm", "// @try\nNSLog(@\"@throw\");"), Is.Empty);
            Assert.That(Forbidden.IsMatch(CodeOnly("@tr\\\ny { }")), Is.True);
            Assert.That(Forbidden.IsMatch(CodeOnly("@synchronized (self) { }")), Is.False, "@synchronized compiles with exceptions disabled");
            Assert.That(Forbidden.IsMatch(CodeOnly("// no @try here\n/* @throw */ NSLog(@\"@catch\");")), Is.False);
        }
    }
}
