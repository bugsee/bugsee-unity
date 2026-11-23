using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ManagedViewCaptureTests
    {
        // A scene of `n` objects under one scene node, as the walk reads it: preorder, some nested, some with components, bounds and flags.
        static ManagedViewSnapshot Snapshot(int n, bool truncatedLeaf = false)
        {
            var s = new ManagedViewSnapshot(4);
            var root = s.Add(ManagedViewSnapshot.NoParent, 0, null, "UnityPlayer", null);
            s.SetBounds(root, 0, 0, 1080, 2400);
            var scene = s.Add(root, 5, "scene-5", "Scene", "Main \"scene\"");
            var parent = scene;
            for (var i = 0; i < n; i++)
            {
                var node = s.Add(parent, 100 + i, null, i % 3 == 0 ? "Image" : "MeshRenderer", "Obj\\" + i + "é");
                for (var c = 0; c < i % 11; c++) s.AddComponent(node, "Comp" + c);
                if (i % 4 != 3) s.SetBounds(node, i * 3.14159f, i * 2.5f, 10 + i % 50, 20.123f);
                if (i % 7 == 0) parent = node; // nest
                else if (i % 5 == 0) parent = scene;
                if (i == n - 1 && truncatedLeaf) s.SetTruncated(node);
            }

            return s;
        }

        // The same scene built the way the walk did before: nodes with a child list and a joined component string.
        static ManagedViewNode OldPathTree(int n, bool truncatedLeaf = false)
        {
            var root = new ManagedViewNode { Id = 0, ClassName = "UnityPlayer", HasBounds = true, Width = 1080, Height = 2400 };
            root.X = 0; root.Y = 0;
            var scene = new ManagedViewNode { Id = 5, IdText = "scene-5", ClassName = "Scene", Name = "Main \"scene\"" };
            root.Children.Add(scene);
            var parent = scene;
            for (var i = 0; i < n; i++)
            {
                var node = new ManagedViewNode { Id = 100 + i, ClassName = i % 3 == 0 ? "Image" : "MeshRenderer", Name = "Obj\\" + i + "é" };
                string components = null;
                for (var c = 0; c < i % 11; c++)
                {
                    if (c >= 8) continue;
                    components = components == null ? "Comp" + c : components + ",Comp" + c;
                }

                node.Components = components;
                if (i % 4 != 3) { node.HasBounds = true; node.X = i * 3.14159f; node.Y = i * 2.5f; node.Width = 10 + i % 50; node.Height = 20.123f; }
                parent.Children.Add(node);
                if (i % 7 == 0) parent = node;
                else if (i % 5 == 0) parent = scene;
                if (i == n - 1 && truncatedLeaf) node.Truncated = true;
            }

            return root;
        }

        static readonly int[] Rects = { 100, 100, 400, 700, 0, 2000, 50, 2100 };

        [TestCase(0, false)]
        [TestCase(1, false)]
        [TestCase(40, true)]
        [TestCase(1500, false)]
        public void TheSnapshotPathWritesTheSameBytesAsTheOldPath(int n, bool truncated)
        {
            var oldJson = ManagedViewTreeJson.Write(OldPathTree(n, truncated), Rects, 2, 2f, out var oldSecured);
            var s = Snapshot(n, truncated);
            var newJson = ManagedViewTreeJson.Write(s.ToTree(), Rects, 2, 2f, out var newSecured);
            Assert.That(newJson, Is.EqualTo(oldJson));
            Assert.That(newSecured, Is.EqualTo(oldSecured));
            Assert.That(s.Count, Is.EqualTo(n + 2));
        }

        [Test]
        public void TheDeadlineOverloadIsByteIdenticalWhileTheDeadlineHoldsOff()
        {
            var tree = OldPathTree(300);
            var plain = ManagedViewTreeJson.Write(tree, Rects, 2, 1f);
            var timed = ManagedViewTreeJson.Write(OldPathTree(300), Rects, 2, 1f, () => false, out _, out var cut);
            Assert.That(timed, Is.EqualTo(plain));
            Assert.That(cut, Is.False);
        }

        [Test]
        public void ADeadlineThatPassesMidWriteGivesWholeJsonThatSaysItIsCut()
        {
            var calls = 0;
            var tree = OldPathTree(400);
            var json = ManagedViewTreeJson.Write(tree, null, 0, 1f, () => ++calls > 3, out _, out var cut);
            Assert.That(cut, Is.True);
            var doc = JsonDocument.Parse(json).RootElement; // still valid JSON
            Assert.That(CountNodes(doc), Is.LessThan(402).And.GreaterThan(10));
            Assert.That(json, Does.Contain("\"truncated\":true"));
            Assert.That(calls, Is.LessThan(10), "the clock is not read again once the deadline passed");
        }

        [Test]
        public void ADeadlineAlreadyPassedStillWritesTheRootAndSaysTruncated()
        {
            var json = ManagedViewTreeJson.Write(OldPathTree(40), null, 0, 1f, () => true, out _, out var cut);
            var doc = JsonDocument.Parse(json).RootElement;
            Assert.That(doc.GetProperty("class_name").GetString(), Is.EqualTo("UnityPlayer"));
            Assert.That(cut || json.Contains("truncated"), Is.True);
        }

        static int CountNodes(JsonElement e)
        {
            var n = 1;
            if (e.TryGetProperty("subitems", out var kids))
                foreach (var k in kids.EnumerateArray()) n += CountNodes(k);
            return n;
        }

        [Test]
        public void ComponentNamesPastTheLimitAreDroppedAndTheOrderIsKept()
        {
            var s = new ManagedViewSnapshot(4);
            var root = s.Add(ManagedViewSnapshot.NoParent, 0, null, "UnityPlayer", null);
            for (var i = 0; i < 12; i++) s.AddComponent(root, "C" + i);
            Assert.That(s.ToTree().Components, Is.EqualTo("C0,C1,C2,C3,C4,C5,C6,C7"));
        }

        [Test]
        public void ANodeNeedsItsParentFirst()
        {
            var s = new ManagedViewSnapshot(4);
            Assert.Throws<ArgumentOutOfRangeException>(() => s.Add(0, 1, null, "X", null));
            s.Add(ManagedViewSnapshot.NoParent, 0, null, "R", null);
            Assert.Throws<ArgumentOutOfRangeException>(() => s.Add(ManagedViewSnapshot.NoParent, 1, null, "X", null));
            Assert.Throws<ArgumentOutOfRangeException>(() => s.Add(5, 1, null, "X", null));
        }

        [Test]
        public void TheSliceIsFivePercentOfTheFrame()
        {
            Assert.That(ManagedViewRequests.SliceMs(60), Is.EqualTo(1000.0 / 60 * 0.05).Within(1e-9));
            Assert.That(ManagedViewRequests.SliceMs(240), Is.EqualTo(1000.0 / 240 * 0.05).Within(1e-9));
            Assert.That(ManagedViewRequests.SliceMs(0), Is.EqualTo(ManagedViewRequests.SliceMs(60)));
            Assert.That(ManagedViewRequests.SliceMs(double.NaN), Is.EqualTo(ManagedViewRequests.SliceMs(60)));
        }

        [Test]
        public void AChangedRenderSizeMidWalkIsReportedOnceSoTheCallerCanCoverEverything()
        {
            var s = new ManagedViewSnapshot();
            Assert.That(s.NoteRenderSize(540, 1200), Is.False, "the first slice");
            Assert.That(s.NoteRenderSize(540, 1200), Is.False, "the same size again");
            Assert.That(s.NoteRenderSize(1080, 2400), Is.True, "rectangles kept from the old size are in another space");
            Assert.That(s.NoteRenderSize(1080, 2400), Is.False);
        }

        [Test]
        public void SecureRectanglesSeenDuringTheWalkAllStayInForce()
        {
            var s = new ManagedViewSnapshot(4);
            s.AddSecureRects(new[] { 0, 0, 100, 100 }, 1);
            s.AddSecureRects(new[] { 0, 0, 100, 100, 50, 50, 60, 60 }, 2); // the first is known; one new
            s.AddSecureRects(null, 3);
            s.AddSecureRects(new[] { 1, 2, 3, 4 }, 9); // a count past the array is clamped
            Assert.That(s.SecureCount, Is.EqualTo(3));
            Assert.That(s.SecureRects.AsSpan(0, 12).ToArray(), Is.EqualTo(new[] { 0, 0, 100, 100, 50, 50, 60, 60, 1, 2, 3, 4 }));
        }

        [Test]
        public void ARectangleRemovedBeforeTheWalkEndedStillProtectsWhatItCovered()
        {
            var s = Snapshot(3);
            s.AddSecureRects(new[] { 0, 0, 5000, 5000 }, 1); // present at the start of the walk, gone at the end
            var json = ManagedViewTreeJson.Write(s.ToTree(), s.SecureRects, s.SecureCount, 1f);
            Assert.That(json, Does.Not.Contain("Obj"));
        }

        [Test]
        public void TheDeadlinesAreOrderedInsideTheSdksWindow()
        {
            Assert.That(ManagedViewRequests.SnapshotDeadlineMs, Is.LessThan(ManagedViewRequests.WriteDeadlineMs));
            Assert.That(ManagedViewRequests.WriteDeadlineMs, Is.LessThan(ManagedViewRequests.ReplyDeadlineMs));
            Assert.That(ManagedViewRequests.ReplyDeadlineMs, Is.LessThanOrEqualTo(400), "the SDK gives up at 500 ms; keep 100 ms of margin");
        }

        // ---- the engine over fakes ----

        sealed class FakeSource : IManagedViewSource
        {
            public readonly ManagedViewSnapshot Snap;
            public int Remaining;
            public int PerStep = 1;
            public int Steps;
            public bool Abandoned;
            public Exception Throw;
            public Action<int> OnStep;
            public FakeSource(int nodes)
            {
                Snap = new ManagedViewSnapshot(4);
                Snap.Add(ManagedViewSnapshot.NoParent, 0, null, "UnityPlayer", null);
                Snap.SetBounds(0, 0, 0, 100, 100);
                Remaining = nodes;
            }

            public ManagedViewSnapshot Snapshot => Snap;

            public bool Step(Func<bool> sliceExpired)
            {
                Steps++;
                if (Throw != null) throw Throw;
                do
                {
                    if (Remaining == 0) return true;
                    Remaining--;
                    var i = Snap.Add(0, 10 + Snap.Count, null, "Node", "n" + Snap.Count);
                    Snap.SetBounds(i, Snap.Count, 0, 5, 5);
                    OnStep?.Invoke(Snap.Count);
                }
                while (!sliceExpired());
                return Remaining == 0;
            }

            public void Abandon() => Abandoned = true;
        }

        sealed class Rig
        {
            public double Now = 1000;
            public readonly List<Action> Worker = new List<Action>();
            public readonly List<(double delay, Action action, Disposable d)> Timers = new List<(double, Action, Disposable)>();
            public readonly List<ManagedViewCaptureStats> Stats = new List<ManagedViewCaptureStats>();
            public FakeSource Source;
            public int Nodes = 3;
            public double SliceMs = 0.8;
            public double StepClockMs = 0; // advance the clock this much per clock read inside a slice
            public bool WorkerThrows;
            public ManagedViewCaptureEngine Engine;
            public int Sealed;

            public sealed class Disposable : IDisposable
            {
                public bool Disposed;
                public void Dispose() => Disposed = true;
            }

            public Rig()
            {
                Engine = new ManagedViewCaptureEngine(
                    () => { var n = Now; Now += StepClockMs; return n; },
                    a => { if (WorkerThrows) throw new InvalidOperationException("no pool"); Worker.Add(a); },
                    (d, a) => { var t = new Disposable(); Timers.Add((d, a, t)); return t; },
                    budget => Source = new FakeSource(Nodes),
                    s => { Sealed++; s.SecureRects = new[] { 0, 0, 0, 0 }; s.SecureCount = 0; },
                    () => SliceMs,
                    Stats.Add);
            }

            public (ManagedViewRequests.Once once, List<string> replies) Ask() => Ask(Now);

            public (ManagedViewRequests.Once once, List<string> replies) Ask(double at, bool inline = false)
            {
                var replies = new List<string>();
                var once = new ManagedViewRequests.Once(replies.Add);
                Engine.Submit(once, at, () => 1f, inline);
                return (once, replies);
            }

            public void RunWorker() { var w = Worker.ToArray(); Worker.Clear(); foreach (var a in w) a(); }
        }

        [Test]
        public void TheWalkIsSlicedPerFrameAndTheWorkerWritesAndReplies()
        {
            var rig = new Rig { Nodes = 5 };
            var (_, replies) = rig.Ask();
            Assert.That(replies, Is.Empty, "nothing is replied inside the request");
            Assert.That(rig.Source.Steps, Is.EqualTo(0), "the main thread does no walking inside the request (not on iOS inline either unless asked)");
            rig.StepClockMs = 1.0; // every clock read costs 1 ms: a slice of 0.8 ms ends after one node
            rig.Engine.Pump();
            Assert.That(rig.Source.Remaining, Is.EqualTo(4));
            Assert.That(rig.Worker, Is.Empty);
            for (var i = 0; i < 3; i++) rig.Engine.Pump();
            Assert.That(rig.Source.Remaining, Is.EqualTo(1));
            Assert.That(rig.Worker, Is.Empty, "not complete before the last node");
            rig.Engine.Pump();
            Assert.That(rig.Worker.Count, Is.EqualTo(1));
            Assert.That(rig.Sealed, Is.EqualTo(1));
            Assert.That(replies, Is.Empty, "the reply is the worker's, not the main thread's");
            rig.RunWorker();
            Assert.That(replies.Count, Is.EqualTo(1));
            Assert.That(JsonDocument.Parse(replies[0]).RootElement.GetProperty("subitems").GetArrayLength(), Is.EqualTo(5));
            var st = rig.Stats[0];
            Assert.That(st.Outcome, Is.EqualTo("reply"));
            Assert.That(st.Nodes, Is.EqualTo(6));
            Assert.That(st.Slices, Is.EqualTo(5));
            Assert.That(rig.Timers[0].d.Disposed, Is.True, "the deadline timer is released once settled");
        }

        [Test]
        public void ASliceTakesAtLeastOneStepEvenWhenItsBudgetIsZero()
        {
            var rig = new Rig { Nodes = 2, SliceMs = 0 };
            rig.Ask();
            rig.Engine.Pump();
            Assert.That(rig.Source.Remaining, Is.EqualTo(1));
        }

        [Test]
        public void TheWorkerPathGivesTheSameJsonAsTheOldPath()
        {
            using var done = new ManualResetEventSlim();
            var s = Snapshot(1500);
            var src = new PrebuiltSource(s);
            string reply = null;
            int replyThread = 0;
            var engine = new ManagedViewCaptureEngine(
                () => 0.0, // a clock that never reaches a deadline: the test is about the bytes, not about timing
                a => ThreadPool.QueueUserWorkItem(_ => a()),
                (d, a) => new Timer(_ => a(), null, (long)d, Timeout.Infinite),
                b => src,
                x => { x.SecureRects = (int[])Rects.Clone(); x.SecureCount = 2; },
                () => 0.8, _ => { });
            var main = Environment.CurrentManagedThreadId;
            var asked = 0.0;
            engine.Submit(new ManagedViewRequests.Once(j => { reply = j; replyThread = Environment.CurrentManagedThreadId; done.Set(); }), asked, () => 2f);
            while (!done.IsSet) { engine.Pump(); Thread.Sleep(1); }
            Assert.That(done.Wait(5000), Is.True);
            Assert.That(replyThread, Is.Not.EqualTo(main), "the JSON is written and given off the calling thread");
            var expected = ManagedViewTreeJson.Write(OldPathTree(1500), Rects, 2, 2f);
            Assert.That(reply, Is.EqualTo(expected));
        }

        sealed class PrebuiltSource : IManagedViewSource
        {
            public PrebuiltSource(ManagedViewSnapshot s) { Snapshot = s; }
            public ManagedViewSnapshot Snapshot { get; }
            public bool Step(Func<bool> sliceExpired) => true;
            public void Abandon() { }
        }

        [Test]
        public void ARequestMadeWhileACaptureRunsJoinsItAndBothGetTheSameReply()
        {
            var rig = new Rig { Nodes = 2 };
            var a = rig.Ask();
            rig.Engine.Pump();
            var b = rig.Ask();
            Assert.That(rig.Engine.IsBusy, Is.True);
            for (var i = 0; i < 6; i++) rig.Engine.Pump();
            rig.RunWorker();
            Assert.That(a.replies.Count, Is.EqualTo(1));
            Assert.That(b.replies, Is.EqualTo(a.replies));
            Assert.That(rig.Stats.Count, Is.EqualTo(1));
            Assert.That(rig.Stats[0].Requests, Is.EqualTo(2));
            Assert.That(rig.Timers.Count, Is.EqualTo(1), "one job, one timer");
        }

        [Test]
        public void ARequestAfterTheCaptureSettledStartsANewOne()
        {
            var rig = new Rig { Nodes = 1 };
            rig.Ask();
            for (var i = 0; i < 4; i++) rig.Engine.Pump();
            rig.RunWorker();
            var second = rig.Ask();
            Assert.That(rig.Engine.IsBusy, Is.True);
            for (var i = 0; i < 4; i++) rig.Engine.Pump();
            rig.RunWorker();
            Assert.That(second.replies.Count, Is.EqualTo(1));
            Assert.That(rig.Stats.Count, Is.EqualTo(2));
        }

        [Test]
        public void TheDeadlineTimerAnswersNullAndALaterWorkerResultIsIgnored()
        {
            var rig = new Rig { Nodes = 3 };
            var (_, replies) = rig.Ask();
            Assert.That(rig.Timers[0].delay, Is.EqualTo(ManagedViewRequests.ReplyDeadlineMs).Within(1));
            for (var i = 0; i < 5; i++) rig.Engine.Pump(); // the worker is queued but has not run
            rig.Timers[0].action();
            Assert.That(replies, Is.EqualTo(new string[] { null }));
            rig.RunWorker();
            Assert.That(replies.Count, Is.EqualTo(1), "a late result is never sent");
            Assert.That(rig.Stats.Count, Is.EqualTo(1));
            Assert.That(rig.Stats[0].Outcome, Is.EqualTo("deadline"));
        }

        [Test]
        public void AStalledMainThreadStillSettlesThroughTheTimer()
        {
            var rig = new Rig { Nodes = 3 };
            var (_, replies) = rig.Ask();
            rig.Timers[0].action(); // never pumped: the loop is paused or blocked
            Assert.That(replies, Is.EqualTo(new string[] { null }));
            rig.Engine.Pump();
            Assert.That(rig.Source.Abandoned, Is.True, "the walk's hooks are released when the loop runs again");
            Assert.That(rig.Source.Steps, Is.EqualTo(0));
        }

        [Test]
        public void ARequestThatWaitedTooLongForTheMainThreadIsAnsweredNullWithoutAWalk()
        {
            var rig = new Rig();
            var (_, replies) = rig.Ask(rig.Now - ManagedViewRequests.MaxQueueDelayMs - 5);
            Assert.That(replies, Is.EqualTo(new string[] { null }));
            Assert.That(rig.Source, Is.Null);
            Assert.That(rig.Stats[0].Outcome, Is.EqualTo("stale"));
        }

        [Test]
        public void AnExceptionInTheWalkSettlesNull()
        {
            var rig = new Rig();
            var (_, replies) = rig.Ask();
            rig.Source.Throw = new InvalidOperationException("destroyed");
            rig.Engine.Pump();
            Assert.That(replies, Is.EqualTo(new string[] { null }));
            Assert.That(rig.Source.Abandoned, Is.True);
            Assert.That(rig.Stats[0].Outcome, Does.StartWith("error"));
            Assert.That(rig.Engine.IsBusy, Is.False);
        }

        [Test]
        public void AnExceptionOnTheWorkerSettlesNullAndNeverHangs()
        {
            var rig = new Rig { Nodes = 1 };
            var (_, replies) = rig.Ask();
            rig.Source.Snap.SecureRects = null;
            for (var i = 0; i < 3; i++) rig.Engine.Pump();
            // The snapshot is corrupted after the hand-off: a throwing write must not escape the worker.
            rig.Source.Snap.SecureCount = 1;
            rig.Source.Snap.SecureRects = new int[1];
            rig.Source.Snap.PixelsPerNativeUnit = 1f;
            rig.RunWorker();
            Assert.That(replies.Count, Is.EqualTo(1));
            Assert.That(rig.Stats.Count, Is.EqualTo(1));
        }

        [Test]
        public void APoolThatRefusesTheWorkSettlesNull()
        {
            var rig = new Rig { Nodes = 1, WorkerThrows = true };
            var (_, replies) = rig.Ask();
            for (var i = 0; i < 4; i++) rig.Engine.Pump();
            Assert.That(replies, Is.EqualTo(new string[] { null }));
        }

        [Test]
        public void AbortAnswersTheRunningCaptureNullAtOnce()
        {
            var rig = new Rig { Nodes = 50, StepClockMs = 1.0 };
            var a = rig.Ask();
            var b = rig.Ask();
            rig.Engine.Pump();
            rig.Engine.Abort();
            Assert.That(a.replies, Is.EqualTo(new string[] { null }));
            Assert.That(b.replies, Is.EqualTo(new string[] { null }));
            Assert.That(rig.Source.Abandoned, Is.True);
            Assert.That(rig.Timers[0].d.Disposed, Is.True);
            rig.Engine.Pump(); // nothing running: harmless
            Assert.That(rig.Stats[0].Outcome, Is.EqualTo("aborted"));
        }

        [Test]
        public void SlowFramesEndTheWalkBeforeTheNextFrameWouldPassTheDeadline()
        {
            // 125 ms frames: the walk cannot take a slice at 250 ms and still leave the worker its time, so the slice before ends it.
            var rig = new Rig { Nodes = 100000, SliceMs = 0.8, StepClockMs = 0.0001 };
            var (_, replies) = rig.Ask();
            var asked = rig.Now;
            double handedAt = -1;
            for (var frame = 0; frame < 10 && rig.Worker.Count == 0; frame++)
            {
                rig.Now += 125;
                rig.Engine.Pump();
                if (rig.Worker.Count > 0) handedAt = rig.Now - asked;
            }

            Assert.That(rig.Worker.Count, Is.EqualTo(1));
            Assert.That(handedAt, Is.LessThanOrEqualTo(ManagedViewRequests.SnapshotDeadlineMs + 5), "handed over at the last frame before the deadline, not a frame after it");
            rig.RunWorker();
            Assert.That(replies[0], Is.Not.Null);
            Assert.That(replies[0].Contains("\"truncated\":true"), Is.True);
            Assert.That(rig.Source.Abandoned, Is.True);
        }

        [Test]
        public void ARequestThatWaitedLongForTheMainThreadStillGetsItsSlices()
        {
            var rig = new Rig { Nodes = 100000, SliceMs = 0.8, StepClockMs = 1.0 };
            rig.Ask();
            rig.Now += 200; // the main thread was stuck in a long frame; the request waited 200 ms
            rig.Engine.Pump();
            Assert.That(rig.Worker, Is.Empty, "one long wait is not a frame time");
            rig.Now += 16;
            rig.Engine.Pump();
            Assert.That(rig.Worker, Is.Empty);
        }

        [Test]
        public void FastFramesKeepSlicingUntilTheDeadlineIsReallyNear()
        {
            var rig = new Rig { Nodes = 100000, SliceMs = 0.0001, StepClockMs = 0.0001 };
            rig.Ask();
            var asked = rig.Now;
            while (rig.Worker.Count == 0 && rig.Now - asked < 400)
            {
                rig.Now += 16;
                rig.Engine.Pump();
            }

            Assert.That(rig.Now - asked, Is.GreaterThan(ManagedViewRequests.SnapshotDeadlineMs - 20));
        }

        [Test]
        public void APauseFinishesTheWalkInOneGoAndTheWorkerAnswersWhilePaused()
        {
            var rig = new Rig { Nodes = 50, StepClockMs = 1.0 };
            var (_, replies) = rig.Ask();
            rig.Engine.Pump(); // one node: the slice is over
            Assert.That(rig.Source.Remaining, Is.GreaterThan(40));
            rig.Engine.Flush();
            Assert.That(rig.Source.Remaining, Is.EqualTo(0), "no frame will come: the rest is read now, not sliced");
            Assert.That(rig.Worker.Count, Is.EqualTo(1));
            Assert.That(replies, Is.Empty);
            rig.RunWorker();
            Assert.That(JsonDocument.Parse(replies[0]).RootElement.GetProperty("subitems").GetArrayLength(), Is.EqualTo(50));
        }

        [Test]
        public void AFlushWithNothingToFlushOrAnExceptionIsHarmless()
        {
            var rig = new Rig { Nodes = 5 };
            Assert.DoesNotThrow(() => rig.Engine.Flush());
            var (_, replies) = rig.Ask();
            rig.Source.Throw = new InvalidOperationException("gone");
            rig.Engine.Flush();
            Assert.That(replies, Is.EqualTo(new string[] { null }));
            rig.Engine.Flush();
            rig.Engine.Pump();
        }

        [Test]
        public void AbortWithNothingRunningDoesNothing()
        {
            var rig = new Rig();
            Assert.DoesNotThrow(() => rig.Engine.Abort());
            Assert.DoesNotThrow(() => rig.Engine.Pump());
        }

        [Test]
        public void ASmallSceneIsAnsweredInsideTheRequestWhenTheCallerMayAndABigOneIsNot()
        {
            var small = new Rig { Nodes = 10 };
            var (_, r1) = small.Ask(small.Now, inline: true);
            Assert.That(r1.Count, Is.EqualTo(1), "answered before the request returned");
            Assert.That(small.Worker, Is.Empty);
            Assert.That(small.Timers, Is.Empty, "no timer: it cannot be late");

            var big = new Rig { Nodes = ManagedViewRequests.InlineMaxNodes + 20, SliceMs = 1e9 };
            var (_, r2) = big.Ask(big.Now, inline: true);
            Assert.That(r2, Is.Empty);
            Assert.That(big.Worker.Count, Is.EqualTo(1), "handed to the worker");
            big.RunWorker();
            Assert.That(r2.Count, Is.EqualTo(1));

            var notAllowed = new Rig { Nodes = 10 };
            var (_, r3) = notAllowed.Ask(notAllowed.Now, inline: false);
            Assert.That(r3, Is.Empty);
        }

        [Test]
        public void ASceneTooBigForTheSliceStaysAsyncAndRunsOutOfNodesNotOfTime()
        {
            var rig = new Rig { Nodes = 500, SliceMs = 0.8 };
            rig.StepClockMs = 0.3;
            var (_, replies) = rig.Ask(rig.Now, inline: true);
            Assert.That(replies, Is.Empty);
            Assert.That(rig.Engine.IsBusy, Is.True);
        }

        [Test]
        public void ADeadlineThatPassesDuringTheWalkEmitsWhatWasGathered()
        {
            // A real budget: its clock is the rig's, and the snapshot deadline passes after a few nodes.
            double now = 1000;
            FakeSource src = null;
            var jobs = new List<Action>();
            ManagedViewBudget captured = null;
            var replies = new List<string>();
            var engine = new ManagedViewCaptureEngine(
                () => now, jobs.Add, (d, a) => new Rig.Disposable(),
                b => { captured = b; return src = new FakeSource(100000) { OnStep = c => { now += 100; if (captured.IsExhausted) src.Remaining = 0; } }; },
                s => { }, () => 1e9, _ => { });
            engine.Submit(new ManagedViewRequests.Once(replies.Add), now, () => 1f);
            engine.Pump();
            Assert.That(jobs.Count, Is.EqualTo(1));
            Assert.That(src.Snap.Count, Is.LessThan(10));
            jobs[0]();
            Assert.That(replies.Count, Is.EqualTo(1));
            Assert.That(replies[0], Is.Not.Null);
        }
    }
}
