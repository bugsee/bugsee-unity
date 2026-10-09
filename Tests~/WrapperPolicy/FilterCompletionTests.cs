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
            string keepDecisionBlock)
        {
            string body = ExtractMethodBody(mm, signature);
            AssertDisableBranchSetsFlag(body, flagName, nilSetter);
            Assert.That(
                DecisionBlockAfter(body, "if (!" + flagName + ")"),
                Is.EqualTo(keepDecisionBlock));
            Assert.That(
                DecisionBlockAfter(body, "if (!gFilterCb)"),
                Is.EqualTo("decisionBlock(nil)"));
        }

        static void AssertNullFilterDrops(string ios, string methodSignature)
        {
            string body = ExtractMethodBody(ios, methodSignature);
            int nullFilter = body.IndexOf("if (filter == null)", StringComparison.Ordinal);
            Assert.That(nullFilter, Is.GreaterThanOrEqualTo(0));
            int complete = body.IndexOf("_bugsee_complete_filter", nullFilter, StringComparison.Ordinal);
            Assert.That(complete, Is.GreaterThan(nullFilter));
            int lineEnd = body.IndexOf(';', complete);
            string completion = body.Substring(complete, lineEnd - complete);
            Assert.That(completion, Is.EqualTo("_bugsee_complete_filter(requestId, FilterCompletion.Drop, null)"));
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
        }

        [Test]
        public void Ios_complete_methods_drop_when_filter_null()
        {
            string ios = File.ReadAllText(RepoFile("Runtime/Platform/IOS/IosNativeCallbacks.cs"));
            AssertNullFilterDrops(ios, "static void CompleteNetwork(long requestId, string json)");
            AssertNullFilterDrops(ios, "static void CompleteLog(long requestId, string json)");
            AssertNullFilterDrops(ios, "static void CompleteBreadcrumb(long requestId, string json)");
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
                "decisionBlock(event)");
            AssertNativeSetFilter(
                mm,
                "void _bugsee_set_log_filter_enabled(int enabled)",
                "gLogFilterCallbackInstalled",
                "setLogEventFilter:nil",
                "decisionBlock(event)");
            AssertNativeSetFilter(
                mm,
                "void _bugsee_set_breadcrumb_filter_enabled(int enabled)",
                "gBreadcrumbFilterCallbackInstalled",
                "setBreadcrumbFilter:nil",
                "decisionBlock(crumb)");
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
    }
}
