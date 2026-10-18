using System;
using System.IO;
using Bugsee.Editor;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class LocalPackageCopyTests
    {
        string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "bugsee-copy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            LocalPackageCopy.DeleteTree(_root, bestEffort: true);
        }

        string MakePackage(string name, bool readOnly = false)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.Combine(dir, "Bugsee.xcframework", "ios-arm64", "Bugsee.framework", "Versions", "A"));
            File.WriteAllText(Path.Combine(dir, "Package.swift"), "// package");
            File.WriteAllText(Path.Combine(dir, "Bugsee.xcframework", "ios-arm64", "Bugsee.framework", "Versions", "A", "Bugsee"), "binary");
            File.CreateSymbolicLink(Path.Combine(dir, "Bugsee.xcframework", "ios-arm64", "Bugsee.framework", "Versions", "Current"), "A");
            Directory.CreateDirectory(Path.Combine(dir, "Sources", "BugseeSwiftUI"));
            File.WriteAllText(Path.Combine(dir, "Sources", "BugseeSwiftUI", "A.swift"), "// swift");
            File.WriteAllText(Path.Combine(dir, "BUILD-INFO.txt"), "not copied");
            if (readOnly) Chmod("a-w", dir);
            return dir;
        }

        static void Chmod(string mode, string path)
        {
            var info = new System.Diagnostics.ProcessStartInfo("/bin/chmod") { UseShellExecute = false };
            info.ArgumentList.Add("-R");
            info.ArgumentList.Add(mode);
            info.ArgumentList.Add(path);
            using (var p = System.Diagnostics.Process.Start(info)) p.WaitForExit();
        }

        static bool IsRoot() => Environment.UserName == "root";

        string[] StagingFolders() => Directory.GetDirectories(_root, "*.staging-*");

        [Test]
        public void A_read_only_source_is_copied_marked_and_writable_with_its_symlinks()
        {
            Assume.That(Environment.OSVersion.Platform, Is.EqualTo(PlatformID.Unix));
            string source = MakePackage("ro", readOnly: true);
            string dest = Path.Combine(_root, "export", "Bugsee");
            Directory.CreateDirectory(Path.GetDirectoryName(dest));

            LocalPackageCopy.CopyAndSwapIn(source, dest, selective: true);

            Assert.That(File.Exists(Path.Combine(dest, LocalPackageCopy.Marker)));
            Assert.That(LocalPackageCopy.Problem(dest), Is.Null);
            Assert.That(File.Exists(Path.Combine(dest, "Sources", "BugseeSwiftUI", "A.swift")));
            Assert.That(File.Exists(Path.Combine(dest, "BUILD-INFO.txt")), Is.False);
            var link = new FileInfo(Path.Combine(dest, "Bugsee.xcframework", "ios-arm64", "Bugsee.framework", "Versions", "Current"));
            Assert.That(link.LinkTarget, Is.EqualTo("A"));
            Assert.That(Directory.GetDirectories(Path.GetDirectoryName(dest), "*.staging-*"), Is.Empty);
            File.WriteAllText(Path.Combine(dest, "writable.txt"), "x");
            Chmod("u+w", source);
        }

        [Test]
        public void An_unusable_source_throws_leaves_the_old_copy_and_no_staging_folder()
        {
            string good = MakePackage("good");
            string dest = Path.Combine(_root, "Bugsee");
            LocalPackageCopy.CopyAndSwapIn(good, dest, selective: true);
            File.WriteAllText(Path.Combine(dest, "keep.txt"), "old");

            string broken = Path.Combine(_root, "broken");
            Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, "Package.swift"), "// package");

            Assert.Throws<InvalidOperationException>(() => LocalPackageCopy.CopyAndSwapIn(broken, dest, selective: true));
            Assert.That(File.Exists(Path.Combine(dest, "keep.txt")), "the old copy is untouched");
            Assert.That(StagingFolders(), Is.Empty);
        }

        [Test]
        public void A_copy_that_fails_half_way_throws_and_cleans_up_without_touching_the_old_copy()
        {
            Assume.That(Environment.OSVersion.Platform, Is.EqualTo(PlatformID.Unix));
            Assume.That(IsRoot(), Is.False, "root reads an unreadable file");
            string good = MakePackage("good");
            string dest = Path.Combine(_root, "Bugsee");
            LocalPackageCopy.CopyAndSwapIn(good, dest, selective: true);
            File.WriteAllText(Path.Combine(dest, "keep.txt"), "old");

            string source = MakePackage("unreadable");
            string unreadable = Path.Combine(source, "Bugsee.xcframework", "ios-arm64", "Bugsee.framework", "Versions", "A", "Bugsee");
            Chmod("000", unreadable);
            try
            {
                Assert.Throws<IOException>(() => LocalPackageCopy.CopyAndSwapIn(source, dest, selective: true));
            }
            finally
            {
                Chmod("u+rw", unreadable);
            }

            Assert.That(File.Exists(Path.Combine(dest, "keep.txt")), "the old copy is untouched");
            Assert.That(StagingFolders(), Is.Empty, "the failed copy's staging folder is removed");
        }

        [Test]
        public void A_read_only_tree_is_deleted()
        {
            Assume.That(Environment.OSVersion.Platform, Is.EqualTo(PlatformID.Unix));
            string tree = MakePackage("tree", readOnly: true);
            LocalPackageCopy.DeleteTree(tree);
            Assert.That(Directory.Exists(tree), Is.False);
        }

        [Test]
        public void A_symlink_alias_resolves_to_the_same_physical_path()
        {
            Assume.That(Environment.OSVersion.Platform, Is.EqualTo(PlatformID.Unix));
            string real = Path.Combine(_root, "real");
            Directory.CreateDirectory(Path.Combine(real, "inner"));
            string alias = Path.Combine(_root, "alias");
            Directory.CreateSymbolicLink(alias, real);
            Assert.That(LocalPackageCopy.PhysicalPath(Path.Combine(alias, "inner")), Is.EqualTo(LocalPackageCopy.PhysicalPath(Path.Combine(real, "inner"))));
        }
    }
}
