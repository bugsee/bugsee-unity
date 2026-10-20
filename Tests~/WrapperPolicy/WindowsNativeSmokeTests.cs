using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// Runs on a real Windows host only (the Windows CI job; skipped elsewhere): the CLI lookup against the real file
    /// system with real drive letters, spaces, case-insensitivity and a real bugsee-cli.exe, and the process launch the way
    /// BugseeCliRunner does it. The stand-in executable is Tests~/WindowsSmoke/FakeCli, found through
    /// BUGSEE_FAKE_CLI_EXE. This is .NET's Process, not the Mono one inside a Unity 2021.3 Editor, and not the real CLI.
    /// </summary>
    [Platform("Win")]
    public class WindowsNativeSmokeTests
    {
        string _root;
        string _fake;

        [SetUp]
        public void SetUp()
        {
            _fake = Environment.GetEnvironmentVariable("BUGSEE_FAKE_CLI_EXE");
            Assert.That(_fake, Is.Not.Null.And.Not.Empty, "BUGSEE_FAKE_CLI_EXE must point at the built Tests~/WindowsSmoke/FakeCli bugsee-cli.exe");
            Assert.That(File.Exists(_fake), Is.True, _fake);
            _root = Path.Combine(Path.GetTempPath(), "bugsee smoke " + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { /* best effort */ }
        }

        string Install(string dir)
        {
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "bugsee-cli.exe");
            // The apphost needs its runtime config and managed assembly next to it.
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_fake)))
            {
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
            }

            return target;
        }

        static string Run(string exe, IEnumerable<string> args, out int exitCode, IDictionary<string, string> env = null)
        {
            // Like Editor/BugseeCliRunner.cs (ArgumentList, redirected output, no window), except that stdout is decoded as
            // UTF-8 here so the non-ASCII argument can be compared; the runner uses the default encoding.
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            foreach (var a in args) psi.ArgumentList.Add(a ?? "");
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            using (var p = Process.Start(psi))
            {
                // Read both streams concurrently and bound the wait, so a hung child fails this test instead of the job.
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(30000))
                {
                    try { p.Kill(); } catch { /* already gone */ }
                    Assert.Fail("bugsee-cli.exe stand-in did not exit within 30 s");
                }

                p.WaitForExit();
                Assert.That(System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { stdout, stderr }, 10000), Is.True, "output pipes did not close");
                exitCode = p.ExitCode;
                return stdout.Result;
            }
        }

        static CliProbe RealProbe() => new CliProbe
        {
            FileExists = File.Exists,
            QueryVersion = p =>
            {
                string o = Run(p, new[] { "--version" }, out var code);
                return code == 0 ? BugseeCliPolicy.ParseVersionOutput(o) : null;
            },
        };

        static CliSettings RealSettings(string path) => new CliSettings
        {
            IsWindows = true,
            PathEnv = path,
            Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Triple = BugseeCliPolicy.HostTriple("windows", "X64"),
        };

        [Test]
        public void The_real_host_is_what_the_windows_policy_assumes()
        {
            Assert.That(Path.DirectorySeparatorChar, Is.EqualTo('\\'));
            Assert.That(Path.PathSeparator, Is.EqualTo(';'));
            Assert.That(Environment.GetEnvironmentVariable("LOCALAPPDATA"), Does.Match("^[A-Za-z]:\\\\"));
            Assert.That(Environment.GetEnvironmentVariable("PATHEXT"), Does.Contain(".EXE").IgnoreCase);
        }

        [Test]
        public void Arguments_with_spaces_quotes_and_trailing_backslashes_reach_the_exe_unchanged()
        {
            string exe = Install(Path.Combine(_root, "bin"));
            var args = new[]
            {
                "upload", "--mapping", "C:\\Build Output\\My Game\\game.apk", "C:\\Build Output\\", "\\\\build01\\share\\out dir\\",
                "say \"hi\"", "", "a\\\"b", "x\\\\", "100% $HOME ^& |", "Ünï 日本", "--flag=C:\\a b\\c", new string('x', 300),
            };

            string output = Run(exe, args, out var code, new Dictionary<string, string> { ["BUGSEE_APP_TOKEN"] = "tok-123" });

            Assert.That(code, Is.EqualTo(0));
            var echoed = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("[")).Select(l => l.Substring(1, l.Length - 2)).ToArray();
            Assert.That(echoed, Is.EqualTo(args));
            Assert.That(output, Does.Contain("token=tok-123"));
        }

        [Test]
        public void The_exit_code_comes_back_and_a_path_with_spaces_launches()
        {
            string exe = Install(Path.Combine(_root, "Program Files", "Bugsee CLI"));
            Run(exe, new[] { "x" }, out var code, new Dictionary<string, string> { ["BUGSEE_FAKE_EXIT"] = "7" });
            Assert.That(code, Is.EqualTo(7));
        }

        [Test]
        public void Path_lookup_finds_the_exe_by_a_bare_name_and_prefers_the_earlier_directory()
        {
            string first = Install(Path.Combine(_root, "one two"));
            Install(Path.Combine(_root, "three"));
            string path = "\"" + Path.Combine(_root, "one two") + "\";" + Path.Combine(_root, "three") + "\\;" + _root.ToUpperInvariant();

            var r = BugseeCliPolicy.Resolve(RealSettings(path), RealProbe());

            Assert.That(r.Found, Is.True, string.Join("; ", r.Notes));
            Assert.That(r.Path, Is.EqualTo(first).IgnoreCase);
            Assert.That(r.Source, Is.EqualTo(CliSource.Path));
            Assert.That(r.Version, Is.EqualTo("0.8.1"));
        }

        [TestCase("quoted")]
        [TestCase("no-extension")]
        [TestCase("lower-drive")]
        [TestCase("forward-slashes")]
        public void BUGSEE_CLI_PATH_forms_resolve_against_the_real_file_system(string form)
        {
            string exe = Install(Path.Combine(_root, "Bugsee Tools"));
            string value = form == "quoted" ? "\"" + exe + "\""
                : form == "no-extension" ? exe.Substring(0, exe.Length - 4)
                : form == "lower-drive" ? char.ToLowerInvariant(exe[0]) + exe.Substring(1)
                : exe.Replace('\\', '/');
            var s = RealSettings("");
            s.ExplicitPath = value;

            var r = BugseeCliPolicy.Resolve(s, RealProbe());

            Assert.That(r.Found, Is.True, string.Join("; ", r.Notes));
            Assert.That(r.Source, Is.EqualTo(CliSource.Explicit));
            Assert.That(Path.GetFullPath(r.Path), Is.EqualTo(exe).IgnoreCase);
            Assert.That(r.Version, Is.EqualTo("0.8.1"));
        }

        [Test]
        public void A_missing_explicit_path_falls_through_to_the_path_entry()
        {
            Install(Path.Combine(_root, "bin"));
            var s = RealSettings(Path.Combine(_root, "bin"));
            s.ExplicitPath = Path.Combine(_root, "nope", "bugsee-cli.exe");

            var r = BugseeCliPolicy.Resolve(s, RealProbe());

            Assert.That(r.Found, Is.True);
            Assert.That(r.Notes.Single(), Does.Contain("does not exist"));
        }

        [Test]
        public void A_cached_download_is_found_at_the_real_windows_cache_path_without_downloading()
        {
            string cache = Path.Combine(_root, "Local", "Bugsee", "cache", "cli");
            var s = RealSettings("");
            s.DownloadAllowed = true;
            s.CacheDirectories.Add(cache);
            string target = BugseeCliPolicy.CachedBinaryPath(s, cache, "0.8.1", "x86_64-pc-windows-msvc");
            Assert.That(target, Is.EqualTo(Path.Combine(cache, "0.8.1", "x86_64-pc-windows-msvc", "bugsee-cli.exe")));
            Install(Path.GetDirectoryName(target));

            var probe = RealProbe();
            probe.Fetch = u => throw new InvalidOperationException("must not download: " + u);
            var r = BugseeCliPolicy.Resolve(s, probe);

            Assert.That(r.Found, Is.True, string.Join("; ", r.Notes));
            Assert.That(r.Source, Is.EqualTo(CliSource.Cache));
            Assert.That(r.Path, Is.EqualTo(target).IgnoreCase);
        }

        [Test]
        public void Build_output_under_the_real_temp_folder_is_judged_relative_to_its_root()
        {
            // Windows' own temp is %LOCALAPPDATA%\Temp: a project built there must still be scanned (#124).
            string root = Path.Combine(Path.GetTempPath(), "My Game", "Build Output");
            string file = Path.Combine(root, "arm64-v8a", "libil2cpp.so");
            Assert.That(BuildPathExclusion.IsExcludedPath(file, root), Is.False);
            Assert.That(BuildPathExclusion.IsExcludedPath(Path.Combine(root, "Temp", "libil2cpp.so"), root), Is.True);
            Assert.That(BuildPathExclusion.IsExcludedPath(file.ToUpperInvariant(), root.ToLowerInvariant()), Is.False);
        }

        [Test]
        public void A_path_beyond_260_characters_can_be_written_and_found_by_a_recursive_scan()
        {
            // Records what this host does with long paths; the Editor's own handling stays on the manual checklist.
            string dir = Path.Combine(_root, string.Join("\\", Enumerable.Repeat(new string('d', 40), 7)));
            Assert.That(dir.Length, Is.GreaterThan(260));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "libil2cpp.so"), "x");
            Assert.That(Directory.GetFiles(_root, "libil2cpp.so", SearchOption.AllDirectories), Has.Length.EqualTo(1));
        }
    }
}
