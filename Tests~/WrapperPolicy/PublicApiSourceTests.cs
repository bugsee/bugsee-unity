using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class PublicApiSourceTests
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

        static string ExtractNativeFunctionBody(string mm, string functionName)
        {
            var marker = "void " + functionName;
            int start = mm.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                marker = "char *" + functionName;
                start = mm.IndexOf(marker, StringComparison.Ordinal);
            }
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "Native export missing: " + functionName);
            int brace = mm.IndexOf('{', start);
            Assert.That(brace, Is.GreaterThanOrEqualTo(0));
            int depth = 0;
            for (int i = brace; i < mm.Length; i++)
            {
                if (mm[i] == '{') depth++;
                else if (mm[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return mm.Substring(start, i - start + 1);
                }
            }

            throw new InvalidOperationException("Could not extract native body for " + functionName);
        }

        [Test]
        public void Facade_exposes_public_api_methods()
        {
            string facade = File.ReadAllText(RepoFile("Runtime/Bugsee.cs"));
            Assert.That(facade, Does.Contain("DeleteCollectedDataOnDevice"));
            Assert.That(facade, Does.Contain("CreateReport"));
            Assert.That(facade, Does.Contain("AddBreadcrumb"));
            Assert.That(facade, Does.Contain("AddNetworkEvent"));
        }

        [Test]
        public void Unknown_breadcrumb_level_throws_with_breadcrumb_level_message()
        {
            var ex = Assert.Throws<ArgumentException>(() => BreadcrumbLevelMap.ParseOrThrow("verbose"));
            Assert.That(ex.Message, Does.Contain("breadcrumb level"));
        }

        [Test]
        public void Ios_delete_collected_data_body_uses_main_thread_hop()
        {
            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            string body = ExtractNativeFunctionBody(bridge, "_bugsee_delete_collected_data");
            Assert.That(body, Does.Contain("BugseeRunOnMain"));
            Assert.That(body, Does.Contain("deleteCollectedDataOnDevice"));
        }

        [Test]
        public void Ios_channel_exports_use_gChannel_in_callbacks()
        {
            string callbacks = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityCallbacks.mm"));
            string network = ExtractNativeFunctionBody(callbacks, "_bugsee_channel_network");
            Assert.That(network, Does.Contain("gChannel"));
            Assert.That(network, Does.Contain("addNetworkEvent:requiresFiltering:"));

            string breadcrumb = ExtractNativeFunctionBody(callbacks, "_bugsee_channel_breadcrumb");
            Assert.That(breadcrumb, Does.Contain("gChannel"));
            Assert.That(breadcrumb, Does.Contain("addBreadcrumb:"));

            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            Assert.That(bridge, Does.Not.Contain("BugseeInstallChannelCapture"));
            Assert.That(bridge, Does.Not.Contain("method_exchangeImplementations"));
            Assert.That(bridge, Does.Not.Contain("_bugsee_channel_network"));
            Assert.That(bridge, Does.Not.Contain("_bugsee_channel_breadcrumb"));
        }

        [Test]
        public void Ios_apply_open_report_clears_attachments_before_re_add()
        {
            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            string body = ExtractNativeFunctionBody(bridge, "BugseeBridgeApplyReportDict");
            Assert.That(body, Does.Contain("clearAttachments"));
            Assert.That(body, Does.Contain("addAttachmentWithData:name:mimeType:"));
            Assert.That(body, Does.Not.Contain("createAndAddAttachmentWithName"));
        }

        [Test]
        public void Editor_CreateReport_throws_NotSupportedException()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/Editor/EditorBridge.cs")),
                "public IReport CreateReport()");
            Assert.That(body, Does.Contain("NotSupportedException"));
        }

        [Test]
        public void Editor_AddBreadcrumb_unknown_level_throws_before_no_op()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/Editor/EditorBridge.cs")),
                "public void AddBreadcrumb(string category, string message, string levelName)");
            Assert.That(body, Does.Contain("BreadcrumbLevelMap.ParseOrThrow"));
            Assert.That(body, Does.Not.Contain("_bugsee_"));
            Assert.That(body, Does.Not.Contain("CallStatic"));
        }

        [Test]
        public void Editor_DeleteCollectedDataOnDevice_is_no_op()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/Editor/EditorBridge.cs")),
                "public void DeleteCollectedDataOnDevice()");
            Assert.That(body, Does.Not.Contain("_bugsee_"));
            Assert.That(body, Does.Not.Contain("CallStatic"));
        }

        [Test]
        public void Android_does_not_use_network_launch_buffer()
        {
            string android = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            Assert.That(android, Does.Not.Contain("NetworkEventLaunchBuffer"));
            Assert.That(android, Does.Not.Contain("_networkLaunchBuffer"));
        }

        [Test]
        public void Android_delete_collected_data_skips_when_launched()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs")),
                "public void DeleteCollectedDataOnDevice()");
            Assert.That(body, Does.Match(new Regex(@"if\s*\(\s*GetLaunched\s*\(\s*\)\s*\)\s*return")));
            Assert.That(body, Does.Contain("deleteCollectedDataOnDevice"));
        }

        [Test]
        public void Android_breadcrumb_bridge_passes_ToAndroid_mapping()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs")),
                "public void AddBreadcrumb(string category, string message, string levelName)");
            Assert.That(body, Does.Contain("BreadcrumbLevelMap.ParseOrThrow"));
            Assert.That(body, Does.Contain("BreadcrumbLevelMap.ToAndroid(level)"));
            Assert.That(BreadcrumbLevelMap.TryParse("debug", out var debugLevel), Is.True);
            Assert.That(BreadcrumbLevelMap.ToAndroid(debugLevel), Is.EqualTo(1));
            Assert.That(BreadcrumbLevelMap.TryParse("fatal", out var fatalLevel), Is.True);
            Assert.That(BreadcrumbLevelMap.ToAndroid(fatalLevel), Is.EqualTo(5));
        }

        [Test]
        public void Ios_network_buffer_wiring()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));

            string addNetwork = ExtractMethodBody(ios, "public void AddNetworkEvent(INetworkEvent networkEvent)");
            Assert.That(addNetwork, Does.Contain("_networkLaunchBuffer.Enqueue"));

            string notify = ExtractMethodBody(ios, "public void NotifyLifecycle(string eventType)");
            Assert.That(notify, Does.Contain("NetworkLaunchPhase.Launched"));
            Assert.That(notify, Does.Contain("NetworkLaunchPhase.Stopped"));

            string launch = ExtractMethodBody(ios, "public void Launch(string appToken, IDictionary<string, object> options)");
            Assert.That(launch, Does.Contain("SetPhase(NetworkLaunchPhase.BeforeLaunched)"));

            string stop = ExtractMethodBody(ios, "public void Stop(Action completion = null)");
            Assert.That(stop, Does.Contain("_bugsee_clear_wrapper_channel"));
            Assert.That(stop, Does.Not.Contain("_bugsee_clear_bridge_wrapper_channel"));
            Assert.That(stop, Does.Contain("SetPhase(NetworkLaunchPhase.Stopped)"));

            string facadeLifecycle = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Bugsee.cs")),
                "internal static void HandleNativeLifecycle(string eventType, object data)");
            Assert.That(facadeLifecycle, Does.Contain("Bridge.NotifyLifecycle"));
        }

        [Test]
        public void Ios_breadcrumb_bridge_passes_ToIos_mapping()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs")),
                "public void AddBreadcrumb(string category, string message, string levelName)");
            Assert.That(body, Does.Contain("BreadcrumbLevelMap.ParseOrThrow"));
            Assert.That(body, Does.Contain("BreadcrumbLevelMap.ToIos(level)"));
            Assert.That(BreadcrumbLevelMap.TryParse("debug", out var debugLevel), Is.True);
            Assert.That(BreadcrumbLevelMap.ToIos(debugLevel), Is.EqualTo(4));
            Assert.That(BreadcrumbLevelMap.TryParse("fatal", out var fatalLevel), Is.True);
            Assert.That(BreadcrumbLevelMap.ToIos(fatalLevel), Is.EqualTo(1));
        }

        [Test]
        public void Ios_create_report_guards_second_open_report()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs")),
                "public IReport CreateReport()");
            Assert.That(body, Does.Contain("a report is already open"));
        }

        [Test]
        public void Ios_submit_network_uses_ToNativeMapJson_for_headers()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs")),
                "void SubmitNetworkEventToChannel(INetworkEvent networkEvent)");
            Assert.That(body, Does.Contain("ToNativeMapJson"));
            Assert.That(body, Does.Contain("\"headers\""));
        }

        [Test]
        public void Ios_live_report_wraps_attachment_list()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));

            string attachments = ExtractMethodBody(ios, "public IReadOnlyList<IAttachment> Attachments");
            Assert.That(attachments, Does.Contain("IosLiveAttachment(iosAttachment, _owner)"));

            string create = ExtractMethodBody(ios, "public IAttachment CreateAndAddAttachment(string name)");
            Assert.That(create, Does.Contain("(IosAttachment)"));
        }
    }
}
