using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ChannelLogSourceTests
    {
        static string RepoFile(string relative)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return Path.Combine(dir, relative);
                var parent = Directory.GetParent(dir);
                dir = parent?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        [Test]
        public void Host_logs_use_the_channel_and_public_log_does_not()
        {
            string java = File.ReadAllText(RepoFile("Plugins/Android/UnityWrapperProvider.java"));
            Assert.That(java, Does.Contain("channel = value"));
            Assert.That(java, Does.Contain("channelLog"));
            Assert.That(java, Does.Contain("current.log"));

            string forwarder = File.ReadAllText(RepoFile("Runtime/Internal/HostLogForwarder.cs"));
            Assert.That(forwarder, Does.Contain("logMessageReceived"));
            Assert.That(forwarder, Does.Not.Contain("WrapperLogSourcePolicy"));

            string android = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            Assert.That(android, Does.Contain("WrapperLogSourcePolicy.Resolve(null)"));
            Assert.That(android, Does.Contain("\"channelLog\""));
            Assert.That(android, Does.Contain("\"log\""));

            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));
            Assert.That(ios, Does.Contain("WrapperLogSourcePolicy.Resolve(null)"));
            Assert.That(ios, Does.Contain("_bugsee_channel_log"));
            Assert.That(ios, Does.Contain("_bugsee_log"));

            string callbacks = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityCallbacks.mm"));
            Assert.That(callbacks, Does.Contain("onWrapperChannelAvailable:"));
            Assert.That(callbacks, Does.Contain("gChannel = channel"));
            Assert.That(callbacks, Does.Contain("logWithTag:nil"));
        }
    }
}
