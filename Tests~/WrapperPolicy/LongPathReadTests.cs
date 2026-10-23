using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bugsee.Editor;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// Build outputs under a path over 260 characters on a Windows Editor (Mono 6.13 lists them but cannot open them),
    /// issue 252. The policy is exercised on any host; the CreateFileW route itself runs only on Windows.
    /// </summary>
    public class LongPathReadTests
    {
        static readonly string Long = @"E:\" + string.Join(@"\", Enumerable.Repeat(new string('a', 40), 6)) + @"\game.apk";

        [Test]
        public void A_drive_path_gets_the_extended_prefix_and_backslashes()
        {
            Assert.That(LongPathPolicy.Extend(@"C:\a\b.apk"), Is.EqualTo(@"\\?\C:\a\b.apk"));
            Assert.That(LongPathPolicy.Extend("C:/a/b.apk"), Is.EqualTo(@"\\?\C:\a\b.apk"));
        }

        [Test]
        public void A_unc_path_gets_the_unc_form()
        {
            Assert.That(LongPathPolicy.Extend(@"\\srv\share\a.apk"), Is.EqualTo(@"\\?\UNC\srv\share\a.apk"));
        }

        [Test]
        public void Relative_device_and_already_extended_paths_are_left_alone()
        {
            Assert.That(LongPathPolicy.Extend(@"a\b.apk"), Is.EqualTo(@"a\b.apk"));
            Assert.That(LongPathPolicy.Extend("/tmp/a.apk"), Is.EqualTo("/tmp/a.apk"));
            Assert.That(LongPathPolicy.Extend(@"\\.\COM1"), Is.EqualTo(@"\\.\COM1"));
            Assert.That(LongPathPolicy.Extend(@"\\?\C:\a.apk"), Is.EqualTo(@"\\?\C:\a.apk"));
            Assert.That(LongPathPolicy.Extend(null), Is.Null);
            Assert.That(LongPathPolicy.Extend("//?/C:/a/b.apk"), Is.EqualTo(@"\\?\C:\a\b.apk"), "the extended form with slashes is not read as a UNC share");
        }

        [Test]
        public void Only_a_long_absolute_windows_path_is_worth_the_retry()
        {
            Assert.That(LongPathPolicy.NeedsExtended(Long, true), Is.True);
            Assert.That(LongPathPolicy.NeedsExtended(Long, false), Is.False, "other platforms never take the branch");
            Assert.That(LongPathPolicy.NeedsExtended(@"C:\short\a.apk", true), Is.False);
            Assert.That(LongPathPolicy.NeedsExtended(@"\\?\" + Long, true), Is.False);
            Assert.That(LongPathPolicy.NeedsExtended(new string('a', 300), true), Is.False, "relative: nothing to extend");
        }

        [Test]
        public void A_path_is_long_from_the_length_the_retry_starts_at()
        {
            Assert.That(LongPathPolicy.IsLongPath(new string('a', LongPathPolicy.ExtendFrom)), Is.True);
            Assert.That(LongPathPolicy.IsLongPath(new string('a', LongPathPolicy.ExtendFrom - 1)), Is.False);
            Assert.That(LongPathPolicy.IsLongPath(null), Is.False);
        }

        [Test]
        public void An_ordinary_file_is_read_through_the_helper_on_every_host()
        {
            var path = Path.Combine(Path.GetTempPath(), "bugsee-longpath-" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            try
            {
                Assert.That(BugseeLongPathFile.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3 }));
                using (var s = BugseeLongPathFile.OpenRead(path))
                {
                    Assert.That(s.Length, Is.EqualTo(3));
                }

                Assert.That(BugseeLongPathFile.TryOpenExtended(path), Is.Null, "a short path never takes the extended route");
                Assert.Throws<FileNotFoundException>(() => BugseeLongPathFile.OpenRead(path + ".missing").Dispose());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void A_file_under_a_path_over_260_characters_can_be_read_through_the_extended_route()
        {
            Assume.That(Path.DirectorySeparatorChar, Is.EqualTo('\\'), "Windows only");
            var top = Path.Combine(Path.GetTempPath(), "bugsee-lp-" + Guid.NewGuid().ToString("N"));
            var dir = Path.Combine(top, new string('a', 120), new string('b', 120));
            Directory.CreateDirectory(@"\\?\" + dir);
            var plain = Path.Combine(dir, "game.apk");
            using (var fs = new FileStream(@"\\?\" + plain, FileMode.Create))
            using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("assets/x.txt").Open()))
            {
                w.Write("hello");
            }

            try
            {
                Assert.That(plain.Length, Is.GreaterThan(260));
                // The fallback itself (CI's .NET opens long paths without it): bytes, and seekable for ZipArchive.
                using (var stream = BugseeLongPathFile.TryOpenExtended(plain))
                {
                    Assert.That(stream, Is.Not.Null);
                    Assert.That(stream.CanSeek, Is.True);
                    using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read))
                    using (var r = new StreamReader(zip.GetEntry("assets/x.txt").Open()))
                    {
                        Assert.That(r.ReadToEnd(), Is.EqualTo("hello"));
                    }
                }

                Assert.That(BugseeLongPathFile.ReadAllBytes(plain).Length, Is.GreaterThan(0));

                // A missing file reports the system's message and the path, not a length hint.
                var ex = Assert.Throws<IOException>(() => BugseeLongPathFile.ReadAllText(Path.Combine(dir, "missing.txt")));
                Assert.That(ex.Message, Does.Contain("missing.txt").And.Not.Contain("shorter"));
            }
            finally
            {
                Directory.Delete(@"\\?\" + top, true);
            }
        }

        [Test]
        public void The_editor_reads_of_build_outputs_all_go_through_the_long_path_route()
        {
            foreach (var name in new[] { "BugseeBuildIdentity.cs", "BugseeAndroidNativeLibUpload.cs", "BugseeIl2CppModuleIdentity.cs" })
            {
                var text = File.ReadAllText(Path.Combine(Root(), "Editor", name));
                Assert.That(text, Does.Contain("BugseeLongPathFile."), name);
                Assert.That(System.Text.RegularExpressions.Regex.IsMatch(text,
                        @"(?<![A-Za-z])(File\.(OpenRead|ReadAllBytes|ReadAllText|Copy)|ZipFile\.OpenRead)\(|new FileStream\("),
                    Is.False, name);
            }
        }

        [Test]
        public void The_native_upload_tells_an_unreadable_apk_from_a_stale_library()
        {
            var lib = new NativeLibCandidate { Path = "/p/libplugin.so", Abi = "arm64-v8a", Name = "libplugin.so", BuildId = "abc", Length = 10 };
            var notes = new List<string>();

            var chosen = AndroidNativeLibs.Choose(new[] { lib }, new HashSet<string>(), new HashSet<string>(), new HashSet<string> { "arm64-v8a" }, notes,
                "game.apk: could not open");

            Assert.That(chosen, Is.Empty);
            Assert.That(notes.Single(), Does.Contain("could not be checked against the app").And.Contain("game.apk: could not open")
                .And.Not.Contain("not the copy packaged"));
        }

        [Test]
        public void Libraries_are_scanned_and_staged_through_the_supplied_opener()
        {
            var opened = new List<string>();
            var libs = AndroidNativeLibs.ScanLibraries(new[] { "/x/lib/arm64-v8a/libfoo.so" }, out var without, new List<string>(),
                p => { opened.Add(p); return new MemoryStream(new byte[8]); });

            Assert.That(opened, Is.EqualTo(new[] { "/x/lib/arm64-v8a/libfoo.so" }));
            Assert.That(libs, Is.Empty);
            Assert.That(without, Is.EqualTo(0));

            var root = Path.Combine(Path.GetTempPath(), "bugsee-stage-" + Guid.NewGuid().ToString("N"));
            var lib = new NativeLibCandidate { Path = "/virtual/libfoo.so", Abi = "arm64-v8a", Name = "libfoo.so", BuildId = "abc", Length = 3 };
            try
            {
                AndroidNativeLibs.Stage(new[] { lib }, root, p => new MemoryStream(new byte[] { 4, 5, 6 }));

                var staged = Directory.GetFiles(root, "libfoo.so", SearchOption.AllDirectories).Single();
                Assert.That(File.ReadAllBytes(staged), Is.EqualTo(new byte[] { 4, 5, 6 }));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void A_whole_file_read_returns_every_byte_in_one_buffer()
        {
            var path = Path.Combine(Path.GetTempPath(), "bugsee-rab-" + Guid.NewGuid().ToString("N"));
            var data = Enumerable.Range(0, 200000).Select(i => (byte)(i % 251)).ToArray();
            File.WriteAllBytes(path, data);
            try
            {
                Assert.That(BugseeLongPathFile.ReadAllBytes(path), Is.EqualTo(data));
                File.WriteAllText(path, "text");
                Assert.That(BugseeLongPathFile.ReadAllText(path), Is.EqualTo("text"));
            }
            finally
            {
                File.Delete(path);
            }
        }

        static string Root()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "package.json")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            return dir;
        }
    }
}
