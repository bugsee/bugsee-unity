using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class BuildPathExclusionTests
    {
        static bool X(string path, string root) => BuildPathExclusion.IsExcludedPath(path, root);

        [TestCase("/Users/me/Library/CloudStorage/OneDrive-Acme/proj/Builds", "/Users/me/Library/CloudStorage/OneDrive-Acme/proj/Builds/a/libil2cpp.so")]
        [TestCase("/tmp/Temp/obj/Bee/proj/Builds", "/tmp/Temp/obj/Bee/proj/Builds/libil2cpp.so")]
        [TestCase("C:\\Temp\\proj\\Builds", "C:\\Temp\\proj\\Builds\\arm64-v8a\\libil2cpp.so")]
        [TestCase("C:\\Users\\me\\AppData\\Local\\Temp\\proj\\Builds", "C:\\Users\\me\\AppData\\Local\\Temp\\proj\\Builds\\x.symbols.zip")]
        [TestCase("/Users/me/Library/CloudStorage/OneDrive/proj/Builds/", "/Users/me/Library/CloudStorage/OneDrive/proj/Builds/x.symbols.zip")]
        public void A_project_under_an_excluded_looking_folder_is_not_excluded(string root, string file)
        {
            Assert.That(X(file, root), Is.False);
        }

        [Test]
        public void The_old_absolute_rule_would_have_excluded_these()
        {
            // Documents the bug: the same files, tested with no root (whole path), are excluded.
            Assert.That(X("/Users/me/Library/CloudStorage/OneDrive/proj/Builds/libil2cpp.so", null), Is.True);
            Assert.That(X("/Users/me/Library/CloudStorage/OneDrive/proj/Builds/libil2cpp.so", "/Users/me/Library/CloudStorage/OneDrive/proj/Builds"), Is.False);
        }

        [TestCase("/p/Builds", "/p/Builds/Library/Bee/artifacts/Android/x/libil2cpp.so")]
        [TestCase("/p/Builds", "/p/Builds/Temp/StagingArea/libil2cpp.so")]
        [TestCase("/p/Builds", "/p/Builds/x/obj/local/arm64-v8a/libil2cpp.so")]
        [TestCase("/p/Builds", "/p/Builds/a/Bee/libil2cpp.so")]
        [TestCase("/p", "/p/Library/Bee/artifacts/libil2cpp.so")]
        [TestCase("C:\\p\\Builds", "C:\\p\\Builds\\Library\\Bee\\libil2cpp.so")]
        [TestCase("C:\\p\\Builds", "C:/p/Builds/Temp/libil2cpp.so")]
        [TestCase("/p/Builds", "/p/Builds/a/b/OBJ/libil2cpp.so")]
        [TestCase("/p/Builds", "/p/builds/LIBRARY/libil2cpp.so")]
        [TestCase("C:\\P\\Builds", "c:\\p\\builds\\temp\\libil2cpp.so")]
        public void Intermediate_folders_below_the_root_are_still_excluded(string root, string file)
        {
            Assert.That(X(file, root), Is.True);
        }

        [Test]
        public void A_root_that_is_itself_an_intermediate_folder_scans_its_own_contents()
        {
            // A symbol scan may legitimately start in Library/Bee/artifacts/Android/il2cppOutput.
            var root = "/p/Library/Bee/artifacts/Android/il2cppOutput";
            Assert.That(X(root + "/arm64-v8a/libil2cpp.so", root), Is.False);
            Assert.That(X(root + "/obj/arm64-v8a/libil2cpp.so", root), Is.True);
        }

        [Test]
        public void Only_folders_count_not_the_file_or_a_name_that_merely_contains_the_word()
        {
            const string root = "/p/Builds";
            Assert.That(X("/p/Builds/Temp", root), Is.False);
            Assert.That(X("/p/Builds/obj", root), Is.False);
            Assert.That(X("/p/Builds/Library.framework/UnityFramework", root), Is.False);
            Assert.That(X("/p/Builds/Template/libil2cpp.so", root), Is.False);
            Assert.That(X("/p/Builds/objects/libil2cpp.so", root), Is.False);
            Assert.That(X("/p/Builds/MyBee/libil2cpp.so", root), Is.False);
            Assert.That(X("/p/Builds/Libraries/libil2cpp.so", root), Is.False);
        }

        [Test]
        public void A_sibling_folder_sharing_the_roots_name_as_a_prefix_is_not_under_it()
        {
            // "/p/Builds2/Temp/x" is not below "/p/Builds"; it is tested whole, where Temp excludes it.
            Assert.That(X("/p/Builds2/Temp/x.so", "/p/Builds"), Is.True);
            Assert.That(X("/p/Builds2/x.so", "/p/Builds"), Is.False);
        }

        [Test]
        public void A_relative_path_is_tested_as_given()
        {
            Assert.That(X("arm64-v8a/libil2cpp.so", "/p/Builds"), Is.False);
            Assert.That(X("Library/Bee/libil2cpp.so", "/p/Builds"), Is.True);
            Assert.That(X("x\\obj\\libil2cpp.so", ""), Is.True);
        }

        [Test]
        public void Empty_input_is_not_excluded_and_a_filesystem_root_works()
        {
            Assert.That(X(null, "/p"), Is.False);
            Assert.That(X("", "/p"), Is.False);
            Assert.That(X("/Builds/libil2cpp.so", "/"), Is.False);
            Assert.That(X("/Library/libil2cpp.so", "/"), Is.True);
        }

        [Test]
        public void A_scan_of_a_real_tree_under_an_excluded_looking_path_keeps_only_the_builds_own_files()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "bugsee-excl-" + Guid.NewGuid().ToString("N"));
            var root = Path.Combine(tmp, "Library", "CloudStorage", "OneDrive-Test", "Temp", "proj", "Builds");
            try
            {
                var own = Path.Combine(root, "x_BackUpThisFolder_ButDontShipItWithYourGame", "arm64-v8a", "libil2cpp.so");
                var junk = Path.Combine(root, "Library", "Bee", "artifacts", "Android", "libil2cpp.so");
                var obj = Path.Combine(root, "x", "obj", "libil2cpp.so");
                foreach (var f in new[] { own, junk, obj })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(f));
                    File.WriteAllText(f, "x");
                }

                var kept = 0;
                foreach (var f in Directory.EnumerateFiles(root, "libil2cpp.so", SearchOption.AllDirectories))
                {
                    if (!X(f, root))
                    {
                        Assert.That(f, Is.EqualTo(own));
                        kept++;
                    }
                }
                Assert.That(kept, Is.EqualTo(1));
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        [Test]
        public void A_symlinked_root_and_a_symlinked_child_are_judged_by_the_names_the_scan_walks()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                Assert.Ignore("Symlink creation needs privileges on Windows; the rule is lexical and covered above.");
            }

            var tmp = Path.Combine(Path.GetTempPath(), "bugsee-excl-" + Guid.NewGuid().ToString("N"));
            try
            {
                var real = Path.Combine(tmp, "Temp", "real");
                Directory.CreateDirectory(Path.Combine(real, "arm64-v8a"));
                File.WriteAllText(Path.Combine(real, "arm64-v8a", "libil2cpp.so"), "x");
                var link = Path.Combine(tmp, "link");
                Directory.CreateSymbolicLink(link, real);

                // Root reached through a link: the real location is under Temp, the walked path is not.
                var viaLink = Path.Combine(link, "arm64-v8a", "libil2cpp.so");
                Assert.That(X(viaLink, link), Is.False);

                // A child link named like an intermediate folder is excluded by the name the scan sees.
                var obj = Path.Combine(tmp, "link", "obj");
                Directory.CreateSymbolicLink(obj, Path.Combine(real, "arm64-v8a"));
                Assert.That(X(Path.Combine(obj, "libil2cpp.so"), link), Is.True);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        [Test]
        public void The_editor_scans_use_the_shared_helper_with_their_scan_root_and_keep_no_local_copy()
        {
            var repo = FindRepoRoot();
            foreach (var name in new[] { "BugseeSymbolUpload.cs", "BugseeIl2CppLinemapUpload.cs", "BugseeIl2CppModuleIdentity.cs" })
            {
                var src = File.ReadAllText(Path.Combine(repo, "Editor", name));
                Assert.That(src, Does.Contain("BuildPathExclusion.IsExcludedPath("), name);
                Assert.That(src, Does.Not.Contain("static bool IsExcludedPath"), name);
                Assert.That(src, Does.Not.Contain("\"/Library/\""), name);
                Assert.That(src, Does.Not.Contain("\"/Temp/\""), name);
                Assert.That(src, Does.Not.Contain("\"/obj/\""), name);
                Assert.That(src, Does.Not.Contain("\"/Bee/\""), name);
                Assert.That(src, Does.Not.Match(@"IsExcludedPath\([A-Za-z]+\)"), name);
            }
        }

        static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json")))
            {
                dir = dir.Parent;
            }
            Assert.That(dir, Is.Not.Null, "package.json not found above the test binaries");
            return dir.FullName;
        }
    }
}
