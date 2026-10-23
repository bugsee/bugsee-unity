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
        public void A_read_failure_on_a_path_at_the_limit_names_the_length()
        {
            var path = Long + new string('x', 20) + ".apk";
            var text = LongPathPolicy.Describe(path, "Could not find a part of the path");

            Assert.That(path.Length, Is.GreaterThanOrEqualTo(260));
            Assert.That(text, Does.StartWith("Could not find a part of the path").And.Contain(path.Length + " characters"));
            Assert.That(LongPathPolicy.Describe(Long, "boom"), Is.EqualTo("boom"), "under the limit: nothing to add");
            Assert.That(LongPathPolicy.Describe(@"C:\a.apk", "boom"), Is.EqualTo("boom"));
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

                Assert.Throws<FileNotFoundException>(() => BugseeLongPathFile.OpenRead(path + ".missing").Dispose());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void A_file_under_a_path_over_260_characters_can_be_read()
        {
            Assume.That(Path.DirectorySeparatorChar, Is.EqualTo('\\'), "Windows only");
            var dir = @"\\?\" + Path.Combine(Path.GetTempPath(), "bugsee-lp-" + Guid.NewGuid().ToString("N"),
                new string('a', 120), new string('b', 120));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "game.apk");
            File.WriteAllBytes(file, new byte[] { 9, 8, 7 });
            try
            {
                var plain = file.Substring(4);
                Assert.That(plain.Length, Is.GreaterThan(260));
                Assert.That(BugseeLongPathFile.ReadAllBytes(plain), Is.EqualTo(new byte[] { 9, 8, 7 }));
            }
            finally
            {
                Directory.Delete(dir.Substring(0, dir.IndexOf(new string('a', 120), StringComparison.Ordinal)), true);
            }
        }

        [Test]
        public void The_editor_reads_of_build_outputs_all_go_through_the_long_path_route()
        {
            foreach (var name in new[] { "BugseeBuildIdentity.cs", "BugseeAndroidNativeLibUpload.cs", "BugseeIl2CppModuleIdentity.cs" })
            {
                var text = File.ReadAllText(Path.Combine(Root(), "Editor", name));
                Assert.That(text, Does.Contain("BugseeLongPathFile."), name);
                Assert.That(System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![A-Za-z])(File\.OpenRead|File\.ReadAllBytes|ZipFile\.OpenRead)\(|new FileStream\("),
                    Is.False, name);
            }
        }

        [Test]
        public void An_unreadable_apk_skips_the_mapping_instead_of_uploading_it_under_a_derived_id()
        {
            var source = File.ReadAllText(Path.Combine(Root(), "Editor", "BugseeSymbolUpload.cs"));

            Assert.That(source, Does.Contain("identity.SkipMapping"));
            var skip = source.Substring(source.IndexOf("identity.SkipMapping", StringComparison.Ordinal));
            Assert.That(skip.Substring(0, skip.IndexOf("else", StringComparison.Ordinal)), Does.Not.Contain("UploadIfFound("));
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
