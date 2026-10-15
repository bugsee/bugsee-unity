using System;
using System.Collections.Generic;
using Bugsee.Contracts.Apm;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ManagedSpanTests
    {
        sealed class FakeHandle : ISpanHandle
        {
            public readonly List<string> Calls;
            public readonly List<FakeHandle> Children = new List<FakeHandle>();
            public bool ThrowOnFinish;
            public bool ReturnNullChild;
            public bool ThrowOnSetName;
            public bool ThrowOnRelease;
            public bool ThrowOnScope;
            readonly string _name;

            public FakeHandle(string name, List<string> calls)
            {
                _name = name;
                Calls = calls;
            }

            public string SpanId => "span-" + _name;
            public string TraceId => "trace";
            public void SetName(string name)
            {
                if (ThrowOnSetName) throw new InvalidOperationException("setter failed");
                Calls.Add(_name + ":name=" + name);
            }
            public void SetDescription(string description) => Calls.Add(_name + ":desc=" + description);
            public void SetAttribute(string key, object value) => Calls.Add(_name + ":attr " + key + "=" + string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", value) + ":" + value.GetType().Name);
            public void SetStatus(SpanStatus status) => Calls.Add(_name + ":status=" + status);

            public ISpanHandle StartChild(string operation, string description)
            {
                if (ReturnNullChild) return null;
                var child = new FakeHandle(operation, Calls);
                Children.Add(child);
                Calls.Add(_name + ":child " + operation + " desc=" + (description ?? "<null>"));
                return child;
            }

            public void Finish(SpanStatus status)
            {
                Calls.Add(_name + ":finish " + status);
                if (ThrowOnFinish) throw new InvalidOperationException("native finish failed");
            }

            public int Scopes;
            public int OpenScopes;
            public IDisposable EnterScope()
            {
                if (ThrowOnScope) throw new InvalidOperationException("attach failed");
                Scopes++;
                OpenScopes++;
                return new Scope(this);
            }

            sealed class Scope : IDisposable
            {
                readonly FakeHandle _h;
                public Scope(FakeHandle h) { _h = h; }
                public void Dispose() => _h.OpenScopes--;
            }

            public void Release()
            {
                Calls.Add(_name + ":release");
                if (ThrowOnRelease) throw new InvalidOperationException("release failed");
            }
        }

        static (ManagedSpan span, FakeHandle handle, List<string> calls, List<string> warnings) Make()
        {
            var calls = new List<string>();
            var warnings = new List<string>();
            var handle = new FakeHandle("root", calls);
            return (new ManagedSpan(handle, "op", warnings.Add), handle, calls, warnings);
        }

        [Test]
        public void Ids_and_operation_are_read_once_from_the_handle()
        {
            var (span, _, _, _) = Make();
            Assert.That(span.SpanId, Is.EqualTo("span-root"));
            Assert.That(span.TraceId, Is.EqualTo("trace"));
            Assert.That(span.Operation, Is.EqualTo("op"));
            Assert.That(span.IsFinished, Is.False);
        }

        [Test]
        public void Finish_finishes_then_releases_exactly_once()
        {
            var (span, _, calls, _) = Make();
            span.Finish(SpanStatus.Error);
            span.Finish();
            span.Dispose();
            Assert.That(calls, Is.EqualTo(new[] { "root:finish Error", "root:release" }));
            Assert.That(span.IsFinished, Is.True);
        }

        [Test]
        public void Dispose_finishes_with_ok()
        {
            var (span, _, calls, _) = Make();
            span.Dispose();
            Assert.That(calls, Is.EqualTo(new[] { "root:finish Ok", "root:release" }));
        }

        [Test]
        public void Calls_after_finish_do_not_reach_native()
        {
            var (span, _, calls, _) = Make();
            span.Finish();
            calls.Clear();
            span.SetName("n").SetDescription("d").SetAttribute("k", "v").SetStatus(SpanStatus.Error);
            Assert.That(span.StartChild("child"), Is.InstanceOf<NoopSpan>());
            Assert.That(calls, Is.Empty);
        }

        [Test]
        public void A_throwing_parent_finish_is_swallowed_and_the_handle_is_still_released_once()
        {
            var (span, handle, calls, warnings) = Make();
            handle.ThrowOnFinish = true;
            Assert.DoesNotThrow(() => span.Finish());
            Assert.That(calls, Is.EqualTo(new[] { "root:finish Ok", "root:release" }));
            Assert.That(span.IsFinished, Is.True);
            span.Dispose();
            Assert.That(calls.Count, Is.EqualTo(2));
            Assert.That(warnings.Count, Is.EqualTo(1));
        }

        [Test]
        public void A_throwing_child_finish_does_not_stop_siblings_or_the_parent()
        {
            var (span, handle, calls, warnings) = Make();
            span.StartChild("a");
            span.StartChild("b");
            handle.Children[0].ThrowOnFinish = true;
            calls.Clear();
            Assert.DoesNotThrow(() => span.Dispose());
            Assert.That(calls, Is.EqualTo(new[]
            {
                "a:finish Cancelled", "a:release",
                "b:finish Cancelled", "b:release",
                "root:finish Ok", "root:release"
            }));
            Assert.That(warnings.Count, Is.EqualTo(1));
        }

        [Test]
        public void A_throwing_release_is_swallowed()
        {
            var (span, handle, _, warnings) = Make();
            handle.ThrowOnRelease = true;
            Assert.DoesNotThrow(() => span.Finish());
            Assert.That(warnings.Count, Is.EqualTo(1));
        }

        [Test]
        public void A_throwing_setter_warns_and_leaves_the_span_live()
        {
            var (span, handle, calls, warnings) = Make();
            handle.ThrowOnSetName = true;
            Assert.That(span.SetName("x"), Is.SameAs(span));
            Assert.That(warnings.Count, Is.EqualTo(1));
            Assert.That(span.IsFinished, Is.False);
            span.Finish();
            Assert.That(calls, Is.EqualTo(new[] { "root:finish Ok", "root:release" }));
        }

        [Test]
        public void A_throwing_warning_callback_does_not_escape()
        {
            var handle = new FakeHandle("root", new List<string>()) { ThrowOnFinish = true };
            var span = new ManagedSpan(handle, "op", _ => throw new InvalidOperationException("log"));
            Assert.DoesNotThrow(() => span.Finish());
        }

        [Test]
        public void A_child_that_finished_first_keeps_its_own_status_when_the_parent_finishes()
        {
            var (span, _, calls, _) = Make();
            var child = span.StartChild("db");
            child.Finish(SpanStatus.Timeout);
            span.Finish();
            Assert.That(calls, Does.Contain("db:finish Timeout"));
            Assert.That(calls, Does.Not.Contain("db:finish Cancelled"));
        }

        [Test]
        public void Finishing_a_parent_cancels_live_children_before_itself_and_releases_all()
        {
            var (span, _, calls, _) = Make();
            var child = span.StartChild("db");
            var grand = child.StartChild("query");
            var done = span.StartChild("done");
            done.Finish();
            calls.Clear();

            span.Finish();

            Assert.That(calls, Is.EqualTo(new[]
            {
                "query:finish Cancelled",
                "query:release",
                "db:finish Cancelled",
                "db:release",
                "root:finish Ok",
                "root:release"
            }));
            Assert.That(child.IsFinished, Is.True);
            Assert.That(grand.IsFinished, Is.True);
            Assert.That(done.IsFinished, Is.True);
        }

        [Test]
        public void A_finished_child_is_not_finished_again_by_its_parent()
        {
            var (span, _, calls, _) = Make();
            var child = span.StartChild("db");
            child.Finish(SpanStatus.Timeout);
            calls.Clear();
            span.Finish();
            Assert.That(calls, Is.EqualTo(new[] { "root:finish Ok", "root:release" }));
        }

        [Test]
        public void Child_finish_leaves_the_parent_live()
        {
            var (span, _, calls, _) = Make();
            var child = span.StartChild("db", "select");
            child.Finish();
            Assert.That(span.IsFinished, Is.False);
            Assert.That(calls, Does.Contain("root:child db desc=select"));
            Assert.That(calls.FindAll(c => c == "db:release").Count, Is.EqualTo(1));
        }

        [Test]
        public void Child_without_description_passes_null_and_an_empty_operation_is_refused()
        {
            var (span, _, calls, warnings) = Make();
            span.StartChild("db");
            Assert.That(calls, Does.Contain("root:child db desc=<null>"));
            Assert.That(span.StartChild(""), Is.InstanceOf<NoopSpan>());
            Assert.That(span.StartChild(null), Is.InstanceOf<NoopSpan>());
            Assert.That(warnings.Count, Is.EqualTo(2));
        }

        [Test]
        public void A_native_child_that_is_null_yields_an_inert_span()
        {
            var (span, handle, _, _) = Make();
            handle.ReturnNullChild = true;
            Assert.That(span.StartChild("db"), Is.InstanceOf<NoopSpan>());
        }

        [Test]
        public void Setters_validate_and_chain()
        {
            var (span, _, calls, warnings) = Make();
            Assert.That(span.SetName("n"), Is.SameAs(span));
            span.SetName("");
            span.SetName(null);
            span.SetDescription(null);
            span.SetAttribute("k", 5L);
            span.SetAttribute("big", ulong.MaxValue);
            span.SetAttribute("", "x");
            span.SetAttribute("obj", new object());
            span.SetStatus((SpanStatus)99);
            Assert.That(calls, Is.EqualTo(new[]
            {
                "root:name=n",
                "root:desc=",
                "root:attr k=5:Int64",
                "root:status=Unknown"
            }));
            Assert.That(warnings.Count, Is.EqualTo(5));
        }

        [Test]
        public void Attribute_values_are_made_boxable_before_the_handle()
        {
            var (span, _, calls, _) = Make();
            span.SetAttribute("u", (uint)7);
            span.SetAttribute("d", 1.5m);
            Assert.That(calls, Is.EqualTo(new[] { "root:attr u=7:Int64", "root:attr d=1.5:Double" }));
        }

        [Test]
        public void Concurrent_finishes_release_once()
        {
            var (span, _, calls, _) = Make();
            var tasks = new System.Threading.Tasks.Task[8];
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = System.Threading.Tasks.Task.Run(() => span.Finish());
            System.Threading.Tasks.Task.WaitAll(tasks);
            Assert.That(calls, Is.EqualTo(new[] { "root:finish Ok", "root:release" }));
        }

        [Test]
        public void One_scope_covers_the_whole_finish_cascade()
        {
            var (span, handle, calls, _) = Make();
            var a = span.StartChild("a");
            a.StartChild("a1");
            span.StartChild("b");
            span.Finish();
            Assert.That(handle.Scopes, Is.EqualTo(1));
            Assert.That(handle.OpenScopes, Is.EqualTo(0));
            foreach (var child in handle.Children) Assert.That(child.Scopes, Is.EqualTo(0));
            Assert.That(calls[calls.Count - 1], Is.EqualTo("root:release"));
        }

        [Test]
        public void The_scope_is_closed_when_a_native_finish_throws()
        {
            var (span, handle, _, _) = Make();
            handle.ThrowOnFinish = true;
            span.Finish();
            Assert.That(handle.OpenScopes, Is.EqualTo(0));
        }

        [Test]
        public void A_scope_that_cannot_be_entered_warns_and_the_finish_still_runs()
        {
            var (span, handle, calls, warnings) = Make();
            handle.ThrowOnScope = true;
            Assert.DoesNotThrow(() => span.Finish());
            Assert.That(calls, Is.EqualTo(new[] { "root:finish Ok", "root:release" }));
            Assert.That(warnings.Count, Is.EqualTo(1));
        }

        [Test]
        public void Null_handle_is_rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new ManagedSpan(null, "op"));
        }

        [Test]
        public void Noop_span_accepts_everything()
        {
            var noop = new NoopSpan();
            Assert.That(noop.SetName("a").SetAttribute("k", 1), Is.SameAs(noop));
            Assert.That(noop.StartChild("x"), Is.InstanceOf<NoopSpan>());
            Assert.That(noop.IsFinished, Is.False);
            noop.Finish();
            Assert.That(noop.IsFinished, Is.True);
            noop.Dispose();
            Assert.That(noop.SpanId, Is.Empty);
        }
    }
}
