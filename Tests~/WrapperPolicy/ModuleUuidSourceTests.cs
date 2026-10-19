using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The managed payload's moduleUUID must come from the running image, never from a file the build writes: the
    /// UUID of a build is only known after the player is built, so a baked-in file is one build stale.
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
        public void Runtime_does_not_read_a_build_written_module_uuid_resource()
        {
            var payload = Read("Runtime/Internal/ManagedExceptionPayload.cs");
            Assert.That(payload, Does.Not.Contain("Resources.Load"));
            Assert.That(payload, Does.Not.Contain("BugseeIl2CppModuleUuids"));
            Assert.That(payload, Does.Contain("Il2CppNativeStack.RuntimeImageUuid"));
        }

        [Test]
        public void Runtime_reads_the_image_uuid_from_a_probe_exception_that_was_thrown()
        {
            var native = Read("Runtime/Internal/Il2CppNativeStack.cs");
            Assert.That(native, Does.Contain("RuntimeImageUuid"));
            Assert.That(native, Does.Contain("throw new InvalidOperationException(\"Bugsee image probe\")"));
        }

        [Test]
        public void Both_payload_paths_use_the_runtime_uuid_not_a_cached_csv()
        {
            var payload = Read("Runtime/Internal/ManagedExceptionPayload.cs");
            Assert.That(payload, Does.Not.Contain("_moduleUuid"));
            Assert.That(payload, Does.Not.Contain("ModuleUuidsCsv"));
        }

        [Test]
        public void Build_does_not_write_into_Assets_Resources()
        {
            var identity = Read("Editor/BugseeBuildIdentity.cs");
            var linemap = Read("Editor/BugseeIl2CppLinemapUpload.cs");
            Assert.That(identity, Does.Not.Contain("WriteRuntimeModuleUuidFile"));
            Assert.That(identity, Does.Not.Contain("Assets/Resources"));
            Assert.That(linemap, Does.Not.Contain("WriteRuntimeModuleUuidFile"));
        }

        [Test]
        public void Sample_does_not_track_the_stale_module_uuid_file()
        {
            var resources = Path.Combine(RepoRoot(), "Samples~/BugseeMiniGame/Assets/Resources");
            Assert.That(File.Exists(Path.Combine(resources, "BugseeIl2CppModuleUuids.txt")), Is.False);
        }
    }
}
