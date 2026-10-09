using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ReportPathSourceTests
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

        static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "Missing method: " + signature);
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

            Assert.Fail("Unclosed method body: " + signature);
            return null;
        }

        static string ExtractNativeFunctionBody(string source, string functionName)
        {
            var marker = "static void " + functionName;
            int start = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "Missing function: " + functionName);
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

            Assert.Fail("Unclosed function body: " + functionName);
            return null;
        }

        [Test]
        public void Ios_extended_apply_uses_path_and_bytes_attachments()
        {
            string mm = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityBridge.mm"));
            string apply = ExtractNativeFunctionBody(mm, "BugseeBridgeApplyExtendedReportDict");
            Assert.That(apply, Does.Contain("att[@\"path\"]"));
            Assert.That(apply, Does.Contain("dataWithContentsOfFile:"));
            Assert.That(apply, Does.Contain("dataBase64"));
            Assert.That(apply, Does.Contain("[BugseeAttachment attachmentWithName:name filename:fileName data:data]"));
        }

        [Test]
        public void Ios_create_report_upload_releases_snapshots_after_native_apply()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));
            string discard = ExtractMethodBody(ios, "public void DiscardReport(IReport report)");
            Assert.That(discard, Does.Contain("ReleaseSnapshotFiles()"));

            string upload = ExtractMethodBody(ios, "public void UploadReport(IReport report)");
            Assert.That(upload, Does.Contain("_uploadSnapshotReports[uploadToken]"));

            string completion = ExtractMethodBody(ios, "void HandleManagedReportCreateCompletion(bool succeeded, ulong uploadToken)");
            Assert.That(completion, Does.Contain("ReleaseUploadSnapshotReport(uploadToken)"));

            string stop = ExtractMethodBody(ios, "public void Stop(Action completion = null)");
            Assert.That(stop, Does.Contain("_openReport?.ReleaseSnapshotFiles()"));
            string delete = ExtractMethodBody(ios, "public void DeleteCollectedDataOnDevice()");
            Assert.That(delete, Does.Contain("_openReport?.ReleaseSnapshotFiles()"));
        }

        [Test]
        public void Ios_apply_report_dict_uses_path_and_bytes_attachments()
        {
            string mm = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityCallbacks.mm"));
            Assert.That(mm, Does.Not.Contain("createAndAddAttachmentWithName"));

            string apply = ExtractMethodBody(mm, "static void BugseeUnityApplyReportDict");
            Assert.That(apply, Does.Contain("path"));
            Assert.That(apply, Does.Contain("dataBase64"));
            Assert.That(apply, Does.Contain("addAttachmentWithFilePath:name:mimeType:move:"));
            Assert.That(apply, Does.Contain("addAttachmentWithData:name:mimeType:"));
            Assert.That(apply, Does.Contain("move:YES"));
            Assert.That(apply, Does.Contain("fileName"));
            Assert.That(apply, Does.Not.Contain("createAndAddAttachmentWithName"));
        }

        [Test]
        public void Ios_forwardReport_terminates_before_report_callback()
        {
            string mm = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityCallbacks.mm"));
            string forward = ExtractMethodBody(
                mm,
                "- (void)forwardReport:(id<BGSReportContract>)report");
            int terminating = forward.IndexOf("isTerminating", StringComparison.Ordinal);
            int reportCb = forward.IndexOf("gReportCb", StringComparison.Ordinal);
            Assert.That(terminating, Is.GreaterThanOrEqualTo(0));
            Assert.That(reportCb, Is.GreaterThan(terminating));

            string terminatingBranch = ExtractMethodBody(forward, "if (isTerminating)");
            Assert.That(terminatingBranch, Does.Contain("completion()"));
            Assert.That(terminatingBranch, Does.Not.Contain("gReportCb"));
        }

        [Test]
        public void Ios_on_report_releases_snapshots_after_complete()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosNativeCallbacks.cs"));
            string onReport = ExtractMethodBody(ios, "static void OnReport(long requestId, int phase, int isTerminating, string json)");
            Assert.That(onReport, Does.Contain("CompleteReport(requestId, report"));
            Assert.That(onReport, Does.Contain("ReleaseSnapshotFiles"));
            Assert.That(onReport, Does.Not.Contain("finally"));
            string complete = ExtractMethodBody(onReport, "static void CompleteReport(long requestId, IosReport report, string resultJson)");
            int completeReport = complete.IndexOf("_bugsee_complete_report", StringComparison.Ordinal);
            int release = complete.IndexOf("ReleaseSnapshotFiles", StringComparison.Ordinal);
            Assert.That(completeReport, Is.GreaterThanOrEqualTo(0));
            Assert.That(release, Is.GreaterThan(completeReport));
        }

        [Test]
        public void Ios_on_report_skips_csharp_when_terminating()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosNativeCallbacks.cs"));
            string onReport = ExtractMethodBody(ios, "static void OnReport(long requestId, int phase, int isTerminating, string json)");
            string terminatingBranch = ExtractMethodBody(onReport, "if (isTerminating != 0)");
            Assert.That(terminatingBranch, Does.Contain("_bugsee_complete_report"));
            Assert.That(terminatingBranch, Does.Not.Contain("_reportHandler"));
            Assert.That(terminatingBranch, Does.Not.Contain("OnBeforeReportCreated"));
            Assert.That(terminatingBranch, Does.Not.Contain("OnAfterReportCreated"));
        }

        [Test]
        public void Android_report_handler_proxy_skips_handler_when_terminating()
        {
            string proxies = File.ReadAllText(RepoFile("Runtime/Platform/Android/Proxies/CallbackProxies.cs"));
            string invoke = ExtractMethodBody(proxies, "void Invoke(AndroidJavaObject report, bool isTerminating");
            string terminatingBranch = ExtractMethodBody(invoke, "if (isTerminating)");
            Assert.That(terminatingBranch, Does.Contain("completion?.Call(\"run\")"));
            Assert.That(terminatingBranch, Does.Not.Contain("_handler"));
            Assert.That(terminatingBranch, Does.Not.Contain("OnBeforeReportCreated"));
            Assert.That(terminatingBranch, Does.Not.Contain("OnAfterReportCreated"));
        }

        [Test]
        public void Android_unity_wrapper_terminates_without_csharp_hop()
        {
            string java = File.ReadAllText(RepoFile("Plugins/Android/UnityWrapperProvider.java"));
            string before = ExtractMethodBody(java, "public void onBeforeReportCreated(Report report, boolean isTerminating, Runnable completion)");
            string beforeTerm = ExtractMethodBody(before, "if (isTerminating)");
            Assert.That(beforeTerm, Does.Contain("completion.run()"));
            Assert.That(beforeTerm, Does.Not.Contain("Unity"));

            string after = ExtractMethodBody(java, "public void onAfterReportCreated(Report report, boolean isTerminating, Runnable completion)");
            string afterTerm = ExtractMethodBody(after, "if (isTerminating)");
            Assert.That(afterTerm, Does.Contain("completion.run()"));
        }

        [Test]
        public void Bridges_enforce_attribute_and_user_identifier_policy()
        {
            string android = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            string androidSetAttr = ExtractMethodBody(android, "public void SetAttribute(string key, object value)");
            int evaluate = androidSetAttr.IndexOf("AttributePolicy.Evaluate", StringComparison.Ordinal);
            int nativeSet = androidSetAttr.IndexOf("CallStatic(\"setAttribute\"", StringComparison.Ordinal);
            int readBack = androidSetAttr.IndexOf("GetAttribute(key)", StringComparison.Ordinal);
            Assert.That(evaluate, Is.GreaterThanOrEqualTo(0));
            Assert.That(nativeSet, Is.GreaterThan(evaluate));
            Assert.That(readBack, Is.GreaterThan(nativeSet));
            Assert.That(androidSetAttr, Does.Contain("attribute '"));
            Assert.That(androidSetAttr, Does.Contain("' was dropped"));

            string androidSetUser = ExtractMethodBody(android, "public void SetUserIdentifier(string userIdentifier)");
            Assert.That(androidSetUser, Does.Contain("UserIdentifierPolicy.ForSet"));
            string androidGetUser = ExtractMethodBody(android, "public string GetUserIdentifier()");
            Assert.That(androidGetUser, Does.Contain("UserIdentifierPolicy.ForGet"));

            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IOSBridge.cs"));
            string iosSetAttr = ExtractMethodBody(ios, "public void SetAttribute(string key, object value)");
            evaluate = iosSetAttr.IndexOf("AttributePolicy.Evaluate", StringComparison.Ordinal);
            nativeSet = iosSetAttr.IndexOf("_bugsee_set_attribute", StringComparison.Ordinal);
            readBack = iosSetAttr.IndexOf("GetAttribute(key)", StringComparison.Ordinal);
            Assert.That(evaluate, Is.GreaterThanOrEqualTo(0));
            Assert.That(nativeSet, Is.GreaterThan(evaluate));
            Assert.That(readBack, Is.GreaterThan(nativeSet));
            Assert.That(iosSetAttr, Does.Contain("attribute '"));
            Assert.That(iosSetAttr, Does.Contain("' was dropped"));

            string iosSetUser = ExtractMethodBody(ios, "public void SetUserIdentifier(string userIdentifier)");
            Assert.That(iosSetUser, Does.Contain("UserIdentifierPolicy.ForSet"));
            string iosGetUser = ExtractMethodBody(ios, "public string GetUserIdentifier()");
            Assert.That(iosGetUser, Does.Contain("UserIdentifierPolicy.ForGet"));
        }

        [Test]
        public void Report_severity_getters_use_try_from_wire_without_throw()
        {
            string androidReport = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidReport.cs"));
            string androidGet = ExtractMethodBody(
                androidReport,
                "using (var s = _report.Call<AndroidJavaObject>(\"getSeverity\"))");
            Assert.That(androidGet, Does.Contain("IssueSeverityWire.TryFromWire"));
            Assert.That(androidGet, Does.Contain("(IssueSeverity?)null"));
            Assert.That(androidGet, Does.Not.Contain("FromIntValue"));
            Assert.That(androidGet, Does.Not.Contain("throw"));

            string iosReport = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosReport.cs"));
            Assert.That(
                iosReport,
                Does.Contain("get => IssueSeverityWire.TryFromWire(_dto.severity, out var severity) ? severity : (IssueSeverity?)null;"));
            Assert.That(iosReport, Does.Not.Contain("FromIntValue"));
        }

        [Test]
        public void Android_report_uses_path_and_bytes_attachments()
        {
            string report = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidReport.cs"));
            string addFile = ExtractMethodBody(report, "public IAttachment AddAttachmentFile(string path, string name, string mimeType)");
            Assert.That(addFile, Does.Contain("string.IsNullOrEmpty(path)"));
            Assert.That(addFile, Does.Contain("attachmentName"));
            Assert.That(addFile, Does.Contain("addAttachment\", file, attachmentName, mimeType, false"));
            Assert.That(addFile, Does.Contain("att == null ? null"));
            Assert.That(addFile, Does.Not.Contain("createAndAddAttachment"));

            string addBytes = ExtractMethodBody(report, "public IAttachment AddAttachmentBytes(byte[] data, string name, string mimeType)");
            Assert.That(addBytes, Does.Contain("addAttachment\", data, attachmentName, mimeType"));
            Assert.That(addBytes, Does.Contain("att == null ? null"));
            Assert.That(addBytes, Does.Not.Contain("createAndAddAttachment"));

            Assert.That(report, Does.Not.Contain("CreateAndAddAttachment"));
        }

        [Test]
        public void Ios_report_snapshots_files_and_base64_bytes()
        {
            string report = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosReport.cs"));
            string addFile = ExtractMethodBody(report, "public IAttachment AddAttachmentFile(string path, string name, string mimeType)");
            Assert.That(addFile, Does.Contain("SnapshotAttachmentFile"));
            Assert.That(addFile, Does.Contain("File.Exists(path)"));
            Assert.That(addFile, Does.Contain("Path.GetFileName(path)"));
            Assert.That(addFile, Does.Contain("SetSnapshotPath"));
            Assert.That(addFile, Does.Contain("catch (IOException)"));

            string addBytes = ExtractMethodBody(report, "public IAttachment AddAttachmentBytes(byte[] data, string name, string mimeType)");
            Assert.That(addBytes, Does.Contain("SetAttachmentBytes"));
            Assert.That(addBytes, Does.Not.Contain("SetData(data)"));
        }

        [Test]
        public void Android_managed_report_snapshots_files_at_add()
        {
            string report = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidReport.cs"));
            int managedStart = report.IndexOf("sealed class AndroidManagedReport", StringComparison.Ordinal);
            Assert.That(managedStart, Is.GreaterThanOrEqualTo(0));
            string managed = report.Substring(managedStart);
            string addFile = ExtractMethodBody(managed, "public IAttachment AddAttachmentFile(string path, string name, string mimeType)");
            Assert.That(addFile, Does.Contain("SnapshotAttachmentFile"));
            Assert.That(addFile, Does.Contain("SetSnapshotPath"));
            Assert.That(addFile, Does.Contain("File.Exists(path)"));
        }

        [Test]
        public void IReport_contract_exposes_path_and_bytes_attachments()
        {
            string contract = File.ReadAllText(RepoFile("Runtime/Contracts/Reporting/IReport.cs"));
            Assert.That(
                contract,
                Does.Contain("IAttachment AddAttachmentFile(string path, string name, string mimeType);"));
            Assert.That(
                contract,
                Does.Contain("IAttachment AddAttachmentBytes(byte[] data, string name, string mimeType);"));
            Assert.That(contract, Does.Not.Contain("CreateAndAddAttachment"));
            Assert.That(contract, Does.Contain("IssueSeverity?"));
        }
    }
}
