using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class MonoPortablePdbTests
    {
        static string PackageFile(params string[] parts)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!File.Exists(Path.Combine(dir, "package.json"))) dir = Directory.GetParent(dir).FullName;
            return Path.Combine(new[] { dir }.Concat(parts).ToArray());
        }

        [TestCase("Mono2x", false, false, true, MonoPortablePdb.Outcome.Upload)]
        [TestCase("Mono2x", false, true, true, MonoPortablePdb.Outcome.OptedOut)]
        [TestCase("Mono2x", false, false, false, MonoPortablePdb.Outcome.NoToken)]
        [TestCase("Mono2x", false, true, false, MonoPortablePdb.Outcome.OptedOut)]
        // A Development build ships its own pdb files; IL2CPP has the line map.
        [TestCase("Mono2x", true, false, true, MonoPortablePdb.Outcome.NotApplicable)]
        [TestCase("IL2CPP", false, false, true, MonoPortablePdb.Outcome.NotApplicable)]
        [TestCase(null, false, false, true, MonoPortablePdb.Outcome.NotApplicable)]
        public void Only_a_release_mono_build_uploads(string backend, bool development, bool optOut, bool token, MonoPortablePdb.Outcome expected)
        {
            Assert.That(MonoPortablePdb.Decide(backend, development, optOut, token), Is.EqualTo(expected));
        }

        [Test]
        public void The_notice_names_the_reason_and_the_manual_upload()
        {
            var optOut = MonoPortablePdb.NotUploadedNotice(MonoPortablePdb.Outcome.OptedOut);
            var token = MonoPortablePdb.NotUploadedNotice(MonoPortablePdb.Outcome.NoToken);
            Assert.That(optOut, Does.Contain("BUGSEE_NO_SYMBOL_UPLOAD").And.Contain("--type portable-pdb").And.Contain("0.9.0").And.Contain("--shipped-dir"));
            Assert.That(token, Does.Contain("BUGSEE_APP_TOKEN"));
            Assert.That(MonoPortablePdb.NotUploadedNotice(MonoPortablePdb.Outcome.Upload), Is.Null);
            Assert.That(MonoPortablePdb.NotUploadedNotice(MonoPortablePdb.Outcome.NotApplicable), Is.Null);
        }

        [TestCase("0.8.1", true)]
        [TestCase("0.8.9", true)]
        [TestCase("0.9.0", false)]
        [TestCase("0.10.0", false)]
        [TestCase("1.0.0", false)]
        [TestCase(null, false)]
        [TestCase("", false)]
        public void An_older_cli_is_refused_and_an_unknown_version_is_tried(string version, bool tooOld)
        {
            Assert.That(MonoPortablePdb.CliTooOld(version) != null, Is.EqualTo(tooOld));
        }

        [Test]
        public void The_minimum_is_not_above_the_pinned_cli()
        {
            Assert.That(BugseeCliPolicy.VersionAtLeast(BugseeCliPolicy.DefaultVersion, MonoPortablePdb.MinCliVersion), Is.True);
        }

        static CompilerOutputDirectory Dir(string name, int? hoursAgo) => new CompilerOutputDirectory
        {
            Name = name,
            Path = "/p/Library/Bee/artifacts/" + name,
            AssemblyWriteUtc = hoursAgo == null ? (DateTime?)null : new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc).AddHours(-hoursAgo.Value),
        };

        [Test]
        public void The_release_player_folder_is_chosen_over_development_and_editor_ones()
        {
            var picked = MonoPortablePdb.PickCompilerOutput(new[]
            {
                Dir("1300b0aE.dag", 0), Dir("1300b0aPDevDbg.dag", 0), Dir("1300b0aP.dag", 5), Dir("200b0aE.dag", 0),
            });
            Assert.That(picked?.Name, Is.EqualTo("1300b0aP.dag"));
        }

        [Test]
        public void The_newest_of_two_release_folders_wins_and_a_folder_without_the_assembly_is_skipped()
        {
            var picked = MonoPortablePdb.PickCompilerOutput(new[]
            {
                Dir("900b0aP.dag", 48), Dir("1300b0aP.dag", 3), Dir("1400b0aP.dag", null),
            });
            Assert.That(picked.Name, Is.EqualTo("1300b0aP.dag"));
        }

        [Test]
        public void Equal_times_choose_the_same_folder_whatever_the_order()
        {
            var a = Dir("900b0aP.dag", 1);
            var b = Dir("1300b0aP.dag", 1);
            Assert.That(MonoPortablePdb.PickCompilerOutput(new[] { a, b }).Name,
                Is.EqualTo(MonoPortablePdb.PickCompilerOutput(new[] { b, a }).Name));
        }

        [TestCase("1300b0aPDevDbg.dag")]
        [TestCase("1300b0aE.dag")]
        [TestCase("P.dag")]
        [TestCase("xyzP.dag")]
        [TestCase("1300b0aP.dag.bak")]
        [TestCase("1300b0ap.dag")]
        public void Other_folders_are_not_the_release_player(string name)
        {
            Assert.That(MonoPortablePdb.PickCompilerOutput(new[] { Dir(name, 0) }), Is.Null);
        }

        [Test]
        public void No_candidates_is_no_folder() => Assert.That(MonoPortablePdb.PickCompilerOutput(null), Is.Null);

        [Test]
        public void Arguments_are_the_portable_pdb_upload_with_the_shipped_folder_and_no_uuid()
        {
            var args = MonoPortablePdb.Arguments("/c/1300b0aP.dag", "/c/Android/ManagedStripped", "1.2.3", "45");
            Assert.That(args, Is.EqualTo(new[]
            {
                "debug-files", "upload", "/c/1300b0aP.dag", "--type", "portable-pdb", "--version", "1.2.3", "--build", "45",
                "--shipped-dir", "/c/Android/ManagedStripped",
            }));
            Assert.That(args, Does.Not.Contain("--uuid"));
            Assert.That(MonoPortablePdb.Arguments("/c/x.dag", null, "1", "2"), Does.Not.Contain("--shipped-dir"));
        }

        // The Editor class must follow the policy's gates, run after the build and never fail it.
        [Test]
        public void The_editor_step_uses_the_policy_and_never_fails_the_build()
        {
            var text = File.ReadAllText(PackageFile("Editor", "BugseeAndroidMonoPdbUpload.cs"));
            Assert.That(text, Does.Contain("IPostprocessBuildWithReport"));
            Assert.That(text, Does.Contain("MonoPortablePdb.Decide("));
            Assert.That(text, Does.Contain("BUGSEE_NO_SYMBOL_UPLOAD"));
            Assert.That(text, Does.Contain("BugseeCliRunner.HasAppToken"));
            Assert.That(text, Does.Contain("MonoPortablePdb.PickCompilerOutput("));
            Assert.That(text, Does.Contain("MonoPortablePdb.CliTooOld("));
            Assert.That(text, Does.Contain("MonoPortablePdb.Arguments("));
            Assert.That(text, Does.Contain("BuildOptions.Development"));
            Assert.That(text, Does.Contain("BugseeBuildIdentity.AndroidNameSettings(report).Backend"));
            Assert.That(text, Does.Not.Contain("\"Assembly-CSharp.dll\""), "an asmdef-only project has no Assembly-CSharp.dll");
            Assert.That(text, Does.Contain("ManagedStripped"));
            Assert.That(text, Does.Contain("not found: the Portable PDBs are uploaded under the compiler's module ids"));
            Assert.That(text, Does.Contain("catch (Exception"));
            Assert.That(text, Does.Not.Contain("throw"));
        }

        [Test]
        public void The_old_managed_lines_warning_that_said_nothing_resolves_the_offset_is_gone()
        {
            foreach (var file in new[] { "Editor/BugseeAndroidMonoWarning.cs", "Runtime/WrapperPolicy/AndroidMonoSymbolPolicy.cs", "Documentation~/index.md" })
            {
                var text = File.ReadAllText(PackageFile(file.Split('/')));
                Assert.That(text, Does.Not.Contain("TakeManagedLinesWarning"), file);
                Assert.That(text, Does.Not.Contain("worker reads no Portable PDB"), file);
                Assert.That(text, Does.Not.Contain("is a scaffold"), file);
            }
        }
    }
}
