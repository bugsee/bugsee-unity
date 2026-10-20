using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// A release carries a real wrapper build id: stamp-wrapper-build.sh writes it, check-release-build.sh (the
    /// release-check workflow, on every v* tag) refuses a tag without one. Both run against a scratch repository.
    /// </summary>
    [TestFixture]
    public class ReleaseBuildStampTests
    {
        const string Cs = "Runtime/BugseePackageVersion.cs";
        const string Java = "Plugins/Android/UnityWrapperIdentity.java";

        string _root;
        string _scripts;

        [SetUp]
        public void SetUp()
        {
            Assume.That(Environment.OSVersion.Platform, Is.EqualTo(PlatformID.Unix), "bash scripts");
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json"))) dir = dir.Parent;
            Assert.That(dir, Is.Not.Null);
            _scripts = Path.Combine(dir.FullName, "Tools~", "scripts");

            _root = Path.Combine(Path.GetTempPath(), "bugsee-stamp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "Runtime"));
            Directory.CreateDirectory(Path.Combine(_root, "Plugins", "Android"));
            File.WriteAllText(Path.Combine(_root, Cs), "public const string Version = \"1.2.3\";\n        public const string Build = \"dev\";\n");
            File.WriteAllText(Path.Combine(_root, Java), "static final String VERSION = \"1.2.3\";\n    static final String BUILD = \"dev\";\n");
            File.WriteAllText(Path.Combine(_root, "package.json"), "{\n  \"name\": \"com.bugsee.unity\",\n  \"version\": \"1.2.3\"\n}\n");
            Git("init", "-q");
            Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "base");
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        string Git(params string[] args) => Run("git", "-C " + _root + " " + string.Join(" ", args), out _);

        string Run(string file, string arguments, out int exitCode)
        {
            var info = new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.Environment["BUGSEE_ROOT"] = _root;
            using (var process = Process.Start(info))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                Assert.That(process.WaitForExit(30000), Is.True, "timed out: " + file + " " + arguments);
                exitCode = process.ExitCode;
                return stdout.Result + stderr.Result;
            }
        }

        string Script(string name, out int exit, params string[] args) =>
            Run("/bin/bash", "\"" + Path.Combine(_scripts, name) + "\" " + string.Join(" ", args), out exit);

        string Head() => Git("rev-parse", "--short=12", "HEAD").Trim();

        [Test]
        public void Stamp_defaults_to_the_head_commit_in_both_constants()
        {
            var output = Script("stamp-wrapper-build.sh", out var exit);
            Assert.That(exit, Is.EqualTo(0), output);
            var head = Head();
            Assert.That(File.ReadAllText(Path.Combine(_root, Cs)), Does.Contain("Build = \"" + head + "\";"));
            Assert.That(File.ReadAllText(Path.Combine(_root, Java)), Does.Contain("BUILD = \"" + head + "\";"));
            Assert.That(File.ReadAllText(Path.Combine(_root, Cs)), Does.Contain("Version = \"1.2.3\""), "only the build changes");
            Assert.That(File.ReadAllText(Path.Combine(_root, Java)), Does.Contain("VERSION = \"1.2.3\""));
        }

        [Test]
        public void Stamp_is_idempotent_and_takes_an_explicit_id()
        {
            Script("stamp-wrapper-build.sh", out _, "r42.1");
            var first = File.ReadAllText(Path.Combine(_root, Cs));
            Script("stamp-wrapper-build.sh", out var exit, "r42.1");
            Assert.That(exit, Is.EqualTo(0));
            Assert.That(File.ReadAllText(Path.Combine(_root, Cs)), Is.EqualTo(first));
            Assert.That(first, Does.Contain("Build = \"r42.1\";"));
        }

        [TestCase("dev")]
        [TestCase("a b")]
        [TestCase("\\\"x")]
        [TestCase("-x")]
        public void Stamp_refuses_the_placeholder_and_malformed_ids(string id)
        {
            var output = Script("stamp-wrapper-build.sh", out var exit, "'" + id + "'");
            Assert.That(exit, Is.Not.EqualTo(0), output);
            Assert.That(File.ReadAllText(Path.Combine(_root, Cs)), Does.Contain("Build = \"dev\""));
        }

        [Test]
        public void Check_refuses_the_dev_placeholder()
        {
            var output = Script("check-release-build.sh", out var exit);
            Assert.That(exit, Is.Not.EqualTo(0));
            Assert.That(output, Does.Contain("dev"));
        }

        [Test]
        public void Check_accepts_a_stamped_head_and_the_matching_tag()
        {
            Script("stamp-wrapper-build.sh", out _);
            var output = Script("check-release-build.sh", out var exit, "v1.2.3");
            Assert.That(exit, Is.EqualTo(0), output);
            Assert.That(output, Does.Contain("OK: wrapper build " + Head()));
        }

        [Test]
        public void Check_refuses_a_tag_that_is_not_the_package_version()
        {
            Script("stamp-wrapper-build.sh", out _);
            var output = Script("check-release-build.sh", out var exit, "v9.9.9");
            Assert.That(exit, Is.Not.EqualTo(0));
            Assert.That(output, Does.Contain("package.json"));
        }

        [Test]
        public void Check_refuses_constants_that_disagree()
        {
            Script("stamp-wrapper-build.sh", out _);
            File.WriteAllText(Path.Combine(_root, Java), "static final String BUILD = \"dev\";\n");
            var output = Script("check-release-build.sh", out var exit);
            Assert.That(exit, Is.Not.EqualTo(0));
            Assert.That(output, Does.Contain("Java"));
        }

        [Test]
        public void Check_refuses_a_hash_that_is_not_in_the_history()
        {
            Script("stamp-wrapper-build.sh", out _, "0123456789ab");
            var output = Script("check-release-build.sh", out var exit);
            Assert.That(exit, Is.Not.EqualTo(0));
            Assert.That(output, Does.Contain("not a commit"));
        }

        [Test]
        public void Check_accepts_a_non_hash_id_without_consulting_git()
        {
            Script("stamp-wrapper-build.sh", out _, "2026.10-rc1");
            Script("check-release-build.sh", out var exit);
            Assert.That(exit, Is.EqualTo(0));
        }

        // A pin bump on main must not stamp a hash no release is cut from: the generator keeps the build as it is.
        [Test]
        public void The_pin_generator_keeps_the_build_and_does_not_ask_git_for_one()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json"))) dir = dir.Parent;
            var text = File.ReadAllText(Path.Combine(dir.FullName, "Tools~", "scripts", "update-native-sdks.sh"));
            Assert.That(text, Does.Not.Contain("rev-parse"));
            Assert.That(text, Does.Contain("Build = \"${WRAPPER_BUILD}\""));
            Assert.That(text, Does.Contain("BUILD = \"${WRAPPER_BUILD}\""));
        }

        [Test]
        public void The_release_workflow_runs_the_check_on_version_tags_with_full_history()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json"))) dir = dir.Parent;
            var text = File.ReadAllText(Path.Combine(dir.FullName, ".github", "workflows", "release-check.yml"));
            Assert.That(text, Does.Contain("- \"v*\""));
            Assert.That(text, Does.Contain("fetch-depth: 0"));
            Assert.That(text, Does.Contain("check-release-build.sh"));
        }
    }
}
