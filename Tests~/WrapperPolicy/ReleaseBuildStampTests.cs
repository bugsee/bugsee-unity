using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    static class RepoRoot
    {
        public static string Find()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json"))) dir = dir.Parent;
            Assert.That(dir, Is.Not.Null, "repo root (package.json) not found");
            return dir.FullName;
        }
    }

    /// <summary>Source-level checks of the release files; they run everywhere.</summary>
    [TestFixture]
    public class ReleaseBuildFilesTests
    {
        static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot.Find() }.Concat(parts).ToArray()));

        [Test]
        public void The_release_workflow_runs_the_check_on_version_tags_with_full_history()
        {
            var text = Read(".github", "workflows", "release-check.yml");
            Assert.That(text, Does.Contain("- \"v[0-9]*\""));
            Assert.That(text, Does.Contain("fetch-depth: 0"));
            Assert.That(text, Does.Contain("check-release-build.sh"));
        }

        [Test]
        public void Ci_requires_the_dev_placeholder_on_main()
        {
            var text = Read(".github", "workflows", "ci.yml");
            Assert.That(text, Does.Contain("check-release-build.sh --dev"));
        }

        [Test]
        public void The_committed_build_is_the_dev_placeholder()
        {
            Assert.That(Read("Runtime", "BugseePackageVersion.cs"), Does.Contain("Build = \"dev\";"));
            Assert.That(Read("Plugins", "Android", "UnityWrapperIdentity.java"), Does.Contain("BUILD = \"dev\";"));
        }

        [Test]
        public void The_release_steps_are_written_down()
        {
            var design = Read("DESIGN.md");
            Assert.That(design, Does.Contain("## Releasing"));
            Assert.That(design, Does.Contain("stamp-wrapper-build.sh"));
            Assert.That(design, Does.Contain("Do not merge the release branch into `main`"));
        }
    }

    /// <summary>
    /// A release carries a real wrapper build id: stamp-wrapper-build.sh writes it, check-release-build.sh refuses a tag
    /// without one. Both run (as bash scripts) against a scratch git repository.
    /// </summary>
    [TestFixture]
    public class ReleaseBuildStampTests
    {
        const string Cs = "Runtime/BugseePackageVersion.cs";
        const string Java = "Plugins/Android/UnityWrapperIdentity.java";
        const string CsDev = "public const string Version = \"1.2.3\";\n        public const string Build = \"dev\";\n";
        const string JavaDev = "static final String VERSION = \"1.2.3\";\n    static final String BUILD = \"dev\";\n";

        string _root;
        string _scripts;

        [SetUp]
        public void SetUp()
        {
            Assume.That(Environment.OSVersion.Platform, Is.EqualTo(PlatformID.Unix), "bash scripts");
            _scripts = Path.Combine(RepoRoot.Find(), "Tools~", "scripts");

            // A path with a space: the scripts must quote it.
            _root = Path.Combine(Path.GetTempPath(), "bugsee stamp " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "Runtime"));
            Directory.CreateDirectory(Path.Combine(_root, "Plugins", "Android"));
            File.WriteAllText(Path.Combine(_root, Cs), CsDev);
            File.WriteAllText(Path.Combine(_root, Java), JavaDev);
            File.WriteAllText(Path.Combine(_root, "package.json"), "{\n  \"name\": \"com.bugsee.unity\",\n  \"version\": \"1.2.3\"\n}\n");
            File.WriteAllText(Path.Combine(_root, "CHANGELOG.md"), "x\n");
            Directory.CreateDirectory(Path.Combine(_root, "Tools~"));
            File.WriteAllText(Path.Combine(_root, "Tools~", "versions.env"), "# pins\nBUGSEE_PACKAGE_VERSION=\"1.2.3\"\nANDROID_SDK_VERSION=\"7.4.2\"\n");
            Git("init", "-q");
            Commit("base");
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        string Run(string file, IEnumerable<string> args, out int exitCode)
        {
            var info = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) info.ArgumentList.Add(a);
            info.Environment["BUGSEE_ROOT"] = _root;
            using (var process = Process.Start(info))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                Assert.That(process.WaitForExit(30000), Is.True, "timed out: " + file);
                exitCode = process.ExitCode;
                return stdout.Result + stderr.Result;
            }
        }

        string Git(params string[] args)
        {
            var all = new List<string> { "-C", _root, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false" };
            all.AddRange(args);
            var output = Run("git", all, out var exit);
            Assert.That(exit, Is.EqualTo(0), "git " + string.Join(" ", args) + ": " + output);
            return output;
        }

        void Commit(string message) { Git("add", "-A"); Git("commit", "-q", "--allow-empty", "-m", message); }

        string Script(string name, out int exit, params string[] args) =>
            Run("/bin/bash", new[] { Path.Combine(_scripts, name) }.Concat(args), out exit);

        string Head() => Git("rev-parse", "--short=12", "HEAD").Trim();
        string ReadCs() => File.ReadAllText(Path.Combine(_root, Cs));
        string ReadJava() => File.ReadAllText(Path.Combine(_root, Java));

        // --- stamp ---

        [Test]
        public void Stamp_defaults_to_the_head_commit_in_both_constants()
        {
            var output = Script("stamp-wrapper-build.sh", out var exit);
            Assert.That(exit, Is.EqualTo(0), output);
            var head = Head();
            Assert.That(ReadCs(), Does.Contain("Build = \"" + head + "\";"));
            Assert.That(ReadJava(), Does.Contain("BUILD = \"" + head + "\";"));
            Assert.That(ReadCs(), Does.Contain("Version = \"1.2.3\""), "only the build changes");
            Assert.That(ReadJava(), Does.Contain("VERSION = \"1.2.3\""));
        }

        [Test]
        public void Stamp_is_idempotent_and_takes_an_explicit_id()
        {
            Script("stamp-wrapper-build.sh", out _, "r42.1");
            var first = ReadCs();
            Script("stamp-wrapper-build.sh", out var exit, "r42.1");
            Assert.That(exit, Is.EqualTo(0));
            Assert.That(ReadCs(), Is.EqualTo(first));
            Assert.That(first, Does.Contain("Build = \"r42.1\";"));
        }

        [TestCase("dev")]
        [TestCase("a b")]
        [TestCase("-x")]
        [TestCase("a\"b")]
        [TestCase("")]
        [TestCase("20261021")]
        [TestCase("abcdef1234")]
        public void Stamp_refuses_the_placeholder_malformed_and_hash_shaped_ids(string id)
        {
            var output = Script("stamp-wrapper-build.sh", out var exit, id);
            Assert.That(exit, Is.Not.EqualTo(0), "'" + id + "' was accepted: " + output);
            Assert.That(ReadCs(), Is.EqualTo(CsDev));
            Assert.That(ReadJava(), Is.EqualTo(JavaDev));
        }

        [Test]
        public void Stamp_without_an_id_refuses_a_dirty_tree()
        {
            File.WriteAllText(Path.Combine(_root, "CHANGELOG.md"), "edited\n");
            var output = Script("stamp-wrapper-build.sh", out var exit);
            Assert.That(exit, Is.Not.EqualTo(0));
            Assert.That(output, Does.Contain("uncommitted"));
            Assert.That(ReadCs(), Is.EqualTo(CsDev));
            Script("stamp-wrapper-build.sh", out exit, "r1");
            Assert.That(exit, Is.EqualTo(0), "an explicit id does not need a clean tree");
        }

        [Test]
        public void Stamp_writes_neither_file_when_one_has_no_constant()
        {
            File.WriteAllText(Path.Combine(_root, Java), "static final String VERSION = \"1.2.3\";\n");
            Commit("java without build");
            var output = Script("stamp-wrapper-build.sh", out var exit, "r1");
            Assert.That(exit, Is.Not.EqualTo(0));
            Assert.That(output, Does.Contain("UnityWrapperIdentity.java"));
            Assert.That(ReadCs(), Is.EqualTo(CsDev), "the C# file must not be half-stamped");
            Assert.That(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories), Is.Empty);
        }

        // --- check ---

        [Test]
        public void Check_refuses_the_dev_placeholder()
        {
            var output = Script("check-release-build.sh", out var exit);
            Assert.That(exit, Is.Not.EqualTo(0));
            Assert.That(output, Does.Contain("'dev' placeholder"));
        }

        [Test]
        public void Check_dev_mode_wants_the_placeholder()
        {
            Script("check-release-build.sh", out var exit, "--dev");
            Assert.That(exit, Is.EqualTo(0));
            Script("stamp-wrapper-build.sh", out _);
            var output = Script("check-release-build.sh", out exit, "--dev");
            Assert.That(exit, Is.Not.EqualTo(0));
            Assert.That(output, Does.Contain("merged into main"));
        }

        [Test]
        public void Check_accepts_the_stamp_of_the_parent_commit_and_the_matching_tag()
        {
            Script("stamp-wrapper-build.sh", out _);
            var stamped = Head();
            Commit("release");
            var output = Script("check-release-build.sh", out var exit, "v1.2.3");
            Assert.That(exit, Is.EqualTo(0), output);
            Assert.That(output, Does.Contain("OK: wrapper build " + stamped + " for v1.2.3"));
        }

        [Test]
        public void Check_allows_the_release_edits_after_the_stamped_commit()
        {
            Script("stamp-wrapper-build.sh", out _);
            File.WriteAllText(Path.Combine(_root, "CHANGELOG.md"), "release notes\n");
            Commit("release");
            Script("check-release-build.sh", out var exit, "v1.2.3");
            Assert.That(exit, Is.EqualTo(0));
        }

        [Test]
        public void Check_refuses_a_tag_that_is_not_the_package_version()
        {
            Script("stamp-wrapper-build.sh", out _);
            Commit("stamp");
            var output = Script("check-release-build.sh", out var exit, "v9.9.9");
            Assert.That(exit, Is.Not.EqualTo(0));
            Assert.That(output, Does.Contain("package.json"));
        }

        [Test]
        public void Check_refuses_constants_that_disagree()
        {
            Script("stamp-wrapper-build.sh", out _);
            File.WriteAllText(Path.Combine(_root, Java), JavaDev);
            var output = Script("check-release-build.sh", out var exit);
            Assert.That(exit, Is.Not.EqualTo(0));
            Assert.That(output, Does.Contain("Java says"));
        }

        [Test]
        public void Check_refuses_a_hash_that_is_not_in_the_repository()
        {
            Script("stamp-wrapper-build.sh", out _, "r1");
            File.WriteAllText(Path.Combine(_root, Cs), CsDev.Replace("\"dev\"", "\"0123456789ab\""));
            File.WriteAllText(Path.Combine(_root, Java), JavaDev.Replace("\"dev\"", "\"0123456789ab\""));
            var output = Script("check-release-build.sh", out var exit);
            Assert.That(exit, Is.Not.EqualTo(0));
            Assert.That(output, Does.Contain("not a commit"));
        }

        [Test]
        public void Check_refuses_a_hash_from_another_branch()
        {
            Git("switch", "-q", "-c", "side");
            File.WriteAllText(Path.Combine(_root, "CHANGELOG.md"), "side\n");
            Commit("side work");
            var side = Head();
            Git("switch", "-q", "-");
            Script("stamp-wrapper-build.sh", out _, "r1");
            File.WriteAllText(Path.Combine(_root, Cs), CsDev.Replace("\"dev\"", "\"" + side + "\""));
            File.WriteAllText(Path.Combine(_root, Java), JavaDev.Replace("\"dev\"", "\"" + side + "\""));
            Commit("release");
            var output = Script("check-release-build.sh", out var exit);
            Assert.That(exit, Is.Not.EqualTo(0));
            Assert.That(output, Does.Contain("not an ancestor"));
        }

        [Test]
        public void Check_refuses_code_changed_after_the_stamped_commit()
        {
            Script("stamp-wrapper-build.sh", out _);
            File.WriteAllText(Path.Combine(_root, "Runtime", "Other.cs"), "class Other {}\n");
            Commit("release plus code");
            var output = Script("check-release-build.sh", out var exit);
            Assert.That(exit, Is.Not.EqualTo(0));
            Assert.That(output, Does.Contain("Other.cs"));
        }

        [Test]
        public void Check_refuses_a_native_sdk_pin_changed_after_the_stamp()
        {
            File.WriteAllText(Path.Combine(_root, Cs), CsDev + "        public const string AndroidSdkVersion = \"7.4.2\";\n");
            Commit("pins");
            Script("stamp-wrapper-build.sh", out _);
            Commit("stamp");
            File.WriteAllText(Path.Combine(_root, Cs), ReadCs().Replace("7.4.2", "7.5.0"));
            Commit("bump");
            var output = Script("check-release-build.sh", out var exit);
            Assert.That(exit, Is.Not.EqualTo(0), output);
            Assert.That(output, Does.Contain("beyond its build/version lines"));
        }

        [Test]
        public void Check_allows_a_package_version_bump_after_the_stamp()
        {
            File.WriteAllText(Path.Combine(_root, Cs), CsDev + "        public const string AndroidSdkVersion = \"7.4.2\";\n");
            Commit("pins");
            Script("stamp-wrapper-build.sh", out _);
            Commit("stamp");
            File.WriteAllText(Path.Combine(_root, Cs), ReadCs().Replace("Version = \"1.2.3\"", "Version = \"1.2.4\""));
            File.WriteAllText(Path.Combine(_root, Java), ReadJava().Replace("VERSION = \"1.2.3\"", "VERSION = \"1.2.4\""));
            File.WriteAllText(Path.Combine(_root, "package.json"), "{\n  \"name\": \"com.bugsee.unity\",\n  \"version\": \"1.2.4\"\n}\n");
            File.WriteAllText(Path.Combine(_root, "Tools~", "versions.env"), "# pins\nBUGSEE_PACKAGE_VERSION=\"1.2.4\"\nANDROID_SDK_VERSION=\"7.4.2\"\n");
            Commit("bump version");
            var output = Script("check-release-build.sh", out var exit, "v1.2.4");
            Assert.That(exit, Is.EqualTo(0), output);
        }

        [Test]
        public void Check_with_a_tag_refuses_uncommitted_changes()
        {
            Script("stamp-wrapper-build.sh", out _);
            Commit("stamp");
            File.WriteAllText(Path.Combine(_root, "package.json"), "{\n  \"name\": \"com.bugsee.unity\",\n  \"version\": \"1.2.4\"\n}\n");
            File.WriteAllText(Path.Combine(_root, Cs), ReadCs().Replace("Version = \"1.2.3\"", "Version = \"1.2.4\""));
            File.WriteAllText(Path.Combine(_root, Java), ReadJava().Replace("VERSION = \"1.2.3\"", "VERSION = \"1.2.4\""));
            var output = Script("check-release-build.sh", out var exit, "v1.2.4");
            Assert.That(exit, Is.Not.EqualTo(0), output);
            Assert.That(output, Does.Contain("uncommitted"));
        }

        [Test]
        public void Check_refuses_a_package_version_that_the_wrapper_does_not_report()
        {
            Script("stamp-wrapper-build.sh", out _);
            File.WriteAllText(Path.Combine(_root, "package.json"), "{\n  \"name\": \"com.bugsee.unity\",\n  \"version\": \"1.2.4\"\n}\n");
            Commit("package only");
            var output = Script("check-release-build.sh", out var exit, "v1.2.4");
            Assert.That(exit, Is.Not.EqualTo(0), output);
            Assert.That(output, Does.Contain("versions disagree"));
        }

        [Test]
        public void Check_refuses_a_versions_env_version_that_the_others_do_not_follow()
        {
            Script("stamp-wrapper-build.sh", out _);
            File.WriteAllText(Path.Combine(_root, "Tools~", "versions.env"), "# pins\nBUGSEE_PACKAGE_VERSION=\"1.2.4\"\nANDROID_SDK_VERSION=\"7.4.2\"\n");
            Commit("versions.env only");
            var output = Script("check-release-build.sh", out var exit, "v1.2.3");
            Assert.That(exit, Is.Not.EqualTo(0), output);
            Assert.That(output, Does.Contain("versions.env '1.2.4'"));
        }

        [Test]
        public void Check_refuses_other_package_json_edits_after_the_stamp()
        {
            Script("stamp-wrapper-build.sh", out _);
            File.WriteAllText(Path.Combine(_root, "package.json"), "{\n  \"name\": \"com.bugsee.unity\",\n  \"version\": \"1.2.3\",\n  \"dependencies\": {}\n}\n");
            Commit("package edit");
            Script("check-release-build.sh", out var exit);
            Assert.That(exit, Is.Not.EqualTo(0));
        }

        [Test]
        public void Check_accepts_a_non_hash_id_without_consulting_git()
        {
            Script("stamp-wrapper-build.sh", out _, "2026.10-rc1");
            var output = Script("check-release-build.sh", out var exit);
            Assert.That(exit, Is.EqualTo(0), output);
            Assert.That(output, Does.Contain("OK: wrapper build 2026.10-rc1"));
        }
    }

    /// <summary>A pin bump on main must not stamp a hash no release is cut from: update-native-sdks.sh keeps the build.</summary>
    [TestFixture]
    public class PinGeneratorKeepsBuildTests
    {
        [Test]
        public void The_generator_keeps_the_committed_build_in_both_files()
        {
            Assume.That(Environment.OSVersion.Platform, Is.EqualTo(PlatformID.Unix), "bash script");
            var repo = RepoRoot.Find();
            var scratch = Path.Combine(Path.GetTempPath(), "bugsee gen " + Guid.NewGuid().ToString("N"));
            try
            {
                CopyDir(Path.Combine(repo, "Tools~"), Path.Combine(scratch, "Tools~"));
                var files = new List<string> { "package.json", "Runtime/BugseePackageVersion.cs", "Plugins/Android/UnityWrapperIdentity.java" };
                foreach (var line in File.ReadAllLines(Path.Combine(repo, "Tools~", "scripts", "native-pins.txt")))
                    if (line.Trim().Length > 0 && !line.StartsWith("#")) files.Add(line.Split('|')[1]);
                foreach (var f in files.Distinct())
                {
                    var from = Path.Combine(repo, f);
                    if (!File.Exists(from)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(scratch, f)));
                    File.Copy(from, Path.Combine(scratch, f), true);
                }
                foreach (var f in new[] { "Runtime/BugseePackageVersion.cs", "Plugins/Android/UnityWrapperIdentity.java" })
                {
                    var p = Path.Combine(scratch, f);
                    File.WriteAllText(p, File.ReadAllText(p).Replace("\"dev\"", "\"r7-kept\""));
                }

                var info = new ProcessStartInfo("/bin/bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                info.ArgumentList.Add(Path.Combine(scratch, "Tools~", "scripts", "update-native-sdks.sh"));
                using (var process = Process.Start(info))
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    Assert.That(process.WaitForExit(120000), Is.True, "generator timed out");
                    var output = stdout.Result + stderr.Result;
                    Assume.That(output, Does.Not.Contain("python3 is required"));
                    Assert.That(process.ExitCode, Is.EqualTo(0), output);
                    Assert.That(File.ReadAllText(Path.Combine(scratch, "Runtime/BugseePackageVersion.cs")),
                        Does.Contain("Regenerated by Tools~/scripts/update-native-sdks.sh"), "the generator rewrote the file");
                    Assert.That(File.ReadAllText(Path.Combine(scratch, "Runtime/BugseePackageVersion.cs")), Does.Contain("Build = \"r7-kept\";"), output);
                    Assert.That(File.ReadAllText(Path.Combine(scratch, "Plugins/Android/UnityWrapperIdentity.java")), Does.Contain("BUILD = \"r7-kept\";"), output);
                }
            }
            finally
            {
                try { Directory.Delete(scratch, true); } catch (IOException) { }
            }
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }
}
