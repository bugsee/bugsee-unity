using System.Linq;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AndroidUploadIdentityTests
    {
        const string App = "26958224-3a30-3621-9ce7-564c211abee3";
        const string Synth = "0f5c0792-affb-3962-af95-d6e66c5828e2";

        [Test]
        public void The_id_the_app_carries_wins_over_the_synthesized_id()
        {
            var id = AndroidUploadIdentity.Decide(null, App, Synth);

            Assert.That(id.Uuid, Is.EqualTo(App));
            Assert.That(id.Source, Is.EqualTo(BuildIdSource.AppAsset));
            Assert.That(id.Warning, Is.Null);
        }

        [Test]
        public void With_no_asset_the_sdks_own_id_is_used_and_the_warning_says_why()
        {
            // The block degraded (older AGP, a script replacing assets): the app reports the synthesized id, and the
            // mapping must not be uploaded under a content id the app never reports.
            var id = AndroidUploadIdentity.Decide(null, null, Synth);

            Assert.That(id.Uuid, Is.EqualTo(Synth));
            Assert.That(id.Source, Is.EqualTo(BuildIdSource.Synthesized));
            Assert.That(id.Warning, Does.Contain("no bugsee_build_id.properties").And.Contain("will not match"));
        }

        [Test]
        public void The_apps_id_keys_the_mapping_and_the_environment_value_keys_only_the_native_upload()
        {
            // A CI that sets a fresh GUID per build must not move the mapping off the id the app reports.
            var id = AndroidUploadIdentity.Decide("11111111-1111-1111-1111-111111111111", App, Synth);

            Assert.That(id.Uuid, Is.EqualTo(App));
            Assert.That(id.Source, Is.EqualTo(BuildIdSource.AppAsset));
            Assert.That(id.ElfUuid, Is.EqualTo("11111111-1111-1111-1111-111111111111"));
            Assert.That(id.Warning, Does.Contain("native upload only"));
        }

        [Test]
        public void The_native_upload_follows_the_mapping_when_the_environment_is_unset_or_agrees()
        {
            Assert.That(AndroidUploadIdentity.Decide(null, App, Synth).ElfUuid, Is.EqualTo(App));
            var agrees = AndroidUploadIdentity.Decide(App.ToUpperInvariant(), App, Synth);
            Assert.That(agrees.Warning, Is.Null);
            Assert.That(agrees.Uuid, Is.EqualTo(App));
        }

        [Test]
        public void Without_an_asset_a_valid_environment_uuid_is_the_mapping_id_and_any_value_keys_the_native_upload()
        {
            var uuid = AndroidUploadIdentity.Decide("11111111-1111-1111-1111-111111111111", null, Synth);
            var free = AndroidUploadIdentity.Decide("my-build-7", null, Synth);

            Assert.That(uuid.Uuid, Is.EqualTo("11111111-1111-1111-1111-111111111111"));
            Assert.That(uuid.Source, Is.EqualTo(BuildIdSource.Environment));
            Assert.That(free.Uuid, Is.EqualTo(Synth), "a non-UUID cannot be the mapping's id");
            Assert.That(free.ElfUuid, Is.EqualTo("my-build-7"), "it still keys the native upload, as before");
            Assert.That(free.Warning, Does.Contain("not a UUID"));
        }

        [TestCase("Game.arm64-v8a.apk", "Game", true)]
        [TestCase("game.ARMEABI-V7A.APK", "Game", true)]
        [TestCase("GameLite.apk", "Game", false)]
        [TestCase("Game.apk", "Game", false)]
        [TestCase("Game..apk", "Game", false)]
        [TestCase("Game.arm64-v8a.aab", "Game", false)]
        [TestCase("Game.arm64-v8a.apk", "", false)]
        public void Only_a_players_own_split_apks_are_siblings(string file, string stem, bool expected)
        {
            Assert.That(AndroidUploadIdentity.IsSplitApkOf(file, stem), Is.EqualTo(expected));
        }

        [Test]
        public void Malformed_ids_are_ignored_and_nothing_known_yields_none()
        {
            Assert.That(AndroidUploadIdentity.Decide(null, "not-a-uuid", Synth).Source, Is.EqualTo(BuildIdSource.Synthesized));
            var none = AndroidUploadIdentity.Decide(null, null, null);
            Assert.That(none.Source, Is.EqualTo(BuildIdSource.None));
            Assert.That(none.Uuid, Is.Null);
        }

        [Test]
        public void The_asset_file_is_parsed_like_the_sdk_reads_it()
        {
            string text = "# Bugsee build identifier - auto-generated, do not edit.\nbugsee.build_id=" + App.ToUpperInvariant() + "\r\n";

            Assert.That(AndroidUploadIdentity.ParseAssetProperties(text), Is.EqualTo(App));
            Assert.That(AndroidUploadIdentity.ParseAssetProperties("bugsee.build_id=null\n"), Is.Null);
            Assert.That(AndroidUploadIdentity.ParseAssetProperties("# bugsee.build_id=" + App), Is.Null);
            Assert.That(AndroidUploadIdentity.ParseAssetProperties("bugsee.build_id_other=" + App), Is.Null);
            Assert.That(AndroidUploadIdentity.ParseAssetProperties(null), Is.Null);
        }

        [Test]
        public void The_mapping_arguments_always_carry_the_id_and_the_icon_when_there_is_one()
        {
            Assert.That(AndroidUploadIdentity.MappingArguments(App, "/p/app_icon.png"),
                Is.EqualTo(new[] { "--uuid", App, "--icon", "/p/app_icon.png" }));
            Assert.That(AndroidUploadIdentity.MappingArguments(App, null), Is.EqualTo(new[] { "--uuid", App }));
            Assert.That(AndroidUploadIdentity.MappingArguments(null, null), Is.Empty);
        }

        [Test]
        public void The_icon_is_announced_only_for_arguments_that_carry_one()
        {
            Assert.That(AndroidUploadIdentity.IconNotice(AndroidUploadIdentity.MappingArguments(App, "/p/app_icon.png")),
                Is.EqualTo("Bugsee: attaching launcher icon /p/app_icon.png to the mapping upload."));
            Assert.That(AndroidUploadIdentity.IconNotice(AndroidUploadIdentity.MappingArguments(App, null)), Is.Null);
            Assert.That(AndroidUploadIdentity.IconNotice(new[] { "--icon" }), Is.Null);
            Assert.That(AndroidUploadIdentity.IconNotice(null), Is.Null);
        }

        [Test]
        public void The_apk_version_is_read_from_the_generated_launcher()
        {
            string gradle = "android {\n    defaultConfig {\n        versionCode 2\n        versionName '0.1.0'\n    }\n}\n";

            GradleTemplateDefaults.ReadGradleVersion(gradle, out var name, out var code);

            Assert.That(name, Is.EqualTo("0.1.0"));
            Assert.That(code, Is.EqualTo("2"));
        }

        [Test]
        public void A_version_the_launcher_does_not_state_as_a_literal_stays_unknown()
        {
            GradleTemplateDefaults.ReadGradleVersion("versionCode project.ext.code\nversionName rootProject.name\n", out var name, out var code);

            Assert.That(name, Is.Null);
            Assert.That(code, Is.Null);
            GradleTemplateDefaults.ReadGradleVersion(null, out name, out code);
            Assert.That(code, Is.Null);
        }

        [Test]
        public void Split_apks_that_all_carry_the_same_id_give_that_id()
        {
            var id = AndroidUploadIdentity.SelectAppAssetId(new[] { App, App.ToUpperInvariant() }, out var conflict);

            Assert.That(id, Is.EqualTo(App));
            Assert.That(conflict, Is.Null);
        }

        [Test]
        public void Split_apks_with_different_ids_give_none_and_a_conflict()
        {
            var id = AndroidUploadIdentity.SelectAppAssetId(new[] { App, "11111111-1111-1111-1111-111111111111" }, out var conflict);

            Assert.That(id, Is.Null);
            Assert.That(conflict, Does.Contain("different ids"));
            Assert.That(AndroidUploadIdentity.Decide(null, id, Synth, conflict).Warning, Does.Contain("different ids"));
        }

        [Test]
        public void Split_apks_where_only_some_carry_an_id_give_none_and_a_conflict()
        {
            var id = AndroidUploadIdentity.SelectAppAssetId(new[] { App, null }, out var conflict);

            Assert.That(id, Is.Null, "the APKs without an asset report the synthesized id, so the first one's id is no better");
            Assert.That(conflict, Does.Contain("some have none"));
        }

        [Test]
        public void No_apks_or_none_with_an_id_is_not_a_conflict()
        {
            Assert.That(AndroidUploadIdentity.SelectAppAssetId(new string[0], out var c1), Is.Null);
            Assert.That(c1, Is.Null);
            Assert.That(AndroidUploadIdentity.SelectAppAssetId(new string[] { null, "" }, out var c2), Is.Null);
            Assert.That(c2, Is.Null);
        }

        [Test]
        public void Every_elf_upload_carries_the_uuid_the_cli_requires()
        {
            foreach (var path in new[] { "/o/symbols.zip", "/o/Il2CppOutputProject/libs" })
            {
                var args = AndroidUploadIdentity.ElfArguments(path, "0.1.0", "2", App);

                Assert.That(args, Is.EqualTo(new[]
                {
                    "debug-files", "upload", path, "--type", "elf", "--version", "0.1.0", "--build", "2", "--uuid", App,
                }));
            }
        }

        [Test]
        public void A_launcher_that_overrides_the_version_code_per_output_does_not_state_one()
        {
            string gradle = "defaultConfig {\n versionCode 2\n versionName '0.1.0'\n}\nandroid.applicationVariants.all { v -> v.outputs.each { it.versionCodeOverride = 3 } }\n";

            GradleTemplateDefaults.ReadGradleVersion(gradle, out var name, out var code);

            Assert.That(name, Is.EqualTo("0.1.0"));
            Assert.That(code, Is.Null, "the literal is not what each APK carries");
        }

        [Test]
        public void An_app_id_that_could_not_be_read_skips_the_mapping_instead_of_falling_back_to_the_derived_id()
        {
            var id = AndroidUploadIdentity.Decide(null, null, Synth, null, "game.apk: Could not find a part of the path");

            Assert.That(id.SkipMapping, Is.True);
            Assert.That(id.Uuid, Is.Null);
            Assert.That(id.Source, Is.EqualTo(BuildIdSource.Unreadable));
            Assert.That(id.Warning, Does.Contain("game.apk: Could not find a part of the path").And.Contain("no mapping is uploaded").And.Contain("BUGSEE_BUILD_UUID"));
            Assert.That(id.ElfUuid, Is.EqualTo(Synth), "the native upload keeps the derived id as its correlation key");
        }

        [Test]
        public void The_notice_names_the_cause_it_can_tell()
        {
            Assert.That(AndroidUploadIdentity.UnreadableNotice("game.apk: locked", false), Does.Contain("locked or damaged").And.Not.Contain("shorter"));
            Assert.That(AndroidUploadIdentity.UnreadableNotice("game.apk: x", true), Does.Contain("shorter output folder"));
            Assert.That(AndroidUploadIdentity.Decide(null, null, Synth, null, "game.apk: x", true).Warning, Does.Contain("shorter output folder"));
        }

        [Test]
        public void A_split_apk_that_was_read_still_gives_the_id_when_a_sibling_could_not_be_opened()
        {
            // The editor leaves an unopenable archive out of the id list instead of counting it as "carries none".
            var id = AndroidUploadIdentity.SelectAppAssetId(new[] { App }, out var conflict);
            var decided = AndroidUploadIdentity.Decide(null, id, Synth, conflict, "game.x86.apk: locked");

            Assert.That(conflict, Is.Null);
            Assert.That(decided.Uuid, Is.EqualTo(App));
            Assert.That(decided.SkipMapping, Is.False);
        }

        [Test]
        public void An_id_the_user_states_or_the_app_carries_still_wins_over_an_unreadable_archive()
        {
            const string Env = "11111111-1111-1111-1111-111111111111";
            var env = AndroidUploadIdentity.Decide(Env, null, Synth, null, "x");
            var app = AndroidUploadIdentity.Decide(null, App, Synth, null, "other.apk: x");
            var freeText = AndroidUploadIdentity.Decide("my-build", null, Synth, null, "x");

            Assert.That(env.SkipMapping, Is.False);
            Assert.That(env.Uuid, Is.EqualTo(Env));
            Assert.That(app.SkipMapping, Is.False);
            Assert.That(app.Uuid, Is.EqualTo(App));
            Assert.That(freeText.SkipMapping, Is.True);
            Assert.That(freeText.ElfUuid, Is.EqualTo("my-build"));
            Assert.That(freeText.Warning, Does.Contain("not a UUID").And.Contain("no mapping is uploaded"));
        }

        [Test]
        public void An_app_without_the_asset_that_was_read_fine_still_uses_the_derived_id()
        {
            var id = AndroidUploadIdentity.Decide(null, null, Synth, null, null);

            Assert.That(id.SkipMapping, Is.False);
            Assert.That(id.Uuid, Is.EqualTo(Synth));
        }
    }
}
