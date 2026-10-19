using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AndroidMappingNoticeTests
    {
        static readonly DateTime Start = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        static readonly string[] Roots = { "/out/Builds/E2E" };

        static MappingFileInfo File(string path, long length, int minutesAfterStart) =>
            new MappingFileInfo(path, length, Start.AddMinutes(minutesAfterStart));

        static string Missing(bool minify, AndroidOutputFormat format, params MappingFileInfo[] found) =>
            AndroidMappingNotice.MissingMapping(minify, format, "Game_mapping.txt", Roots, found, Start, "1.2.3", "45");

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
        public void A_usable_mapping_says_nothing()
        {
            Assert.That(Missing(true, AndroidOutputFormat.Apk, File("/out/Builds/E2E/Game_mapping.txt", 4096, 1)), Is.Null);
            // A stale or empty one beside a good one does not matter either.
            Assert.That(Missing(true, AndroidOutputFormat.Apk, File("/out/a/mapping.txt", 0, 1), File("/out/b/mapping.txt", 4096, 0)), Is.Null);
        }

        [Test]
        public void No_mapping_with_minify_on_warns_with_the_paths_the_consequence_and_the_fix()
        {
            var message = Missing(true, AndroidOutputFormat.Aab);
            Assert.That(message, Does.Contain("App Bundle (AAB)"));
            Assert.That(message, Does.Contain("Game_mapping.txt"));
            Assert.That(message, Does.Contain("/out/Builds/E2E"));
            Assert.That(message, Does.Contain("stay obfuscated"));
            Assert.That(message, Does.Contain("Build App Bundle"));
            Assert.That(message, Does.Contain("bugsee-cli debug-files upload"));
            Assert.That(message, Does.Contain("--type proguard --version 1.2.3 --build 45"));
        }

        [Test]
        public void The_fix_depends_on_the_build_format()
        {
            Assert.That(Missing(true, AndroidOutputFormat.Apk), Does.Contain("APK").And.Contain("beside the APK"));
            Assert.That(Missing(true, AndroidOutputFormat.ExportProject), Does.Contain("Export Project").And.Contain("Gradle writes the mapping"));
        }

        [Test]
        public void An_empty_mapping_warns_and_names_the_file()
        {
            var message = Missing(true, AndroidOutputFormat.Apk, File("/out/Builds/E2E/Game_mapping.txt", 0, 1));
            Assert.That(message, Does.Contain("/out/Builds/E2E/Game_mapping.txt: empty"));
            Assert.That(message, Does.Contain("stay obfuscated"));
        }

        [Test]
        public void A_mapping_older_than_the_build_warns_with_both_times()
        {
            var message = Missing(true, AndroidOutputFormat.Apk, File("/out/Builds/E2E/Game_mapping.txt", 4096, -30));
            Assert.That(message, Does.Contain("not this build's own"));
            Assert.That(message, Does.Contain("/out/Builds/E2E/Game_mapping.txt"));
            Assert.That(message, Does.Contain("2026-10-20 11:30:00Z"));
            Assert.That(message, Does.Contain("2026-10-20 12:00:00Z"));
        }

        [Test]
        public void A_mapping_written_at_the_instant_the_build_started_counts()
        {
            Assert.That(AndroidMappingNotice.IsUsable(File("/m", 1, 0), Start), Is.True);
            Assert.That(AndroidMappingNotice.IsUsable(File("/m", 1, -1), Start), Is.False);
            Assert.That(AndroidMappingNotice.IsUsable(File("/m", 0, 5), Start), Is.False);
        }

        [Test]
        public void No_output_path_is_said_plainly()
        {
            var message = AndroidMappingNotice.MissingMapping(true, AndroidOutputFormat.Apk, "Game_mapping.txt", new string[0], null, Start, "1", "1");
            Assert.That(message, Does.Contain("the build has no output path"));
        }

        [Test]
        public void A_mapping_that_is_not_uploaded_says_why_and_how_to_upload_it()
        {
            var message = AndroidMappingNotice.MappingNotUploaded("/out/Game_mapping.txt", "BUGSEE_APP_TOKEN is not set in the Unity Editor's environment", "1.2.3", "45");
            Assert.That(message, Does.Contain("/out/Game_mapping.txt was not uploaded (BUGSEE_APP_TOKEN is not set"));
            Assert.That(message, Does.Contain("stay obfuscated"));
            Assert.That(message, Does.Contain("bugsee-cli debug-files upload \"/out/Game_mapping.txt\" --type proguard --version 1.2.3 --build 45"));
        }

        [Test]
        public void No_message_carries_a_token()
        {
            // The inputs never include the app token, so no message can; the upload command names no token flag.
            var all = Missing(true, AndroidOutputFormat.Aab) + Missing(true, AndroidOutputFormat.ExportProject)
                + AndroidMappingNotice.MappingNotUploaded("/m", "r", "1", "1");
            Assert.That(all, Does.Not.Contain("--token").And.Not.Contain("BUGSEE_APP_TOKEN="));
        }
    }
}
