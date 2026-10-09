using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ChannelNetworkSourceTests
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

        static string ChannelAddNetworkMethodBody(string java)
        {
            const string marker = "public static void channelAddNetwork";
            int start = java.IndexOf(marker, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "channelAddNetwork method missing");
            int brace = java.IndexOf('{', start);
            Assert.That(brace, Is.GreaterThanOrEqualTo(0));
            int depth = 0;
            for (int i = brace; i < java.Length; i++)
            {
                if (java[i] == '{') depth++;
                else if (java[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return java.Substring(start, i - start + 1);
                }
            }

            throw new InvalidOperationException("Could not extract channelAddNetwork method body.");
        }

        [Test]
        public void Network_events_use_the_channel_not_public_addNetworkEvent()
        {
            string java = File.ReadAllText(RepoFile("Plugins/Android/UnityWrapperProvider.java"));
            string body = ChannelAddNetworkMethodBody(java);

            Assert.That(body, Does.Contain("channelAddNetwork"));
            Assert.That(body, Does.Contain("addNetworkEvent"));
            Assert.That(body, Does.Contain("addNetworkEvent(event, true)"));
            Assert.That(body, Does.Not.Contain("Bugsee.addNetworkEvent"));

            Assert.That(body, Does.Match(new Regex(@"current\s*=\s*channel")));
            Assert.That(body, Does.Match(new Regex(@"if\s*\(\s*current\s*==\s*null\s*\|\|\s*event\s*==\s*null\s*\)\s*return")));
            Assert.That(body, Does.Match(new Regex(@"current\.addNetworkEvent\s*\(\s*event\s*,\s*true\s*\)")));

            Assert.That(java, Does.Contain("channelLog"));
            Assert.That(body, Does.Not.Contain("channelLog"));
        }

        [Test]
        public void Proguard_keeps_jni_channelAddNetwork_entry_point()
        {
            string proguard = File.ReadAllText(RepoFile("Plugins/Android/bugsee-unity-wrapper.proguard"));
            Assert.That(proguard, Does.Contain("channelAddNetwork(com.bugsee.library.contracts.exchange.NetworkEvent)"));
        }
    }
}
