using System;
using System.Collections.Generic;
using Bugsee.Contracts.Options;

namespace Bugsee.WrapperPolicy
{
    /// <summary>The walk of the Unity scene on the main thread, in slices. Implemented over Unity; tests use a fake.</summary>
    public interface IManagedViewSource
    {
        /// <summary>What the walk fills; the main thread owns it until the walk completes, the worker after.</summary>
        ManagedViewSnapshot Snapshot { get; }

        /// <summary>
        /// Main thread, once per frame. Gathers nodes until <paramref name="sliceExpired"/> answers true (at least one step
        /// is always taken). True when the snapshot is complete, whether the whole scene was read or the budget stopped it.
        /// </summary>
        bool Step(Func<bool> sliceExpired);

        /// <summary>Main thread: the capture ended before the walk did (pause, deadline, error); release what the walk holds.</summary>
        void Abandon();
    }

    /// <summary>What one capture cost and how it ended; passed to the engine's report callback from whichever thread settled it.</summary>
    public sealed class ManagedViewCaptureStats
    {
        public int Nodes;
        public int Chars;
        public int Secured;
        /// <summary>Frames the walk spent on the main thread, the time it took there in total, and the longest single slice.</summary>
        public int Slices;
        public double MainMs;
        public double MaxSliceMs;
        /// <summary>Building the tree and the JSON on the worker.</summary>
        public double WorkerMs;
        /// <summary>From the request to the reply.</summary>
        public double TotalMs;
        /// <summary>The node budget, the walk's deadline or the write's deadline left something out.</summary>
        public bool Truncated;
        /// <summary>The write deadline stopped the JSON.</summary>
        public bool Cut;
        /// <summary>The 3D scene: objects written, whether the reply carries a <c>scene</c> at all, whether it is incomplete, its characters, and the worker time its conversion took.</summary>
        public bool Scene;
        public int SceneObjects;
        public int SceneCameras;
        public bool SceneTruncated;
        public int SceneChars;
        public double SceneWorkerMs;
        /// <summary>Main-thread time of the scene read alone (the rest of <see cref="MainMs"/> is the tree).</summary>
        public double SceneMainMs;
        public int SceneUnits, SceneRenderers;
        /// <summary>How many requests this reply answered (a request made while a capture ran joins it).</summary>
        public int Requests = 1;
        /// <summary>True when a JSON was given; false when the reply was null.</summary>
        public bool Replied;
        /// <summary>"reply", or why the reply is null: "stale", "deadline", "aborted", "error: ...".</summary>
        public string Outcome;
    }

    /// <summary>
    /// One <c>vh</c> capture at a time, in three stages: the main thread reads the scene into a <see cref="ManagedViewSnapshot"/> in
    /// short per-frame slices (<see cref="Pump"/>), a worker thread turns it into JSON and applies the secure rectangles, and the
    /// reply is given from that worker. The main thread never waits for the worker or for anything else. Every stage is bounded by a
    /// deadline counted from the request, and a timer on its own thread answers null when the capture has not replied by
    /// <see cref="ManagedViewRequests.ReplyDeadlineMs"/> (so it fires also when the main thread is stalled or paused), so a request
    /// is always settled and never late. All members except the ones documented otherwise run on the main thread.
    /// </summary>
    public sealed class ManagedViewCaptureEngine
    {
        readonly Func<double> _nowMs;
        readonly Action<Action> _runWorker;
        readonly Func<double, Action, IDisposable> _schedule;
        readonly Func<ManagedViewBudget, IManagedViewSource> _begin;
        readonly Action<ManagedViewSnapshot> _seal;
        readonly Func<double> _sliceMs;
        readonly Action<ManagedViewCaptureStats> _report;
        Job _active;

        /// <param name="nowMs">A monotonic clock in fractional ms, readable from any thread.</param>
        /// <param name="runWorker">Runs the action on a worker thread (the thread pool).</param>
        /// <param name="schedule">Runs the action on some thread after the given delay in ms; disposing the result cancels it.</param>
        /// <param name="begin">Main thread: starts the walk of the scene within the budget.</param>
        /// <param name="seal">Main thread: adds what only the main thread can read (the secure rectangles) once the walk is complete.</param>
        /// <param name="sliceMs">The main-thread time one frame may give the walk.</param>
        /// <param name="report">Called once per capture, on the thread that settled it.</param>
        public ManagedViewCaptureEngine(
            Func<double> nowMs, Action<Action> runWorker, Func<double, Action, IDisposable> schedule,
            Func<ManagedViewBudget, IManagedViewSource> begin, Action<ManagedViewSnapshot> seal, Func<double> sliceMs,
            Action<ManagedViewCaptureStats> report)
        {
            _nowMs = nowMs;
            _runWorker = runWorker;
            _schedule = schedule;
            _begin = begin;
            _seal = seal;
            _sliceMs = sliceMs;
            _report = report;
        }

        /// <summary>True while a capture is running (not yet replied).</summary>
        public bool IsBusy => _active != null && !_active.IsSettled;

        /// <summary>
        /// Main thread. <paramref name="askedAtMs"/> is when the request arrived, on the engine's clock. A request that waited too
        /// long for the main thread is answered null; one made while a capture runs joins it (both get the same JSON).
        /// </summary>
        /// <param name="replyInlineIfSmall">
        /// The caller is inside the SDK's request and its reply cannot time out while the request has not returned (iOS: the timer
        /// starts when the request returns). The first slice is then taken at once, and when it completes a snapshot of at most
        /// <see cref="ManagedViewRequests.InlineMaxNodes"/> nodes the JSON is written and the reply given before returning, as the
        /// capture always did before: no timer, no hop, no silent mode. Anything bigger goes the asynchronous way.
        /// </param>
        /// <param name="level">What the reply carries (<c>None</c> is answered by the caller, never submitted): <c>All</c> shares the walk's time between the tree and the scene.</param>
        public void Submit(
            ManagedViewRequests.Once once, double askedAtMs, Func<float> pixelsPerNativeUnit, bool replyInlineIfSmall = false,
            SceneGraphCapture level = SceneGraphCapture.All)
        {
            if (once == null) return;
            if (ManagedViewRequests.IsStale((long)askedAtMs, (long)_nowMs()))
            {
                once.Reply(null);
                Notify(new ManagedViewCaptureStats { Outcome = "stale", TotalMs = _nowMs() - askedAtMs });
                return;
            }

            var running = _active;
            // A capture started at another level (a relaunch changed the option) is not joined: it may carry more than this request may send.
            if (running != null && running.Level == level && running.TryJoin(once)) return;
            if (running != null && running.Level != level) running.Settle(null, "level changed");
            if (running != null) Release(running);
            _active = null;

            Job job = null;
            try
            {
                // The tree keeps its whole window; with a scene, the scene may read a little past it.
                var sceneDeadline = level == SceneGraphCapture.All ? ManagedViewRequests.SceneReadDeadlineMs : ManagedViewRequests.SnapshotDeadlineMs;
                var budget = new ManagedViewBudget(
                    expired: () => _nowMs() - askedAtMs > ManagedViewRequests.SnapshotDeadlineMs,
                    sceneExpired: () => _nowMs() - askedAtMs > sceneDeadline,
                    level: level);
                var source = _begin(budget);
                float scale;
                try { scale = pixelsPerNativeUnit != null ? pixelsPerNativeUnit() : 1f; }
                catch (Exception) { scale = 1f; }
                source.Snapshot.PixelsPerNativeUnit = scale;
                job = new Job(this, once, askedAtMs, source, level);
                _active = job;
            }
            catch (Exception ex)
            {
                once.Reply(null);
                Notify(new ManagedViewCaptureStats { Outcome = "error: " + ex.Message, TotalMs = _nowMs() - askedAtMs });
                return;
            }

            if (replyInlineIfSmall && job.Source != null)
            {
                Pump(true);
                if (job.IsSettled) return;
            }

            var delay = ManagedViewRequests.ReplyDeadlineMs - (_nowMs() - askedAtMs);
            IDisposable timer = null;
            try { timer = _schedule(delay > 0 ? delay : 0, () => job.Settle(null, "deadline")); }
            catch (Exception) { /* no timer: the stages are still bounded by their own deadlines */ }
            job.SetTimer(timer);
        }

        /// <summary>Main thread, once per frame: advances the walk of the running capture by one slice, and hands it to the worker when it is complete.</summary>
        public void Pump() => Pump(false);

        void Pump(bool inlineSmall)
        {
            var job = _active;
            if (job == null) return;
            if (job.IsSettled)
            {
                _active = null;
                Release(job);
                return;
            }

            if (job.Handed) return;
            var started = _nowMs();
            var sincePump = started - job.LastPumpAt;
            job.LastPumpAt = started;
            job.SliceLimit = started + _sliceMs();
            bool done;
            try
            {
                done = job.Source.Step(job.SliceExpired);
            }
            catch (Exception ex)
            {
                _active = null;
                Release(job);
                job.Settle(null, "error: " + ex.Message);
                return;
            }

            var took = _nowMs() - started;
            job.MainMs += took;
            job.Slices++;
            if (took > job.MaxSliceMs) job.MaxSliceMs = took;
            if (!done)
            {
                // Slow frames: when the next pump would come after the walk's deadline, this is the last slice. The walk stops here and
                // the worker gets the time that is left, instead of the hand-off happening past the write deadline (the reply would be cut
                // to nothing, or answered null by the timer).
                // sincePump is a frame only once a slice has run: before that it is the time the request waited for the main thread.
                if (inlineSmall || job.Slices < 2 || started + sincePump - job.AskedAt <= ManagedViewRequests.SnapshotDeadlineMs) return;
                var snapshot = job.Source.Snapshot;
                if (snapshot.Count > 0 && !snapshot.TreeComplete) snapshot.SetTruncated(0);
                try { job.Source.Abandon(); } catch (Exception) { }
            }

            HandOff(job, inlineSmall);
        }

        void HandOff(Job job, bool inlineSmall)
        {
            try
            {
                _seal(job.Source.Snapshot);
                job.Handed = true;
                var snapshot = job.Source.Snapshot;
                var small = snapshot.Count <= ManagedViewRequests.InlineMaxNodes
                    && (snapshot.Scene == null || snapshot.Scene.ObjectCount <= ManagedViewRequests.InlineMaxNodes);
                if (inlineSmall && small) Work(job);
                else _runWorker(() => Work(job));
            }
            catch (Exception ex)
            {
                _active = null;
                job.Settle(null, "error: " + ex.Message);
            }
        }

        /// <summary>
        /// Main thread: the app is pausing (a manual report opens a native screen, which pauses the player), so no further frame comes
        /// to pump the walk. The rest of it is read now, in one go and still bounded by the walk's budget and deadline, and handed to
        /// the worker, whose thread pool keeps running while the player is paused: the tree reaches the report that caused the pause.
        /// </summary>
        public void Flush()
        {
            var job = _active;
            if (job == null || job.IsSettled || job.Handed) return;
            var started = _nowMs();
            bool done;
            try
            {
                done = job.Source.Step(NeverExpired);
            }
            catch (Exception ex)
            {
                _active = null;
                Release(job);
                job.Settle(null, "error: " + ex.Message);
                return;
            }

            var took = _nowMs() - started;
            job.MainMs += took;
            job.Slices++;
            if (took > job.MaxSliceMs) job.MaxSliceMs = took;
            if (done) HandOff(job, false);
        }

        static bool NeverExpired() => false;

        /// <summary>Main thread: the player is quitting; the running capture cannot finish, so it is answered null now.</summary>
        public void Abort()
        {
            var job = _active;
            if (job == null) return;
            _active = null;
            Release(job);
            job.Settle(null, "aborted");
        }

        // Worker thread.
        void Work(Job job)
        {
            if (job.IsSettled) return;
            var started = _nowMs();
            try
            {
                var snapshot = job.Source.Snapshot;
                var tree = snapshot.ToTree();
                var options = new ManagedViewWriteOptions { EmitBounds = snapshot.EmitBounds, SceneExpired = job.WriteExpired };
                if (snapshot.Scene != null)
                {
                    // Built after the tree has been written in full (inside the write), so it only spends the time the tree left.
                    options.SceneFactory = () =>
                    {
                        if (job.SceneBuildExpired()) return null; // no time left for the scene: the finished tree is answered as it is
                        var sceneStarted = _nowMs();
                        try { return ManagedViewSceneBuilder.Build(snapshot.Scene, job.SceneBuildExpired); }
                        catch (Exception) { return null; } // the scene is best effort: the tree is still answered
                        finally { job.SceneWorkerMs = _nowMs() - sceneStarted; }
                    };
                }

                var json = ManagedViewTreeJson.Write(
                    tree, snapshot.SecureRects, snapshot.SecureCount, snapshot.PixelsPerNativeUnit,
                    job.WriteExpired, out var secured, out var cut, options);
                job.Scene = options.SceneWritten;
                job.SceneObjects = options.SceneSent;
                job.SceneCameras = options.Scene == null ? 0 : options.Scene.Cameras.Count;
                job.SceneTruncated = options.SceneTruncated;
                job.SceneChars = options.SceneChars;
                job.SceneMainMs = snapshot.Scene == null ? 0 : snapshot.Scene.MainMs;
                job.SceneUnits = snapshot.Scene == null ? 0 : snapshot.Scene.ReadUnits;
                job.SceneRenderers = snapshot.Scene == null ? 0 : snapshot.Scene.ReadRenderers;
                job.WorkerMs = _nowMs() - started;
                job.Nodes = snapshot.Count;
                job.Chars = json == null ? 0 : json.Length;
                job.Secured = secured;
                job.Cut = cut;
                job.Truncated = cut || snapshot.AnyTruncated;
                job.Settle(json, json == null ? "error: no tree" : null);
            }
            catch (Exception ex)
            {
                job.Settle(null, "error: " + ex.Message);
            }
        }

        static void Release(Job job)
        {
            if (job.Handed) return;
            try { job.Source.Abandon(); } catch (Exception) { }
        }

        void Notify(ManagedViewCaptureStats stats)
        {
            try { _report?.Invoke(stats); } catch (Exception) { }
        }

        sealed class Job
        {
            readonly ManagedViewCaptureEngine _engine;
            readonly object _sync = new object();
            readonly List<ManagedViewRequests.Once> _waiters = new List<ManagedViewRequests.Once>(2);
            bool _settled;
            IDisposable _timer;

            public readonly double AskedAt;
            public readonly IManagedViewSource Source;
            public readonly Func<bool> SliceExpired;
            public readonly Func<bool> WriteExpired;
            public readonly Func<bool> SceneBuildExpired;
            public double SliceLimit;
            public double LastPumpAt;
            public bool Handed;
            public int Slices;
            public double MainMs, MaxSliceMs, WorkerMs;
            public int Nodes, Chars, Secured;
            public bool Truncated, Cut;
            public bool Scene, SceneTruncated;
            public int SceneObjects, SceneCameras, SceneChars;
            public double SceneWorkerMs, SceneMainMs;
            public int SceneUnits, SceneRenderers;

            public readonly SceneGraphCapture Level;

            public Job(ManagedViewCaptureEngine engine, ManagedViewRequests.Once first, double askedAt, IManagedViewSource source, SceneGraphCapture level)
            {
                Level = level;
                _engine = engine;
                _waiters.Add(first);
                AskedAt = askedAt;
                LastPumpAt = askedAt;
                Source = source;
                SliceExpired = () => engine._nowMs() >= SliceLimit;
                WriteExpired = () => engine._nowMs() - askedAt > ManagedViewRequests.WriteDeadlineMs;
                SceneBuildExpired = () => engine._nowMs() - askedAt > ManagedViewRequests.SceneBuildDeadlineMs;
            }

            public bool IsSettled { get { lock (_sync) return _settled; } }

            /// <summary>False when the capture already settled (the caller starts its own).</summary>
            public bool TryJoin(ManagedViewRequests.Once once)
            {
                lock (_sync)
                {
                    if (_settled) return false;
                    _waiters.Add(once);
                    return true;
                }
            }

            public void SetTimer(IDisposable timer)
            {
                if (timer == null) return;
                var dispose = false;
                lock (_sync)
                {
                    if (_settled) dispose = true;
                    else _timer = timer;
                }

                if (dispose) timer.Dispose();
            }

            /// <summary>Answers every request with <paramref name="json"/> (null when <paramref name="failure"/> says why) the first time only.</summary>
            public void Settle(string json, string failure)
            {
                ManagedViewRequests.Once[] waiters;
                IDisposable timer;
                lock (_sync)
                {
                    if (_settled) return;
                    _settled = true;
                    waiters = _waiters.ToArray();
                    timer = _timer;
                    _timer = null;
                }

                try { timer?.Dispose(); } catch (Exception) { }
                for (var i = 0; i < waiters.Length; i++)
                {
                    try { waiters[i].Reply(json); } catch (Exception) { }
                }

                _engine.Notify(new ManagedViewCaptureStats
                {
                    Nodes = Nodes, Chars = Chars, Secured = Secured, Slices = Slices, MainMs = MainMs, MaxSliceMs = MaxSliceMs,
                    WorkerMs = WorkerMs, TotalMs = _engine._nowMs() - AskedAt, Truncated = Truncated, Cut = Cut,
                    Requests = waiters.Length, Replied = json != null, Outcome = json != null ? "reply" : failure ?? "error",
                    // The scene numbers belong to the reply that won: a null answer (the deadline beat the worker) reports none.
                    Scene = json != null && Scene, SceneObjects = json != null ? SceneObjects : 0, SceneCameras = json != null ? SceneCameras : 0,
                    SceneTruncated = json != null && SceneTruncated, SceneChars = json != null ? SceneChars : 0, SceneWorkerMs = SceneWorkerMs,
                    SceneMainMs = SceneMainMs, SceneUnits = SceneUnits, SceneRenderers = SceneRenderers,
                });
            }
        }
    }
}
