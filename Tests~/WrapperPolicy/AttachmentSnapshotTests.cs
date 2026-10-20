using System;
using System.Collections.Generic;
using System.IO;
using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AttachmentSnapshotTests
    {
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
                AttachmentSnapshot.Configure(cache, null);
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
    }
}
