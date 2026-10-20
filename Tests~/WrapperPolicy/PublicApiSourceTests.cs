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
            Assert.That(body, Does.Contain("[Bugsee deleteCollectedDataOnDevice:done]"));
            // A delete is never skipped (request order, as on Android): no generation check in the native part.
            Assert.That(body, Does.Not.Contain("shouldRun"));
            Assert.That(body, Does.Not.Contain("capturedGeneration"));
            // Only a running SDK is stopped, so the wipe on an already stopped one runs before a launch that follows.
            Assert.That(body, Does.Contain("status != BugseeStatusStopped"));
            // ...and a stop already running (Stopping) is waited for, not stopped a second time.
            Assert.That(body, Does.Contain("status == BugseeStatusStopping"));
        }

        [Test]
        public void Ios_launch_passes_the_started_answer_back_by_token()
        {
            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            string launch = ExtractNativeFunctionBody(bridge, "_bugsee_launch");
            Assert.That(launch, Does.Contain("launchWithToken:BugseeNSString(appToken) andOptions:dict"));
            Assert.That(launch, Does.Contain("started:BugseeStartedBlockFor(startedToken, started)"));
            string relaunch = ExtractNativeFunctionBody(bridge, "_bugsee_relaunch");
            Assert.That(relaunch, Does.Contain("relaunchWithDictionaryOptions:dict"));
            Assert.That(relaunch, Does.Contain("started:BugseeStartedBlockFor(startedToken, started)"));
        }

        [Test]
        public void Facade_launch_options_are_null_until_launched_and_read_only()
        {
            string facade = File.ReadAllText(RepoFile("Runtime/Bugsee.cs"));
            string body = ExtractMethodBody(facade, "public static IReadOnlyDictionary<string, object> GetLaunchOptions()");
            Assert.That(body, Does.Contain("LaunchOptionsWire.Normalize(Bridge.GetLaunched() ? Bridge.GetLaunchOptions() : null)"));
            Assert.That(body, Does.Contain("new ReadOnlyDictionary<string, object>"));
        }

        [Test]
        public void Facade_notify_requires_a_title_and_cleans_the_fields_before_the_bridge()
        {
            string facade = File.ReadAllText(RepoFile("Runtime/Bugsee.cs"));
            string body = ExtractMethodBody(facade, "public static void Notify(");
            Assert.That(body, Does.Contain("NotifyPolicy.HasTitle(title)"));
            Assert.That(body, Does.Contain("Bridge.Notify(title, body, severity, NotifyPolicy.CleanFields(fields), urgent)"));
        }

        [Test]
        public void Notify_reaches_the_native_five_argument_overloads()
        {
            string android = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            Assert.That(ExtractMethodBody(android, "public void Notify("), Does.Contain("_bugsee.CallStatic(\"notify\", title, NotifyPolicy.Body(body), sev, javaFields, urgent)"));
            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            string native = ExtractNativeFunctionBody(bridge, "_bugsee_notify");
            Assert.That(native, Does.Contain("notifyWithTitle:titleStr"));
            Assert.That(native, Does.Contain("urgent:urgent != 0"));
        }

        [Test]
        public void Facade_async_lifecycle_uses_the_result_bridge_and_never_blocks()
        {
            string facade = File.ReadAllText(RepoFile("Runtime/Bugsee.cs"));
            string launch = ExtractMethodBody(facade, "public static Task<bool> LaunchAsync(string appToken, Dictionary<string, object> options = null)");
            Assert.That(launch, Does.Contain("bridge.Launch(appToken, options, accepted =>"));
            Assert.That(launch, Does.Contain("try { AfterLaunchResultCounted(accepted, startPusher, options); }"));
            Assert.That(launch, Does.Contain("finally { result.TrySetResult(accepted); }"));
            string relaunch = ExtractMethodBody(facade, "public static Task<bool> RelaunchAsync(Dictionary<string, object> options = null)");
            Assert.That(relaunch, Does.Contain("bridge.Relaunch(options, accepted =>"));
            Assert.That(relaunch, Does.Contain("try { AfterLaunchResultCounted(accepted, startPusher, options); }"));
            Assert.That(relaunch, Does.Contain("finally { result.TrySetResult(accepted); }"));
            string stop = ExtractMethodBody(facade, "public static Task StopAsync()");
            Assert.That(stop, Does.Contain("bridge.Stop("));
            Assert.That(stop, Does.Contain("TimeoutException"));
            foreach (var body in new[] { launch, relaunch, stop })
            {
                Assert.That(body, Does.Not.Contain(".Wait("));
                Assert.That(body, Does.Not.Contain(".Result"));
            }
            // The existing signatures stay.
            Assert.That(facade, Does.Contain("public static void Launch(string appToken, Dictionary<string, object> options = null)"));
            Assert.That(facade, Does.Contain("public static void Relaunch(Dictionary<string, object> options = null)"));
            Assert.That(facade, Does.Contain("public static void Stop(Action completion = null)"));
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

            string apply = ExtractNativeFunctionBody(bridge, "BugseeBridgeApplyExtendedReportDict");
            Assert.That(apply, Does.Contain("[report clearAllAttachments]"));
            Assert.That(apply, Does.Contain("att[@\"path\"]"));
            Assert.That(apply, Does.Contain("dataWithContentsOfFile:"));
            Assert.That(apply, Does.Contain("[BugseeAttachment attachmentWithName:name filename:fileName data:data]"));
            Assert.That(apply, Does.Contain("[report setAttachment:attachment]"));
            Assert.That(apply, Does.Not.Contain("createAndAddAttachmentWithName"));
            Assert.That(bridge, Does.Not.Contain("_bugsee_apply_open_report"));
            Assert.That(bridge, Does.Not.Contain("[Bugsee createReport]"));
        }

        [Test]
        public void Ios_cancel_invalidates_native_uploads_before_deleting_snapshot_files()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));
            string cancel = ExtractMethodBody(ios, "void CancelManagedReportUpload()");
            int invalidate = cancel.IndexOf("_bugsee_invalidate_managed_report_uploads();", StringComparison.Ordinal);
            int release = cancel.IndexOf("ReleaseAllUploadSnapshotReports();", StringComparison.Ordinal);
            Assert.That(invalidate, Is.GreaterThanOrEqualTo(0));
            Assert.That(release, Is.GreaterThan(invalidate));
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
        public void Editor_SetAttribute_validates_like_the_device_bridges()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/Editor/EditorBridge.cs")),
                "public void SetAttribute(string key, object value)");
            Assert.That(body, Does.Contain("AttributePolicy.Evaluate(key, value)"));
            Assert.That(body, Does.Contain("throw new ArgumentException(decision.Error, nameof(value))"));
        }

        [Test]
        public void Removed_dead_ios_exports_stay_removed()
        {
            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            string cs = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));
            foreach (string dead in new[]
            {
                "_bugsee_add_secure_rect", "_bugsee_remove_secure_rect", "_bugsee_remove_all_secure_rects",
                "_bugsee_get_device_id", "_bugsee_cancel_managed_report_upload",
            })
            {
                Assert.That(bridge, Does.Not.Contain(dead), dead);
                Assert.That(cs, Does.Not.Contain(dead), dead);
            }
            Assert.That(bridge, Does.Not.Contain("BUGSEE_IOS_LEGACY_EXCEPTION_API"));
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
            string body = ExtractMethodBody(android, "internal void DeleteCollectedDataOnDevice(Action<bool> completion)");
            Assert.That(body, Does.Contain("Lifecycle.DeleteCollectedData(completion)"));
            Assert.That(body, Does.Contain("MainThreadDispatcher.Run("));

            string callback = File.ReadAllText(RepoFile("Runtime/Platform/Android/Proxies/CallbackProxies.cs"));
            Assert.That(callback, Does.Contain("BooleanCallback1Proxy"));
            Assert.That(callback, Does.Contain("com.bugsee.library.contracts.common.Callback1"));
            // Unity passes Callback1<Boolean>'s argument already unboxed (System.Boolean), so a
            // run(AndroidJavaObject) overload is never matched on device ("No such proxy method").
            Assert.That(callback, Does.Contain("public void run(object value)"));
            Assert.That(callback, Does.Not.Contain("public void run(AndroidJavaObject value)"));

            // One onEvent overload: with two, Unity's JNI cannot choose when the data is null ("Ambiguous overloads").
            Assert.That(callback, Does.Contain("public void onEvent(string eventType, object data)"));
            Assert.That(callback, Does.Not.Contain("public void onEvent(string eventType, string data)"));
            Assert.That(callback, Does.Not.Contain("public void onEvent(string eventType, AndroidJavaObject data)"));

            // Launch/Relaunch/Stop/Delete all go through AndroidLifecycleMachine on the main thread; the ordering,
            // stale-result and deadline rules are covered by AndroidLaunchStatePolicyTests.
            string bridge = android;
            Assert.That(ExtractMethodBody(bridge, "internal void Launch(string appToken, IDictionary<string, object> options, Action<bool> completion)"),
                Does.Contain("Lifecycle.Launch(appToken, options, completion);"));
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));
            string iosLaunch = ExtractMethodBody(ios, "void Launch(string appToken, IDictionary<string, object> options, Action<bool> started)");
            Assert.That(iosLaunch.IndexOf("RunSyncLifecycle", StringComparison.Ordinal),
                Is.LessThan(iosLaunch.IndexOf("ManagedExceptionPayload.EnsureBuildIdentity()", StringComparison.Ordinal)),
                "iOS Launch must read the build identity inside the main-thread hand-off");
            // The identity/wrapper setup reads main-thread-only Unity APIs, so it must run inside the main-thread hop.
            string launchBody = ExtractMethodBody(bridge, "internal void Launch(string appToken, IDictionary<string, object> options, Action<bool> completion)");
            Assert.That(launchBody.IndexOf("MainThreadDispatcher.Run", StringComparison.Ordinal),
                Is.LessThan(launchBody.IndexOf("ManagedExceptionPayload.EnsureBuildIdentity()", StringComparison.Ordinal)));
            Assert.That(ExtractMethodBody(bridge, "internal void Relaunch(IDictionary<string, object> options, Action<bool> completion)"),
                Does.Contain("Lifecycle.Relaunch(options, completion)"));
            Assert.That(ExtractMethodBody(bridge, "void Stop(Action completion, Action<bool> finished)"),
                Does.Contain("Lifecycle.Stop(completion, finished)"));
            Assert.That(bridge, Does.Contain("class AndroidBridge : IBugseeNativeBridge, ILifecycleResultBridge"));
            Assert.That(ExtractMethodBody(bridge, "void ILifecycleResultBridge.Stop(Action<bool> finished)"),
                Does.Contain("Stop(null, finished)"));
            foreach (var call in new[] { "\"launch\"", "\"relaunch\"", "\"stop\"", "\"deleteCollectedDataOnDevice\"" })
                Assert.That(System.Text.RegularExpressions.Regex.Matches(bridge, "CallStatic\\(" + System.Text.RegularExpressions.Regex.Escape(call)).Count,
                    Is.EqualTo(1), call + " is only called by the lifecycle machine's native side");
            // Every SDK lifecycle callback reaches the machine on the main thread.
            Assert.That(System.Text.RegularExpressions.Regex.Matches(bridge, "new BooleanCallback1Proxy\\(OnMain\\(done\\)\\)").Count, Is.EqualTo(3));
            Assert.That(bridge, Does.Contain("new RunnableProxy(() => MainThreadDispatcher.Run(done))"));
            Assert.That(bridge, Does.Contain("_bridge.Lifecycle.Tick()"));
            Assert.That(System.Text.RegularExpressions.Regex.IsMatch(bridge,
                @"""deleteCollectedDataOnDevice"",\s*true,\s*true,"), Is.True, "includingIntermediate and includingBundles");
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
            Assert.That(apply, Does.Contain("if (_attributesClearAll)"));
            Assert.That(apply, Does.Contain("else if (_attributesOverlayDirty)"));
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
            string body = ExtractNativeFunctionBody(bridge, "BugseeBridgeApplyExtendedReportDict");
            Assert.That(body, Does.Contain("sevVal != 0"));
            Assert.That(body, Does.Not.Match("report\\.severity\\s*=.*integerValue\\]"));
        }

        [Test]
        public void Ios_apply_report_dict_overlays_attributes_without_default_clear()
        {
            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            string body = ExtractNativeFunctionBody(bridge, "BugseeBridgeApplyExtendedReportDict");
            Assert.That(body, Does.Contain("attributeRemovals"));
            Assert.That(body, Does.Contain("attributesReplaceAll"));
            Assert.That(body, Does.Contain("shouldReplaceAll"));
            Assert.That(body, Does.Contain("setAttribute:(NSString *)key withValue:obj"));
            Assert.That(body, Does.Contain("clearAllAttachments"));
        }

        [Test]
        public void Ios_apply_report_dict_applies_attachment_mime_type()
        {
            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            string body = ExtractNativeFunctionBody(bridge, "BugseeBridgeApplyExtendedReportDict");
            Assert.That(body, Does.Contain("att[@\"mimeType\"]"));
            Assert.That(body, Does.Contain("BugseeBridgeSetAttachmentMimeTypeIfNeeded"));
        }

        [Test]
        public void Ios_channel_breadcrumb_uses_enqueue_timestamp()
        {
            string callbacks = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityCallbacks.mm"));
            string body = ExtractNativeFunctionBody(callbacks, "_bugsee_channel_breadcrumb");
            Assert.That(body, Does.Contain("timestampUnixSeconds"));
            Assert.That(body, Does.Contain("createBreadcrumbWithTimestamp:timestamp"));
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
        public void Ios_to_result_json_emits_attribute_overlay_metadata()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosReport.cs")),
                "public string ToResultJson()");
            Assert.That(body, Does.Contain("attributeRemovals"));
            Assert.That(body, Does.Contain("attributesReplaceAll"));
        }

        [Test]
        public void Ios_upload_report_warns_when_create_report_email_set()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs")),
                "public void UploadReport(IReport report)");
            Assert.That(body, Does.Contain("Email on CreateReport is not supported on iOS managed upload"));
        }

        [Test]
        public void Ios_handler_apply_appends_attachments_unless_replace_all_flag()
        {
            string callbacks = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityCallbacks.mm"));
            string body = ExtractNativeFunctionBody(callbacks, "BugseeUnityApplyReportDict");
            Assert.That(body, Does.Contain("attachmentsReplaceAll"));
            Assert.That(body, Does.Contain("shouldReplaceAttachments"));
            Assert.That(body, Does.Contain("if (shouldReplaceAttachments)"));
        }

        [Test]
        public void Ios_to_result_json_emits_attachments_replace_all_only_after_clear()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosReport.cs")),
                "public string ToResultJson()");
            Assert.That(body, Does.Contain("_attachmentsReplaceAll"));
            Assert.That(body, Does.Contain("attachmentsReplaceAll"));
        }

        [Test]
        public void Ios_create_report_apply_still_clears_attachments()
        {
            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            string body = ExtractNativeFunctionBody(bridge, "BugseeBridgeApplyExtendedReportDict");
            Assert.That(body, Does.Contain("[report clearAllAttachments]"));
            Assert.That(body, Does.Not.Contain("attachmentsReplaceAll"));
        }

        [Test]
        public void Ios_delete_chains_stop_completion_before_delete_when_instance_exists()
        {
            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            string body = ExtractNativeFunctionBody(bridge, "_bugsee_delete_collected_data");
            Assert.That(body, Does.Contain("[Bugsee stop:"));
            Assert.That(body, Does.Contain("[Bugsee deleteCollectedDataOnDevice:done]"));
            Assert.That(body, Does.Contain("sharedInstance"));

            string iosDelete = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs")),
                "public void DeleteCollectedDataOnDevice()");
            Assert.That(iosDelete, Does.Not.Contain("CaptureForPendingDelete"));
            Assert.That(iosDelete, Does.Not.Contain("RunIfCurrent"));

            // The wipe is a native operation in flight that holds a following launch until it is reported.
            string beginDelete = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs")),
                "static void BeginNativeDelete()");
            Assert.That(beginDelete, Does.Contain("holdsLaunch: true"));
            Assert.That(beginDelete, Does.Contain("if (StopWaiters.TryCoalesceWipe()) return;"));
            Assert.That(iosDelete, Does.Contain("if (StopWaiters.TryCoalesceWipe()) return;"));
            Assert.That(beginDelete, Does.Contain("GiveUpIfStopNotReported(token)"));
            Assert.That(beginDelete, Does.Contain("_bugsee_delete_collected_data(token, OnNativeDeleted)"));
            Assert.That(body, Does.Contain("completion(token, success ? 1 : 0)"));
            // The timeout path reports "not wiped" instead of ordinary completion, and managed code warns about it
            // but still releases the deferred launch.
            Assert.That(body, Does.Contain("completion(token, 0)"));
            string deleted = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs")),
                "static void OnNativeDeleted(int token, int wiped)");
            Assert.That(deleted, Does.Contain("if (wiped == 0)"));
            Assert.That(deleted, Does.Contain("Debug.LogWarning"));
            Assert.That(deleted, Does.Contain("CompleteStop(token)"));
            Assert.That(iosDelete, Does.Contain("StopNativeThen(BeginNativeDelete, first: true)"));
        }

        [Test]
        public void Launch_and_relaunch_bump_delete_generation()
        {
            // Android orders a delete before any later Launch (AndroidLifecycleMachine), so it needs no generation.
            string android = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            Assert.That(android, Does.Not.Contain("DeleteCollectedDataLaunchGeneration"));

            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));
            string iosLaunch = ExtractMethodBody(ios, "void LaunchNow(string appToken, IDictionary<string, object> options, Action<bool> started)");
            Assert.That(iosLaunch, Does.Contain("BumpForLaunch()"));
            Assert.That(iosLaunch, Does.Contain("BeginNewLaunchCycle()"));
            Assert.That(iosLaunch, Does.Contain("_bugsee_launch"));
            int iosBump = iosLaunch.IndexOf("BumpForLaunch()", StringComparison.Ordinal);
            int iosNative = iosLaunch.IndexOf("_bugsee_launch", StringComparison.Ordinal);
            Assert.That(iosBump, Is.LessThan(iosNative));
        }

        [Test]
        public void Ios_breadcrumb_buffer_wiring()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));
            string add = ExtractMethodBody(ios, "public void AddBreadcrumb(string category, string message, string levelName)");
            Assert.That(add, Does.Contain("_breadcrumbLaunchBuffer.Enqueue"));
            Assert.That(add, Does.Contain("TimestampUnixSeconds"));
            Assert.That(add, Does.Not.Contain("_bugsee_channel_breadcrumb"));

            string notify = ExtractMethodBody(ios, "public void NotifyLifecycle(string eventType)");
            Assert.That(notify, Does.Contain("_breadcrumbLaunchBuffer.SetPhaseFromLifecycle"));
            Assert.That(notify, Does.Contain("_networkLaunchBuffer.SetPhaseFromLifecycle"));
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
            Assert.That(notify, Does.Contain("_breadcrumbLaunchBuffer.SetPhaseFromLifecycle"));
            Assert.That(notify, Does.Contain("_networkLaunchBuffer.SetPhaseFromLifecycle"));

            string launchNow = ExtractMethodBody(ios, "void LaunchNow(string appToken, IDictionary<string, object> options, Action<bool> started)");
            Assert.That(launchNow, Does.Contain("SetPhase(NetworkLaunchPhase.BeforeLaunched)"));
            Assert.That(launchNow, Does.Contain("RegisterLaunchResult(started)"));

            // Launch and Relaunch wait for a native stop in flight instead of racing it.
            string launch = ExtractMethodBody(ios, "void Launch(string appToken, IDictionary<string, object> options, Action<bool> started)");
            Assert.That(launch, Does.Contain("LaunchOrDefer(\"Launch\", () => LaunchNow(appToken, options, started), started)"));
            string relaunch = ExtractMethodBody(ios, "void Relaunch(IDictionary<string, object> options, Action<bool> started)");
            Assert.That(relaunch, Does.Contain("LaunchOrDefer(\"Relaunch\", () => RelaunchNow(options, started), started)"));
            string defer = ExtractMethodBody(ios, "void LaunchOrDefer(string name, Action launchNow, Action<bool> started)");
            Assert.That(defer, Does.Contain("StopWaiters.TryDeferLaunch("));
            Assert.That(defer, Does.Contain("cancelled?.Invoke(false)"));

            string stop = ExtractMethodBody(ios, "void Stop(Action completion, Action<bool> finished)");
            // The SDK keeps the channel live across stop and never hands out a second one to the
            // same wrapper object, so clearing it on Stop would silence it for every later launch.
            Assert.That(stop, Does.Not.Contain("wrapper_channel"));
            Assert.That(stop, Does.Contain("StopWithSoftDeadline(completion, finished)"));
            // A Stop whose body the main thread never ran faults StopAsync, drops the public completion (it cannot
            // signal failure, and Stop(() => Launch(t)) would launch over a session never stopped), and arms nothing.
            int skipped = stop.IndexOf("Debug.LogError(\"[Bugsee] Stop timed out", StringComparison.Ordinal);
            Assert.That(skipped, Is.GreaterThan(0));
            string afterSkip = stop.Substring(skipped);
            Assert.That(afterSkip, Does.Contain("RunResult(finished, false)"));
            Assert.That(afterSkip, Does.Not.Contain("RunCompletion("));
            Assert.That(afterSkip, Does.Not.Contain("completion()"));
            Assert.That(afterSkip, Does.Contain("its completion was dropped"));
            Assert.That(afterSkip, Does.Not.Contain("Arm"));
            Assert.That(afterSkip, Does.Not.Contain("GiveUp"));

            // Soft deadline: per Stop call, runs only that call's completion (taken once, by token); the stop stays
            // in flight, so Launch keeps waiting and a further Stop joins.
            string soft = ExtractMethodBody(ios, "void StopWithSoftDeadline(Action completion, Action<bool> finished)");
            Assert.That(soft, Does.Contain("StopCompletions.Register("));
            Assert.That(soft, Does.Contain("if (StopNativeThen(run, dropped)) ArmStopSoftDeadline(token);"));
            string softTimer = ExtractMethodBody(ios, "static void ArmStopSoftDeadline(int token)");
            Assert.That(softTimer, Does.Contain("StopCompletions.TryTake(token, out var callback)"));
            Assert.That(softTimer, Does.Contain("callback(false)"));
            Assert.That(softTimer, Does.Not.Contain("CompleteStop"));
            Assert.That(softTimer, Does.Not.Contain("StopWaiters"));
            Assert.That(ios, Does.Contain("const int StopSoftDeadlineMs = 10000;"));

            // One native stop at a time, shared by Stop and Delete. Only the hard deadline gives up on it.
            string stopNative = ExtractMethodBody(ios, "bool StopNativeThen(Action then, Action dropped = null, bool first = false)");
            Assert.That(stopNative, Does.Contain("StopWaiters.TryJoin(then, generation, out var cancelledLaunch, dropped, first)"));
            Assert.That(stopNative, Does.Contain("cancelledLaunch?.Invoke(false)"));
            Assert.That(stopNative, Does.Contain("GiveUpIfStopNotReported(token)"));
            Assert.That(stopNative, Does.Contain("_bugsee_stop(token, OnNativeStopped)"));
            string watchdog = ExtractMethodBody(ios, "static void GiveUpIfStopNotReported(int token)");
            Assert.That(watchdog, Does.Contain("AfterOnMainThread(StopHardDeadlineMs"));
            Assert.That(watchdog, Does.Contain("CompleteStop(token)"));
            Assert.That(ios, Does.Contain("const int StopHardDeadlineMs = 60000;"));
            Assert.That(ExtractMethodBody(ios, "static void AfterOnMainThread(int delayMs, Action action)"),
                Does.Contain("MainThreadDispatcher.Run("));

            // The launch answer: matched by token, taken once, with a status fallback at the deadline.
            string started = ExtractMethodBody(ios, "static void OnNativeStarted(int token, int success)");
            Assert.That(started, Does.Contain("LaunchResults.TryTake(token, out var callback)"));
            Assert.That(started, Does.Contain("catch (Exception"));
            Assert.That(ios, Does.Contain("[MonoPInvokeCallback(typeof(LaunchStartedNativeCallback))]"));
            string register = ExtractMethodBody(ios, "int RegisterLaunchResult(Action<bool> started)");
            Assert.That(register, Does.Contain("if (started == null) return 0;"));
            Assert.That(register, Does.Contain("LaunchResults.TryTake(token, out var callback)"));

            // Status comes from the SDK, so Launching/Stopping show and a refused launch reads as Stopped.
            string getStatus = ExtractMethodBody(ios, "public BugseeStatus GetStatus()");
            Assert.That(getStatus, Does.Contain("_bugsee_get_status()"));
            Assert.That(ios, Does.Not.Contain("_launched"), "the managed flag was dead once status came from the SDK");

            // Delete tears the managed hooks down unconditionally, in one main-thread closure: Launch installs them
            // even when the SDK then refuses it, and the SDK's status must not be read off the main thread.
            string delete = ExtractMethodBody(ios, "public void DeleteCollectedDataOnDevice()");
            Assert.That(delete, Does.Contain("RunSyncLifecycle"));
            // ...and in request order: no generation fence skips it when a Launch follows.
            Assert.That(delete, Does.Not.Contain("DeleteCollectedDataLaunchGeneration"));
            Assert.That(delete, Does.Contain("HostLogForwarder.Uninstall()"));
            Assert.That(delete, Does.Contain("ExceptionPipeline.Uninstall()"));
            Assert.That(delete, Does.Not.Contain("GetStatus()"));
            Assert.That(delete, Does.Contain("StopNativeThen("));

            // Stop: matched by token, exception-safe, with a watchdog.
            string native = ExtractMethodBody(ios, "static void OnNativeStopped(int token)");
            Assert.That(native, Does.Contain("catch (Exception"));
            Assert.That(native, Does.Contain("CompleteStop(token)"));
            string completeStop = ExtractMethodBody(ios, "static void CompleteStop(int token)");
            Assert.That(completeStop, Does.Contain("DeleteCollectedDataLaunchGeneration.CaptureForPendingDelete, RunCompletion"));
            string completion = ExtractMethodBody(ios, "static void RunCompletion(Action completion)");
            Assert.That(completion, Does.Contain("catch (Exception"));
            Assert.That(ios, Does.Contain("[MonoPInvokeCallback(typeof(StopNativeCallback))]"));

            string facadeLifecycle = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Bugsee.cs")),
                "internal static void HandleNativeLifecycle(string eventType, object data)");
            Assert.That(facadeLifecycle, Does.Contain("Bridge.NotifyLifecycle"));
        }

        [Test]
        public void Ios_launch_sends_wrapper_info_and_typed_channel_stages()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));

            string launch = ExtractMethodBody(ios, "void LaunchNow(string appToken, IDictionary<string, object> options, Action<bool> started)");
            Assert.That(launch, Does.Contain("WithWrapperInfo("));
            string relaunch = ExtractMethodBody(ios, "void RelaunchNow(IDictionary<string, object> options, Action<bool> started)");
            Assert.That(relaunch, Does.Contain("WithWrapperInfo("));
            Assert.That(ios, Does.Contain("\"wrapper_info\""));
            Assert.That(ios, Does.Contain("IosNetworkEvent.ToNativeStage"));

            // The SDK's constants are begin="before", complete, cancel, error (BugseeConstants.m), and
            // timestamps are Unix milliseconds: "begin" and a /1000 were both wrong on the first attempt.
            string events = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosExchangeEvents.cs"));
            Assert.That(events, Does.Contain("case NetworkEventStage.RequestStarted: return \"before\";"));
            Assert.That(events, Does.Contain("case NetworkEventStage.RequestCompleted: return \"complete\";"));
            Assert.That(events, Does.Contain("case NetworkEventStage.RequestAborted: return \"cancel\";"));
            Assert.That(events, Does.Contain("case NetworkEventStage.RequestErrored: return \"error\";"));
            Assert.That(events, Does.Not.Contain("return \"begin\""));
            string callbacks = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityCallbacks.mm"));
            Assert.That(callbacks, Does.Contain("event.timestamp = [timestampMs doubleValue];"));
            Assert.That(callbacks, Does.Not.Contain("/ 1000.0"));
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
        public void Ios_delete_clears_open_report_slot_when_stopped()
        {
            string body = ExtractMethodBody(
                File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs")),
                "public void DeleteCollectedDataOnDevice()");
            Assert.That(body, Does.Contain("_openReport = null"));
            Assert.That(body, Does.Contain("_openReportHandle = null"));
            Assert.That(body, Does.Contain("_networkLaunchBuffer.SetPhase(NetworkLaunchPhase.Stopped)"));
            Assert.That(body, Does.Contain("_breadcrumbLaunchBuffer.SetPhase(NetworkLaunchPhase.Stopped)"));
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
        public void Report_dialog_and_upload_default_to_no_severity_so_the_sdk_default_applies()
        {
            string facade = File.ReadAllText(RepoFile("Runtime/Bugsee.cs"));
            Assert.That(facade, Does.Match(@"ShowReportDialog\(\s*string summary = """",\s*string description = """",\s*IssueSeverity\? severity = null,"));
            Assert.That(facade, Does.Match(@"Upload\(\s*string summary,\s*string description = """",\s*IssueSeverity\? severity = null,"));
            Assert.That(facade, Does.Not.Contain("IssueSeverity severity = IssueSeverity.High"));

            string android = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            Assert.That(ExtractMethodBody(android, "public void ShowReportDialog("), Does.Contain("OptionalSeverity(severity)"));
            Assert.That(ExtractMethodBody(android, "public void Upload("), Does.Contain("OptionalSeverity(severity)"));
            Assert.That(ExtractMethodBody(android, "static AndroidJavaObject OptionalSeverity("), Does.Contain("severity.HasValue && IssueSeverityWire.ToWire(severity) > 0 ? Severity(severity.Value) : null"));

            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));
            Assert.That(ios, Does.Contain("_bugsee_show_report(summary ?? \"\", description ?? \"\", IssueSeverityWire.ToWire(severity)"));
            Assert.That(ios, Does.Contain("_bugsee_upload(summary ?? \"\", description ?? \"\", IssueSeverityWire.ToWire(severity)"));
        }

        [Test]
        public void Ios_report_entry_points_use_the_native_overloads_without_severity_when_it_is_omitted()
        {
            string bridge = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            string show = ExtractNativeFunctionBody(bridge, "_bugsee_show_report");
            Assert.That(show, Does.Contain("[Bugsee showReportDialog];"));
            Assert.That(show, Does.Contain("showReportDialogWithSummary:summaryStr ?: @\"\" description:descriptionStr ?: @\"\"];"));
            Assert.That(show, Does.Contain("hasSeverity ? (BugseeSeverityLevel)severity : BugseeDefaultBugSeverity()"));
            string upload = ExtractNativeFunctionBody(bridge, "_bugsee_upload");
            Assert.That(upload, Does.Contain("uploadWithSummary:summaryStr description:descriptionStr];"));
            Assert.That(upload, Does.Contain("severity > 0 ? (BugseeSeverityLevel)severity : BugseeDefaultBugSeverity()"));
        }

        [Test]
        public void Ios_live_report_wraps_attachment_list()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));

            string attachments = ExtractMethodBody(ios, "public IReadOnlyList<IAttachment> Attachments");
            Assert.That(attachments, Does.Contain("IosLiveAttachment(iosAttachment)"));
            Assert.That(ios, Does.Not.Contain("ApplyOpenReportToNative"));

            string create = ExtractMethodBody(ios, "public IAttachment AddAttachmentBytes(byte[] data, string name, string mimeType)");
            Assert.That(create, Does.Contain("as IosAttachment"));
        }
    }
}
