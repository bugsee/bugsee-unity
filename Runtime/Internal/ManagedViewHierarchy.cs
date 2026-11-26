using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Bugsee.Contracts.Options;
using Bugsee.WrapperPolicy;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace Bugsee.Internal
{
    /// <summary>
    /// Answers the native SDK's <c>requestData("vh")</c>: the Unity scene as the managed view hierarchy of the report
    /// (specs: sdk/wrapper-data-requests). The SDK asks on its main thread when it takes a report snapshot on that thread
    /// or Bugsee.CaptureViewHierarchy runs, and waits 500 ms from when the request returns; a later reply is ignored.
    ///
    /// Only Unity's main thread may read the scene (UnityEngine.Object, Transform, Renderer.bounds, Camera: no thread-safety
    /// flag on their icalls). So the main thread does the minimal read into a <see cref="ManagedViewSnapshot"/> of plain values, in
    /// slices of 5% of a frame (<see cref="ManagedViewWalker"/>, driven from <see cref="Pump"/>), and never waits. The tree and the
    /// JSON, the secure-rectangle test and the string escaping (pure managed code, <see cref="ManagedViewCaptureEngine"/>) run on a
    /// thread-pool thread, which also gives the reply: on Android through an attached JNI scope, on iOS through the P/Invoke
    /// completion (the iOS SDK's pass is claimed atomically and records on whichever thread answers). The capture is answered null
    /// by a timer at 400 ms if it has not replied, so a request is never late. A request made while Unity's main thread is blocked
    /// in a synchronous SDK call (a logged exception) is answered null at once.
    /// </summary>
    static class ManagedViewHierarchy
    {
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static readonly ManagedViewCaptureEngine Engine = new ManagedViewCaptureEngine(
            NowMs, RunWorker, Schedule, Begin, Seal, SliceMs, Finished);
        static double _sliceMs = ManagedViewRequests.SliceMs(60);
        static bool _sceneChanged;
        static int _sliceFrame = -1;
        static string Fmt(double v, string format) => v.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        static bool _isDebugBuild; // read on the main thread (Begin); Debug.isDebugBuild is a Unity call

        static double NowMs() => Clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency;

        static int _level = (int)SceneGraphCaptureOption.Default;

        /// <summary>What the reply carries; set from the launch options (<see cref="ApplyLaunchOptions"/>), readable from any thread.</summary>
        internal static SceneGraphCapture Level
        {
            get => (SceneGraphCapture)Volatile.Read(ref _level);
            set => Volatile.Write(ref _level, (int)value);
        }

        /// <summary>
        /// Reads <c>com.bugsee.option.unity.capture-scene-graph</c> from the options a launch or relaunch was given (absent: the default,
        /// <c>All</c>; a relaunch that leaves it out resets it, as for every option). Call where the launch's managed hooks are installed.
        /// </summary>
        internal static void ApplyLaunchOptions(IDictionary<string, object> options)
        {
            object raw = null;
            if (options != null) options.TryGetValue(Options.CaptureSceneGraph, out raw);
            var level = SceneGraphCaptureOption.Parse(raw, out var recognized);
            if (!recognized)
                Debug.LogWarning("[Bugsee] " + Options.CaptureSceneGraph + " is \"" + InvariantText.Of(raw) + "\", not one of None, NamesOnly, All: "
                    + "the managed view hierarchy is not sent (an option that asks for less never sends more).");
            Level = level;
        }

        static void RunWorker(Action work) =>
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { work(); }
                catch (Exception) { /* the engine settles its own failures; nothing may escape a pool thread */ }
            });

        static IDisposable Schedule(double delayMs, Action action) =>
            new Timer(_ =>
            {
                try { action(); }
                catch (Exception) { }
            }, null, (long)Math.Ceiling(delayMs), Timeout.Infinite);

        // 5% of a frame: of the display's (or Application.targetFrameRate's) frame, or of the game's real frame when that is longer
        // (smoothed, since a game that renders at 30 fps on a 60 Hz display has 33 ms frames, and the hitch this capture
        // must not cause is a share of those).
        static double SliceMs()
        {
            if (ManagedViewDiagnostics.SliceMsOverride > 0) return ManagedViewDiagnostics.SliceMsOverride;
            var frameMs = Time.unscaledDeltaTime * 1000.0;
            if (!(frameMs > 0)) return _sliceMs;
            _frameMs = _frameMs <= 0 ? frameMs : _frameMs * 0.7 + frameMs * 0.3;
            return Math.Max(MinSliceMs, Math.Min(_frameMs, MaxFrameMsForSlice) * ManagedViewRequests.FrameShare);
        }

        const double MinSliceMs = 0.1;
        const double MaxFrameMsForSlice = 100; // a 10 fps game gets at most a 5 ms slice
        static double _frameMs;

        /// <summary>
        /// Called on whichever thread the native SDK asks on. <paramref name="reply"/> runs exactly once, with the JSON
        /// or null (nothing to contribute), on the main thread (a null that needs no work) or a worker thread.
        /// <paramref name="pixelsPerNativeUnit"/> is read on the main thread. <paramref name="replyInlineIfSmall"/>: see
        /// <see cref="ManagedViewCaptureEngine.Submit"/> (iOS only).
        /// </summary>
        public static void Handle(string dataType, Func<float> pixelsPerNativeUnit, Action<string> reply, bool replyInlineIfSmall = false)
        {
            if (reply == null) return;
            IDisposable watchdog = null;
            var once = new ManagedViewRequests.Once(json =>
            {
                var t = Volatile.Read(ref watchdog);
                if (t != null) try { t.Dispose(); } catch (Exception) { }
                ManagedViewDiagnostics.NoteReply(json);
                reply(json);
            });
            var level = Level;
            if (dataType != ManagedViewTreeJson.DataType || level == SceneGraphCapture.None || ManagedViewRequests.IsMainThreadInBlockingNativeCall)
            {
                once.Reply(null);
                return;
            }

            // A request that waits for a main thread that is stalled or paused has no capture yet, so no timer of the engine's: this
            // one makes sure it is still answered inside the SDK's window (never from the main thread's queue after it).
            if (!MainThreadDispatcher.IsMainThread)
                Volatile.Write(ref watchdog, Schedule(ManagedViewRequests.ReplyDeadlineMs, () => once.Reply(null)));

            var askedAt = NowMs();
            MainThreadDispatcher.Run(() =>
            {
                try
                {
                    Engine.Submit(once, askedAt, pixelsPerNativeUnit, replyInlineIfSmall, level);
                    if (replyInlineIfSmall) _sliceFrame = Time.frameCount; // the inline slice is this frame's
                }
                catch (Exception ex)
                {
                    // Nothing to contribute: the report still carries the native half.
                    LogLater(ex.Message);
                    once.Reply(null);
                }
            });
        }

        /// <summary>Main thread, every frame (MainThreadDispatcher.Update): one slice of the running capture.</summary>
        internal static void Pump()
        {
            // One slice per frame: an iOS request that took its first slice inline in this frame does not get a second one.
            var frame = Time.frameCount;
            if (frame == _sliceFrame) return;
            _sliceFrame = frame;
            try { Engine.Pump(); }
            catch (Exception ex) { LogLater(ex.Message); }
        }

        /// <summary>Main thread: the app pauses and no frame will pump the walk; the rest of it is read now and the worker finishes the capture.</summary>
        internal static void Flush()
        {
            try { Engine.Flush(); }
            catch (Exception ex) { LogLater(ex.Message); }
        }

        /// <summary>Main thread: the player quits; the running capture is answered null now.</summary>
        internal static void Abort()
        {
            try { Engine.Abort(); }
            catch (Exception ex) { LogLater(ex.Message); }
        }

        static IManagedViewSource Begin(ManagedViewBudget budget)
        {
            _isDebugBuild = Debug.isDebugBuild;
            _frameMs = 0; // the smoothing starts from this capture's own frames
            var hz = Application.targetFrameRate;
#if UNITY_2022_2_OR_NEWER
            var display = Screen.currentResolution.refreshRateRatio.value;
#else
            double display = Screen.currentResolution.refreshRate;
#endif
            if (!(display >= 1.0)) display = 60.0;
            _sliceMs = ManagedViewRequests.SliceMs(hz > 0 ? Math.Min(hz, display) : display);
            return new ManagedViewWalker(budget);
        }

        static int[] _rectScratch;

        // Main thread: the secure rectangles that exist now join those seen earlier in the walk (see ManagedViewSnapshot.AddSecureRects).
        internal static void CollectSecureRects(ManagedViewSnapshot snapshot)
        {
            var count = SecureRectRegistry.Instance.CopyRects(0, ref _rectScratch);
            // The registry holds native pixels, the node boxes are Unity render pixels (Resolution Scaling, Screen.SetResolution, a
            // surface offset, an iOS scale that differs from the screen's): both go into the render pixels before the overlap test.
            if (count > 0)
            {
                global::Bugsee.Bugsee.ConvertSecureRectsToRender(_rectScratch, count);
                // The rectangles an earlier slice kept are in that slice's render space: a changed size covers everything for the rest of the walk.
                if (snapshot.NoteRenderSize(Screen.width, Screen.height))
                {
                    var all = new[] { -SecureRectSpace.Far, -SecureRectSpace.Far, SecureRectSpace.Far, SecureRectSpace.Far };
                    snapshot.AddSecureRects(all, 1);
                }
            }
            snapshot.AddSecureRects(_rectScratch, count);
        }

        static void Seal(ManagedViewSnapshot snapshot)
        {
            CollectSecureRects(snapshot);
            if (_sceneChanged && snapshot.Count > 0) snapshot.SetTruncated(0);
        }

        // Called on whichever thread settled the capture, which may be the SDK's own thread inside a request.
        static void Finished(ManagedViewCaptureStats stats)
        {
            ManagedViewDiagnostics.Record(stats);
            if (!_isDebugBuild && !ManagedViewDiagnostics.LogEveryCapture) return;
            // Logged on the next frame: a Debug.Log is captured by the SDK, and the SDK may be inside requestData right now. This runs on
            // the worker or the timer, so it must not create the dispatcher (a destroyed one is not recreated off the main thread).
            MainThreadDispatcher.PostIfAlive(() =>
            {
                if (stats.Outcome == "reply")
                    Debug.Log("[Bugsee] managed view hierarchy: " + stats.Nodes + " nodes, " + stats.Chars + " chars, walk "
                        + Fmt(stats.MainMs, "F1") + " ms in " + stats.Slices + " slices (longest " + Fmt(stats.MaxSliceMs, "F2")
                        + " ms), write " + Fmt(stats.WorkerMs, "F1") + " ms, total " + Fmt(stats.TotalMs, "F0") + " ms"
                        + (stats.Truncated ? ", truncated" : "") + ", secured " + stats.Secured
                        + (stats.Scene
                            ? "; scene " + stats.SceneObjects + " objects, " + stats.SceneCameras + " cameras, " + stats.SceneChars + " chars (units " + stats.SceneUnits + ", renderers looked at " + stats.SceneRenderers + "), read "
                                + Fmt(stats.SceneMainMs, "F1") + " ms, convert+write " + Fmt(stats.SceneWorkerMs, "F1") + " ms" + (stats.SceneTruncated ? ", truncated" : "")
                            : "; no scene"));
                else if (stats.Outcome.StartsWith("error", StringComparison.Ordinal))
                    Debug.LogWarning("[Bugsee] The managed view hierarchy could not be read: " + stats.Outcome);
                else
                    Debug.Log("[Bugsee] managed view hierarchy: answered null (" + stats.Outcome + ") after " + Fmt(stats.TotalMs, "F0") + " ms");
            });
        }

        static void LogLater(string error)
        {
            MainThreadDispatcher.Post(() => Debug.LogWarning("[Bugsee] The managed view hierarchy could not be read: " + error));
        }

        internal static void NoteSceneChange() => _sceneChanged = true;
        internal static void ClearSceneChange() => _sceneChanged = false;
    }

    /// <summary>
    /// The main-thread half: reads the scene into a <see cref="ManagedViewSnapshot"/> a slice at a time with an explicit stack (no
    /// recursion, so a slice can stop anywhere). Everything it holds across frames is a Unity object that may be destroyed
    /// before the next slice, so each step checks it (Unity null) and a step that throws gives up that object's remaining
    /// children and nothing else. Objects move between slices: the snapshot is not one instant.
    /// </summary>
    sealed class ManagedViewWalker : IManagedViewSource
    {
        static readonly Vector3[] Corners = new Vector3[4];
        static readonly float[] CornerValues = new float[12];
        static readonly Vector3[] BoxCorners = new Vector3[8];
        static readonly List<Component> ComponentBuffer = new List<Component>();
        static readonly Dictionary<Type, string> TypeNames = new Dictionary<Type, string>();
        static readonly List<string> NameBuffer = new List<string>(16);

        // The canvas a uGUI element sits under: an overlay canvas lays out in screen pixels already, any other is projected by a camera.
        struct CanvasContext
        {
            public bool Valid;
            public bool Projected; // a camera projects the corners (a destroyed one gives no geometry, never wrong geometry)
            public Camera Camera;  // not projected: the corners already are screen pixels
        }

        struct Frame
        {
            public Transform Transform;   // children come from here, or
            public GameObject[] Roots;    // from this list (a scene frame)
            public int Next;
            public int Node;              // snapshot index of the node the children hang under
            public int Depth;             // depth of that node
            public CanvasContext Canvas;
            public int Count;             // children it had when last looked at: fewer now means an earlier sibling went away and the indexes shifted
            public Scene SceneHandle;     // a scene frame: the scene whose roots are listed
        }

        readonly ManagedViewSnapshot _snapshot = new ManagedViewSnapshot();
        readonly ManagedViewBudget _budget;
        int _width, _height; // refreshed each slice (a rotation or a resolution change between slices)
        readonly Camera _camera;
        readonly MainThreadDispatcher _host;
        readonly List<Scene> _scenes = new List<Scene>();
        readonly HashSet<int> _seen = new HashSet<int>(); // objects move between slices: one that was read is not read again
        readonly Dictionary<Camera, ManagedViewProjection> _projections = new Dictionary<Camera, ManagedViewProjection>();
        // What the tree walk hands to the 3D scene read (null when the capture carries no scene): the scene read walks only what the tree did not.
        readonly ManagedViewSceneFeed<Renderer, Transform> _feed;
        Frame[] _stack = new Frame[32];
        int _sp;
        int _sceneIndex;
        int _root;
        bool _finished;
        bool _treeDone;

        public ManagedViewSnapshot Snapshot => _snapshot;

        public ManagedViewWalker(ManagedViewBudget budget)
        {
            _budget = budget;
            _snapshot.EmitBounds = budget.Level == SceneGraphCapture.All;
            if (budget.Level == SceneGraphCapture.All)
            {
                _snapshot.Scene = new ManagedViewSceneSnapshot();
                _feed = new ManagedViewSceneFeed<Renderer, Transform>();
            }

            _width = Screen.width;
            _height = Screen.height;
            _camera = Camera.main;
            _root = _snapshot.Add(ManagedViewSnapshot.NoParent, 0, null, "UnityPlayer", null);
            _snapshot.SetBounds(_root, 0, 0, _width, _height);
            _budget.TryTake(1);

            // Scenes first, then the DontDestroyOnLoad scene (SceneManager never lists it; Bugsee's own dispatcher object lives
            // there, so its scene is found through that object, and the object itself is left out).
            _host = MainThreadDispatcher.Ensure();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) _scenes.Add(scene);
            }

            if (_host != null && _host.gameObject.scene.IsValid()) _scenes.Add(_host.gameObject.scene);

            ManagedViewHierarchy.ClearSceneChange();
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ManagedViewHierarchy.NoteSceneChange();
        static void OnSceneUnloaded(Scene scene) => ManagedViewHierarchy.NoteSceneChange();

        // The capture ended before the walk did (the deadline, a pause, an error): a scene not read to the end says it is incomplete.
        public void Abandon()
        {
            var scene = _snapshot.Scene;
            if (scene != null && !_sceneDone)
            {
                scene.Incomplete = true;
                if (scene.ObjectCount == 0 && scene.Skipped == null) scene.Skipped = ManagedViewSceneSnapshot.SkippedTimeBudget;
            }

            Finish();
        }

        void Finish()
        {
            if (_finished) return;
            _finished = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            Array.Clear(_stack, 0, _stack.Length);
            _sp = 0;
        }

        public bool Step(Func<bool> sliceExpired)
        {
            if (_finished) return true;
            ManagedViewHierarchy.CollectSecureRects(_snapshot);
            _width = Screen.width;
            _height = Screen.height;
            _projections.Clear(); // a camera that moved since the last slice is read again, not projected with old matrices
            if (!_treeDone)
            {
                do
                {
                    if (_sp == 0)
                    {
                        if (!StartNextScene()) { _treeDone = true; _snapshot.TreeComplete = true; break; } // TreeComplete: every cut is flagged where it happened (a node, a frame, the root)
                        continue;
                    }

                    try
                    {
                        if (!StepFrame()) _sp--;
                    }
                    catch (Exception)
                    {
                        // Destroyed under the walk, a missing script: this object's remaining children are lost, not the tree.
                        _sp--;
                        if (_feed != null) _feed.MarkBroken(); // the scene read walks them itself
                    }
                }
                while (!sliceExpired());
                if (!_treeDone) return false;
            }

            // The tree is read; the 3D scene follows, best effort, in the same slices (the main thread's share per frame is the same).
            if (_snapshot.Scene != null && !_sceneDone)
            {
                if (sliceExpired()) return false;
                var started = Stopwatch.GetTimestamp();
                try { _sceneDone = SceneStep(sliceExpired); }
                catch (Exception)
                {
                    // The scene is best effort: whatever was read stands, and the document says it is incomplete.
                    _snapshot.Scene.Incomplete = true;
                    if (_snapshot.Scene.ObjectCount == 0) _snapshot.Scene.Skipped = ManagedViewSceneSnapshot.SkippedError;
                    _sceneDone = true;
                }

                _snapshot.Scene.MainMs += (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                if (!_sceneDone) return false;
            }

            Finish();
            return true;
        }

        // False: the scene list is done (or the budget is spent).
        bool StartNextScene()
        {
            while (_sceneIndex < _scenes.Count)
            {
                if (_budget.IsExhausted || !_budget.TryTake(2))
                {
                    _snapshot.SetTruncated(_root);
                    FeedCut(null);
                    return false;
                }

                var scene = _scenes[_sceneIndex++];
                try
                {
                    var node = _snapshot.Add(_root, scene.handle, "scene-" + scene.handle, "Scene", scene.name);
                    if (!scene.isLoaded) { if (_feed != null) _feed.MarkBroken(); continue; } // unloaded since the walk began: an empty scene node, and the tree is flagged
                    Push(new Frame { Roots = scene.GetRootGameObjects(), Node = node, Depth = 2, SceneHandle = scene });
                    return true;
                }
                catch (Exception)
                {
                    // The scene went away: nothing below it.
                    if (_feed != null) _feed.MarkBroken();
                }
            }

            return false;
        }

        void Push(Frame frame)
        {
            if (_sp == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
            _stack[_sp++] = frame;
        }

        static bool SceneRootsUnchanged(Frame f)
        {
            try
            {
                if (!f.SceneHandle.isLoaded) return true;
                if (f.SceneHandle.rootCount != f.Roots.Length) return false;
                for (var i = 0; i < f.Roots.Length; i++) if (f.Roots[i] == null) return false; // one went away and another may have come: the count says nothing
                return true;
            }
            catch (Exception) { return false; }
        }

        // One unit of work on the top frame; false when the frame has no more children.
        bool StepFrame()
        {
            var f = _stack[_sp - 1];
            if (f.Roots != null)
            {
                if (f.Next >= f.Roots.Length)
                {
                    // Roots created since the scene was listed are not in this frame: the scene read would find them.
                    if (_feed != null && !SceneRootsUnchanged(f)) _feed.MarkBroken();
                    return false;
                }

                var root = f.Roots[f.Next];
                if (root != null && _host != null && root == _host.gameObject) { f.Next++; _stack[_sp - 1] = f; return true; }
                if (_budget.IsExhausted) { _snapshot.SetTruncated(f.Node); FeedCut(null); return false; }
                f.Next++;
                _stack[_sp - 1] = f;
                if (Visit(root, 3, default, f.Node)) _snapshot.SetTruncated(f.Node);
                return true;
            }

            if (f.Transform == null) { _snapshot.SetTruncated(f.Node); if (_feed != null) _feed.MarkBroken(); return false; } // destroyed between slices: its other children are lost
            var childCount = f.Transform.childCount;
            if (_feed != null && childCount < f.Count) _feed.MarkBroken(); // a child went away: the next one is not at the index the walk holds
            f.Count = childCount;
            _stack[_sp - 1] = f;
            if (f.Next >= childCount) return false;
            if (_budget.IsExhausted) { _snapshot.SetTruncated(f.Node); FeedCut(null); return false; }
            var child = f.Transform.GetChild(f.Next++).gameObject;
            _stack[_sp - 1] = f;
            if (Visit(child, f.Depth + 1, f.Canvas, f.Node)) _snapshot.SetTruncated(f.Node);
            return true;
        }

        // Reads one object; true when the budget cut it off (the caller marks itself truncated). A node whose own read throws is left out.
        bool Visit(GameObject go, int depth, CanvasContext canvas, int parent)
        {
            if (go == null || !go.activeInHierarchy) return false;
            int id;
            try { id = go.GetInstanceID(); }
            catch (Exception) { if (_feed != null) _feed.MarkBroken(); return false; }
            if (!_seen.Add(id))
            {
                // read already (moved between slices): neither a node nor budget. It moved under the walk, so the feed (what was seen where)
                // is no longer the tree the scene read would find: the scene read walks it itself.
                if (_feed != null) _feed.MarkBroken();
                return false;
            }
            if (!_budget.TryTake(depth))
            {
                // Out of nodes or time (permanent: the rest of the scene is the scene read's), or below the depth limit (the walk goes on, so
                // the tree is not a prefix of the scene: the scene read walks it all).
                if (_feed != null)
                {
                    if (_budget.IsExhausted) FeedCut(go); else _feed.MarkBroken();
                }

                return true;
            }

            int node;
            Transform transform;
            Canvas own;
            Renderer renderer;
            try
            {
                var name = go.name;
                var className = ReadComponents(go, out transform, out own, out renderer);
                if (className == null) className = transform is RectTransform ? "RectTransform" : "GameObject";
                node = _snapshot.Add(parent, id, null, className, name);
                for (var i = 0; i < NameBuffer.Count; i++) _snapshot.AddComponent(node, NameBuffer[i]);
                if (_feed != null) _feed.Visited(renderer != null, renderer, id, renderer != null ? go.layer : 0);
            }
            catch (Exception)
            {
                if (_feed != null) _feed.MarkBroken();
                return false;
            }

            try
            {
                if (own != null) canvas = CanvasOf(own, _camera);
                var rect = transform as RectTransform;
                if (rect != null && canvas.Valid)
                {
                    ReadWorldCorners(rect);
                    SetBounds(node, Corners, canvas.Camera, canvas.Projected);
                }
                else if (_camera != null && renderer != null && renderer.enabled)
                {
                    var b = renderer.bounds;
                    var c = b.center;
                    var e = b.extents;
                    var k = 0;
                    for (var sx = -1; sx <= 1; sx += 2)
                    for (var sy = -1; sy <= 1; sy += 2)
                    for (var sz = -1; sz <= 1; sz += 2)
                        BoxCorners[k++] = new Vector3(c.x + sx * e.x, c.y + sy * e.y, c.z + sz * e.z);
                    SetBounds(node, BoxCorners, _camera, true);
                }

                var kids = transform.childCount;
                if (kids > 0) Push(new Frame { Transform = transform, Node = node, Depth = depth, Canvas = canvas, Count = kids });
            }
            catch (Exception)
            {
                // Keep what this object has; its siblings are read by the caller.
                if (_feed != null) _feed.MarkBroken(); // (its children may not have been walked)
            }

            return false;
        }

        // The budget stopped the tree: records, for the scene read, everything the tree has not visited, in the order the scene read walks it
        // (the node that was refused, the rest of each open frame from the innermost out, the scenes not started). Nothing is visited after the cut.
        void FeedCut(GameObject refused)
        {
            if (_feed == null || !_feed.BeginCut(_sceneIndex)) return;
            if (refused != null) _feed.AddRemaining(refused.transform, -1);
            for (var i = _sp - 1; i >= 0; i--)
            {
                var f = _stack[i];
                if (f.Roots != null)
                {
                    if (!SceneRootsUnchanged(f)) { _feed.MarkBroken(); return; } // a root was created since the scene was listed: the scene read lists them again
                    for (var k = f.Next; k < f.Roots.Length; k++)
                    {
                        var root = f.Roots[k];
                        if (root == null || (_host != null && root == _host.gameObject)) continue;
                        _feed.AddRemaining(root.transform, -1);
                    }
                }
                else if (f.Transform != null)
                {
                    _feed.AddRemaining(f.Transform, f.Next);
                }
            }
        }

        // Screen Space - Overlay lays out in screen pixels. Screen Space - Camera with no camera assigned renders as overlay too, so it
        // is read as one. World Space is projected by its event camera, or the main camera when it has none.
        static CanvasContext CanvasOf(Canvas canvas, Camera mainCamera)
        {
            var root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            switch (root.renderMode)
            {
                case RenderMode.ScreenSpaceOverlay:
                    return new CanvasContext { Valid = true };
                case RenderMode.ScreenSpaceCamera:
                    return new CanvasContext { Valid = true, Camera = root.worldCamera, Projected = root.worldCamera != null };
                default:
                    var camera = root.worldCamera != null ? root.worldCamera : mainCamera;
                    return new CanvasContext { Valid = camera != null, Camera = camera, Projected = camera != null };
            }
        }

        // Projects the corners to Unity screen pixels (origin bottom left) and stores their box with the origin at the top left.
        // A corner behind the camera, or one that is not a number, leaves the node without geometry rather than with a wrong box.
        void SetBounds(int node, Vector3[] corners, Camera camera, bool projected)
        {
            ManagedViewProjection projection = null;
            if (projected)
            {
                if (camera == null) return; // destroyed between slices
                projection = ProjectionOf(camera);
            }

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (var i = 0; i < corners.Length; i++)
            {
                var p = corners[i];
                float px, py;
                if (projection != null)
                {
                    if (!projection.TryProject(p.x, p.y, p.z, out px, out py)) return;
                }
                else
                {
                    px = p.x;
                    py = p.y;
                }

                if (float.IsNaN(px) || float.IsNaN(py)) return;
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
            }

            if (!ManagedViewGeometry.TryBox(minX, minY, maxX, maxY, _width, _height, out var x, out var y, out var w, out var h)) return;
            _snapshot.SetBounds(node, x, y, w, h);
        }

        // The camera's matrices, copied once per capture (a camera that is destroyed between slices is a Unity null: its nodes get no geometry).
        ManagedViewProjection ProjectionOf(Camera camera)
        {
            if (_projections.TryGetValue(camera, out var cached)) return cached;
            var view = camera.worldToCameraMatrix;
            var proj = camera.projectionMatrix;
            var pixels = camera.pixelRect;
            var v = new float[16];
            var q = new float[16];
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
            {
                v[r * 4 + c] = view[r, c];
                q[r * 4 + c] = proj[r, c];
            }

            cached = new ManagedViewProjection(v, q, pixels.x, pixels.y, pixels.width, pixels.height);
            _projections[camera] = cached;
            return cached;
        }

        // ------------------------------------------------------------------------------------------------------------------
        // The 3D scene (SceneGraphCapture.All): the cameras, then every renderer drawn last frame and every world-space canvas, read as
        // plain values (ManagedViewSceneSnapshot); the conversion to the wire's convention, the selection by view volume and the JSON are the
        // worker's. The enumeration is the sliced part: a unit is one subtree read with a single GetComponentsInChildren call, and a
        // transform with many children is split into its children, so no single call is larger than a small subtree.
        // ------------------------------------------------------------------------------------------------------------------

        // Objects read at most (twice what a reply carries): the rest cannot be sent, and the document says it is incomplete.
        const int MaxSceneObjectsRead = ManagedViewSceneJson.DefaultMaxObjects * 2;

        static readonly Dictionary<Type, SceneObjectKind> RendererKinds = new Dictionary<Type, SceneObjectKind>();
        static readonly float[] Matrix16 = new float[16];
        static readonly float[] CameraPose = new float[7];
        static readonly float[] CameraView = new float[16];
        static readonly float[] CameraProjection = new float[16];
        static readonly float[] CameraRect = new float[4];
        static readonly float[] IdentityMatrix = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        readonly List<Transform> _units = new List<Transform>(64);
        readonly List<int> _next = new List<int>(64);
        Canvas[] _canvases;
        int _canvasIndex;
        bool _sceneDone;
        bool _useFeed;
        bool _feedTailChecked;
        int _feedIndex;
        int _scenePhase; // 0 cameras and seeding, 1 enumerating the units, 2 the canvases
        int _sceneSeen;

        // True when the scene is complete (or has been given up). Main thread; one unit of work per check of the slice.
        bool SceneStep(Func<bool> sliceExpired)
        {
            var scene = _snapshot.Scene;
            if (_scenePhase == 0)
            {
                ReadCameras(scene);
                scene.TimeScale = Time.timeScale;
                scene.Frame = Time.frameCount;
                var names = new string[32];
                for (var i = 0; i < names.Length; i++) names[i] = LayerMask.LayerToName(i);
                scene.LayerNames = names;
                if (scene.CameraCount == 0)
                {
                    // The selection is "drawn by a camera": with none, nothing qualifies, and that is a complete, empty scene.
                    scene.Skipped = ManagedViewSceneSnapshot.SkippedNoCamera;
                    return true;
                }

                SeedUnits(scene);
                _scenePhase = 1;
                if (sliceExpired()) return false;
            }

            if (_scenePhase == 1)
            {
                // First the renderers the tree walk already found (no second read of those objects), then the part of the scene the tree
                // did not reach, walked as before.
                var fed = _useFeed ? _feed.Renderers : null;
                while (fed != null && _feedIndex < fed.Count)
                {
                    if (SceneLimitReached(scene)) return true;
                    var entry = fed[_feedIndex++];
                    try { ReadFed(scene, entry); }
                    catch (Exception)
                    {
                        // A renderer destroyed under the read: it is not in the scene.
                    }

                    if (sliceExpired()) return false;
                }

                // (the old walk checked the limits before every step and its own stack was still open after the last object, so a limit reached
                // by the very last object, or an expired scene time with nothing read, counted too)
                if (fed != null && !_feedTailChecked)
                {
                    _feedTailChecked = true;
                    if (_units.Count == 0 && SceneLimitReached(scene)) return true;
                }

                while (_units.Count > 0)
                {
                    if (SceneLimitReached(scene)) return true;

                    try { ReadUnit(scene); }
                    catch (Exception)
                    {
                        // An object destroyed under the read, a missing script: this transform's remaining children are lost.
                        if (_units.Count > 0) Pop();
                    }

                    if (sliceExpired()) return false;
                }

                _scenePhase = 2;
                if (sliceExpired()) return false;
            }

            // Phase 2: the world-space and camera canvases, one per step.
            if (_canvases == null)
            {
                // The objects were read over several frames: the cameras are read again, so the selection uses where they ended up.
                try { scene.ClearCameras(); ReadCameras(scene); }
                catch (Exception) { scene.Incomplete = true; }

                if (_budget.SceneExpired)
                {
                    // No time left for the canvases: the scene is a part of what there is.
                    scene.Incomplete = true;
                    return true;
                }

#if UNITY_2022_2_OR_NEWER
                _canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
#else
                _canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
#endif
                _canvasIndex = 0;
                if (sliceExpired()) return false;
            }

            while (_canvasIndex < _canvases.Length)
            {
                if (_budget.SceneExpired || scene.ObjectCount >= MaxSceneObjectsRead) { scene.Incomplete = true; return true; }
                try { ReadCanvas(scene, _canvases[_canvasIndex++]); }
                catch (Exception) { scene.Incomplete = true; /* the canvases that were not read are missing: the counts are not exact */ }
                if (sliceExpired()) return false;
            }

            scene.Seen = _sceneSeen;
            return true;
        }

        // Out of time (or of room) with objects unread: what was read is a part of the scene.
        bool SceneLimitReached(ManagedViewSceneSnapshot scene)
        {
            if (!_budget.SceneExpired && scene.ObjectCount < MaxSceneObjectsRead) return false;
            scene.Incomplete = true;
            if (scene.ObjectCount == 0) scene.Skipped = ManagedViewSceneSnapshot.SkippedTimeBudget;
            _units.Clear();
            _next.Clear();
            return true;
        }

        void ReadCameras(ManagedViewSceneSnapshot scene)
        {
            var count = Camera.allCamerasCount;
            if (count <= 0) return;
            var cameras = new Camera[count];
            count = Camera.GetAllCameras(cameras);
            for (var i = 0; i < count; i++)
            {
                var camera = cameras[i];
                if (camera == null) continue;
                try
                {
                    var t = camera.transform;
                    var p = t.position;
                    var q = t.rotation;
                    CameraPose[0] = p.x; CameraPose[1] = p.y; CameraPose[2] = p.z;
                    CameraPose[3] = q.x; CameraPose[4] = q.y; CameraPose[5] = q.z; CameraPose[6] = q.w;
                    CopyMatrix(camera.worldToCameraMatrix, CameraView);
                    CopyMatrix(camera.projectionMatrix, CameraProjection);
                    var rect = camera.rect;
                    CameraRect[0] = rect.x; CameraRect[1] = rect.y; CameraRect[2] = rect.width; CameraRect[3] = rect.height;
                    scene.AddCamera(
                        camera.gameObject.GetInstanceID(), CameraPose, CameraView, CameraProjection, camera.nearClipPlane, camera.farClipPlane,
                        camera.depth, CameraRect, camera.cullingMask, camera == _camera, camera.targetTexture != null, camera.orthographic);
                }
                catch (Exception)
                {
                    // A camera destroyed under the read: it is not in the scene.
                }
            }
        }

        // The same four corners RectTransform.GetWorldCorners gives, without its extra native call (see ManagedViewCorners).
        static void ReadWorldCorners(RectTransform rect)
        {
            var r = rect.rect;
            var m = rect.localToWorldMatrix;
            ManagedViewCorners.World(
                m.m00, m.m01, m.m02, m.m03, m.m10, m.m11, m.m12, m.m13, m.m20, m.m21, m.m22, m.m23, m.m30, m.m31, m.m32, m.m33,
                r.x, r.y, r.xMax, r.yMax, CornerValues);
            for (var i = 0; i < 4; i++) Corners[i] = new Vector3(CornerValues[i * 3], CornerValues[i * 3 + 1], CornerValues[i * 3 + 2]);
        }

        static void CopyMatrix(Matrix4x4 m, float[] into)
        {
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
                into[r * 4 + c] = m[r, c];
        }

        void SeedUnits(ManagedViewSceneSnapshot scene)
        {
            _units.Clear();
            _next.Clear();
            var feed = _feed;
            ManagedViewDiagnostics.LastSceneFeedUsed = false;
            // A tree that is a clean prefix of the scene's walk (complete, or cut by the node count or the deadline) answers for what it
            // visited; anything else (a level below the depth limit, a read that threw) and the scene is walked in full, as it always was.
            if (feed != null && !feed.Broken && !ManagedViewDiagnostics.DisableSceneFeed && (feed.Cut || !_budget.Truncated))
            {
                _useFeed = true;
                ManagedViewDiagnostics.LastSceneFeedUsed = true;
                scene.ReadUnits += feed.Units;
                if (feed.Cut)
                {
                    SeedScenes(feed.ResumeSceneIndex);
                    feed.FillResumeStack(_units, _next);
                }

                return;
            }

            SeedScenes(0);
        }

        // The roots of the loaded scenes from index `from` on, pushed so the first scene's first root is read first.
        void SeedScenes(int from)
        {
            for (var i = _scenes.Count - 1; i >= from; i--)
            {
                var scene = _scenes[i];
                if (!scene.isLoaded) continue;
                GameObject[] roots;
                try { roots = scene.GetRootGameObjects(); }
                catch (Exception) { continue; }
                for (var k = roots.Length - 1; k >= 0; k--)
                {
                    var root = roots[k];
                    if (root == null || (_host != null && root == _host.gameObject)) continue;
                    _units.Add(root.transform);
                    _next.Add(-1);
                }
            }
        }

        // One transform per step: its own renderer, then its next child pushed. A step is a few native calls whatever the size of the sub-tree,
        // so a slice always ends within a handful of microseconds of its limit. (`_units` holds the transforms being walked, `_next` the index of the
        // next child to visit of each.)
        void ReadUnit(ManagedViewSceneSnapshot scene)
        {
            var top = _units.Count - 1;
            var t = _units[top];
            if (_next[top] < 0)
            {
                // First visit: this transform's own renderer, and how many children it has.
                scene.ReadUnits++;
                if (t == null || !t.gameObject.activeInHierarchy) { Pop(); return; }
                _next[top] = 0;
                if (t.TryGetComponent<Renderer>(out var own)) ReadRenderer(scene, own);
                return;
            }

            if (t == null || _next[top] >= t.childCount) { Pop(); return; }
            var child = t.GetChild(_next[top]++);
            _units.Add(child);
            if (_next.Count == _units.Count - 1) _next.Add(-1); else _next[_units.Count - 1] = -1;
        }

        void Pop()
        {
            _units.RemoveAt(_units.Count - 1);
            if (_next.Count > _units.Count) _next.RemoveAt(_next.Count - 1);
        }

        // A renderer the tree walk found: its object's id and layer were read there.
        void ReadFed(ManagedViewSceneSnapshot scene, ManagedViewSceneFeed<Renderer, Transform>.Entry entry)
        {
            if (entry.Renderer == null) return; // destroyed since the tree read it: the walk would not have found it
            ReadRenderer(scene, entry.Renderer, entry.Id, entry.Layer);
        }

        void ReadRenderer(ManagedViewSceneSnapshot scene, Renderer r) => ReadRenderer(scene, r, 0, 0, true);

        void ReadRenderer(ManagedViewSceneSnapshot scene, Renderer r, int id, int layer, bool resolveObject = false)
        {
            _sceneSeen++;
            scene.ReadRenderers++;
            if (r == null || !r.isVisible) return; // not drawn last frame (a disabled renderer, a culled one): not in a "visible" scene
            if (resolveObject)
            {
                var go = r.gameObject;
                id = go.GetInstanceID();
                layer = go.layer;
            }

            var kind = KindOf(r.GetType(), out var worldBox);
            float cx, cy, cz, sx, sy, sz;
            float[] matrix;
            if (worldBox)
            {
                // Skinned meshes (bounds relative to their root bone), particles, lines and trails (often simulated in world space): the
                // world-space box the engine keeps, not rotated.
                var b = r.bounds;
                var c = b.center;
                var size = b.size;
                cx = c.x; cy = c.y; cz = c.z; sx = size.x; sy = size.y; sz = size.z;
                matrix = IdentityMatrix;
            }
            else
            {
                var lb = r.localBounds;
                var c = lb.center;
                var size = lb.size;
                cx = c.x; cy = c.y; cz = c.z; sx = size.x; sy = size.y; sz = size.z;
                // A sprite is a flat quad: Unity gives its local bounds a thickness of 0.2 that is not part of the picture, the spec's flat object has none.
                if (kind == SceneObjectKind.Sprite) { cz = 0f; sz = 0f; }
                CopyMatrix(r.localToWorldMatrix, Matrix16);
                matrix = Matrix16;
            }

            scene.AddObject(id, kind, layer, 0, matrix, cx, cy, cz, sx, sy, sz);
        }

        // The kind from the renderer's type or the first base type that is known; a type name is matched, so a module that is not part of the
        // project (tilemaps, particles) is never referenced. Cached per type.
        static SceneObjectKind KindOf(Type type, out bool worldBox)
        {
            if (!RendererKinds.TryGetValue(type, out var kind))
            {
                kind = SceneObjectKind.Other;
                for (var t = type; t != null && t != typeof(Renderer) && t != typeof(object); t = t.BaseType)
                {
                    var known = true;
                    switch (t.Name)
                    {
                        case "MeshRenderer": kind = SceneObjectKind.Mesh; break;
                        case "SkinnedMeshRenderer": kind = SceneObjectKind.Skinned; break;
                        case "SpriteRenderer": kind = SceneObjectKind.Sprite; break;
                        case "ParticleSystemRenderer": kind = SceneObjectKind.Particles; break;
                        case "LineRenderer":
                        case "TrailRenderer": kind = SceneObjectKind.Line; break;
                        case "TilemapRenderer": kind = SceneObjectKind.Tilemap; break;
                        default: known = false; break;
                    }

                    if (known) break;
                }

                RendererKinds[type] = kind;
            }

            worldBox = kind == SceneObjectKind.Skinned || kind == SceneObjectKind.Particles || kind == SceneObjectKind.Line;
            return kind;
        }

        // World-space and screen-space-camera canvases are planes in the scene; an overlay canvas (and a camera canvas without a camera) is
        // drawn straight onto the screen and has no position in the world: its elements stay in the tree with their bounds.
        void ReadCanvas(ManagedViewSceneSnapshot scene, Canvas canvas)
        {
            if (canvas == null || !canvas.isRootCanvas || !canvas.enabled) return;
            _sceneSeen++;
            var mode = canvas.renderMode;
            var drawnBy = 0;
            if (mode == RenderMode.ScreenSpaceOverlay) return;
            if (mode == RenderMode.ScreenSpaceCamera)
            {
                var worldCamera = canvas.worldCamera;
                if (worldCamera == null || !worldCamera.isActiveAndEnabled) return; // Unity draws such a canvas as an overlay
                drawnBy = worldCamera.gameObject.GetInstanceID();
            }

            var rt = canvas.transform as RectTransform;
            if (rt == null) return;
            var rect = rt.rect;
            CopyMatrix(rt.localToWorldMatrix, Matrix16);
            var go = canvas.gameObject;
            scene.AddObject(
                go.GetInstanceID(), SceneObjectKind.Ui, go.layer, drawnBy, Matrix16, rect.center.x, rect.center.y, 0f, rect.width, rect.height, 0f);
        }

        // Fills NameBuffer with the component type names (up to the shown limit) and returns the type of the first component that says
        // what the object is (null when none). Type names only: no component is asked for a value, so a Text, an input field or a
        // TextMeshPro label contributes its class and never what it shows. The name of a type is cached: Type.Name allocates. The one
        // GetComponents call also finds the Transform, the first Canvas and the first Renderer, so no further native lookup is made
        // for them (each costs about as much as the list itself).
        static string ReadComponents(GameObject go, out Transform transform, out Canvas canvas, out Renderer renderer)
        {
            go.GetComponents(typeof(Component), ComponentBuffer);
            NameBuffer.Clear();
            string first = null;
            transform = null;
            canvas = null;
            renderer = null;
            for (var i = 0; i < ComponentBuffer.Count; i++)
            {
                var component = ComponentBuffer[i];
                if (component == null) continue;
                if (component is Transform t)
                {
                    if (transform == null) transform = t;
                    continue;
                }

                if (component is CanvasRenderer) continue;
                if (canvas == null && component is Canvas c) canvas = c;
                if (renderer == null && component is Renderer r) renderer = r;
                var type = component.GetType();
                if (!TypeNames.TryGetValue(type, out var name))
                {
                    name = type.Name;
                    TypeNames[type] = name;
                }

                if (first == null) first = name;
                if (NameBuffer.Count < ManagedViewSnapshot.MaxShownComponents) NameBuffer.Add(name);
            }

            ComponentBuffer.Clear();
            if (transform == null) transform = go.transform;
            return first;
        }
    }
}
