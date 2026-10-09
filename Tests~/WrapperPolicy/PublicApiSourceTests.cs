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
            Assert.That(facade, Does.Contain("Discard"));
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
            Assert.That(body, Does.Contain("deleteCollectedDataOnDevice:YES"));
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
        public void Ios_upload_managed_report_uses_beta5_create_and_apply_once()
        {
            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            string upload = ExtractNativeFunctionBody(bridge, "_bugsee_upload_managed_report");
            Assert.That(upload, Does.Contain("createReportWithCompletion:"));
            Assert.That(upload, Does.Contain("uploadReport:"));
            Assert.That(upload, Does.Contain("BugseeExtendedReport"));
            Assert.That(upload, Does.Contain("uploadFence"));
            Assert.That(bridge, Does.Contain("gManagedReportUploadFence"));
            Assert.That(upload, Does.Not.Contain("gActiveManagedReportUploadId"));
            Assert.That(bridge, Does.Not.Contain("gCancelManagedReportUpload"));

            string apply = ExtractNativeFunctionBody(bridge, "BugseeBridgeApplyReportDict");
            Assert.That(apply, Does.Contain("[report clearAttachments]"));
            Assert.That(apply, Does.Contain("addAttachmentWithData:data name:name"));
            Assert.That(apply, Does.Contain("BugseeBridgeSetAttachmentFileNameIfNeeded"));
            Assert.That(apply, Does.Not.Contain("createAndAddAttachmentWithName"));
            Assert.That(bridge, Does.Not.Contain("_bugsee_apply_open_report"));
            Assert.That(bridge, Does.Not.Contain("[Bugsee createReport]"));
        }

        [Test]
        public void Ios_upload_report_clears_slot_before_native_upload()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs")),
                "public void UploadReport(IReport report)");
            Assert.That(body, Does.Contain("ToResultJson()"));
            Assert.That(body, Does.Contain("_openReport = null"));
            Assert.That(body, Does.Contain("_openReportHandle = null"));
            Assert.That(body, Does.Contain("_bugsee_upload_managed_report"));
            Assert.That(body, Does.Not.Contain("a report upload is already in progress"));
        }

        [Test]
        public void Ios_channel_breadcrumb_uses_exchange_factory()
        {
            string callbacks = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityCallbacks.mm"));
            string body = ExtractNativeFunctionBody(callbacks, "_bugsee_channel_breadcrumb");
            Assert.That(body, Does.Contain("createBreadcrumbWithTimestamp:"));
            Assert.That(body, Does.Not.Contain("BugseeBreadcrumb"));
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
        public void Android_delete_collected_data_stops_then_deletes()
        {
            string android = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            string body = ExtractMethodBody(android, "public void DeleteCollectedDataOnDevice()");
            Assert.That(body, Does.Contain("Stop(InvokeDeleteCollectedDataOnDevice)"));
            string invoke = ExtractMethodBody(android, "void InvokeDeleteCollectedDataOnDevice()");
            Assert.That(invoke, Does.Contain("deleteCollectedDataOnDevice"));
            Assert.That(invoke, Does.Contain("true"));
            Assert.That(invoke, Does.Contain("BooleanCallback1Proxy"));

            string callback = File.ReadAllText(RepoFile("Runtime/Platform/Android/Proxies/CallbackProxies.cs"));
            Assert.That(callback, Does.Contain("BooleanCallback1Proxy"));
            Assert.That(callback, Does.Contain("com.bugsee.library.contracts.common.Callback1"));
            Assert.That(callback, Does.Contain("public void run(AndroidJavaObject value)"));
        }

        [Test]
        public void Android_create_report_uses_listener_and_managed_snapshot()
        {
            string android = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            string create = ExtractMethodBody(android, "public IReport CreateReport()");
            Assert.That(create, Does.Contain("AndroidManagedReport"));
            Assert.That(create, Does.Not.Contain("CallStatic<AndroidJavaObject>(\"createReport\")"));
            Assert.That(create, Does.Contain("a report is already open"));

            string upload = ExtractMethodBody(android, "public void UploadReport(IReport report)");
            Assert.That(upload, Does.Contain("ReportCreationListenerProxy"));
            Assert.That(upload, Does.Contain("createReport"));
        }

        [Test]
        public void Android_breadcrumb_uses_fromValue_and_six_arg_factory()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs")),
                "public void AddBreadcrumb(string category, string message, string levelName)");
            Assert.That(body, Does.Contain("fromValue"));
            Assert.That(body, Does.Not.Contain("fromRawValue"));
            Assert.That(body, Does.Contain("\"manual\""));
            Assert.That(body, Does.Contain("java.util.HashMap"));
        }

        [Test]
        public void Android_apply_to_overlays_only_set_fields()
        {
            string apply = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidReport.cs")),
                "internal void ApplyTo(AndroidJavaObject javaReport)");
            Assert.That(apply, Does.Contain("if (Summary != null)"));
            Assert.That(apply, Does.Contain("if (_emailAssigned)"));
            Assert.That(apply, Does.Contain("if (_attributesDirty)"));
            Assert.That(apply, Does.Contain("if (_attachmentsDirty)"));
            Assert.That(apply, Does.Contain("report.ClearAttachments()"));
            Assert.That(apply, Does.Not.Contain("Email ?? \"\""));
            Assert.That(apply, Does.Not.Contain("Summary ?? \"\""));
        }

        [Test]
        public void Android_report_upload_fenced_across_stop_and_delete()
        {
            string callbacks = File.ReadAllText(RepoFile("Runtime/Platform/Android/Proxies/CallbackProxies.cs"));
            string onCreated = ExtractMethodBody(callbacks, "public void onCreated(AndroidJavaObject report)");
            Assert.That(onCreated, Does.Contain("AndroidManagedReportUploadFence.IsActive"));
            Assert.That(onCreated, Does.Contain("CallStatic(\"upload\""));

            string android = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            string stop = ExtractMethodBody(android, "public void Stop(Action completion = null)");
            Assert.That(stop, Does.Contain("AndroidManagedReportUploadFence.Invalidate"));
            Assert.That(stop, Does.Contain("_openReport = null"));
            string delete = ExtractMethodBody(android, "public void DeleteCollectedDataOnDevice()");
            Assert.That(delete, Does.Contain("AndroidManagedReportUploadFence.Invalidate"));
            Assert.That(delete, Does.Contain("_openReport = null"));
        }

        [Test]
        public void Ios_apply_report_dict_skips_unset_severity_zero()
        {
            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            string body = ExtractNativeFunctionBody(bridge, "BugseeBridgeApplyReportDict");
            Assert.That(body, Does.Contain("sevVal != 0"));
            Assert.That(body, Does.Not.Match("report\\.severity\\s*=.*integerValue\\]"));
        }

        [Test]
        public void Ios_to_result_json_omits_unset_severity_zero()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosReport.cs")),
                "public string ToResultJson()");
            Assert.That(body, Does.Contain("if (_dto.severity != 0)"));
        }

        [Test]
        public void Ios_delete_chains_stop_completion_before_delete_when_instance_exists()
        {
            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            string body = ExtractNativeFunctionBody(bridge, "_bugsee_delete_collected_data");
            Assert.That(body, Does.Contain("[Bugsee stop:"));
            Assert.That(body, Does.Contain("deleteCollectedDataOnDevice:YES"));
            Assert.That(body, Does.Contain("sharedInstance"));
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
            Assert.That(body, Does.Not.Contain("_bugsee_create_report"));
        }

        [Test]
        public void Ios_discard_report_clears_managed_slot()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs")),
                "public void DiscardReport(IReport report)");
            Assert.That(body, Does.Contain("_openReport = null"));
            Assert.That(body, Does.Contain("_reportUploadGeneration++"));
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
        public void Ios_json_encoding_escapes_controls_in_strings()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosReport.cs"));
            string body = ExtractMethodBody(ios, "internal static void AppendQuoted(StringBuilder sb, string value)");
            Assert.That(body, Does.Contain("\\t"));
            Assert.That(body, Does.Contain("\\b"));
            Assert.That(body, Does.Contain("\\f"));
            Assert.That(body, Does.Contain("c < '\\u0020'"));
        }

        [Test]
        public void Ios_to_result_json_omits_untouched_labels_and_attachments()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosReport.cs")),
                "public string ToResultJson()");
            Assert.That(body, Does.Contain("if (_labelsDirty)"));
            Assert.That(body, Does.Contain("if (_attachmentsDirty)"));
        }

        [Test]
        public void Ios_upload_skips_upload_when_json_deserializes_invalid()
        {
            string upload = ExtractMethodBody(
                File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm")),
                "_bugsee_upload_managed_report");
            int uploadIdx = upload.IndexOf("uploadReport:", StringComparison.Ordinal);
            int dictGuard = upload.IndexOf("isKindOfClass:[NSDictionary class]", StringComparison.Ordinal);
            Assert.That(dictGuard, Is.GreaterThanOrEqualTo(0));
            Assert.That(uploadIdx, Is.GreaterThan(dictGuard));
            Assert.That(upload, Does.Contain("callback(0, uploadId)"));
        }

        [Test]
        public void Ios_live_report_wraps_attachment_list()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));

            string attachments = ExtractMethodBody(ios, "public IReadOnlyList<IAttachment> Attachments");
            Assert.That(attachments, Does.Contain("IosLiveAttachment(iosAttachment)"));
            Assert.That(ios, Does.Not.Contain("ApplyOpenReportToNative"));

            string create = ExtractMethodBody(ios, "public IAttachment CreateAndAddAttachment(string name)");
            Assert.That(create, Does.Contain("(IosAttachment)"));
        }
    }
}
