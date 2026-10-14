using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The Editor and Runtime scripts are compiled only by Unity, so a bad merge (two PRs both declaring a
    /// local, both adding a using) reached the integration branch and broke every build with "Scripts have
    /// compiler errors". Unity's assemblies are not available here, so this compiles the package sources
    /// without them and fails only on errors that do not depend on resolving a Unity type: syntax errors,
    /// clashing local/member/type declarations and duplicate usings.
    /// </summary>
    public class PackageSourceHygieneTests
    {
        static readonly HashSet<string> ReferenceIndependent = new HashSet<string>
        {
            "CS0101", // namespace already contains a definition
            "CS0102", // type already contains a definition
            "CS0105", // using directive appeared previously
            "CS0111", // member with the same signature already defined
            "CS0128", // local already defined in this scope
            "CS0136", // local conflicts with a local in an enclosing scope
            "CS0841", // local used before declaration
        };

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

        static IEnumerable<string> PackageSources()
        {
            var root = RepoRoot();
            return new[] { "Editor", "Runtime" }
                .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
                .OrderBy(p => p, StringComparer.Ordinal);
        }

        // Each Unity platform define set the package is compiled with.
        static readonly string[][] DefineSets =
        {
            new[] { "UNITY_EDITOR", "UNITY_2021_2_OR_NEWER", "UNITY_IOS" },
            new[] { "UNITY_EDITOR", "UNITY_2021_2_OR_NEWER", "UNITY_ANDROID" },
            new[] { "UNITY_IOS", "UNITY_2021_2_OR_NEWER" },
            new[] { "UNITY_ANDROID", "UNITY_2021_2_OR_NEWER" },
        };

        [TestCaseSource(nameof(DefineSets))]
        public void PackageSourcesHaveNoReferenceIndependentCompileErrors(string[] defines)
        {
            var options = new CSharpParseOptions(LanguageVersion.CSharp9, preprocessorSymbols: defines);
            var trees = PackageSources()
                .Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), options, p))
                .ToList();
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create(
                "PackageSourceHygiene", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            var problems = trees.SelectMany(t => t.GetDiagnostics())
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Concat(compilation.GetDiagnostics().Where(d => ReferenceIndependent.Contains(d.Id)))
                .Select(d => d.ToString())
                .Distinct()
                .ToList();

            Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
        }
    }
}
