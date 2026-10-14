using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class IosDsymSourceTests
    {
        static string RepoFile(string relative)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return Path.Combine(dir, relative);
                var parent = Directory.GetParent(dir);
                dir = parent?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "Missing method: " + signature);
            int brace = source.IndexOf('{', start);
            int depth = 0;
            for (int i = brace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(start, i - start + 1);
            }

            Assert.Fail("Unclosed method body: " + signature);
            return null;
        }

        static string PostProcessSource() =>
            File.ReadAllText(RepoFile("Editor/BugseeIosSpmPostProcess.cs"));

        [Test]
        public void Dsym_force_honours_env_var_and_editor_pref_opt_outs()
        {
            var body = ExtractMethodBody(PostProcessSource(), "static void EnsureDsymGeneration(");

            Assert.That(body, Does.Contain("GetEnvironmentVariable(\"BUGSEE_NO_DSYM_FORCE\")"));
            Assert.That(body, Does.Contain("EditorPrefs.GetBool(PrefNoDsymForce, false)"),
                "A Finder-launched Editor never sees a shell export; the menu toggle must also skip the force.");
        }

        [Test]
        public void Dsym_opt_out_menu_toggles_the_same_pref()
        {
            var source = PostProcessSource();
            var body = ExtractMethodBody(source, "static void MenuToggleNoDsymForce(");

            Assert.That(source, Does.Contain("[MenuItem(MenuNoDsymForce)]"));
            Assert.That(body, Does.Contain("EditorPrefs.SetBool(PrefNoDsymForce, next)"));
        }
    }
}
