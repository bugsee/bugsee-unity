using System;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AndroidMappingNoticeTests
    {
        static readonly string[] Roots = { "/out/Builds/E2E" };

        static MappingFileInfo File(string path, long length) => new MappingFileInfo(path, length);

        static string Missing(bool minify, AndroidOutputFormat format, params MappingFileInfo[] found) =>
            AndroidMappingNotice.MissingMapping(minify, format, "Game_mapping.txt", Roots, found, "1.2.3", "45");

        [Test]
        public void Minify_follows_the_build_type()
        {
            Assert.That(AndroidMappingNotice.MinifyIsOn(false, minifyRelease: true, minifyDebug: false), Is.True);
            Assert.That(AndroidMappingNotice.MinifyIsOn(false, minifyRelease: false, minifyDebug: true), Is.False);
            Assert.That(AndroidMappingNotice.MinifyIsOn(true, minifyRelease: false, minifyDebug: true), Is.True);
            Assert.That(AndroidMappingNotice.MinifyIsOn(true, minifyRelease: true, minifyDebug: false), Is.False);
        }

        [Test]
        public void Minify_off_says_nothing_even_with_no_mapping()
        {
            Assert.That(Missing(false, AndroidOutputFormat.Apk), Is.Null);
            Assert.That(Missing(false, AndroidOutputFormat.Aab), Is.Null);
        }

        [Test]
        public void A_mapping_with_content_says_nothing_whatever_its_age()
        {
            // Age is not judged: Unity copies Gradle's file with its modification time, and an unchanged rebuild
            // leaves that time before the build started.
            Assert.That(Missing(true, AndroidOutputFormat.Apk, File("/out/Builds/E2E/Game_mapping.txt", 4096)), Is.Null);
            Assert.That(Missing(true, AndroidOutputFormat.Apk, File("/out/a/mapping.txt", 0), File("/out/b/mapping.txt", 4096)), Is.Null);
        }

        [Test]
        public void Only_a_file_with_content_is_usable()
        {
            Assert.That(AndroidMappingNotice.IsUsable(File("/m", 1)), Is.True);
            Assert.That(AndroidMappingNotice.IsUsable(File("/m", 0)), Is.False);
            Assert.That(AndroidMappingNotice.IsUsable(File("/m", -1)), Is.False);
        }

        [Test]
        public void No_mapping_with_minify_on_warns_with_the_paths_the_consequence_and_the_fix()
        {
            var message = Missing(true, AndroidOutputFormat.Aab);
            Assert.That(message, Does.Contain("App Bundle (AAB)"));
            Assert.That(message, Does.Contain("Game_mapping.txt"));
            Assert.That(message, Does.Contain("/out/Builds/E2E"));
            Assert.That(message, Does.Contain("lose their line numbers"));
            Assert.That(message, Does.Not.Contain("stay obfuscated"));
            Assert.That(message, Does.Not.Contain("build an APK"));
            Assert.That(message, Does.Contain("Library/Bee/Android/Prj"));
            Assert.That(message, Does.Contain("bugsee-cli debug-files upload"));
            Assert.That(message, Does.Contain("--type proguard --version 1.2.3 --build 45"));
            Assert.That(message, Does.Contain("BUGSEE_APP_TOKEN set in the environment"));
        }

        [Test]
        public void The_fix_depends_on_the_build_format()
        {
            Assert.That(Missing(true, AndroidOutputFormat.Apk), Does.Contain("APK").And.Contain("beside the APK"));
            var export = Missing(true, AndroidOutputFormat.ExportProject);
            Assert.That(export, Does.Contain("Export Project").And.Contain("Gradle writes the mapping"));
            Assert.That(export, Does.Contain("AGP 8.6").And.Contain("harmless"));
        }

        [Test]
        public void An_empty_mapping_warns_and_names_the_file()
        {
            var message = Missing(true, AndroidOutputFormat.Apk, File("/out/Builds/E2E/Game_mapping.txt", 0));
            Assert.That(message, Does.Contain("/out/Builds/E2E/Game_mapping.txt: empty"));
            Assert.That(message, Does.Contain("lose their line numbers"));
        }

        [Test]
        public void No_output_path_is_said_plainly()
        {
            var message = AndroidMappingNotice.MissingMapping(true, AndroidOutputFormat.Apk, "Game_mapping.txt", new string[0], null, "1", "1");
            Assert.That(message, Does.Contain("the build has no output path"));
        }

        [Test]
        public void A_mapping_that_is_not_uploaded_says_why_and_how_to_upload_it()
        {
            var message = AndroidMappingNotice.MappingNotUploaded("/out/Game_mapping.txt", "BUGSEE_APP_TOKEN is not set in the Unity Editor's environment", "1.2.3", "45");
            Assert.That(message, Does.Contain("/out/Game_mapping.txt was not uploaded (BUGSEE_APP_TOKEN is not set"));
            Assert.That(message, Does.Contain("lose their line numbers"));
            Assert.That(message, Does.Contain("bugsee-cli debug-files upload \"/out/Game_mapping.txt\" --type proguard --version 1.2.3 --build 45"));
            Assert.That(message, Does.Contain("BUGSEE_APP_TOKEN set in the environment"));
        }

        [Test]
        public void No_message_carries_a_token()
        {
            // The inputs never include the app token, so no message can; the upload command names no token flag.
            var all = Missing(true, AndroidOutputFormat.Aab) + Missing(true, AndroidOutputFormat.ExportProject)
                + AndroidMappingNotice.MappingNotUploaded("/m", "r", "1", "1");
            Assert.That(all, Does.Not.Contain("--token").And.Not.Contain("--app-token").And.Not.Contain("BUGSEE_APP_TOKEN="));
        }

        // BuildSummary.buildStartedAt: UTC ticks, Kind Unspecified.
        [TestCase(-8)]
        [TestCase(0)]
        [TestCase(5)]
        [TestCase(13)]
        public void The_build_start_is_read_as_utc_at_every_machine_offset(int hours)
        {
            var ticks = new DateTime(2026, 10, 19, 19, 13, 37, DateTimeKind.Utc).Ticks;
            var reported = new DateTime(ticks);
            Assert.That(reported.Kind, Is.EqualTo(DateTimeKind.Unspecified));

            var started = BuildClock.StartedUtc(reported);
            Assert.That(started.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(started.Ticks, Is.EqualTo(ticks));

            // What ToUniversalTime() does to the same value on a machine at that offset: the shift this avoids.
            var zone = TimeZoneInfo.CreateCustomTimeZone("t" + hours, TimeSpan.FromHours(hours), "t", "t");
            var shifted = TimeZoneInfo.ConvertTimeToUtc(reported, zone);
            Assert.That(shifted.Ticks - ticks, Is.EqualTo(-TimeSpan.FromHours(hours).Ticks));
            Assert.That(started, Is.EqualTo(new DateTime(2026, 10, 19, 19, 13, 37, DateTimeKind.Utc)));
        }
    }
}
