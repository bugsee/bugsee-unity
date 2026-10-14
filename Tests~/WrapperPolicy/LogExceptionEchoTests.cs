using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class LogExceptionEchoTests
    {
        static readonly Exception Thrown = new InvalidOperationException("E2E StableA marker-1");

        static bool EchoWhileExpecting(Exception exception, string condition)
        {
            using (LogExceptionEcho.Expect(exception)) return LogExceptionEcho.IsEcho(condition);
        }

        [Test]
        public void Unity_rendered_text_raised_inside_the_call_is_an_echo()
        {
            Assert.That(EchoWhileExpecting(Thrown, "InvalidOperationException: E2E StableA marker-1"), Is.True);
        }

        [Test]
        public void The_full_type_name_spelling_matches_too()
        {
            Assert.That(EchoWhileExpecting(Thrown, "System.InvalidOperationException: E2E StableA marker-1"), Is.True);
        }

        [Test]
        public void A_different_exception_is_not_an_echo()
        {
            Assert.That(EchoWhileExpecting(Thrown, "InvalidOperationException: something else"), Is.False);
            Assert.That(EchoWhileExpecting(Thrown, "ArgumentException: E2E StableA marker-1"), Is.False);
        }

        // Unity raises the echo from inside the call; once it returned, the same text is a new event.
        [Test]
        public void The_same_text_after_the_call_returned_is_a_new_event()
        {
            using (LogExceptionEcho.Expect(Thrown)) { }
            Assert.That(LogExceptionEcho.IsEcho("InvalidOperationException: E2E StableA marker-1"), Is.False);
        }

        [Test]
        public void Nothing_expected_means_nothing_is_an_echo()
        {
            Assert.That(LogExceptionEcho.IsEcho("InvalidOperationException: x"), Is.False);
            Assert.That(LogExceptionEcho.IsEcho(null), Is.False);
            Assert.That(EchoWhileExpecting(Thrown, ""), Is.False);
            Assert.That(EchoWhileExpecting(null, "InvalidOperationException: x"), Is.False);
        }

        // Thread A is inside its LogException when thread B logs an exception with the same text (default
        // NullReferenceException messages are identical across throw sites). B's is a real event, and A's own
        // echo must still be recognised afterwards.
        [Test]
        public void Another_threads_exception_with_the_same_text_is_not_an_echo()
        {
            const string text = "NullReferenceException: Object reference not set to an instance of an object";
            var exception = new NullReferenceException("Object reference not set to an instance of an object");
            bool otherThread = true, ownEcho = false;
            using (LogExceptionEcho.Expect(exception))
            {
                var t = new Thread(() => otherThread = LogExceptionEcho.IsEcho(text));
                t.Start();
                t.Join();
                ownEcho = LogExceptionEcho.IsEcho(text);
            }

            Assert.That(otherThread, Is.False, "another thread's exception was dropped as an echo");
            Assert.That(ownEcho, Is.True);
        }

        // Unity prints "NullReferenceException" with no colon when the message is blank.
        [Test]
        public void A_blank_message_is_rendered_without_a_colon()
        {
            Assert.That(EchoWhileExpecting(new NullReferenceException(""), "NullReferenceException"), Is.True);
        }

        [Test]
        public void A_blank_message_does_not_swallow_a_different_exception_of_that_type()
        {
            Assert.That(EchoWhileExpecting(new NullReferenceException(""), "NullReferenceException: Object reference not set"), Is.False);
        }

        // Unity renders the innermost exception of the chain as the condition.
        [Test]
        public void An_exception_with_an_inner_exception_is_matched_by_the_inner_text()
        {
            var chained = new InvalidOperationException("outer", new ArgumentException("inner"));
            Assert.That(EchoWhileExpecting(chained, "ArgumentException: inner"), Is.True);
            Assert.That(EchoWhileExpecting(chained, "InvalidOperationException: outer"), Is.False);
        }

        [Test]
        public void An_aggregate_exception_is_matched_by_its_first_inner_exception()
        {
            Assert.That(EchoWhileExpecting(new AggregateException(new ArgumentException("x")), "ArgumentException: x"), Is.True);
        }

        [Test]
        public void An_echo_is_consumed_so_it_cannot_swallow_a_second_report()
        {
            using (LogExceptionEcho.Expect(Thrown))
            {
                Assert.That(LogExceptionEcho.IsEcho("InvalidOperationException: E2E StableA marker-1"), Is.True);
                Assert.That(LogExceptionEcho.IsEcho("InvalidOperationException: E2E StableA marker-1"), Is.False);
            }
        }

        // Unity's condition is exactly "Name: message"; "contains" let "m1" match "m12".
        [Test]
        public void The_message_must_match_exactly_not_as_a_prefix()
        {
            var m1 = new InvalidOperationException("m1");
            Assert.That(EchoWhileExpecting(m1, "InvalidOperationException: m12"), Is.False);
            Assert.That(EchoWhileExpecting(m1, "InvalidOperationException: m1"), Is.True);
            Assert.That(EchoWhileExpecting(m1, "InvalidOperationException: m1\nmore"), Is.True);
        }

        // A log callback that itself calls Debug.LogException nests a second call; each finds its own echo and
        // the outer expectation is back when the inner call returns.
        [Test]
        public void A_nested_call_expects_its_own_echo_and_restores_the_outer_one()
        {
            using (LogExceptionEcho.Expect(new InvalidOperationException("outer-call")))
            {
                using (LogExceptionEcho.Expect(new ArgumentException("inner-call")))
                {
                    Assert.That(LogExceptionEcho.IsEcho("InvalidOperationException: outer-call"), Is.False);
                    Assert.That(LogExceptionEcho.IsEcho("ArgumentException: inner-call"), Is.True);
                }

                Assert.That(LogExceptionEcho.IsEcho("InvalidOperationException: outer-call"), Is.True);
            }

            Assert.That(LogExceptionEcho.IsEcho("InvalidOperationException: outer-call"), Is.False);
        }

        // A repeat of the same exception (catch { Debug.LogException(ex); throw; }, or two logs of it) is skipped
        // by the stack dedupe because the first call already reported it; its echo must be dropped too, or the
        // text path files it as a second issue. Only when nothing was delivered is the echo the report.
        [Test]
        public void The_echo_is_expected_after_a_delivery_or_a_duplicate_but_not_when_nothing_was_delivered()
        {
            Assert.That(LogExceptionEcho.ShouldExpect(LogExceptionEcho.Outcome.Delivered), Is.True);
            Assert.That(LogExceptionEcho.ShouldExpect(LogExceptionEcho.Outcome.Duplicate), Is.True);
            Assert.That(LogExceptionEcho.ShouldExpect(LogExceptionEcho.Outcome.NotDelivered), Is.False);
        }

        static Exception ThrowFromOneSite(Exception exception)
        {
            try { throw exception; }
            catch (Exception caught) { return caught; }
        }

        // Duplicate drops the echo, so it must mean the same exception, not just the same type and stack: a
        // per-item message thrown from one line, or new Exception(msg) logged unthrown (null stack), is a new event.
        [Test]
        public void The_duplicate_key_tells_apart_exceptions_that_differ_only_in_message()
        {
            Assert.That(LogExceptionEcho.DuplicateKey(new Exception("login failed"), "LogException"),
                Is.Not.EqualTo(LogExceptionEcho.DuplicateKey(new Exception("inventory sync failed"), "LogException")));

            var a = ThrowFromOneSite(new InvalidOperationException("item 1"));
            var b = ThrowFromOneSite(new InvalidOperationException("item 2"));
            Assert.That(a.StackTrace, Is.EqualTo(b.StackTrace));
            Assert.That(LogExceptionEcho.DuplicateKey(a, "LogException"),
                Is.Not.EqualTo(LogExceptionEcho.DuplicateKey(b, "LogException")));
        }

        // Unity's echo names the innermost exception, so a chain that differs only inside is a different event.
        [Test]
        public void The_duplicate_key_covers_inner_and_aggregate_exceptions()
        {
            Assert.That(LogExceptionEcho.DuplicateKey(new Exception("load", new IOException("disk")), "LogException"),
                Is.Not.EqualTo(LogExceptionEcho.DuplicateKey(new Exception("load", new IOException("net")), "LogException")));
            Assert.That(LogExceptionEcho.DuplicateKey(
                    new AggregateException(new Exception("a"), new Exception("b")), "UnobservedTask"),
                Is.Not.EqualTo(LogExceptionEcho.DuplicateKey(
                    new AggregateException(new Exception("a"), new Exception("c")), "UnobservedTask")));
        }

        [Test]
        public void The_duplicate_key_matches_the_same_exception_and_keeps_stack_and_source()
        {
            var ex = ThrowFromOneSite(new InvalidOperationException("same"));
            Assert.That(LogExceptionEcho.DuplicateKey(ex, "LogException"),
                Is.EqualTo(LogExceptionEcho.DuplicateKey(ex, "LogException")));
            Assert.That(LogExceptionEcho.DuplicateKey(ex, "LogException"),
                Is.Not.EqualTo(LogExceptionEcho.DuplicateKey(ex, "AppDomain")));
            Assert.That(LogExceptionEcho.DuplicateKey(ex, "LogException"),
                Is.Not.EqualTo(LogExceptionEcho.DuplicateKey(new InvalidOperationException("same"), "LogException")),
                "the stack is part of the key");
            Assert.That(LogExceptionEcho.DuplicateKey(new Exception(), "LogException"),
                Is.Not.EqualTo(LogExceptionEcho.DuplicateKey(new ArgumentException(), "LogException")));
        }

        // ExceptionPipeline (Unity-only, not compiled here): the echo may be dropped only when the first path
        // delivered the exception, now or as the duplicate's first call. A nested call, a send that threw, or a
        // sync flush that timed out must leave the echo to report.
        [Test]
        public void The_pipeline_suppresses_the_echo_only_after_a_confirmed_delivery()
        {
            var source = Code(File.ReadAllText(RepoFile("Runtime/Internal/ExceptionPipeline.cs")));

            Assert.That(source, Does.Not.Contain("return payload != null"));
            Assert.That(source, Does.Contain("bool DispatchPayload("));
            Assert.That(source, Does.Contain("bool Send()"));
            Assert.That(Regex.IsMatch(source, @"var\s+completed\s*=\s*MainThreadDispatcher\.RunSync\("), Is.True,
                "the RunSync result (false on timeout) must be used");
            Assert.That(source, Does.Contain("return completed && sent;"));
            Assert.That(Regex.IsMatch(source,
                    @"if\s*\(\s*IsDuplicate\(\s*[\w.:]*LogExceptionEcho\.DuplicateKey\(\s*exception\s*,\s*source\s*\)\s*\)\s*\)\s*\{\s*return\s+EchoOutcome\.Duplicate;"),
                Is.True, "Duplicate (which drops the echo) must use the full-identity key, told apart from a failed delivery");
            Assert.That(Regex.IsMatch(source, @"if\s*\(\s*_reportDepth\s*>\s*0\s*\)\s*\{\s*return\s+EchoOutcome\.NotDelivered;"), Is.True);
            Assert.That(Regex.IsMatch(source,
                @"return\s+DispatchPayload\([^;]*\?\s*EchoOutcome\.Delivered\s*:\s*EchoOutcome\.NotDelivered;"), Is.True);
            Assert.That(Regex.IsMatch(source, @"if\s*\(\s*![\w.:]*LogExceptionEcho\.ShouldExpect\(\s*outcome\s*\)\s*\)"), Is.True);
            Assert.That(source, Does.Not.Contain("!reported"));
            Assert.That(source, Does.Contain("LogExceptionEcho.Expect(exception)"));
        }

        // Source without // comments, so prose cannot satisfy or break an assertion.
        static string Code(string source) =>
            string.Join("\n", source.Split('\n').Select(l =>
            {
                var i = l.IndexOf("//", StringComparison.Ordinal);
                return i >= 0 ? l.Substring(0, i) : l;
            }));

        static string RepoFile(string relative)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json"))) return Path.Combine(dir, relative);
                dir = Directory.GetParent(dir)?.FullName;
            }

            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }
    }
}
