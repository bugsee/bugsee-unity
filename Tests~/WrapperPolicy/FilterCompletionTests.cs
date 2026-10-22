using System;
using System.IO;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class FilterCompletionTests
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

        static string DecisionBlockAfter(string body, string precedingCheck)
        {
            int check = body.IndexOf(precedingCheck, StringComparison.Ordinal);
            Assert.That(check, Is.GreaterThanOrEqualTo(0), "Missing check: " + precedingCheck);
            int decision = body.IndexOf("decisionBlock(", check, StringComparison.Ordinal);
            Assert.That(decision, Is.GreaterThanOrEqualTo(0));
            int end = body.IndexOf(')', decision);
            Assert.That(end, Is.GreaterThan(decision));
            return body.Substring(decision, end - decision + 1);
        }

        static void AssertDisableBranchSetsFlag(string setFilterBody, string flagName, string nilSetter)
        {
            Assert.That(setFilterBody, Does.Not.Contain(nilSetter));
            int disabled = setFilterBody.IndexOf("if (!enabled)", StringComparison.Ordinal);
            Assert.That(disabled, Is.GreaterThanOrEqualTo(0));
            int disabledReturn = setFilterBody.IndexOf("return;", disabled, StringComparison.Ordinal);
            Assert.That(disabledReturn, Is.GreaterThan(disabled));
            string disableBranch = setFilterBody.Substring(disabled, disabledReturn - disabled);
            Assert.That(disableBranch, Does.Contain(flagName + " = NO"));
        }

        static void AssertNativeSetFilter(
            string mm,
            string signature,
            string flagName,
            string nilSetter,
            string keepDecisionBlock,
            string builderSignature,
            string installCall)
        {
            string setter = ExtractMethodBody(mm, signature);
            AssertDisableBranchSetsFlag(setter, flagName, nilSetter);
            Assert.That(setter, Does.Contain(installCall), "the setter must install the filter builder's block");
            string body = ExtractMethodBody(mm, builderSignature);
            Assert.That(
                DecisionBlockAfter(body, "if (!" + flagName + ")"),
                Is.EqualTo(keepDecisionBlock));
            Assert.That(
                DecisionBlockAfter(body, "if (!gFilterCb)"),
                Is.EqualTo("decisionBlock(nil)"));
        }

        static void AssertNullFilterKeeps(string ios, string methodSignature)
        {
            string body = ExtractMethodBody(ios, methodSignature);
            int nullFilter = body.IndexOf("if (filter == null)", StringComparison.Ordinal);
            Assert.That(nullFilter, Is.GreaterThanOrEqualTo(0));
            int complete = body.IndexOf("_bugsee_complete_filter", nullFilter, StringComparison.Ordinal);
            Assert.That(complete, Is.GreaterThan(nullFilter));
            int lineEnd = body.IndexOf(';', complete);
            string completion = body.Substring(complete, lineEnd - complete);
            Assert.That(completion, Is.EqualTo("_bugsee_complete_filter(requestId, FilterCompletion.Keep, json)"));
        }

        [Test]
        public void Throwing_filter_completion_is_drop()
        {
            Assert.That(FilterCompletion.Drop, Is.EqualTo(0));
            Assert.That(FilterCompletion.Keep, Is.EqualTo(1));
            Assert.That(FilterCompletion.OnThrow, Is.EqualTo(FilterCompletion.Drop));
        }

        [Test]
        public void Ios_on_filter_fail_closed()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosNativeCallbacks.cs"));
            string onFilter = ExtractMethodBody(ios, "static void OnFilter(long requestId, int kind, string json)");
            Assert.That(onFilter, Does.Not.Contain("_bugsee_complete_filter(requestId, 1, json)"));
            Assert.That(onFilter, Does.Not.Contain("FilterCompletion.Keep"));

            string defaultBranch = ExtractMethodBody(onFilter, "default:");
            Assert.That(defaultBranch, Does.Contain("FilterCompletion.Drop"));
            Assert.That(defaultBranch, Does.Contain("null"));

            string catchBody = ExtractMethodBody(onFilter, "catch (Exception ex)");
            Assert.That(
                catchBody,
                Does.Match("FilterCompletion\\.(Drop|OnThrow).*null|null.*FilterCompletion\\.(Drop|OnThrow)"));
            Assert.That(catchBody, Does.Not.Contain("FilterCompletion.Keep"));
            Assert.That(catchBody, Does.Contain("filter-failed"));
            Assert.That(catchBody, Does.Contain("GetType().Name"));
            Assert.That(catchBody, Does.Not.Contain("LogException"));
        }

        [Test]
        public void Ios_complete_methods_keep_when_filter_null()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosNativeCallbacks.cs"));
            string onFilter = ExtractMethodBody(ios, "static void OnFilter(long requestId, int kind, string json)");
            Assert.That(onFilter, Does.Contain("var networkFilter = _networkFilter"));
            Assert.That(onFilter, Does.Contain("var logFilter = _logFilter"));
            Assert.That(onFilter, Does.Contain("var breadcrumbFilter = _breadcrumbFilter"));
            AssertNullFilterKeeps(ios, "static void CompleteNetwork(long requestId, string json, EventFilter<INetworkEvent> filter)");
            AssertNullFilterKeeps(ios, "static void CompleteLog(long requestId, string json, EventFilter<ILogEvent> filter)");
            AssertNullFilterKeeps(
                ios,
                "static void CompleteBreadcrumb(long requestId, string json, EventFilter<IBreadcrumb> filter)");
        }

        [Test]
        public void Ios_network_filter_replacement_is_typed_with_the_sdk_stage()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosNativeCallbacks.cs"));
            string complete = ExtractMethodBody(
                ios, "static void CompleteNetwork(long requestId, string json, EventFilter<INetworkEvent> filter)");
            Assert.That(complete, Does.Contain("IosNetworkEvent.ToNativeStage(ios.Stage)"));
            Assert.That(complete, Does.Contain("ios.ToReplacementJson(nativeStage)"));
            Assert.That(complete, Does.Contain("ReferenceEquals(ios, managed)"));
        }

        [Test]
        public void Ios_native_per_kind_filter_flags()
        {
            string mm = File.ReadAllText(RepoFile("Plugins/iOS/BugseeUnityCallbacks.mm"));
            Assert.That(mm, Does.Contain("gNetworkFilterCallbackInstalled"));
            Assert.That(mm, Does.Contain("gLogFilterCallbackInstalled"));
            Assert.That(mm, Does.Contain("gBreadcrumbFilterCallbackInstalled"));
            Assert.That(mm, Does.Not.Contain("static BOOL gFilterCallbackInstalled"));

            AssertNativeSetFilter(
                mm,
                "void _bugsee_set_network_filter_enabled(int enabled)",
                "gNetworkFilterCallbackInstalled",
                "setNetworkEventFilter:nil",
                "decisionBlock(event)",
                "static BugseeNetworkEventFilterBlock BugseeUnityNetworkFilter(void)",
                "[Bugsee setNetworkEventFilter:BugseeUnityNetworkFilter()]");
            AssertNativeSetFilter(
                mm,
                "void _bugsee_set_log_filter_enabled(int enabled)",
                "gLogFilterCallbackInstalled",
                "setLogEventFilter:nil",
                "decisionBlock(event)",
                "static BugseeLogEventFilterBlock BugseeUnityLogFilter(void)",
                "[Bugsee setLogEventFilter:BugseeUnityLogFilter()]");
            AssertNativeSetFilter(
                mm,
                "void _bugsee_set_breadcrumb_filter_enabled(int enabled)",
                "gBreadcrumbFilterCallbackInstalled",
                "setBreadcrumbFilter:nil",
                "decisionBlock(crumb)",
                "static BugseeBreadcrumbFilterBlock BugseeUnityBreadcrumbFilter(void)",
                "[Bugsee setBreadcrumbFilter:BugseeUnityBreadcrumbFilter()]");
        }

        static string StripComments(string source)
        {
            source = System.Text.RegularExpressions.Regex.Replace(source, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            return System.Text.RegularExpressions.Regex.Replace(source, @"//[^\n]*", "");
        }

        [Test]
        public void Android_proxies_release_java_args_in_finally()
        {
            string proxies = StripComments(File.ReadAllText(RepoFile("Runtime/Platform/Android/Proxies/CallbackProxies.cs")));
            string filterProxy = ExtractMethodBody(proxies, "public void filter(AndroidJavaObject data, AndroidJavaObject callback)");

            // no-filter path: release sits in a finally after the callback.
            string noFilter = ExtractMethodBody(filterProxy, "if (filter == null)");
            Assert.That(noFilter, Does.Contain("finally"));
            Assert.That(noFilter.IndexOf("ReleaseArgs(data, callback)", StringComparison.Ordinal),
                Is.GreaterThan(noFilter.IndexOf("finally", StringComparison.Ordinal)));

            // deferred path: wrapper invalidated and args released inside a finally.
            string deferred = ExtractMethodBody(filterProxy, "MainThreadDispatcher.Run(() =>");
            string deferredFinally = ExtractMethodBody(deferred, "finally");
            Assert.That(deferredFinally, Does.Contain("ReleaseNative()"));
            Assert.That(deferredFinally, Does.Contain("ReleaseArgs(data, callback)"));

            string release = ExtractMethodBody(proxies, "static void ReleaseArgs(AndroidJavaObject data, AndroidJavaObject callback)");
            Assert.That(release, Does.Contain("data?.Dispose()"));
            Assert.That(release, Does.Contain("callback?.Dispose()"));

            // report handler: no-handler/terminating path and done() release completion in a finally.
            string invoke = ExtractMethodBody(proxies, "void Invoke(AndroidJavaObject report, bool isTerminating");
            foreach (var branch in new[] { "if (handler == null)", "if (isTerminating)" })
                Assert.That(ExtractMethodBody(ExtractMethodBody(invoke, branch), "finally"), Does.Contain("ReleaseArgs(report, completion)"));
            // The handler's done goes through the late-reply guard, whose single completion releases the argument.
            string finish = ExtractMethodBody(invoke, "new LateReplyGuard(");
            Assert.That(ExtractMethodBody(finish, "finally"), Does.Contain("completion?.Dispose()"));
            Assert.That(invoke, Does.Contain("guard.Done"));
        }

        [Test]
        public void Android_event_wrappers_fail_clearly_after_release()
        {
            string events = StripComments(File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidExchangeEvents.cs")));
            Assert.That(events, Does.Contain("throw new ObjectDisposedException("));
            foreach (var cls in new[] { "AndroidNetworkEvent", "AndroidLogEvent", "AndroidBreadcrumb" })
            {
                Assert.That(events, Does.Contain("sealed class " + cls + " :"));
                Assert.That(events, Does.Contain("NativeEventGuard.Live(_nativeRaw, _released, nameof(" + cls + "))"));
            }
        }

        [Test]
        public void Android_filter_proxy_drops_on_throw()
        {
            string proxies = File.ReadAllText(RepoFile("Runtime/Platform/Android/Proxies/CallbackProxies.cs"));
            string filterProxy = ExtractMethodBody(proxies, "public void filter(AndroidJavaObject data, AndroidJavaObject callback)");
            int catchStart = filterProxy.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
            Assert.That(catchStart, Is.GreaterThanOrEqualTo(0));
            string catchBody = ExtractMethodBody(filterProxy, "catch (Exception ex)");
            Assert.That(catchBody, Does.Contain("filter-failed"));
            Assert.That(catchBody, Does.Not.Contain("callback?.Call(\"run\", data);"));
            Assert.That(catchBody, Does.Contain("(AndroidJavaObject)null"));
        }

        [Test]
        public void Report_handlers_on_both_platforms_go_through_the_late_reply_guard()
        {
            string android = StripComments(File.ReadAllText(RepoFile("Runtime/Platform/Android/Proxies/CallbackProxies.cs")));
            string invoke = ExtractMethodBody(android, "void Invoke(AndroidJavaObject report, bool isTerminating");
            Assert.That(invoke, Does.Contain("new LateReplyGuard(BudgetMillis()"));
            Assert.That(invoke, Does.Contain("var late = !guard.TryBegin();"));
            Assert.That(invoke, Does.Contain("late ? (() => { }) : (Action)guard.Done"), "a late after-phase handler still runs, acknowledging only");
            Assert.That(invoke, Does.Contain("guard.Abandon()"));
            Assert.That(invoke, Does.Contain("guard.ArmTimeout("));
            Assert.That(ExtractMethodBody(invoke, "if (late && before)"), Does.Contain("report?.Dispose()"));
            Assert.That(invoke, Does.Not.Contain("var finished"));

            string filter = ExtractMethodBody(android, "public void filter(AndroidJavaObject data, AndroidJavaObject callback)");
            Assert.That(filter, Does.Contain("new FilterReplyWindow(_sdkTimeoutMs"));
            Assert.That(filter, Does.Contain("if (!window.IsOpen)"));
            Assert.That(filter, Does.Not.Contain("callback?.Call(\"run\", result"));
            string bridge = File.ReadAllText(RepoFile("Runtime/Platform/Android/AndroidBridge.cs"));
            Assert.That(bridge, Does.Contain("new AndroidLogEvent(native), FilterReplyWindow.LogFilterSdkTimeoutMs"));

            string ios = StripComments(File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosNativeCallbacks.cs")));
            string onReport = ExtractMethodBody(ios, "static void OnReport(long requestId, int phase, int isTerminating, string json)");
            Assert.That(onReport, Does.Contain("new LateReplyGuard(ReportHandlerDeadline.DefaultBudgetMs"));
            Assert.That(onReport, Does.Contain("if (!guard.TryBegin()) return;"));
            Assert.That(onReport, Does.Contain("guard.ArmTimeout("));
            Assert.That(onReport, Does.Contain("guard.Done"));
            Assert.That(onReport, Does.Not.Contain("var finished"));
        }
    }
}
