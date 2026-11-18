using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Bugsee.Internal;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Measurement scenario for the managed view hierarchy capture (BUGSEE_E2E_SCENARIO=vhscale, or vhscaleunsliced). For each N of
    /// 500, 1500, 5000 and 20000 it builds a synthetic scene (N objects under groups, about 60% cheap 3D renderers on a shared mesh and
    /// material and 40% uGUI-style RectTransforms under an overlay canvas, some rotated), records the frame time while idle, then asks
    /// the native SDK for the managed view hierarchy <c>Runs</c> times with <c>Bugsee.CaptureViewHierarchy()</c> and logs for each capture
    /// the numbers the wrapper recorded (<see cref="ManagedViewDiagnostics"/>: main-thread ms, slices, worker ms, total ms to reply, nodes,
    /// chars, truncated, outcome) and the frame-time percentiles of the frames from the call until 300 ms after the reply. It ends with one
    /// <c>[VHSCALE] summary</c> line per N (idle against capture-window percentiles, pooled over the runs). Works in release builds
    /// (the numbers come from the diagnostics class, not from a log line). <c>vhscaleunsliced</c> takes the whole snapshot in one frame
    /// (a 1 s slice) to show what slicing buys. Not self-checking: it logs measurements, no PASS/FAIL.
    /// </summary>
    static class VhScaleE2E
    {
        static readonly int[] Counts = { 500, 1500, 5000, 20000 };
        const int Runs = 8;
        const int IdleFrames = 300;
        const float AfterReplySeconds = 0.3f;

        public static IEnumerator Run(bool unsliced)
        {
            Debug.Log("[VHSCALE] start launched=" + Bugsee.IsLaunched + " dev=" + Debug.isDebugBuild + " unity=" + Application.unityVersion
                + " refresh=" + RefreshHz() + " target=" + Application.targetFrameRate + " vsync=" + QualitySettings.vSyncCount
                + " screen=" + Screen.width + "x" + Screen.height + " runs=" + Runs + " unsliced=" + unsliced);
            CheckProjection();
            yield return MicroCost();
            ManagedViewDiagnostics.LogEveryCapture = true;
            ManagedViewDiagnostics.SliceMsOverride = unsliced ? 1000 : 0;
            try
            {
                foreach (var n in Counts)
                {
                    var root = Build(n);
                    yield return new WaitForSecondsRealtime(3f);
                    var idle = new List<float>();
                    for (var i = 0; i < IdleFrames; i++) { yield return null; idle.Add(Time.unscaledDeltaTime * 1000f); }
                    Debug.Log("[VHSCALE] n=" + n + " idle " + Percentiles(idle));

                    var pooled = new List<float>();
                    var mains = new List<double>();
                    var totals = new List<double>();
                    for (var run = 1; run <= Runs; run++)
                    {
                        var before = ManagedViewDiagnostics.Count;
                        var frames = new List<float>();
                        var calledAt = Time.realtimeSinceStartup;
                        Bugsee.CaptureViewHierarchy();
                        var repliedAt = -1f;
                        while (Time.realtimeSinceStartup - calledAt < 5f)
                        {
                            yield return null;
                            frames.Add(Time.unscaledDeltaTime * 1000f);
                            if (repliedAt < 0 && ManagedViewDiagnostics.Count > before) repliedAt = Time.realtimeSinceStartup;
                            if (repliedAt >= 0 && Time.realtimeSinceStartup - repliedAt >= AfterReplySeconds) break;
                        }

                        var st = ManagedViewDiagnostics.Last;
                        if (repliedAt < 0 || st == null)
                        {
                            Debug.Log("[VHSCALE] n=" + n + " run=" + run + " NO REPLY RECORDED");
                        }
                        else
                        {
                            Debug.Log("[VHSCALE] n=" + n + " run=" + run + " outcome=" + st.Outcome + " nodes=" + st.Nodes + " chars=" + st.Chars
                                + " mainMs=" + F(st.MainMs) + " slices=" + st.Slices + " maxSliceMs=" + F(st.MaxSliceMs) + " workerMs=" + F(st.WorkerMs)
                                + " totalMs=" + F(st.TotalMs) + " truncated=" + st.Truncated + " frames " + Percentiles(frames));
                            mains.Add(st.MainMs);
                            totals.Add(st.TotalMs);
                        }

                        pooled.AddRange(frames);
                        yield return new WaitForSecondsRealtime(1.5f);
                    }

                    Debug.Log("[VHSCALE] summary n=" + n + " idle " + Percentiles(idle) + " | capture-window " + Percentiles(pooled)
                        + " | mainMs median=" + F(Median(mains)) + " max=" + F(Max(mains)) + " | totalMs median=" + F(Median(totals)) + " max=" + F(Max(totals)));
                    UnityEngine.Object.Destroy(root);
                    yield return null;
                }
            }
            finally
            {
                ManagedViewDiagnostics.SliceMsOverride = 0;
                ManagedViewDiagnostics.LogEveryCapture = false;
                RestoreCamera();
                Debug.Log("[VHSCALE] done");
            }
        }

        // The wrapper projects box corners with the camera's matrices copied once (ManagedViewProjection) instead of one
        // Camera.WorldToScreenPoint call per corner; this compares the two on this device.
        static void CheckProjection()
        {
            var cam = Camera.main;
            if (cam == null) { Debug.Log("[VHSCALE] projection check skipped: no main camera"); return; }
            var view = cam.worldToCameraMatrix;
            var proj = cam.projectionMatrix;
            var v = new float[16];
            var q = new float[16];
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++) { v[r * 4 + c] = view[r, c]; q[r * 4 + c] = proj[r, c]; }
            var px = cam.pixelRect;
            var mine = new ManagedViewProjection(v, q, px.x, px.y, px.width, px.height);
            var rnd = new System.Random(7);
            var maxDiff = 0f;
            var disagree = 0;
            const int points = 2000;
            for (var i = 0; i < points; i++)
            {
                var p = cam.transform.position + cam.transform.forward * (float)(rnd.NextDouble() * 60 - 10)
                    + cam.transform.right * (float)(rnd.NextDouble() * 80 - 40) + cam.transform.up * (float)(rnd.NextDouble() * 60 - 30);
                var u = cam.WorldToScreenPoint(p);
                var ok = mine.TryProject(p.x, p.y, p.z, out var sx, out var sy);
                if (ok != (u.z > 0f)) { disagree++; continue; }
                if (ok) maxDiff = Mathf.Max(maxDiff, Mathf.Abs(sx - u.x), Mathf.Abs(sy - u.y));
            }

            Debug.Log("[VHSCALE] projection check ortho=" + cam.orthographic + " points=" + points + " maxDiffPx=" + maxDiff.ToString("F4", CultureInfo.InvariantCulture)
                + " frontTestDisagreements=" + disagree);
        }

        // The main-thread cost of each Unity read the walk makes, in microseconds per call, over 1000 objects of the synthetic mix: shows
        // what a node costs and which read dominates (the walk's per-node cost bounds how much the 5% slices can take).
        static IEnumerator MicroCost()
        {
            var root = Build(1000);
            yield return null;
            var all = new List<GameObject>();
            var stack = new Stack<Transform>();
            stack.Push(root.transform);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                all.Add(t.gameObject);
                for (var i = 0; i < t.childCount; i++) stack.Push(t.GetChild(i));
            }

            var comps = new List<Component>();
            var cam = Camera.main;
            long sink = 0;
            var sw = new System.Diagnostics.Stopwatch();
            var lines = new List<string>();
            void Time(string label, Action<GameObject> read)
            {
                for (var warm = 0; warm < 2; warm++)
                {
                    sw.Restart();
                    foreach (var go in all) read(go);
                    sw.Stop();
                }

                lines.Add(label + "=" + (sw.Elapsed.TotalMilliseconds * 1000.0 / all.Count).ToString("F2", CultureInfo.InvariantCulture));
            }

            Time("activeInHierarchy", go => sink += go.activeInHierarchy ? 1 : 0);
            Time("GetInstanceID", go => sink += go.GetInstanceID());
            Time("name", go => sink += go.name.Length);
            Time("transform", go => sink += go.transform != null ? 1 : 0);
            Time("GetComponents", go => { go.GetComponents(typeof(Component), comps); sink += comps.Count; comps.Clear(); });
            Time("TryGetComponent<Canvas>", go => sink += go.TryGetComponent<Canvas>(out _) ? 1 : 0);
            Time("TryGetComponent<Renderer>", go => sink += go.TryGetComponent<Renderer>(out _) ? 1 : 0);
            Time("Renderer.bounds", go => { if (go.TryGetComponent<Renderer>(out var r)) sink += (long)r.bounds.size.x; });
            Time("GetWorldCorners", go => { if (go.transform is RectTransform rt) { rt.GetWorldCorners(Corners4); sink += (long)Corners4[0].x; } });
            Time("childCount", go => sink += go.transform.childCount);
            Time("GetChild", go => { var t = go.transform; if (t.childCount > 0) sink += t.GetChild(0).GetInstanceID(); });
            Time("GetType().Name", go => { if (go.TryGetComponent<Transform>(out var tr)) sink += tr.GetType().Name.Length; });
            Debug.Log("[VHSCALE] cost us/call over " + all.Count + " objects: " + string.Join(" ", lines) + " (sink " + sink + ")");
            UnityEngine.Object.Destroy(root);
            yield return null;
        }

        const int SyntheticLayer = 31;
        static int? _cullingBefore;
        static Camera _cullingCamera;

        static void RestoreCamera()
        {
            if (_cullingBefore != null && _cullingCamera != null) _cullingCamera.cullingMask = _cullingBefore.Value;
            _cullingBefore = null;
            _cullingCamera = null;
        }

        static readonly Vector3[] Corners4 = new Vector3[4];

        static string RefreshHz()
        {
#if UNITY_2022_2_OR_NEWER
            return Screen.currentResolution.refreshRateRatio.value.ToString("F1", CultureInfo.InvariantCulture);
#else
            return Screen.currentResolution.refreshRate.ToString(CultureInfo.InvariantCulture);
#endif
        }

        static GameObject Build(int n)
        {
            var root = new GameObject("VhScaleRoot") { layer = SyntheticLayer };
            var cam = Camera.main;
            if (cam == null)
            {
                var c = new GameObject("VhScaleCamera");
                c.transform.SetParent(root.transform);
                cam = c.AddComponent<Camera>();
                c.tag = "MainCamera";
            }

            // The synthetic objects live on their own layer and the main camera draws only that layer while they exist, so the sample's own
            // world (models, effects) does not set the frame time: what is measured is the capture's effect on a steady frame.
            if (_cullingBefore == null) { _cullingBefore = cam.cullingMask; cam.cullingMask = 1 << SyntheticLayer; _cullingCamera = cam; }
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh = cube.GetComponent<MeshFilter>().sharedMesh;
            // One instanced material: the renderers cost a few draw calls, so the frame time measures the capture, not the synthetic scene.
            var material = new Material(cube.GetComponent<MeshRenderer>().sharedMaterial) { enableInstancing = true };
            UnityEngine.Object.Destroy(cube);

            var canvasGo = new GameObject("VhScaleCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(root.transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var camT = cam.transform;
            Transform world = root.transform;
            Transform ui = canvasGo.transform;
            var rnd = new System.Random(n);
            for (var i = 0; i < n; i++)
            {
                var group = i % 8 == 0;
                if (i % 5 < 3)
                {
                    var go = new GameObject(group ? "Group3D" + i : "Mesh" + i) { layer = SyntheticLayer };
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = material;
                    var t = go.transform;
                    t.SetParent(i % 8 < 5 && i % 8 != 0 ? world : root.transform, false);
                    t.position = camT.position + camT.forward * (8 + rnd.Next(30)) + camT.right * (float)(rnd.NextDouble() * 12 - 6) + camT.up * (float)(rnd.NextDouble() * 8 - 4);
                    t.rotation = Quaternion.Euler(rnd.Next(360), rnd.Next(360), rnd.Next(360));
                    t.localScale = Vector3.one * (0.2f + (float)rnd.NextDouble());
                    if (group) world = t;
                }
                else
                {
                    var go = new GameObject(group ? "Panel" + i : "Element" + i, typeof(RectTransform), typeof(CanvasRenderer));
                    var t = (RectTransform)go.transform;
                    t.SetParent(i % 8 != 0 ? ui : canvasGo.transform, false);
                    t.anchoredPosition = new Vector2(rnd.Next(-400, 400), rnd.Next(-800, 800));
                    t.sizeDelta = new Vector2(20 + rnd.Next(300), 20 + rnd.Next(200));
                    t.localRotation = Quaternion.Euler(0, 0, rnd.Next(4) == 0 ? rnd.Next(360) : 0);
                    if (group) ui = t;
                }
            }

            return root;
        }

        static string Percentiles(List<float> ms)
        {
            if (ms.Count == 0) return "frames=0";
            var a = ms.ToArray();
            Array.Sort(a);
            return "frames=" + a.Length + " p50=" + F(a[a.Length / 2]) + " p95=" + F(a[(int)(a.Length * 0.95)]) + " p99=" + F(a[Math.Min(a.Length - 1, (int)(a.Length * 0.99))])
                + " max=" + F(a[a.Length - 1]);
        }

        static double Median(List<double> v)
        {
            if (v.Count == 0) return 0;
            var a = v.ToArray();
            Array.Sort(a);
            return a[a.Length / 2];
        }

        static double Max(List<double> v)
        {
            var m = 0.0;
            foreach (var x in v) if (x > m) m = x;
            return m;
        }

        static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
