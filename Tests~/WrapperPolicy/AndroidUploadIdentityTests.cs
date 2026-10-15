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
            Assert.That(id.Warning, Does.Contain("no bugsee_build_id.properties").And.Contain("BUGSEE_BUILD_UUID"));
        }

        [Test]
        public void An_environment_override_wins_and_a_conflict_with_the_app_is_called_out()
        {
            var agrees = AndroidUploadIdentity.Decide(App.ToUpperInvariant(), App, Synth);
            var conflicts = AndroidUploadIdentity.Decide("11111111-1111-1111-1111-111111111111", App, Synth);

            Assert.That(agrees.Source, Is.EqualTo(BuildIdSource.Environment));
            Assert.That(agrees.Warning, Is.Null);
            Assert.That(conflicts.Uuid, Is.EqualTo("11111111-1111-1111-1111-111111111111"));
            Assert.That(conflicts.Warning, Does.Contain(App).And.Contain("will not match"));
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
    }
}
