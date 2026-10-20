using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class Il2CppMappingArgsTests
    {
        [TestCase(null, "--emit-source-mapping --emit-method-map")]
        [TestCase("", "--emit-source-mapping --emit-method-map")]
        [TestCase("--emit-source-mapping", "--emit-source-mapping --emit-method-map")]
        [TestCase("--emit-method-map", "--emit-method-map --emit-source-mapping")]
        [TestCase("--emit-null-checks", "--emit-null-checks --emit-source-mapping --emit-method-map")]
        [TestCase("--emit-null-checks ", "--emit-null-checks --emit-source-mapping --emit-method-map")]
        [TestCase("--emit-method-map --emit-source-mapping", "--emit-method-map --emit-source-mapping")]
        [TestCase("--emit-source-mapping=1  --emit-method-map", "--emit-source-mapping=1  --emit-method-map")]
        public void Missing_mapping_arguments_are_appended_and_nothing_else_changes(string existing, string expected)
        {
            Assert.That(Il2CppMappingArgs.Ensure(existing), Is.EqualTo(expected));
        }

        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        public void Emission_follows_the_backend_and_the_opt_out_only(bool il2cpp, bool optOut, bool expected)
        {
            Assert.That(Il2CppMappingArgs.ShouldEmit(il2cpp, optOut), Is.EqualTo(expected));
        }

        // Emission must not depend on the upload configuration: a build without the token (or one a later CI job uploads) still writes the files.
        [Test]
        public void The_editor_step_does_not_look_at_the_token_or_the_explicit_mapping_path()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!File.Exists(Path.Combine(dir, "package.json"))) dir = Directory.GetParent(dir).FullName;
            var text = File.ReadAllText(Path.Combine(dir, "Editor", "BugseeIl2CppMappingArgs.cs"));
            Assert.That(text, Does.Contain("Il2CppMappingArgs.ShouldEmit("));
            Assert.That(text, Does.Not.Contain("HasAppToken"));
            Assert.That(text, Does.Not.Contain("BUGSEE_IL2CPP_MAPPING\""));
            Assert.That(text, Does.Not.Contain("BUGSEE_NO_SYMBOL_UPLOAD"));
            // The tracked ProjectSettings.asset is not edited for a build.
            Assert.That(text, Does.Not.Contain("SetAdditionalIl2CppArgs"));
        }

        [Test]
        public void A_flag_that_only_shares_a_prefix_does_not_count()
        {
            Assert.That(Il2CppMappingArgs.Ensure("--emit-method-map-extra"), Is.EqualTo("--emit-method-map-extra --emit-source-mapping --emit-method-map"));
        }

        [Test]
        public void Ensure_is_idempotent()
        {
            string once = Il2CppMappingArgs.Ensure("--emit-null-checks");

            Assert.That(Il2CppMappingArgs.Ensure(once), Is.EqualTo(once));
        }

        [TestCase(null, null, "--emit-source-mapping --emit-method-map")]
        [TestCase("", "", "--emit-source-mapping --emit-method-map")]
        [TestCase("--emit-null-checks", null, "--emit-source-mapping --emit-method-map")]
        [TestCase("--emit-source-mapping --emit-method-map", null, null)]
        [TestCase("--emit-source-mapping", "--emit-method-map", null)]
        [TestCase("--emit-source-mapping", null, "--emit-method-map")]
        [TestCase(null, "--foo", "--foo --emit-source-mapping --emit-method-map")]
        [TestCase(null, "--foo ", "--foo --emit-source-mapping --emit-method-map")]
        [TestCase("--emit-method-map", "--foo", "--foo --emit-source-mapping")]
        public void The_environment_value_adds_only_what_neither_Player_Settings_nor_the_environment_has(string playerArgs, string envArgs, string expected)
        {
            Assert.That(Il2CppMappingArgs.EnvironmentValue(playerArgs, envArgs), Is.EqualTo(expected));
        }
    }
}
