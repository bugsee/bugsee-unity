using System;
using System.Collections.Generic;
using System.IO;
using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AttachmentSnapshotTests
    {
        [SetUp, TearDown]
        public void ResetState() => AttachmentSnapshot.Configure(null, null);

        static string NewDir()
        {
            return Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "bugsee-snap-" + Guid.NewGuid().ToString("N"))).FullName;
        }

        [Test]
        public void Copies_under_the_first_root()
        {
            var root = NewDir();
            var src = Path.Combine(root, "a.txt");
            File.WriteAllText(src, "hello");
            try
            {
                var dest = AttachmentSnapshot.TryCopy(src, new[] { root }, null);
                Assert.That(dest, Is.Not.Null);
                Assert.That(dest, Does.StartWith(root));
                Assert.That(dest, Does.EndWith(".txt"));
                Assert.That(File.ReadAllText(dest), Is.EqualTo("hello"));
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void Falls_back_to_the_next_root_when_the_first_is_not_writable()
        {
            var root = NewDir();
            var src = Path.Combine(root, "noext");
            File.WriteAllText(src, "x");
            // A file where the directory should be makes CreateDirectory fail, like a read-only /data/local/tmp.
            var blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "");
            try
            {
                var dest = AttachmentSnapshot.TryCopy(src, new[] { Path.Combine(blocked, "sub"), root }, null);
                Assert.That(dest, Is.Not.Null);
                Assert.That(dest, Does.StartWith(root));
                Assert.That(dest, Does.EndWith(".bin"));
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void Warns_and_returns_null_when_no_root_works()
        {
            var root = NewDir();
            var src = Path.Combine(root, "a.txt");
            File.WriteAllText(src, "x");
            var blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "");
            var warnings = new List<string>();
            try
            {
                var dest = AttachmentSnapshot.TryCopy(src, new[] { Path.Combine(blocked, "sub") }, warnings.Add);
                Assert.That(dest, Is.Null);
                Assert.That(warnings, Has.Count.EqualTo(1));
                Assert.That(warnings[0], Does.Contain(src));
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void Configured_cache_root_is_tried_before_the_system_temp_path()
        {
            var cache = NewDir();
            try
            {
                AttachmentSnapshot.Configure(new[] { cache }, null);
                var roots = AttachmentSnapshot.Roots();
                Assert.That(roots[0], Is.EqualTo(cache));
                Assert.That(AttachmentSnapshot.DirectoryPath(), Does.StartWith(cache));
            }
            finally
            {
                AttachmentSnapshot.Configure(null, null);
                Directory.Delete(cache, true);
            }
        }

        [Test]
        public void Roots_without_configuration_is_the_system_temp_path()
        {
            var roots = AttachmentSnapshot.Roots();
            Assert.That(roots, Has.Length.EqualTo(1));
            Assert.That(roots[0], Is.EqualTo(Path.GetTempPath()));
        }

        [Test]
        public void A_configured_root_equal_to_the_temp_path_is_not_listed_twice()
        {
            AttachmentSnapshot.Configure(new[] { Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) }, null);
            Assert.That(AttachmentSnapshot.Roots(), Has.Length.EqualTo(1));
        }

        [Test]
        public void CleanStale_removes_old_copies_and_keeps_recent_ones()
        {
            var root = NewDir();
            try
            {
                var dir = Directory.CreateDirectory(Path.Combine(root, "bugsee-unity-report-attachments")).FullName;
                var old = Path.Combine(dir, "old.bin");
                var fresh = Path.Combine(dir, "fresh.bin");
                File.WriteAllText(old, "x");
                File.WriteAllText(fresh, "x");
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
                AttachmentSnapshot.CleanStale(new[] { root }, TimeSpan.FromDays(1));
                Assert.That(File.Exists(old), Is.False);
                Assert.That(File.Exists(fresh), Is.True);
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void Both_reports_snapshot_through_AttachmentSnapshot_and_the_bootstrap_runs_before_the_scene()
        {
            foreach (var f in new[] { "Runtime/Platform/Android/AndroidReport.cs", "Runtime/Platform/IOS/IosReport.cs" })
                Assert.That(File.ReadAllText(RepoFile(f)), Does.Contain("AttachmentSnapshot.TryCopy(sourcePath)"), f);
            var boot = File.ReadAllText(RepoFile("Runtime/Internal/AttachmentSnapshotBootstrap.cs"));
            Assert.That(boot, Does.Contain("[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]"));
            Assert.That(boot, Does.Contain("Application.temporaryCachePath"));
            Assert.That(boot, Does.Contain("getCacheDir"));
        }


        static string RepoFile(string rel)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "package.json"))) dir = Path.GetDirectoryName(dir);
            return Path.Combine(dir, rel);
        }
    }
}
