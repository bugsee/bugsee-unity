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

        [Test]
        public void Restore_waits_while_a_build_runs_and_after_a_failed_build_restores_what_it_applied()
        {
            Assert.That(Il2CppArgsRestorePolicy.Decide(buildInProgress: true, currentEqualsApplied: true), Is.EqualTo(SymbolRestoreAction.Skip));
            Assert.That(Il2CppArgsRestorePolicy.Decide(buildInProgress: true, currentEqualsApplied: false), Is.EqualTo(SymbolRestoreAction.Skip));
            // The failure window: no post-build callback ran, the build is over, the settings still hold the applied string.
            Assert.That(Il2CppArgsRestorePolicy.Decide(buildInProgress: false, currentEqualsApplied: true), Is.EqualTo(SymbolRestoreAction.Restore));
        }

        [Test]
        public void An_args_string_the_user_edited_since_is_kept()
        {
            Assert.That(Il2CppArgsRestorePolicy.Decide(buildInProgress: false, currentEqualsApplied: false), Is.EqualTo(SymbolRestoreAction.Discard));
        }
    }
}
