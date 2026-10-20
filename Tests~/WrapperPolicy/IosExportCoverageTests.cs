using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// Every export the C# side imports from the iOS bridge must be called by a native test binary (Tests~/iOS/*.mm,
    /// run by CI on a simulator): NativeBindingTests proves the signatures agree, this proves each export is also
    /// executed once through the C ABI, so a symbol that links but crashes or mis-reads its arguments is seen in CI.
    /// A new DllImport without a native test fails here until one is added.
    /// </summary>
    public class IosExportCoverageTests
    {
        // Exports that cannot run in a test process, with the reason.
        static readonly Dictionary<string, string> NotExercised = new Dictionary<string, string>
        {
            { "_bugsee_test_crash", "terminates the process on purpose; covered by the TestCrash device scenarios" },
        };

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

        static string StripComments(string s)
        {
            s = Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(s, @"(?<![:""])//[^\n]*", " ");
        }

        [Test]
        public void Every_imported_export_is_called_by_a_native_test()
        {
            var imports = new SortedSet<string>();
            foreach (var path in Directory.GetFiles(RepoFile("Runtime/Platform/IOS"), "*.cs"))
            {
                var src = StripComments(File.ReadAllText(path));
                foreach (Match m in Regex.Matches(src, @"\[DllImport\(""__Internal""[^\]]*\]\s*(?:internal|public|private)?\s*static\s+extern\s+[\w.<>\[\]]+\s+(_bugsee_\w+)\s*\("))
                    imports.Add(m.Groups[1].Value);
            }

            Assert.That(imports.Count, Is.GreaterThanOrEqualTo(55), "the DllImport parser found too few imports");

            var tests = string.Join("\n", Directory.GetFiles(RepoFile("Tests~/iOS"), "*.mm")
                .Select(f => StripComments(File.ReadAllText(f))));
            // A call, not the prototype that declares it: a prototype has a type (an identifier or "*") right before the name.
            var uncovered = imports
                .Where(name => !NotExercised.ContainsKey(name))
                .Where(name => !Regex.IsMatch(tests, @"(?<![\w*]\s{0,8})\b" + name + @"\("))
                .ToList();
            Assert.That(uncovered, Is.Empty, "DllImported exports no Tests~/iOS/*.mm test calls (add a call to BridgeExportTests.mm)");
        }

        [Test]
        public void The_allow_list_names_real_imports()
        {
            var all = string.Join("\n", Directory.GetFiles(RepoFile("Runtime/Platform/IOS"), "*.cs").Select(File.ReadAllText));
            foreach (var name in NotExercised.Keys)
                Assert.That(all, Does.Contain(name));
        }
    }
}
