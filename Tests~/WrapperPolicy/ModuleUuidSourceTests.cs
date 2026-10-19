using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The build must not write the module UUID into the project: the UUID of a build exists only after the player
    /// is built, so a file baked into the player is one build stale (see ImageUuidCacheTests for the runtime side).
    /// </summary>
    public class ModuleUuidSourceTests
    {
        static string RepoRoot()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

        [Test]
        public void Build_does_not_write_a_module_uuid_resource()
        {
            Assert.That(Read("Editor/BugseeBuildIdentity.cs"), Does.Not.Contain("Assets/Resources"));
            Assert.That(Read("Editor/BugseeBuildIdentity.cs"), Does.Not.Contain("WriteRuntimeModuleUuidFile"));
            Assert.That(Read("Editor/BugseeIl2CppLinemapUpload.cs"), Does.Not.Contain("WriteRuntimeModuleUuidFile"));
        }

        [Test]
        public void Runtime_does_not_read_a_module_uuid_resource()
        {
            Assert.That(Read("Runtime/Internal/ManagedExceptionPayload.cs"), Does.Not.Contain("Resources.Load"));
        }

        [Test]
        public void Sample_ignores_the_resource_an_older_package_wrote()
        {
            var ignore = Read("Samples~/BugseeMiniGame/.gitignore");
            Assert.That(ignore, Does.Contain("Assets/Resources/BugseeIl2CppModuleUuids.txt"));
        }
    }

    public class ImageUuidCacheTests
    {
        [Test]
        public void Probe_runs_once_after_it_found_a_uuid()
        {
            var calls = 0;
            var cache = new ImageUuidCache(() => { calls++; return "abc"; });
            Assert.That(cache.Get(), Is.EqualTo("abc"));
            Assert.That(cache.Get(), Is.EqualTo("abc"));
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void A_probe_that_found_nothing_is_tried_again()
        {
            var calls = 0;
            var cache = new ImageUuidCache(() => ++calls < 2 ? null : "abc");
            Assert.That(cache.Get(), Is.Null);
            Assert.That(cache.Get(), Is.EqualTo("abc"));
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void An_empty_answer_and_a_throwing_probe_count_as_nothing()
        {
            var calls = 0;
            var cache = new ImageUuidCache(() =>
            {
                calls++;
                if (calls == 1) return "";
                if (calls == 2) throw new InvalidOperationException();
                return "abc";
            });
            Assert.That(cache.Get(), Is.Null);
            Assert.That(cache.Get(), Is.Null);
            Assert.That(cache.Get(), Is.EqualTo("abc"));
        }

        [Test]
        public void A_probe_that_never_finds_anything_stops_after_max_attempts()
        {
            var calls = 0;
            var cache = new ImageUuidCache(() => { calls++; return null; }, 3);
            for (var i = 0; i < 10; i++) Assert.That(cache.Get(), Is.Null);
            Assert.That(calls, Is.EqualTo(3));
        }

        [Test]
        public void Concurrent_first_use_probes_once()
        {
            var calls = 0;
            var cache = new ImageUuidCache(() =>
            {
                System.Threading.Interlocked.Increment(ref calls);
                System.Threading.Thread.Sleep(50);
                return "abc";
            });
            var results = new string[8];
            var threads = new System.Threading.Thread[results.Length];
            for (var i = 0; i < threads.Length; i++)
            {
                var n = i;
                threads[i] = new System.Threading.Thread(() => results[n] = cache.Get());
                threads[i].Start();
            }
            foreach (var t in threads) t.Join();
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(results, Is.All.EqualTo("abc"));
        }

        [Test]
        public void Native_uuid_wins_over_the_running_image()
        {
            Assert.That(ModuleUuidChoice.Pick("native", "runtime"), Is.EqualTo("native"));
            Assert.That(ModuleUuidChoice.Pick(null, "runtime"), Is.EqualTo("runtime"));
            Assert.That(ModuleUuidChoice.Pick("", "runtime"), Is.EqualTo("runtime"));
        }

        [Test]
        public void Unknown_uuid_is_null_not_empty()
        {
            Assert.That(ModuleUuidChoice.Pick(null, null), Is.Null);
            Assert.That(ModuleUuidChoice.Pick("", ""), Is.Null);
        }
    }
}
