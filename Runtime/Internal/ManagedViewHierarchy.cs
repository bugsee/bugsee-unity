using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
                reply(json);
            });
            if (dataType != ManagedViewTreeJson.DataType || ManagedViewRequests.IsMainThreadInBlockingNativeCall)
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
                    Engine.Submit(once, askedAt, pixelsPerNativeUnit, replyInlineIfSmall);
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
                        + (stats.Truncated ? ", truncated" : "") + ", secured " + stats.Secured);
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
        }

        readonly ManagedViewSnapshot _snapshot = new ManagedViewSnapshot();
        readonly ManagedViewBudget _budget;
        int _width, _height; // refreshed each slice (a rotation or a resolution change between slices)
        readonly Camera _camera;
        readonly MainThreadDispatcher _host;
        readonly List<Scene> _scenes = new List<Scene>();
        readonly HashSet<int> _seen = new HashSet<int>(); // objects move between slices: one that was read is not read again
        readonly Dictionary<Camera, ManagedViewProjection> _projections = new Dictionary<Camera, ManagedViewProjection>();
        Frame[] _stack = new Frame[32];
        int _sp;
        int _sceneIndex;
        int _root;
        bool _finished;

        public ManagedViewSnapshot Snapshot => _snapshot;

        public ManagedViewWalker(ManagedViewBudget budget)
        {
            _budget = budget;
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

        public void Abandon() => Finish();

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
            do
            {
                if (_sp == 0)
                {
                    if (!StartNextScene())
                    {
                        Finish();
                        return true;
                    }

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
                }
            }
            while (!sliceExpired());
            return false;
        }

        // False: the scene list is done (or the budget is spent).
        bool StartNextScene()
        {
            while (_sceneIndex < _scenes.Count)
            {
                if (_budget.IsExhausted || !_budget.TryTake(2))
                {
                    _snapshot.SetTruncated(_root);
                    return false;
                }

                var scene = _scenes[_sceneIndex++];
                try
                {
                    var node = _snapshot.Add(_root, scene.handle, "scene-" + scene.handle, "Scene", scene.name);
                    if (!scene.isLoaded) continue; // unloaded since the walk began: an empty scene node, and the tree is flagged
                    Push(new Frame { Roots = scene.GetRootGameObjects(), Node = node, Depth = 2 });
                    return true;
                }
                catch (Exception)
                {
                    // The scene went away: nothing below it.
                }
            }

            return false;
        }

        void Push(Frame frame)
        {
            if (_sp == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
            _stack[_sp++] = frame;
        }

        // One unit of work on the top frame; false when the frame has no more children.
        bool StepFrame()
        {
            var f = _stack[_sp - 1];
            if (f.Roots != null)
            {
                if (f.Next >= f.Roots.Length) return false;
                var root = f.Roots[f.Next];
                if (root != null && _host != null && root == _host.gameObject) { f.Next++; _stack[_sp - 1] = f; return true; }
                if (_budget.IsExhausted) { _snapshot.SetTruncated(f.Node); return false; }
                f.Next++;
                _stack[_sp - 1] = f;
                if (Visit(root, 3, default, f.Node)) _snapshot.SetTruncated(f.Node);
                return true;
            }

            if (f.Transform == null) { _snapshot.SetTruncated(f.Node); return false; } // destroyed between slices: its other children are lost
            if (f.Next >= f.Transform.childCount) return false;
            if (_budget.IsExhausted) { _snapshot.SetTruncated(f.Node); return false; }
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
            catch (Exception) { return false; }
            if (!_seen.Add(id)) return false; // read already (moved between slices): neither a node nor budget
            if (!_budget.TryTake(depth)) return true;

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
            }
            catch (Exception)
            {
                return false;
            }

            try
            {
                if (own != null) canvas = CanvasOf(own, _camera);
                var rect = transform as RectTransform;
                if (rect != null && canvas.Valid)
                {
                    rect.GetWorldCorners(Corners);
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

                if (transform.childCount > 0) Push(new Frame { Transform = transform, Node = node, Depth = depth, Canvas = canvas });
            }
            catch (Exception)
            {
                // Keep what this object has; its siblings are read by the caller.
            }

            return false;
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
