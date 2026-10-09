using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class SecureRectSourceTests
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

        static string ExtractMethodBody(string source, string methodSignature)
        {
            int start = source.IndexOf(methodSignature, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "Method missing: " + methodSignature);
            int brace = source.IndexOf('{', start);
            Assert.That(brace, Is.GreaterThanOrEqualTo(0));
            int depth = 0;
            for (int i = brace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return source.Substring(start, i - start + 1);
                }
            }

            throw new InvalidOperationException("Could not extract method body for " + methodSignature);
        }

        static string ExtractNativeFunctionSignature(string mm, string functionName)
        {
            var marker = "void " + functionName;
            int start = mm.IndexOf(marker, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "Native export missing: " + functionName);
            int brace = mm.IndexOf('{', start);
            Assert.That(brace, Is.GreaterThanOrEqualTo(0));
            return mm.Substring(start, brace - start).Trim();
        }

        [Test]
        public void AndroidBridge_does_not_call_addSecureRectangle()
        {
            string android = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            Assert.That(android, Does.Not.Contain("addSecureRectangle"));
        }

        [Test]
        public void Ios_bridge_does_not_push_addSecureRectangle_to_sdk()
        {
            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            Assert.That(bridge, Does.Not.Contain("addSecureRectangle:"));
        }

        [Test]
        public void Facade_add_remove_and_clear_push_after_registry_updates()
        {
            string facade = File.ReadAllText(RepoFile("Runtime/Bugsee.cs"));
            string add = ExtractMethodBody(facade, "public static void AddSecureRectangle(RectInt pixelRect)");
            Assert.That(add, Does.Contain("_manualSecureRectOwners.Add"));
            Assert.That(add, Does.Contain("SecureRectRegistry.Instance"));
            Assert.That(add, Does.Contain("PushSecureBufferForDisplay"));

            string remove = ExtractMethodBody(facade, "public static void RemoveSecureRectangle(RectInt pixelRect)");
            Assert.That(remove, Does.Contain("_manualSecureRectOwners.Remove"));
            Assert.That(remove, Does.Contain("PushSecureBufferForDisplay"));

            string removeAll = ExtractMethodBody(facade, "public static void RemoveAllSecureRectangles()");
            Assert.That(removeAll, Does.Contain("_manualSecureRectOwners.RemoveAll"));
            Assert.That(removeAll, Does.Contain("PushSecureBufferForDisplay"));

            string push = ExtractMethodBody(facade, "static void PushSecureBufferForDisplay(int displayId)");
            Assert.That(push, Does.Contain("SecureRectRegistry.Instance.Snapshot"));
            Assert.That(push, Does.Contain("Bridge.SetSecureBuffer"));
        }

        [Test]
        public void Ios_bridge_set_secure_buffer_passes_packed_length()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));
            string body = ExtractMethodBody(ios, "public void SetSecureBuffer(int display, int[] packed)");
            Assert.That(body, Does.Contain("packed.Length"));
            Assert.That(body, Does.Contain("_bugsee_set_secure_buffer"));

            Assert.That(
                ios,
                Does.Match(@"extern\s+void\s+_bugsee_set_secure_buffer\s*\(\s*int\s+display\s*,\s*int\s*\[\]\s+packed\s*,\s*int\s+packedLength\s*\)"));
        }

        [Test]
        public void Ios_callbacks_set_secure_buffer_accepts_explicit_length()
        {
            string callbacks = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityCallbacks.mm"));
            string signature = ExtractNativeFunctionSignature(callbacks, "_bugsee_set_secure_buffer");
            Assert.That(signature, Does.Contain("int display"));
            Assert.That(signature, Does.Contain("int *packed"));
            Assert.That(signature, Does.Contain("int packedLength"));
            Assert.That(signature, Does.Not.Contain("packed[1]"));
        }

        [Test]
        public void Android_wrapper_setSecureBuffer_clones_and_getSecureRectangles_reads_cache()
        {
            string java = File.ReadAllText(RepoFile("Plugins/Android/UnityWrapperProvider.java"));
            string setBuffer = ExtractMethodBody(java, "public static void setSecureBuffer(int display, int[] packed)");
            Assert.That(setBuffer, Does.Contain("packed.clone()"));
            Assert.That(setBuffer, Does.Contain("secureBuffers.put"));

            string getRects = ExtractMethodBody(java, "public int[] getSecureRectangles(int display)");
            Assert.That(getRects, Does.Contain("secureBuffers.get"));
            Assert.That(getRects, Does.Contain("return new int[] { 1, 0 }"));
        }

        [Test]
        public void Proguard_keeps_setSecureBuffer()
        {
            string rules = File.ReadAllText(RepoFile("Plugins/Android/bugsee-unity-wrapper.proguard"));
            Assert.That(rules, Does.Contain("setSecureBuffer"));
        }
    }
}
