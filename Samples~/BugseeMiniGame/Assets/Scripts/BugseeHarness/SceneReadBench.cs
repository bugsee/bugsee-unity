using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;
using Debug = UnityEngine.Debug;

namespace Bugsee.Sample
{
    /// <summary>
    /// Measurement spike (BUGSEE_E2E_SCENARIO=scenebench): main-thread microseconds per object of different ways to read the Unity scene,
    /// on the vhscale synthetic scene (N = 500, 1500, 5000, 20000). Every cell logs "[SCENEBENCH] n= v= ..." with cold (first call), median and
    /// max of the next captures, us per object, managed allocation per capture (GC.GetTotalMemory delta, -1 when a collection ran) and GCs.
    /// Variants: V0 current walk; V1 bulk FindObjectsByType; V2 TransformAccessArray job; V3 caches by instance id; V4 filters; V5 mix.
    /// Not shipped: it exists to pick a design.
    /// </summary>
    static class SceneReadBench
    {
        static readonly int[] Counts = { 500, 1500, 5000, 20000 };
        const int Reps = 9; // the first is "cold", the other 8 are the captures that are summarized
        static long _sink;
        static readonly Stopwatch Sw = new Stopwatch();
        static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
        static void Log(string s) => Debug.Log("[SCENEBENCH] " + s);

        // ---------- measuring helpers ----------
        static void Cell(int n, string v, int objs, Func<long> body, int reps = Reps, Action before = null)
        {
            var ms = new List<double>();
            var al = new List<long>();
            var gcs = 0;
            double cold = 0;
            for (var r = 0; r < reps; r++)
            {
                before?.Invoke();
                var g0 = GC.CollectionCount(0);
                var m0 = GC.GetTotalMemory(false);
                Sw.Restart();
                _sink += body();
                Sw.Stop();
                var m1 = GC.GetTotalMemory(false);
                var g = GC.CollectionCount(0) - g0;
                gcs += g;
                var t = Sw.Elapsed.TotalMilliseconds;
                if (r == 0) { cold = t; if (reps > 1) continue; }
                ms.Add(t);
                al.Add(g == 0 ? m1 - m0 : -1);
            }

            ms.Sort();
            var valid = al.FindAll(x => x >= 0);
            valid.Sort();
            var med = ms[ms.Count / 2];
            Log("n=" + n + " v=" + v + " objs=" + objs + " cold=" + F(cold) + "ms med=" + F(med) + "ms max=" + F(ms[ms.Count - 1]) + "ms usPerObj=" + F(med * 1000.0 / Math.Max(1, objs))
                + " allocKB=" + (valid.Count == 0 ? "n/a" : F(valid[valid.Count / 2] / 1024.0)) + " gcs=" + gcs);
        }

        // ---------- scene data ----------
        sealed class Scn
        {
            public GameObject Root;
            public Transform[] T;
            public Renderer[] R;
            public RectTransform[] Rt;
            public int[] Ids;
        }

        static Scn Gather(GameObject root)
        {
            var s = new Scn { Root = root };
            var all = root.GetComponentsInChildren<Transform>(false);
            s.T = all;
            var rs = new List<Renderer>();
            var rts = new List<RectTransform>();
            var ids = new int[all.Length];
            for (var i = 0; i < all.Length; i++)
            {
                ids[i] = all[i].gameObject.GetInstanceID();
                if (all[i].TryGetComponent<Renderer>(out var r)) rs.Add(r);
                if (all[i] is RectTransform rt) rts.Add(rt);
            }

            s.R = rs.ToArray();
            s.Rt = rts.ToArray();
            s.Ids = ids;
            return s;
        }

        // ---------- V0: the PR's walk, one primitive set per node ----------
        struct Node { public int Id; public string Name; public string Cls; public int Comps; public Vector3 Min, Max; }
        struct Frame { public Transform T; public int Next; public bool Canvas; }
        static Frame[] _stk = new Frame[512];
        static readonly List<Component> CompBuf = new List<Component>();
        static readonly Dictionary<Type, string> TypeNames = new Dictionary<Type, string>();
        static readonly Vector3[] Corners = new Vector3[4];
        static readonly Plane[] Planes = new Plane[6];
        static int _visited, _skipInactive, _skipInvisible, _skipSmall;

        struct Opts { public bool VisOnly; public float MinRect; public bool Inactive; }

        static int Walk(Transform root, Node[] o, Opts opt)
        {
            _visited = _skipInactive = _skipInvisible = _skipSmall = 0;
            var n = 0;
            var sp = 0;
            Visit(root.gameObject, false, o, ref n, opt, ref sp);
            while (sp > 0)
            {
                var f = _stk[sp - 1];
                if (f.Next >= f.T.childCount) { sp--; continue; }
                var child = f.T.GetChild(f.Next++).gameObject;
                _stk[sp - 1] = f;
                Visit(child, f.Canvas, o, ref n, opt, ref sp);
            }

            return n;
        }

        static void Visit(GameObject go, bool inCanvas, Node[] o, ref int n, Opts opt, ref int sp)
        {
            if (go == null || !go.activeInHierarchy) { _skipInactive++; return; }
            var id = go.GetInstanceID();
            var name = go.name;
            go.GetComponents(typeof(Component), CompBuf);
            Transform t = null;
            Canvas own = null;
            Renderer rend = null;
            string first = null;
            var cnt = 0;
            for (var i = 0; i < CompBuf.Count; i++)
            {
                var c = CompBuf[i];
                if (c == null) continue;
                if (c is Transform tt) { if (t == null) t = tt; continue; }
                if (c is CanvasRenderer) continue;
                if (own == null && c is Canvas cv) own = cv;
                if (rend == null && c is Renderer rr) rend = rr;
                var type = c.GetType();
                if (!TypeNames.TryGetValue(type, out var nm)) { nm = type.Name; TypeNames[type] = nm; }
                if (first == null) first = nm;
                cnt++;
            }

            CompBuf.Clear();
            if (t == null) t = go.transform;
            if (own != null) { inCanvas = own.renderMode == RenderMode.ScreenSpaceOverlay; inCanvas = true; }
            _visited++;
            var node = new Node { Id = id, Name = name, Cls = first, Comps = cnt };
            var rect = t as RectTransform;
            if (rect != null && inCanvas)
            {
                if (opt.MinRect > 0 && rect.rect.width < opt.MinRect && rect.rect.height < opt.MinRect) { _skipSmall++; goto children; }
                rect.GetWorldCorners(Corners);
                MinMax(Corners, ref node);
            }
            else if (rend != null && rend.enabled)
            {
                if (opt.VisOnly && !rend.isVisible) { _skipInvisible++; goto children; }
                var b = rend.bounds;
                node.Min = b.min; node.Max = b.max;
            }

            children:
            o[n++] = node;
            if (t.childCount > 0)
            {
                if (sp == _stk.Length) Array.Resize(ref _stk, sp * 2);
                _stk[sp++] = new Frame { T = t, Next = 0, Canvas = inCanvas };
            }
        }

        static void MinMax(Vector3[] c, ref Node nd)
        {
            var mn = c[0]; var mx = c[0];
            for (var i = 1; i < 4; i++) { mn = Vector3.Min(mn, c[i]); mx = Vector3.Max(mx, c[i]); }
            nd.Min = mn; nd.Max = mx;
        }

        // ---------- V2: transform batch ----------
        struct MatJob : IJobParallelForTransform
        {
            [WriteOnly] public NativeArray<Matrix4x4> M;
            [WriteOnly] public NativeArray<Vector3> P;
            [WriteOnly] public NativeArray<Quaternion> Q;
            public void Execute(int i, TransformAccess t) { M[i] = t.localToWorldMatrix; P[i] = t.position; Q[i] = t.rotation; }
        }

        sealed class Taa
        {
            public TransformAccessArray A;
            public NativeArray<Matrix4x4> M;
            public NativeArray<Vector3> P;
            public NativeArray<Quaternion> Q;
            public int Len;
            public void Alloc(int n)
            {
                M = new NativeArray<Matrix4x4>(n, Allocator.Persistent);
                P = new NativeArray<Vector3>(n, Allocator.Persistent);
                Q = new NativeArray<Quaternion>(n, Allocator.Persistent);
                Len = n;
            }
            public void Free() { if (A.isCreated) A.Dispose(); if (M.IsCreated) { M.Dispose(); P.Dispose(); Q.Dispose(); } }
            public JobHandle Schedule() => new MatJob { M = M, P = P, Q = Q }.Schedule(A);
        }

        // ---------- V3/V5 cache entry ----------
        sealed class Ent
        {
            public int Id, ParentId, Idx;
            public Transform T;
            public string Name, Cls;
            public Renderer R;
            public RectTransform Rt;
            public Bounds LB;
            public Rect RectLocal;
            public bool InCanvas;
        }

        static Ent MakeEntry(Transform t, bool inCanvas)
        {
            var go = t.gameObject;
            var e = new Ent { Id = go.GetInstanceID(), T = t, Name = go.name };
            go.GetComponents(typeof(Component), CompBuf);
            string first = null;
            for (var i = 0; i < CompBuf.Count; i++)
            {
                var c = CompBuf[i];
                if (c == null || c is Transform || c is CanvasRenderer) continue;
                if (e.R == null && c is Renderer rr) e.R = rr;
                if (c is Canvas) inCanvas = true;
                var type = c.GetType();
                if (!TypeNames.TryGetValue(type, out var nm)) { nm = type.Name; TypeNames[type] = nm; }
                if (first == null) first = nm;
            }

            CompBuf.Clear();
            e.Cls = first;
            e.InCanvas = inCanvas;
            var p = t.parent;
            e.ParentId = p == null ? 0 : p.gameObject.GetInstanceID();
            if (e.R != null)
            {
#if UNITY_2021_2_OR_NEWER
                e.LB = e.R.localBounds;
#endif
            }

            e.Rt = t as RectTransform;
            if (e.Rt != null) { e.RectLocal = e.Rt.rect; e.InCanvas = true; } // this scene: every rect is under the overlay canvas
            return e;
        }

        // worker-side: world box from cached local bounds + matrix, AABB of the 8 corners (what Renderer.bounds is for a MeshRenderer)
        static void WorldBox(in Matrix4x4 m, in Bounds lb, out Vector3 mn, out Vector3 mx)
        {
            var c = lb.center; var e = lb.extents;
            mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (var sx = -1; sx <= 1; sx += 2)
            for (var sy = -1; sy <= 1; sy += 2)
            for (var sz = -1; sz <= 1; sz += 2)
            {
                var p = m.MultiplyPoint3x4(new Vector3(c.x + sx * e.x, c.y + sy * e.y, c.z + sz * e.z));
                mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
            }
        }

        static void WorldRect(in Matrix4x4 m, in Rect r, out Vector3 mn, out Vector3 mx)
        {
            var a = m.MultiplyPoint3x4(new Vector3(r.xMin, r.yMin, 0));
            var b = m.MultiplyPoint3x4(new Vector3(r.xMin, r.yMax, 0));
            var c = m.MultiplyPoint3x4(new Vector3(r.xMax, r.yMax, 0));
            var d = m.MultiplyPoint3x4(new Vector3(r.xMax, r.yMin, 0));
            mn = Vector3.Min(Vector3.Min(a, b), Vector3.Min(c, d));
            mx = Vector3.Max(Vector3.Max(a, b), Vector3.Max(c, d));
        }

        // ---------- the run ----------
        public static IEnumerator Run()
        {
            Debug.Log("[SCENEBENCH] start dev=" + Debug.isDebugBuild + " unity=" + Application.unityVersion + " device=" + SystemInfo.deviceModel + " cpu=" + SystemInfo.processorType
                + " cores=" + SystemInfo.processorCount + " reps=" + Reps + " (cold = first call; med/max over the other " + (Reps - 1) + ")");
            yield return Prims();
            foreach (var n in Counts)
            {
                var root = VhScaleE2E.Build(n);
                yield return new WaitForSecondsRealtime(2f);
                yield return Variants(n, root);
                UnityEngine.Object.Destroy(root);
                yield return null;
            }

            VhScaleE2E.RestoreCamera();
            Debug.Log("[SCENEBENCH] done sink=" + _sink);
        }

        // ---- per-primitive costs, including the allocation-free overloads ----
        static IEnumerator Prims()
        {
            var root = VhScaleE2E.Build(1500);
            yield return null;
            var s = Gather(root);
            var all = s.T;
            var gos = new GameObject[all.Length];
            for (var i = 0; i < all.Length; i++) gos[i] = all[i].gameObject;
            var lines = new List<string>();
            var comps = new List<Component>();
            var reuse4 = new Vector3[4];
            var withKidsL = new List<Transform>();
            foreach (var t in all) if (t.childCount > 0) withKidsL.Add(t);
            var withKids = withKidsL.ToArray();
            var rends = s.R;
            var rects = s.Rt;
            long sink = 0;

            void P<T>(string label, T[] arr, Action<T> f)
            {
                for (var w = 0; w < 2; w++)
                {
                    var g0 = GC.CollectionCount(0);
                    var m0 = GC.GetTotalMemory(false);
                    Sw.Restart();
                    for (var i = 0; i < arr.Length; i++) f(arr[i]);
                    Sw.Stop();
                    if (w == 1)
                    {
                        var al = GC.CollectionCount(0) == g0 ? (GC.GetTotalMemory(false) - m0) / (double)arr.Length : -1;
                        lines.Add(label + "=" + F(Sw.Elapsed.TotalMilliseconds * 1000.0 / arr.Length) + (al > 8 ? "(+" + F(al) + "B)" : ""));
                    }
                }
            }

            // GameObject reads
            P("go.activeInHierarchy", gos, g => sink += g.activeInHierarchy ? 1 : 0);
            P("go.activeSelf", gos, g => sink += g.activeSelf ? 1 : 0);
            P("go.GetInstanceID", gos, g => sink += g.GetInstanceID());
            P("go.name", gos, g => sink += g.name.Length);
            P("go.transform", gos, g => sink += g.transform != null ? 1 : 0);
            P("go==null", gos, g => sink += g == null ? 1 : 0);
            P("t.gameObject", all, t => sink += t.gameObject != null ? 1 : 0);
            P("t.GetInstanceID", all, t => sink += t.GetInstanceID());
            P("t.name(Component)", all, t => sink += t.name.Length);
            P("GetComponents(Type)->array", gos, g => sink += g.GetComponents(typeof(Component)).Length);
            P("GetComponents(Type,List)", gos, g => { g.GetComponents(typeof(Component), comps); sink += comps.Count; comps.Clear(); });
            P("GetComponents<Component>(List)", gos, g => { g.GetComponents(comps); sink += comps.Count; comps.Clear(); });
            P("TryGetComponent<Renderer>", gos, g => sink += g.TryGetComponent<Renderer>(out _) ? 1 : 0);
            P("GetComponent<Renderer>", gos, g => sink += g.GetComponent<Renderer>() != null ? 1 : 0);
            P("TryGetComponent<Canvas>", gos, g => sink += g.TryGetComponent<Canvas>(out _) ? 1 : 0);
            // Transform hierarchy
            P("t.childCount", all, t => sink += t.childCount);
            P("GetChild(0)", withKids, t => sink += t.GetChild(0) != null ? 1 : 0);
            P("GetChild(i) loop, count hoisted", withKids, t => { var c = t.childCount; for (var i = 0; i < c; i++) sink += t.GetChild(i).GetInstanceID(); });
            P("GetChild(i) loop, count per iter", withKids, t => { for (var i = 0; i < t.childCount; i++) sink += t.GetChild(i).GetInstanceID(); });
            P("foreach(Transform) enumerator", withKids, t => { foreach (Transform c in t) sink += c.GetInstanceID(); });
            P("t.parent", all, t => sink += t.parent != null ? 1 : 0);
            P("t.position", all, t => sink += (long)t.position.x);
            P("t.rotation", all, t => sink += (long)t.rotation.x);
            P("t.localToWorldMatrix", all, t => sink += (long)t.localToWorldMatrix.m00);
            P("t.lossyScale", all, t => sink += (long)t.lossyScale.x);
            P("t.localPosition", all, t => sink += (long)t.localPosition.x);
#if UNITY_2022_1_OR_NEWER
            P("t.GetPositionAndRotation", all, t => { t.GetPositionAndRotation(out var p, out var q); sink += (long)p.x; });
#endif
            P("pos+rot+matrix (3 calls)", all, t => { sink += (long)(t.position.x + t.rotation.x + t.localToWorldMatrix.m00); });
            // renderers
            P("r.enabled", rends, r => sink += r.enabled ? 1 : 0);
            P("r.bounds", rends, r => sink += (long)r.bounds.size.x);
            P("r.isVisible", rends, r => sink += r.isVisible ? 1 : 0);
#if UNITY_2021_2_OR_NEWER
            P("r.localBounds", rends, r => sink += (long)r.localBounds.size.x);
#endif
            P("r.transform", rends, r => sink += r.transform != null ? 1 : 0);
            P("r.name", rends, r => sink += r.name.Length);
            P("r.GetComponent<MeshFilter>().sharedMesh", rends, r => sink += r.GetComponent<MeshFilter>().sharedMesh != null ? 1 : 0);
            var mf = rends.Length > 0 ? rends[0].GetComponent<MeshFilter>().sharedMesh : null;
            P("mesh.bounds", rends, r => sink += (long)mf.bounds.size.x);
            P("mesh.GetInstanceID", rends, r => sink += mf.GetInstanceID());
            // rects
            P("rt.GetWorldCorners(new Vector3[4])", rects, rt => { var c = new Vector3[4]; rt.GetWorldCorners(c); sink += (long)c[0].x; });
            P("rt.GetWorldCorners(reused)", rects, rt => { rt.GetWorldCorners(reuse4); sink += (long)reuse4[0].x; });
            P("rt.rect", rects, rt => sink += (long)rt.rect.width);
            P("rt.sizeDelta", rects, rt => sink += (long)rt.sizeDelta.x);
            // research file extras: single icalls, hot (same object) vs cold (distinct objects), GetHashCode vs GetInstanceID, rect+matrix
            var hotGo = gos[10]; var hotT = all[10];
            var rep = new int[all.Length];
            P("Time.frameCount (static icall)", rep, _ => sink += Time.frameCount);
            P("HOT go.activeSelf (same object)", rep, _ => sink += hotGo.activeSelf ? 1 : 0);
            P("HOT t.localPosition (same object)", rep, _ => sink += (long)hotT.localPosition.x);
            P("HOT go.GetInstanceID (same object)", rep, _ => sink += hotGo.GetInstanceID());
            P("HOT go.transform (same object)", rep, _ => sink += hotGo.transform != null ? 1 : 0);
            P("HOT go.name (same object)", rep, _ => sink += hotGo.name.Length);
            P("empty lambda", rep, _ => sink += 1);
            P("go.GetHashCode", gos, g => sink += g.GetHashCode());
            P("t.GetHashCode", all, t => sink += t.GetHashCode());
            P("rt.rect+localToWorldMatrix+own corner math", rects, rt =>
            {
                var r = rt.rect; var m = rt.localToWorldMatrix;
                var a0 = m.MultiplyPoint3x4(new Vector3(r.xMin, r.yMin, 0)); var a2 = m.MultiplyPoint3x4(new Vector3(r.xMax, r.yMax, 0));
                sink += (long)(a0.x + a2.x);
            });
            P("rt.rect only", rects, rt => sink += (long)rt.rect.width);
            P("rt.localToWorldMatrix only", rects, rt => sink += (long)rt.localToWorldMatrix.m00);
            P("t as RectTransform (type test)", all, t => sink += t is RectTransform ? 1 : 0);
            P("t.hierarchyCount", all, t => sink += t.hierarchyCount);
            P("shell validation: ReferenceEquals + (obj==null)", all, t => sink += !ReferenceEquals(t, null) && t != null ? 1 : 0);
            P("r.GetHashCode", rends, r => sink += r.GetHashCode());
            P("GeometryUtility.TestPlanesAABB(r.bounds)", rends, r => { sink += GeometryUtility.TestPlanesAABB(Planes, r.bounds) ? 1 : 0; });
            // dictionary and cache lookups
            var dict = new Dictionary<int, string>();
            foreach (var g in gos) dict[g.GetInstanceID()] = g.name;
            P("Dictionary<int,string>.TryGetValue", s.Ids, id => sink += dict.TryGetValue(id, out var nm) ? nm.Length : 0);
            var hs = new HashSet<int>();
            P("HashSet<int>.Add", s.Ids, id => sink += hs.Add(id) ? 1 : 0);
            Log("PRIM us/call over " + all.Length + " objects (" + rends.Length + " renderers, " + rects.Length + " rects) dev=" + Debug.isDebugBuild);
            foreach (var l in lines) Log("PRIM " + l);
            _sink += sink;
            UnityEngine.Object.Destroy(root);
            yield return null;
        }

        static IEnumerator Variants(int n, GameObject root)
        {
            var s = Gather(root);
            var nodes = s.T.Length;
            var o = new Node[nodes + 4096];
            var none = new Opts();
            Log("scene n=" + n + " transforms=" + nodes + " renderers=" + s.R.Length + " rects=" + s.Rt.Length);

            // ===== V0 baseline =====
            Cell(n, "V0.walk", nodes, () => Walk(root.transform, o, none));
            yield return null;
            Log("n=" + n + " V0 visited=" + _visited);

            // ===== V1b GetComponentsInChildren<Transform>(false, list) under one root, then parent per node =====
            {
                var tl = new List<Transform>(nodes + 16);
                Cell(n, "V1b.root.GetComponentsInChildren<Transform>(false, list)", nodes, () => { tl.Clear(); root.GetComponentsInChildren(false, tl); return tl.Count; });
                yield return null;
                Cell(n, "V1b.+ t.parent per node", nodes, () => { long k = 0; for (var i = 0; i < tl.Count; i++) if (tl[i].parent != null) k++; return k; });
                var seen = new HashSet<Transform>(); var preorder = true;
                foreach (var t in tl) { var pa = t.parent; if (pa != null && pa != root.transform.parent && !seen.Contains(pa) && t != root.transform) preorder = false; seen.Add(t); }
                Log("n=" + n + " V1b order is pre-order (parent before child)=" + preorder);
                yield return null;
            }

            // ===== V1 bulk enumeration =====
#if UNITY_2021_3_OR_NEWER
            Cell(n, "V1.FindObjectsByType<Renderer>", s.R.Length, () => UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).Length);
            yield return null;
            Cell(n, "V1.FindObjectsByType<RectTransform>", s.Rt.Length, () => UnityEngine.Object.FindObjectsByType<RectTransform>(FindObjectsSortMode.None).Length);
            yield return null;
            Cell(n, "V1.FindObjectsByType<Transform>", nodes, () => UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Length);
            yield return null;
            Cell(n, "V1.FindObjectsByType<Canvas>", 1, () => UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None).Length);
            yield return null;
            Cell(n, "V1.FindObjectsByType<Camera>", 1, () => UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Length);
            yield return null;
            {
                var rs = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
                var rts = UnityEngine.Object.FindObjectsByType<RectTransform>(FindObjectsSortMode.None);
                Cell(n, "V1.read renderers (id,name,bounds) given array", rs.Length, () =>
                {
                    long k = 0; var ix = 0;
                    foreach (var r in rs) { var b = r.bounds; o[ix++ % o.Length] = new Node { Id = r.GetInstanceID(), Name = r.name, Min = b.min, Max = b.max }; k += ix; }
                    return k;
                });
                yield return null;
                Cell(n, "V1.read rects (id,name,corners) given array", rts.Length, () =>
                {
                    long k = 0; var ix = 0;
                    foreach (var r in rts) { r.GetWorldCorners(Corners); var nd = new Node { Id = r.GetInstanceID(), Name = r.name }; MinMax(Corners, ref nd); o[ix++ % o.Length] = nd; k += ix; }
                    return k;
                });
                yield return null;
                Cell(n, "V1.total (find R+Rt, read id,name,bounds/corners)", rs.Length + rts.Length, () =>
                {
                    var a = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
                    var bb = UnityEngine.Object.FindObjectsByType<RectTransform>(FindObjectsSortMode.None);
                    long k = 0; var ix = 0;
                    foreach (var r in a) { var b = r.bounds; o[ix++ % o.Length] = new Node { Id = r.GetInstanceID(), Name = r.name, Min = b.min, Max = b.max }; }
                    foreach (var r in bb) { r.GetWorldCorners(Corners); var nd = new Node { Id = r.GetInstanceID(), Name = r.name }; MinMax(Corners, ref nd); o[ix++ % o.Length] = nd; }
                    return k + ix;
                });
                yield return null;
            }
#endif

            // ===== V2 transform batch =====
            {
                var ts = s.T;
                // main-thread reference: the same three reads through Transform
                Cell(n, "V2.ref main-thread t.position/rotation/localToWorldMatrix", nodes, () =>
                {
                    float k = 0; for (var i = 0; i < ts.Length; i++) { var t = ts[i]; k += t.position.x + t.rotation.x + t.localToWorldMatrix.m00; }
                    return (long)k;
                });
                yield return null;
                var tb = new Taa();
                tb.Alloc(nodes);
                // building: Add per transform
                Cell(n, "V2.build Add per transform (+Dispose outside)", nodes, () =>
                {
                    var a = new TransformAccessArray(nodes);
                    for (var i = 0; i < ts.Length; i++) a.Add(ts[i]);
                    tb.A = a; return a.length;
                }, 5, () => { if (tb.A.isCreated) tb.A.Dispose(); });
                yield return null;
                if (tb.A.isCreated) tb.A.Dispose();
                Cell(n, "V2.build SetTransforms(Transform[])", nodes, () =>
                {
                    var a = new TransformAccessArray(nodes);
                    a.SetTransforms(ts);
                    tb.A = a; return a.length;
                }, 5, () => { if (tb.A.isCreated) tb.A.Dispose(); });
                yield return null;
                if (tb.A.isCreated) tb.A.Dispose();
                Cell(n, "V2.build new TransformAccessArray(Transform[])", nodes, () => { tb.A = new TransformAccessArray(ts); return tb.A.length; }, 5, () => { if (tb.A.isCreated) tb.A.Dispose(); });
                yield return null;
                // prebuilt array: schedule + complete immediately (job wall time on the main thread), and schedule/complete split
                var sched = new List<double>(); var compl = new List<double>(); var wall = new List<double>();
                var allocs = new List<long>();
                for (var r = 0; r < Reps; r++)
                {
                    var m0 = GC.GetTotalMemory(false); var g0 = GC.CollectionCount(0);
                    Sw.Restart();
                    var h = tb.Schedule();
                    JobHandle.ScheduleBatchedJobs();
                    var t1 = Sw.Elapsed.TotalMilliseconds;
                    h.Complete();
                    var t2 = Sw.Elapsed.TotalMilliseconds;
                    if (r > 0) { sched.Add(t1); compl.Add(t2 - t1); wall.Add(t2); if (GC.CollectionCount(0) == g0) allocs.Add(GC.GetTotalMemory(false) - m0); }
                    yield return null;
                }

                sched.Sort(); compl.Sort(); wall.Sort(); allocs.Sort();
                Log("n=" + n + " v=V2.reuse prebuilt: schedule med=" + F(sched[sched.Count / 2]) + "ms, complete-immediately wait med=" + F(compl[compl.Count / 2]) + "ms (= job duration on workers), sum med=" + F(wall[wall.Count / 2])
                    + "ms usPerObj(sum)=" + F(wall[wall.Count / 2] * 1000 / nodes) + " allocKB=" + (allocs.Count > 0 ? F(allocs[allocs.Count / 2] / 1024.0) : "n/a") + " cores=" + SystemInfo.processorCount);
                // across frames: schedule in frame k, complete in frame k+1 (job finished): the main-thread cost is schedule + complete
                var s2 = new List<double>(); var c2 = new List<double>();
                for (var r = 0; r < Reps; r++)
                {
                    Sw.Restart();
                    var h = tb.Schedule();
                    JobHandle.ScheduleBatchedJobs();
                    var a = Sw.Elapsed.TotalMilliseconds;
                    yield return null;
                    yield return null;
                    var done = h.IsCompleted;
                    Sw.Restart();
                    h.Complete();
                    var b = Sw.Elapsed.TotalMilliseconds;
                    if (r > 0) { s2.Add(a); c2.Add(b); }
                    if (r == Reps - 1) Log("n=" + n + " v=V2.cross-frame jobDoneAfter2Frames=" + done);
                }

                s2.Sort(); c2.Sort();
                Log("n=" + n + " v=V2.cross-frame reuse: schedule med=" + F(s2[s2.Count / 2]) + "ms complete(after 2 frames) med=" + F(c2[c2.Count / 2]) + "ms main-thread usPerObj=" + F((s2[s2.Count / 2] + c2[c2.Count / 2]) * 1000 / nodes));
                // check: job output against the main-thread matrix
                var maxd = 0f;
                for (var i = 0; i < ts.Length; i += 7) maxd = Mathf.Max(maxd, Mathf.Abs(tb.P[i].x - ts[i].position.x), Mathf.Abs(tb.M[i].m03 - ts[i].localToWorldMatrix.m03));
                Log("n=" + n + " V2.check maxPositionDiff=" + maxd.ToString("F5", CultureInfo.InvariantCulture));
                // only renderers
                var rtf = new Transform[s.R.Length];
                for (var i = 0; i < rtf.Length; i++) rtf[i] = s.R[i].transform;
                var tr = new Taa(); tr.Alloc(rtf.Length); tr.A = new TransformAccessArray(rtf);
                var w2 = new List<double>();
                for (var r = 0; r < Reps; r++) { Sw.Restart(); var h = tr.Schedule(); JobHandle.ScheduleBatchedJobs(); h.Complete(); if (r > 0) w2.Add(Sw.Elapsed.TotalMilliseconds); yield return null; }
                w2.Sort();
                Log("n=" + n + " v=V2.renderers-only reuse schedule+complete med=" + F(w2[w2.Count / 2]) + "ms usPerObj=" + F(w2[w2.Count / 2] * 1000 / rtf.Length) + " objs=" + rtf.Length);
                tr.Free();

                // ===== V3 caches =====
                var cache = new Dictionary<int, Ent>(nodes * 2);
                var ids = s.Ids;
                GC.Collect(); yield return null;
                var mem0 = GC.GetTotalMemory(true);
                Cell(n, "V3.cold fill cache while walking (id,name,GetComponents,parent,localBounds,rect)", nodes, () =>
                {
                    long k = 0;
                    foreach (var t in ts) { var e = MakeEntry(t, false); cache[e.Id] = e; k++; }
                    return k;
                }, 1);
                var mem1 = GC.GetTotalMemory(true);
                Log("n=" + n + " V3.cache memory ~" + F((mem1 - mem0) / 1024.0) + "KB total, " + F((mem1 - mem0) / (double)nodes) + " B/object (entries + dictionary + name strings)");
                yield return null;
                Cell(n, "V3.lookup only: Dictionary<int,Ent>.TryGetValue by cached ids", nodes, () => { long k = 0; for (var i = 0; i < ids.Length; i++) if (cache.TryGetValue(ids[i], out var e)) k += e.Idx + 1; return k; });
                yield return null;
                Cell(n, "V3.GetInstanceID + lookup per transform", nodes, () => { long k = 0; for (var i = 0; i < ts.Length; i++) if (cache.TryGetValue(ts[i].GetInstanceID(), out var e)) k += 1; return k; });
                yield return null;
                // warm walks
                Cell(n, "V3.warm walk W1: names/components cached, natives for activeInHierarchy,id,childCount,GetChild,bounds,corners", nodes, () => WarmWalk(root.transform, cache, o, 0));
                yield return null;
                Cell(n, "V3.warm walk W2: W1 but bounds=cached local bounds + t.localToWorldMatrix (main thread)", nodes, () => WarmWalk(root.transform, cache, o, 1));
                yield return null;
                Cell(n, "V3.warm walk W3: W1 without bounds at all (bounds deferred to worker via V2 matrices)", nodes, () => WarmWalk(root.transform, cache, o, 2));
                yield return null;
                // mesh cache: renderer -> mesh id -> local bounds
                var meshCache = new Dictionary<int, Bounds>();
                Cell(n, "V3.mesh cold: GetComponent<MeshFilter>.sharedMesh id + mesh.bounds on miss", s.R.Length, () =>
                {
                    long k = 0;
                    foreach (var r in s.R)
                    {
                        var m = r.GetComponent<MeshFilter>().sharedMesh;
                        var mid = m.GetInstanceID();
                        if (!meshCache.TryGetValue(mid, out var b)) { b = m.bounds; meshCache[mid] = b; }
                        k += (long)b.size.x;
                    }
                    return k;
                }, 1);
                yield return null;
                var rb = new Dictionary<int, Bounds>();
                foreach (var r in s.R) rb[r.GetInstanceID()] = cache[r.gameObject.GetInstanceID()].LB;
                Cell(n, "V3.mesh warm: renderer r.GetInstanceID + lookup of cached local bounds", s.R.Length, () =>
                {
                    long k = 0;
                    foreach (var r in s.R) if (rb.TryGetValue(r.GetInstanceID(), out var b)) k += (long)b.size.x + 1;
                    return k;
                });
                yield return null;
                // worker side: world boxes from local bounds + matrices; accuracy against Renderer.bounds
                {
                    var idx = new Dictionary<Transform, int>();
                    for (var i = 0; i < ts.Length; i++) idx[ts[i]] = i;
                    var rIdx = new int[s.R.Length]; var rLb = new Bounds[s.R.Length];
                    for (var i = 0; i < s.R.Length; i++) { rIdx[i] = idx[s.R[i].transform]; rLb[i] = cache[s.R[i].gameObject.GetInstanceID()].LB; }
                    var tbh = tb.Schedule(); tbh.Complete();
                    var ws = new List<double>();
                    var mnA = new Vector3[rIdx.Length]; var mxA = new Vector3[rIdx.Length];
                    for (var rep = 0; rep < Reps; rep++)
                    {
                        double ms = 0;
                        var t = Task.Run(() =>
                        {
                            var sw = Stopwatch.StartNew();
                            for (var i = 0; i < rIdx.Length; i++) { var m = tb.M[rIdx[i]]; WorldBox(in m, in rLb[i], out mnA[i], out mxA[i]); }
                            ms = sw.Elapsed.TotalMilliseconds;
                        });
                        t.Wait();
                        if (rep > 0) ws.Add(ms);
                        yield return null;
                    }

                    ws.Sort();
                    var maxDiff = 0f;
                    for (var i = 0; i < rIdx.Length; i += 3) { var b = s.R[i].bounds; maxDiff = Mathf.Max(maxDiff, Vector3.Distance(b.min, mnA[i]), Vector3.Distance(b.max, mxA[i])); }
                    Log("n=" + n + " V3.worker world boxes for " + rIdx.Length + " renderers: " + F(ws[ws.Count / 2]) + "ms (" + F(ws[ws.Count / 2] * 1000 / rIdx.Length) + " us/object on a worker thread); max |corner - Renderer.bounds| = " + maxDiff.ToString("F5", CultureInfo.InvariantCulture));
                }

                tb.Free();
            }

            // ===== V4 skipping work =====
            Cell(n, "V4.walk: skip invisible renderers (r.isVisible before bounds)", nodes, () => Walk(root.transform, o, new Opts { VisOnly = true }));
            Log("n=" + n + " V4.vis: visited=" + _visited + " skippedInvisible=" + _skipInvisible + " of " + s.R.Length + " renderers");
            yield return null;
            Cell(n, "V4.walk: skip rects under 40 units (rt.rect before GetWorldCorners)", nodes, () => Walk(root.transform, o, new Opts { MinRect = 40f }));
            Log("n=" + n + " V4.small: visited=" + _visited + " skippedSmall=" + _skipSmall + " of " + s.Rt.Length + " rects");
            yield return null;
            // deactivate every 3rd group subtree, then walk again: inactive-subtree skipping
            {
                var before = Walk(root.transform, o, none);
                var g = 0;
                foreach (var t in s.T) if (t != null && (t.name.StartsWith("Group3D") || t.name.StartsWith("Panel")) && (g++ % 3 == 0)) t.gameObject.SetActive(false);
                yield return null;
                Cell(n, "V4.walk on scene with every 3rd group subtree inactive", before, () => Walk(root.transform, o, none));
                Log("n=" + n + " V4.inactive: visited=" + _visited + " (was " + before + "), inactive roots skipped=" + _skipInactive + " (children of an inactive object are never reached)");
                foreach (var t in s.T) if (t != null) t.gameObject.SetActive(true);
                yield return null;
            }

            // ===== V5 mix: bulk Transform enumeration + id cache + TransformAccessArray job (reused when the object set is unchanged) + worker boxes =====
#if UNITY_2021_3_OR_NEWER
            yield return V5(n, s);
#endif
        }

        // mode 0: native bounds/corners, 1: cached local bounds + matrix on main thread, 2: no bounds
        static long WarmWalk(Transform root, Dictionary<int, Ent> cache, Node[] o, int mode)
        {
            var n = 0; var sp = 0;
            if (_stk.Length < 16) _stk = new Frame[512];
            _stk[sp++] = new Frame { T = root, Next = -1 };
            while (sp > 0)
            {
                var f = _stk[sp - 1];
                Transform t;
                if (f.Next < 0) { t = f.T; sp--; }
                else
                {
                    if (f.Next >= f.T.childCount) { sp--; continue; }
                    t = f.T.GetChild(f.Next++);
                    _stk[sp - 1] = f;
                }

                var go = t.gameObject;
                if (!go.activeInHierarchy) continue;
                if (!cache.TryGetValue(go.GetInstanceID(), out var e)) continue;
                var nd = new Node { Id = e.Id, Name = e.Name, Cls = e.Cls };
                if (mode != 2)
                {
                    if (e.Rt != null && e.InCanvas) { e.Rt.GetWorldCorners(Corners); MinMax(Corners, ref nd); }
                    else if (e.R != null && e.R.enabled)
                    {
                        if (mode == 0) { var b = e.R.bounds; nd.Min = b.min; nd.Max = b.max; }
                        else { var m = e.T.localToWorldMatrix; WorldBox(in m, in e.LB, out nd.Min, out nd.Max); }
                    }
                }

                o[n++] = nd;
                if (e.T.childCount > 0)
                {
                    if (sp == _stk.Length) Array.Resize(ref _stk, sp * 2);
                    _stk[sp++] = new Frame { T = e.T, Next = 0 };
                }
            }

            return n;
        }

#if UNITY_2021_3_OR_NEWER
        static IEnumerator V5(int n, Scn s)
        {
            // Steady-state capture: (1) FindObjectsByType<Transform> (all active objects), (2) per object GetInstanceID + dictionary lookup,
            // cold entries read fully, (3) the transform array is rebuilt only when the set of ids changed (order-independent hash), (4) job
            // scheduled; next frame completed, (5) worker computes boxes from cached local bounds / rects and the matrices.
            var cache = new Dictionary<int, Ent>(s.T.Length * 2);
            Taa tb = null;
            long lastHash = -1;
            int[] taaIds = null;
            var o = new Node[s.T.Length + 4096];
            double mainFind = 0, mainLookup = 0, mainBuild = 0, mainSched = 0, mainComplete = 0, workerMs = 0;
            int misses = 0, rebuilt = 0, found = 0;

            IEnumerator Capture(List<double> mainTotals, List<double> workers, List<double>[] parts)
            {
                var sw = new Stopwatch();
                // step 1+2+3+4
                sw.Restart();
                var ts = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
                mainFind = sw.Elapsed.TotalMilliseconds; found = ts.Length;
                sw.Restart();
                long hash = 0; misses = 0;
                var ents = new Ent[ts.Length]; var tids = new int[ts.Length];
                for (var i = 0; i < ts.Length; i++)
                {
                    var id = ts[i].GetInstanceID(); tids[i] = id;
                    hash += id * 2654435761L ^ id;
                    if (!cache.TryGetValue(id, out var e)) { e = MakeEntry(ts[i], false); cache[id] = e; misses++; }
                    ents[i] = e;
                }

                mainLookup = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                rebuilt = 0;
                if (tb == null || hash != lastHash || tb.Len != ts.Length)
                {
                    tb?.Free();
                    tb = new Taa();
                    tb.Alloc(ts.Length);
                    tb.A = new TransformAccessArray(ts);
                    taaIds = new int[ts.Length];
                    Array.Copy(tids, taaIds, tids.Length);
                    lastHash = hash;
                    rebuilt = 1;
                }

                mainBuild = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                var h = tb.Schedule();
                JobHandle.ScheduleBatchedJobs();
                mainSched = sw.Elapsed.TotalMilliseconds;
                yield return null;
                yield return null;
                sw.Restart();
                h.Complete();
                mainComplete = sw.Elapsed.TotalMilliseconds;
                var tbl = tb; var ids = taaIds;
                var t = Task.Run(() =>
                {
                    var w = Stopwatch.StartNew();
                    var k = 0;
                    for (var i = 0; i < ids.Length; i++)
                    {
                        var e = cache[ids[i]];
                        var m = tbl.M[i];
                        var nd = new Node { Id = e.Id, Name = e.Name, Cls = e.Cls };
                        if (e.Rt != null && e.InCanvas) WorldRect(in m, in e.RectLocal, out nd.Min, out nd.Max);
                        else if (e.R != null) WorldBox(in m, in e.LB, out nd.Min, out nd.Max);
                        o[k++ % o.Length] = nd;
                    }

                    workerMs = w.Elapsed.TotalMilliseconds;
                });
                t.Wait();
                var total = mainFind + mainLookup + mainBuild + mainSched + mainComplete;
                mainTotals.Add(total);
                workers.Add(workerMs);
                parts[0].Add(mainFind); parts[1].Add(mainLookup); parts[2].Add(mainBuild); parts[3].Add(mainSched + mainComplete);
            }

            for (var phase = 0; phase < 2; phase++)
            {
                // phase 0: cold (cache empty, first array), phase 1: warm steady state
                var totals = new List<double>(); var workers = new List<double>();
                var parts = new[] { new List<double>(), new List<double>(), new List<double>(), new List<double>() };
                var reps = phase == 0 ? 1 : Reps;
                if (phase == 1) { yield return null; }
                for (var r = 0; r < reps; r++)
                {
                    var m0 = GC.GetTotalMemory(false); var g0 = GC.CollectionCount(0);
                    yield return Capture(totals, workers, parts);
                    if (phase == 1 && r == reps - 1) Log("n=" + n + " V5.last alloc since capture start ~" + F((GC.GetTotalMemory(false) - m0) / 1024.0) + "KB gcs=" + (GC.CollectionCount(0) - g0) + " (includes the wait frames)");
                    yield return null;
                }

                totals.Sort(); workers.Sort();
                foreach (var p in parts) p.Sort();
                var nodes = found;
                var mt = totals[totals.Count / 2];
                Log("n=" + n + " v=V5." + (phase == 0 ? "cold" : "warm") + " objs=" + nodes + " mainMs med=" + F(mt) + " max=" + F(totals[totals.Count - 1]) + " usPerObj=" + F(mt * 1000 / nodes)
                    + " | find=" + F(parts[0][parts[0].Count / 2]) + " lookup=" + F(parts[1][parts[1].Count / 2]) + " buildArray=" + F(parts[2][parts[2].Count / 2]) + " sched+complete=" + F(parts[3][parts[3].Count / 2])
                    + " | workerMs med=" + F(workers[workers.Count / 2]) + " | lastMisses=" + misses + " lastRebuilt=" + rebuilt
                    + " | objectsPer0.83msSlice(if per-object part only)=" + F(830.0 / Math.Max(0.0001, (parts[1][parts[1].Count / 2] * 1000 / nodes))));
            }

            // accuracy: worker output against the native reads for renderers and rects
            var maxR = 0f; var maxU = 0f;
            var byId = new Dictionary<int, Node>();
            // o holds the last capture; compare a sample with Renderer.bounds / GetWorldCorners
            for (var i = 0; i < o.Length; i++) if (o[i].Name != null) byId[o[i].Id] = o[i];
            foreach (var r in s.R) { if (byId.TryGetValue(r.gameObject.GetInstanceID(), out var nd)) { var b = r.bounds; maxR = Mathf.Max(maxR, Vector3.Distance(b.min, nd.Min), Vector3.Distance(b.max, nd.Max)); } }
            foreach (var rt in s.Rt) { if (byId.TryGetValue(rt.gameObject.GetInstanceID(), out var nd)) { rt.GetWorldCorners(Corners); var tmp = new Node(); MinMax(Corners, ref tmp); maxU = Mathf.Max(maxU, Vector3.Distance(tmp.Min, nd.Min), Vector3.Distance(tmp.Max, nd.Max)); } }
            Log("n=" + n + " V5.accuracy max renderer box diff=" + maxR.ToString("F5", CultureInfo.InvariantCulture) + " max rect box diff=" + maxU.ToString("F5", CultureInfo.InvariantCulture));
            tb?.Free();
            yield return null;
        }
#endif
    }
}
