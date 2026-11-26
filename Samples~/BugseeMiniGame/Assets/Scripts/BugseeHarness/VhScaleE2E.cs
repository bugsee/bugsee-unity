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
        static readonly int[] DefaultCounts = { 500, 1500, 5000, 20000 };
        static int[] Counts = DefaultCounts;
        const int Runs = 8;
        const int IdleFrames = 300;
        const float AfterReplySeconds = 0.3f;

        /// <summary>Parses the optional object-count list (comma separated, e.g. "1000,5000"); null or unusable gives the default 500, 1500, 5000, 20000.</summary>
        public static int[] ParseCounts(string list)
        {
            if (string.IsNullOrEmpty(list)) return DefaultCounts;
            var parsed = new List<int>();
            foreach (var part in list.Split(','))
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0) parsed.Add(n);
            return parsed.Count == 0 ? DefaultCounts : parsed.ToArray();
        }

        public static IEnumerator Run(bool unsliced, int[] counts = null)
        {
            Counts = counts ?? DefaultCounts;
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
                        ManagedViewCaptureStats observed = null;
                        while (Time.realtimeSinceStartup - calledAt < 5f)
                        {
                            yield return null;
                            frames.Add(Time.unscaledDeltaTime * 1000f);
                            if (repliedAt < 0 && ManagedViewDiagnostics.Count > before)
                            {
                                repliedAt = Time.realtimeSinceStartup;
                                observed = ManagedViewDiagnostics.Last; // kept now: a capture the SDK starts later must not replace it
                            }
                            if (repliedAt >= 0 && Time.realtimeSinceStartup - repliedAt >= AfterReplySeconds) break;
                        }

                        var st = observed;
                        if (repliedAt < 0 || st == null || ManagedViewDiagnostics.Count != before + 1)
                        {
                            Debug.Log("[VHSCALE] n=" + n + " run=" + run + " NO REPLY RECORDED (or another capture intervened)");
                        }
                        else
                        {
                            Debug.Log("[VHSCALE] n=" + n + " run=" + run + " outcome=" + st.Outcome + " nodes=" + st.Nodes + " chars=" + st.Chars
                                + " mainMs=" + F(st.MainMs) + " slices=" + st.Slices + " maxSliceMs=" + F(st.MaxSliceMs) + " workerMs=" + F(st.WorkerMs)
                                + " totalMs=" + F(st.TotalMs) + " truncated=" + st.Truncated
                                + " scene=" + st.Scene + " sceneObjects=" + st.SceneObjects + " sceneCameras=" + st.SceneCameras + " sceneChars=" + st.SceneChars
                                + " sceneUnits=" + st.SceneUnits + " sceneRenderers=" + st.SceneRenderers + " sceneMainMs=" + F(st.SceneMainMs) + " sceneWorkerMs=" + F(st.SceneWorkerMs) + " sceneTruncated=" + st.SceneTruncated
                                + " usPerNode=" + (st.Nodes > 0 ? (st.MainMs * 1000.0 / st.Nodes).ToString("F2", CultureInfo.InvariantCulture) : "n/a")
                                + " frames " + Percentiles(frames));
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
                    if (n <= FeedCompareMaxObjects) yield return FeedCompare(n);
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

        const int FeedCompareMaxObjects = 5000;

        // The 3D scene read takes the renderers the tree walk found instead of walking every transform again (ManagedViewDiagnostics.DisableSceneFeed
        // switches that off). On a world that holds still the two must give the same reply: the other root objects are hidden, a fresh synthetic scene
        // of n objects is built, and five unsliced captures alternate feed on, off, on, off, on (the first is dropped: renderers are only "visible"
        // after a few frames). Logs `[VHSCALE] feed-compare n=... identical=...` with the reply sizes. The frame counter in the scene block is ignored.
        static IEnumerator FeedCompare(int n)
        {
            var hidden = new List<GameObject>();
            GameObject root = null;
            var replies = new List<string>();
            var feedUsed = new List<bool>();
            var advanced = true;
            var savedSlice = ManagedViewDiagnostics.SliceMsOverride;
            try
            {
                var me = UnityEngine.Object.FindObjectOfType<BugseeSampleBootstrap>();
                var mine = me != null ? me.transform.root.gameObject : null;
                for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                    foreach (var go in UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).GetRootGameObjects())
                        if (go != mine && go.activeSelf && go.GetComponentInChildren<Camera>(true) == null && go.GetComponentInChildren<AudioListener>(true) == null)
                        {
                            go.SetActive(false);
                            hidden.Add(go);
                        }

                root = Build(n);
                ManagedViewDiagnostics.KeepReplies = true;
                ManagedViewDiagnostics.SliceMsOverride = 1000;
                yield return new WaitForSecondsRealtime(2f);
                for (var run = 1; run <= 5; run++)
                {
                    ManagedViewDiagnostics.DisableSceneFeed = run % 2 == 0;
                    var before = ManagedViewDiagnostics.Count;
                    Bugsee.CaptureViewHierarchy();
                    var started = Time.realtimeSinceStartup;
                    while (ManagedViewDiagnostics.Count == before && Time.realtimeSinceStartup - started < 5f) yield return null;
                    advanced &= ManagedViewDiagnostics.Count > before;
                    feedUsed.Add(ManagedViewDiagnostics.LastSceneFeedUsed);
                    replies.Add(ManagedViewDiagnostics.LastReply);
                    yield return new WaitForSecondsRealtime(0.5f);
                }
            }
            finally
            {
                ManagedViewDiagnostics.DisableSceneFeed = false;
                ManagedViewDiagnostics.KeepReplies = false;
                ManagedViewDiagnostics.SliceMsOverride = savedSlice;
                if (root != null) UnityEngine.Object.Destroy(root);
                foreach (var go in hidden) if (go != null) go.SetActive(true);
                RestoreCamera();
            }

            string Norm(string json) => json == null ? null : System.Text.RegularExpressions.Regex.Replace(json, "\"frame\":\\d+", "\"frame\":0");
            // the "on" captures (1, 3, 5) really used the feed and the "off" ones (2, 4) did not, and every capture settled: otherwise nothing was compared
            var identical = replies.Count == 5 && replies[1] != null && replies[1].Contains("\"kind\":") && advanced && feedUsed[2] && feedUsed[4] && !feedUsed[1] && !feedUsed[3];
            for (var i = 2; identical && i < 5; i++) identical = Norm(replies[i]) == Norm(replies[1]);
            Debug.Log("[VHSCALE] feed-compare n=" + n + " identical=" + identical + " captures=" + replies.Count + " feedUsed=" + string.Join("/", feedUsed) + " chars="
                + string.Join("/", replies.ConvertAll(r => r == null ? "null" : r.Length.ToString(CultureInfo.InvariantCulture))));
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
            Time("GetHashCode", go => sink += go.GetHashCode());
            Time("name", go => sink += go.name.Length);
            Time("transform", go => sink += go.transform != null ? 1 : 0);
            Time("GetComponents", go => { go.GetComponents(typeof(Component), comps); sink += comps.Count; comps.Clear(); });
            Time("TryGetComponent<Canvas>", go => sink += go.TryGetComponent<Canvas>(out _) ? 1 : 0);
            Time("TryGetComponent<Renderer>", go => sink += go.TryGetComponent<Renderer>(out _) ? 1 : 0);
            Time("Renderer.bounds", go => { if (go.TryGetComponent<Renderer>(out var r)) sink += (long)r.bounds.size.x; });
            Time("GetWorldCorners", go => { if (go.transform is RectTransform rt) { rt.GetWorldCorners(Corners4); sink += (long)Corners4[0].x; } });
            Time("OwnCorners", go => { if (go.transform is RectTransform rt) { OwnCorners(rt, Corners12); sink += (long)Corners12[0]; } });
            Time("childCount", go => sink += go.transform.childCount);
            Time("GetChild", go => { var t = go.transform; if (t.childCount > 0) sink += t.GetChild(0).GetInstanceID(); });
            Time("GetType().Name", go => { if (go.TryGetComponent<Transform>(out var tr)) sink += tr.GetType().Name.Length; });
            CheckReads(all);
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
        static readonly float[] Corners12 = new float[12];

        static void OwnCorners(RectTransform rt, float[] into)
        {
            var r = rt.rect;
            var m = rt.localToWorldMatrix;
            ManagedViewCorners.World(
                m.m00, m.m01, m.m02, m.m03, m.m10, m.m11, m.m12, m.m13, m.m20, m.m21, m.m22, m.m23, m.m30, m.m31, m.m32, m.m33,
                r.x, r.y, r.xMax, r.yMax, into);
        }

        // The four world corners (ManagedViewCorners against RectTransform.GetWorldCorners, compared bit for bit) must match on this device. The id
        // comparison is informational: GetHashCode is not the instance id in a player.
        static void CheckReads(List<GameObject> all)
        {
            int idDiff = 0, rects = 0, cornerDiff = 0; // idDiff: GetHashCode against GetInstanceID, logged to show they are NOT the same value in a player
            foreach (var go in all)
            {
                if (go.GetHashCode() != go.GetInstanceID()) idDiff++;
                if (!(go.transform is RectTransform rt)) continue;
                rects++;
                rt.GetWorldCorners(Corners4);
                OwnCorners(rt, Corners12);
                for (var i = 0; i < 4; i++)
                    if (BitConverter.SingleToInt32Bits(Corners4[i].x) != BitConverter.SingleToInt32Bits(Corners12[i * 3])
                        || BitConverter.SingleToInt32Bits(Corners4[i].y) != BitConverter.SingleToInt32Bits(Corners12[i * 3 + 1])
                        || BitConverter.SingleToInt32Bits(Corners4[i].z) != BitConverter.SingleToInt32Bits(Corners12[i * 3 + 2])) { cornerDiff++; break; }
            }

            Debug.Log("[VHSCALE] reads check " + (cornerDiff == 0 ? "PASS" : "FAIL") + " objects=" + all.Count + " idMismatches=" + idDiff
                + " rectTransforms=" + rects + " cornerMismatches=" + cornerDiff);
        }

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
