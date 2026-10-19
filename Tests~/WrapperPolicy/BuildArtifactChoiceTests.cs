using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class BuildArtifactChoiceTests
    {
        const string Ver = "0.1.0";
        const int Code = 2;

        static int Zip(string path, string stem, bool generic = false, string version = Ver, int code = Code, string backend = "IL2CPP") =>
            BuildArtifactChoice.SymbolsZipRank(path, stem, generic, version, code, backend);

        [Test]
        public void The_mapping_unity_names_after_the_player_is_this_builds()
        {
            Assert.That(BuildArtifactChoice.MappingRank("/o/Game_mapping.txt", "Game", false), Is.EqualTo(BuildArtifactChoice.Named));
            Assert.That(BuildArtifactChoice.MappingRank("/o/game_mapping.txt", "Game", false), Is.EqualTo(BuildArtifactChoice.Named));
        }

        // android-il2cpp-release and android-il2cpp-release-min are both real output names in the sample matrix.
        [Test]
        public void A_sibling_build_whose_name_starts_with_this_one_is_not_ours()
        {
            const string stem = "android-il2cpp-release";
            Assert.That(BuildArtifactChoice.MappingRank("/o/android-il2cpp-release-min_mapping.txt", stem, false), Is.EqualTo(BuildArtifactChoice.Reject));
            Assert.That(BuildArtifactChoice.MappingRank("/o/Game2_mapping.txt", "Game", false), Is.EqualTo(BuildArtifactChoice.Reject));
            Assert.That(Zip("/o/android-il2cpp-release-min-0.1.0-v2-IL2CPP.symbols.zip", stem), Is.EqualTo(BuildArtifactChoice.Reject));
            Assert.That(Zip("/o/Game2-0.1.0-v2-IL2CPP.symbols.zip", "Game"), Is.EqualTo(BuildArtifactChoice.Reject));
        }

        [Test]
        public void The_symbols_zip_unity_names_after_the_player_is_this_builds()
        {
            Assert.That(Zip("/o/Game-0.1.0-v2-IL2CPP.symbols.zip", "Game"), Is.EqualTo(BuildArtifactChoice.Named));
            Assert.That(Zip("/o/android-il2cpp-release-min-0.1.0-v2-IL2CPP.symbols.zip", "android-il2cpp-release-min"), Is.EqualTo(BuildArtifactChoice.Named));
            Assert.That(Zip("/o/Game-0.1.0-v2-Mono2x.symbols.zip", "Game", backend: "Mono2x"), Is.EqualTo(BuildArtifactChoice.Named));
        }

        // A reused output folder: Mono and IL2CPP builds of the same player, version and code leave two zips that
        // differ only in the backend. Modification time must never get to choose between them.
        [Test]
        public void The_other_backends_zip_of_the_same_player_version_and_code_is_not_this_builds()
        {
            Assert.That(Zip("/o/Game-0.1.0-v2-Mono2x.symbols.zip", "Game", backend: "IL2CPP"), Is.EqualTo(BuildArtifactChoice.Reject));
            Assert.That(Zip("/o/Game-0.1.0-v2-IL2CPP.symbols.zip", "Game", backend: "Mono2x"), Is.EqualTo(BuildArtifactChoice.Reject));
            Assert.That(Zip("/o/Game-0.1.0-v2-IL2CPP.symbols.zip", "Game", backend: null), Is.EqualTo(BuildArtifactChoice.Reject));
            Assert.That(Zip("/o/Game-0.1.0-v2-IL2CPP-extra.symbols.zip", "Game"), Is.EqualTo(BuildArtifactChoice.Reject));
        }

        // The names Unity 2021.3.45 wrote for the sample's E2E matrix (Builds/E2E): the backend is always there.
        [TestCase("android-il2cpp-dev-0.1.0-v2-IL2CPP.symbols.zip", "android-il2cpp-dev", "IL2CPP")]
        [TestCase("android-il2cpp-release-min-0.1.0-v2-IL2CPP.symbols.zip", "android-il2cpp-release-min", "IL2CPP")]
        [TestCase("android-mono-dev-0.1.0-v2-Mono2x.symbols.zip", "android-mono-dev", "Mono2x")]
        [TestCase("android-mono-release-min-0.1.0-v2-Mono2x.symbols.zip", "android-mono-release-min", "Mono2x")]
        public void The_names_unity_2021_3_wrote_are_matched(string file, string stem, string backend)
        {
            Assert.That(BuildArtifactChoice.SymbolsZipName(stem, Ver, Code, backend), Is.EqualTo(file));
            Assert.That(Zip("/o/" + file, stem, backend: backend), Is.EqualTo(BuildArtifactChoice.Named));
        }

        // The version is part of the name and need not start with a digit: Unity writes whatever bundleVersion is.
        [TestCase("v1.0")]
        [TestCase("beta")]
        [TestCase("1.0.0-rc1")]
        [TestCase("2024.10")]
        public void A_version_that_does_not_start_with_a_digit_still_matches(string version)
        {
            Assert.That(Zip("/o/Game-" + version + "-v7-IL2CPP.symbols.zip", "Game", false, version, 7), Is.EqualTo(BuildArtifactChoice.Named));
        }

        // 'Game' and 'Game-2024' share an output folder; the exact version and code tell their zips apart.
        [Test]
        public void A_player_whose_name_continues_with_a_dash_and_digits_is_not_confused_with_this_one()
        {
            Assert.That(Zip("/o/Game-2024-1.0-v1-IL2CPP.symbols.zip", "Game"), Is.EqualTo(BuildArtifactChoice.Reject));
            Assert.That(Zip("/o/Game-2-0.1.0-v2-IL2CPP.symbols.zip", "Game"), Is.EqualTo(BuildArtifactChoice.Reject));
            Assert.That(Zip("/o/Game-2024-1.0-v1-IL2CPP.symbols.zip", "Game-2024", false, "1.0", 1), Is.EqualTo(BuildArtifactChoice.Named));
        }

        [Test]
        public void A_zip_of_another_version_or_code_of_the_same_player_is_not_this_builds()
        {
            Assert.That(Zip("/o/Game-0.0.9-v2-IL2CPP.symbols.zip", "Game"), Is.EqualTo(BuildArtifactChoice.Reject));
            Assert.That(Zip("/o/Game-0.1.0-v1-IL2CPP.symbols.zip", "Game"), Is.EqualTo(BuildArtifactChoice.Reject));
        }

        [Test]
        public void Another_builds_file_is_never_chosen_when_the_player_is_known()
        {
            Assert.That(BuildArtifactChoice.MappingRank("/o/Other_mapping.txt", "Game", false), Is.EqualTo(BuildArtifactChoice.Reject));
            Assert.That(BuildArtifactChoice.MappingRank("/o/mapping.txt", "Game", false), Is.EqualTo(BuildArtifactChoice.Reject));
            Assert.That(Zip("/o/native-debug-symbols.zip", "Game"), Is.EqualTo(BuildArtifactChoice.Reject));
        }

        [Test]
        public void Gradle_names_are_accepted_only_for_an_exported_project_and_rank_below_a_named_file()
        {
            Assert.That(BuildArtifactChoice.MappingRank("/p/outputs/mapping/release/mapping.txt", "proj", true), Is.EqualTo(BuildArtifactChoice.Generic));
            Assert.That(Zip("/p/native-debug-symbols.zip", "proj", true), Is.EqualTo(BuildArtifactChoice.Generic));
            Assert.That(BuildArtifactChoice.MappingRank("/o/proj_mapping.txt", "proj", true), Is.GreaterThan(BuildArtifactChoice.MappingRank("/o/mapping.txt", "proj", true)));
        }

        [Test]
        public void Not_a_zip_is_rejected()
        {
            Assert.That(Zip("/o/Game-0.1.0-v2-IL2CPP.symbols.tar", "Game", true), Is.EqualTo(BuildArtifactChoice.Reject));
        }

        // An incremental build copies cached outputs with their old timestamps, so another build's file can
        // be newer than this build's own. It must still lose.
        [Test]
        public void An_older_file_of_this_build_beats_a_newer_file_of_another_build()
        {
            var own = new DateTime(2026, 10, 14, 14, 11, 43);
            var other = new DateTime(2026, 10, 14, 14, 14, 46);
            Assert.That(BuildArtifactChoice.Better(BuildArtifactChoice.Named, own, BuildArtifactChoice.Generic, other), Is.True);
            Assert.That(BuildArtifactChoice.Better(BuildArtifactChoice.Generic, other, BuildArtifactChoice.Named, own), Is.False);
        }

        [Test]
        public void Time_decides_between_equally_ranked_files()
        {
            var older = new DateTime(2026, 1, 1);
            var newer = new DateTime(2026, 1, 2);
            Assert.That(BuildArtifactChoice.Better(BuildArtifactChoice.Named, newer, BuildArtifactChoice.Named, older), Is.True);
            Assert.That(BuildArtifactChoice.Better(BuildArtifactChoice.Named, older, BuildArtifactChoice.Named, newer), Is.False);
        }

        [Test]
        public void The_il2cpp_folder_unity_creates_is_named_after_the_player()
        {
            Assert.That(BuildArtifactChoice.Il2CppBackupFolderName("Game"), Is.EqualTo("Game_BackUpThisFolder_ButDontShipItWithYourGame"));
        }

        [TestCase("libil2cpp.so", true)]
        [TestCase("libil2cpp.sym.so", true)]
        [TestCase("libil2cpp.dbg.so", true)]
        [TestCase("libil2cpp.so.debug", true)]
        [TestCase("libil2cpp.so.sym", true)]
        [TestCase("libil2cpp.so.json", false)]
        [TestCase("libil2cpp.sometext", false)]
        [TestCase("libunity.so", false)]
        [TestCase("libil2cppx.so", false)]
        [TestCase("", false)]
        public void Il2cpp_library_names(string name, bool expected)
        {
            Assert.That(BuildArtifactChoice.IsIl2CppLibraryName(name), Is.EqualTo(expected));
        }

        [Test]
        public void Only_data_that_starts_with_the_elf_magic_is_parsed_as_elf()
        {
            Assert.That(BuildArtifactChoice.LooksLikeElf(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2 }), Is.True);
            Assert.That(BuildArtifactChoice.LooksLikeElf(new byte[] { 0x7b, (byte)'}', 0, 0 }), Is.False);
            Assert.That(BuildArtifactChoice.LooksLikeElf(new byte[] { 0x7f, (byte)'E' }), Is.False);
            Assert.That(BuildArtifactChoice.LooksLikeElf(null), Is.False);
        }

        // Unity wrote Game-1.0-v5-IL2CPP.symbols.zip; a postprocess incrementor then set the live code to 6.
        [Test]
        public void The_settings_recorded_for_this_output_name_the_zip_not_the_live_ones()
        {
            var recorded = new PlayerNameSettings("/out/Game.apk", "1.0", 5, "IL2CPP");
            var live = new PlayerNameSettings("/out/Game.apk", "1.0", 6, "IL2CPP");
            var used = BuildArtifactChoice.BuildTimeSettings(recorded, "/out/Game.apk", live);
            Assert.That(used, Is.SameAs(recorded));
            Assert.That(Zip("/out/Game-1.0-v5-IL2CPP.symbols.zip", "Game", version: used.BundleVersion, code: used.VersionCode, backend: used.Backend),
                Is.EqualTo(BuildArtifactChoice.Named));
            Assert.That(BuildArtifactChoice.BuildTimeSettings(recorded, "/out/Game.apk/", live), Is.SameAs(recorded));
        }

        [Test]
        public void Live_settings_are_used_when_nothing_was_recorded_for_this_output()
        {
            var live = new PlayerNameSettings("/out/Game.apk", "1.0", 6, "IL2CPP");
            Assert.That(BuildArtifactChoice.BuildTimeSettings(null, "/out/Game.apk", live), Is.SameAs(live));
            var otherBuild = new PlayerNameSettings("/out/Other.apk", "1.0", 5, "Mono2x");
            Assert.That(BuildArtifactChoice.BuildTimeSettings(otherBuild, "/out/Game.apk", live), Is.SameAs(live));
            var noPath = new PlayerNameSettings(null, "1.0", 5, "IL2CPP");
            Assert.That(BuildArtifactChoice.BuildTimeSettings(noPath, null, live), Is.SameAs(live));
        }

        [Test]
        public void An_exported_project_is_its_own_il2cpp_root_and_an_apk_uses_its_backup_folder_never_the_parent()
        {
            var backup = Path.Combine("/b", "Game_BackUpThisFolder_ButDontShipItWithYourGame");
            Assert.That(BuildArtifactChoice.Il2CppOutputRoot("/b/iOS", true), Is.EqualTo("/b/iOS"));
            Assert.That(BuildArtifactChoice.Il2CppOutputRoot("/b/Game.apk", false), Is.EqualTo(backup));
            Assert.That(BuildArtifactChoice.Il2CppOutputRoot("/b/Game.aab", false), Is.EqualTo(backup));
            Assert.That(BuildArtifactChoice.Il2CppOutputRoot(null, false), Is.Null);
            Assert.That(BuildArtifactChoice.Il2CppOutputRoot("", true), Is.Null);
        }

        // Builds/Android is a Gradle export with its libil2cpp.so under jniLibs, Builds/iOS the Xcode export, and a
        // Builds/Game.apk sits beside them. Each build's root must reach only its own native library and line map.
        [Test]
        public void A_sibling_builds_libil2cpp_and_line_map_are_outside_this_builds_root()
        {
            var builds = Path.Combine(Path.GetTempPath(), "bugsee-il2cpp-roots-" + Guid.NewGuid().ToString("N"));
            try
            {
                var androidSo = Path.Combine(builds, "Android", "unityLibrary", "src", "main", "jniLibs", "arm64-v8a", "libil2cpp.so");
                var androidMap = Path.Combine(builds, "Android", "unityLibrary", "src", "main", "Il2CppOutputProject", "LineNumberMappings.json");
                var iosMap = Path.Combine(builds, "iOS", "Il2CppOutputProject", "LineNumberMappings.json");
                var apkMap = Path.Combine(builds, "Game_BackUpThisFolder_ButDontShipItWithYourGame", "il2cppOutput", "LineNumberMappings.json");
                foreach (var f in new[] { androidSo, androidMap, iosMap, apkMap })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(f));
                    File.WriteAllText(f, "x");
                }
                File.WriteAllText(Path.Combine(builds, "Game.apk"), "x");

                var ios = BuildArtifactChoice.Il2CppOutputRoot(Path.Combine(builds, "iOS"), true);
                Assert.That(Under(ios, "libil2cpp.so"), Is.Empty);
                Assert.That(Under(ios, "LineNumberMappings.json"), Is.EqualTo(new[] { iosMap }));

                var apk = BuildArtifactChoice.Il2CppOutputRoot(Path.Combine(builds, "Game.apk"), false);
                Assert.That(Under(apk, "libil2cpp.so"), Is.Empty);
                Assert.That(Under(apk, "LineNumberMappings.json"), Is.EqualTo(new[] { apkMap }));

                var android = BuildArtifactChoice.Il2CppOutputRoot(Path.Combine(builds, "Android"), true);
                Assert.That(Under(android, "libil2cpp.so"), Is.EqualTo(new[] { androidSo }));
                Assert.That(Under(android, "LineNumberMappings.json"), Is.EqualTo(new[] { androidMap }));
            }
            finally
            {
                if (Directory.Exists(builds)) Directory.Delete(builds, true);
            }
        }

        [Test]
        public void Unix_seconds_are_whole_seconds_since_the_epoch_in_utc()
        {
            Assert.That(BuildArtifactChoice.UnixSeconds(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)), Is.EqualTo(0));
            Assert.That(BuildArtifactChoice.UnixSeconds(new DateTime(2027, 1, 15, 8, 0, 0, 999, DateTimeKind.Utc)), Is.EqualTo(1_800_000_000));
        }

        // BuildSummary.buildStartedAt is UTC with Kind Unspecified (a real export on a UTC+5 machine stamped the script
        // five hours early when it was converted as local time).
        [Test]
        public void A_time_of_unspecified_kind_is_read_as_utc_whatever_the_machines_offset()
        {
            var unspecified = new DateTime(2027, 1, 15, 8, 0, 0, DateTimeKind.Unspecified);
            Assert.That(BuildArtifactChoice.UnixSeconds(unspecified), Is.EqualTo(1_800_000_000));
            Assert.That(BuildArtifactChoice.UnixSeconds(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc)), Is.EqualTo(1_800_000_000));
            var local = DateTime.SpecifyKind(unspecified, DateTimeKind.Local);
            Assert.That(BuildArtifactChoice.UnixSeconds(local), Is.EqualTo(new DateTimeOffset(local).ToUnixTimeSeconds()));
        }

        // Compared to the second, as the Run Script compares them: a map written in the second the export began counts.
        [Test]
        public void A_file_is_this_exports_only_if_written_at_or_after_its_start()
        {
            var start = new DateTime(2027, 1, 15, 8, 0, 0, 700, DateTimeKind.Utc);
            Assert.That(BuildArtifactChoice.WrittenByExport(start.AddHours(-1), start), Is.False);
            Assert.That(BuildArtifactChoice.WrittenByExport(start.AddSeconds(-1), start), Is.False);
            Assert.That(BuildArtifactChoice.WrittenByExport(new DateTime(2027, 1, 15, 8, 0, 0, 100, DateTimeKind.Utc), start), Is.True);
            Assert.That(BuildArtifactChoice.WrittenByExport(start.AddMinutes(3), start), Is.True);
        }

        static string[] Under(string root, string name) =>
            root != null && Directory.Exists(root) ? Directory.GetFiles(root, name, SearchOption.AllDirectories) : new string[0];

        // The module-id lookup and the line-map lookup must search the same single folder, or one build's line map
        // is uploaded under another build's module id.
        [Test]
        public void Module_id_and_line_map_lookups_use_the_same_il2cpp_root_and_never_the_parent()
        {
            var identity = EditorMethod("Editor/BugseeIl2CppModuleIdentity.cs", "static IEnumerable<string> BuildRoots(BuildReport report)");
            var linemap = EditorMethod("Editor/BugseeIl2CppLinemapUpload.cs", "static string FindLineNumberMappings(BuildReport report)");
            foreach (var body in new[] { identity, linemap })
            {
                Assert.That(body, Does.Contain("BuildArtifactChoice.Il2CppOutputRoot("));
                Assert.That(body, Does.Not.Contain("GetDirectoryName"));
            }
        }

        // The method's code with // comments removed, so prose cannot satisfy or break the assertions.
        static string EditorMethod(string relative, string signature)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "package.json")))
                dir = Directory.GetParent(dir)?.FullName;
            Assert.That(dir, Is.Not.Null.And.Not.Empty, "Could not locate repo root (package.json).");
            var source = File.ReadAllText(Path.Combine(dir, relative));
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "Missing method: " + signature);
            var depth = 0;
            for (var i = source.IndexOf('{', start); i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return Regex.Replace(source.Substring(start, i - start + 1), @"//[^\n]*", "");
            }
            Assert.Fail("Unbalanced braces in " + relative);
            return null;
        }
    }
}
