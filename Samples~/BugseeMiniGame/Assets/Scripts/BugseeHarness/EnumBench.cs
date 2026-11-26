using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Bugsee.Sample
{
    /// <summary>
    /// Spike (BUGSEE_E2E_SCENARIO=enumbench): cost of enumerating a Unity scene tree, per returned node, for the walk the managed view
    /// hierarchy uses today (childCount / GetChild / gameObject / activeInHierarchy) against bulk calls: root.GetComponentsInChildren
    /// &lt;Transform&gt;(false, list) per root, then parent per node, and FindObjectsByType&lt;Transform&gt;(None). Also logs whether the bulk order is
    /// pre-order and whether sibling order equals GetChild order (booleans only: not relied on) and the worst single-call duration.
    /// Not shipped.
    /// </summary>
    static class EnumBench
    {
        static readonly int[] Counts = { 1000, 5000, 20000 };
        static readonly int[] RootCounts = { 1, 8 };
        const int Reps = 9;
        static long _sink;
        static readonly Stopwatch Sw = new Stopwatch();
        static string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
        static void Log(string s) => Debug.Log("[ENUMBENCH] " + s);

        // Breadth-first fill, 10 children per node (a root, groups of 10, leaves): about the shape of a UI or level hierarchy.
        // Every 9th second-level group is switched off afterwards (inactive subtrees must be skipped by the active-only calls).
        static List<Transform> Build(int total, int roots, bool someInactive, out int activeCount)
        {
            var rootList = new List<Transform>();
            var queue = new Queue<Transform>();
            var made = 0;
            var per = Math.Max(1, total / roots);
            for (var r = 0; r < roots; r++)
            {
                var root = new GameObject("EB_Root" + r).transform;
                rootList.Add(root);
                queue.Enqueue(root);
                var count = 1;
                while (queue.Count > 0 && count < per)
                {
                    var p = queue.Dequeue();
                    for (var k = 0; k < 10 && count < per; k++)
                    {
                        var c = new GameObject("n" + count).transform;
                        c.SetParent(p, false);
                        queue.Enqueue(c);
                        count++;
                    }
                }
                queue.Clear();
                made += count;
            }

            activeCount = made;
            if (someInactive)
            {
                var off = 0;
                var inactive = 0;
                foreach (var root in rootList)
                    for (var i = 0; i < root.childCount; i++)
                    {
                        var g = root.GetChild(i);
                        if (g.childCount > 0 && (off++ % 9) == 8) { inactive += g.GetComponentsInChildren<Transform>(true).Length; g.gameObject.SetActive(false); }
                    }
                activeCount = made - inactive;
            }

            return rootList;
        }

        static double Time(Func<long> body, out double[] samples)
        {
            samples = new double[Reps];
            for (var r = 0; r < Reps; r++)
            {
                Sw.Restart();
                _sink += body();
                Sw.Stop();
                samples[r] = Sw.Elapsed.TotalMilliseconds;
            }
            var s = new double[Reps - 1];
            Array.Copy(samples, 1, s, 0, Reps - 1); // the first is cold
            Array.Sort(s);
            return s[s.Length / 2];
        }

        static void Cell(string label, int n, int roots, int objs, Func<long> body)
        {
            var med = Time(body, out var all);
            var max = 0.0;
            for (var i = 1; i < all.Length; i++) max = Math.Max(max, all[i]);
            Log("n=" + n + " roots=" + roots + " v=" + label + " objs=" + objs + " cold=" + F(all[0]) + "ms med=" + F(med) + "ms max=" + F(max) + "ms usPerNode=" + F(med * 1000.0 / Math.Max(1, objs)));
        }

        public static IEnumerator Run()
        {
            Log("start unity=" + Application.unityVersion + " dev=" + Debug.isDebugBuild + " frame60=16.67ms 5pct=0.83ms");
            foreach (var n in Counts)
            foreach (var roots in RootCounts)
            {
                var rootList = Build(n, roots, true, out var active);
                yield return null;
                yield return null;
                var all = 0;
                foreach (var r in rootList) all += r.GetComponentsInChildren<Transform>(true).Length;
                Log("scene n=" + n + " roots=" + roots + " total=" + all + " activeInHierarchy=" + active);

                // ---- today's walk: childCount, GetChild, .gameObject, activeInHierarchy per node (explicit stack, active only) ----
                var stack = new Stack<Transform>();
                Func<long> walk = () =>
                {
                    long k = 0;
                    foreach (var root in rootList)
                    {
                        if (!root.gameObject.activeInHierarchy) continue;
                        stack.Push(root);
                        while (stack.Count > 0)
                        {
                            var t = stack.Pop();
                            k++;
                            var cc = t.childCount;
                            for (var i = 0; i < cc; i++)
                            {
                                var go = t.GetChild(i).gameObject;
                                if (!go.activeInHierarchy) continue;
                                stack.Push(go.transform);
                            }
                        }
                    }
                    return k;
                };
                Cell("walk childCount+GetChild+gameObject+activeInHierarchy", n, roots, active, walk);
                yield return null;

                // ---- bulk per root ----
                var lists = new List<Transform>[roots];
                for (var i = 0; i < roots; i++) lists[i] = new List<Transform>(n / roots + 16);
                Cell("bulk GetComponentsInChildren<Transform>(false,list) per root", n, roots, active, () =>
                {
                    long k = 0;
                    for (var i = 0; i < roots; i++) { lists[i].Clear(); rootList[i].GetComponentsInChildren(false, lists[i]); k += lists[i].Count; }
                    return k;
                });
                yield return null;

                // worst single call = the biggest root (all roots are equal here, so the first)
                {
                    var worst = 0.0;
                    for (var r = 0; r < Reps; r++)
                    {
                        lists[0].Clear();
                        Sw.Restart();
                        rootList[0].GetComponentsInChildren(false, lists[0]);
                        Sw.Stop();
                        if (r > 0) worst = Math.Max(worst, Sw.Elapsed.TotalMilliseconds);
                    }
                    Log("n=" + n + " roots=" + roots + " worstSingleBulkCall nodes=" + lists[0].Count + " maxMs=" + F(worst));
                }

                Cell("parent per node (on the bulk lists)", n, roots, active, () =>
                {
                    long k = 0;
                    for (var i = 0; i < roots; i++) { var l = lists[i]; for (var j = 0; j < l.Count; j++) if (l[j].parent != null) k++; }
                    return k;
                });
                yield return null;

                // tree rebuild: parent per node + hash lookup of the parent's slot (what the walker would do to recreate the tree)
                var slot = new Dictionary<int, int>(n + 16);
                var parentOf = new int[n + 16];
                Cell("bulk + parent + id lookup (tree rebuild)", n, roots, active, () =>
                {
                    long k = 0;
                    slot.Clear();
                    var idx = 0;
                    for (var i = 0; i < roots; i++)
                    {
                        var l = lists[i];
                        l.Clear();
                        rootList[i].GetComponentsInChildren(false, l);
                        for (var j = 0; j < l.Count; j++)
                        {
                            var t = l[j];
                            slot[t.GetHashCode()] = idx;
                            var pa = t.parent;
                            parentOf[idx] = pa != null && slot.TryGetValue(pa.GetHashCode(), out var ps) ? ps : -1;
                            idx++;
                        }
                        k += l.Count;
                    }
                    return k;
                });
                yield return null;

                Cell("hierarchyCount per root", n, roots, roots, () => { long k = 0; foreach (var r in rootList) k += r.hierarchyCount; return k; });

                // ---- FindObjectsByType<Transform>(None), active only ----
                Transform[] found = null;
                Cell("FindObjectsByType<Transform>(Exclude, None) (all scenes)", n, roots, active, () => { found = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None); return found.Length; });
                yield return null;
                {
                    var worst = 0.0;
                    for (var r = 0; r < Reps; r++)
                    {
                        Sw.Restart();
                        found = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                        Sw.Stop();
                        if (r > 0) worst = Math.Max(worst, Sw.Elapsed.TotalMilliseconds);
                    }
                    Log("n=" + n + " roots=" + roots + " worstSingleFindObjectsByType returned=" + found.Length + " maxMs=" + F(worst) + " (counts every Transform in the process, the sample's own scene included)");
                }
                Cell("FindObjectsByType + parent per node", n, roots, active, () => { var a = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None); long k = 0; for (var i = 0; i < a.Length; i++) if (a[i].parent != null) k++; return k; });
                yield return null;

                // ---- order checks (booleans, not relied on) ----
                for (var i = 0; i < roots; i++) { lists[i].Clear(); rootList[i].GetComponentsInChildren(false, lists[i]); }
                {
                    var preorderAll = true;
                    var siblingOrderAll = true;
                    var parentBeforeChildAll = true;
                    foreach (var l in lists)
                    {
                        // pre-order: each node's parent is the node nearest above it with a smaller depth; equivalently the sequence equals a DFS over GetChild
                        var expect = new List<Transform>();
                        var st = new Stack<Transform>();
                        st.Push(l[0]);
                        while (st.Count > 0)
                        {
                            var t = st.Pop();
                            if (!t.gameObject.activeInHierarchy) continue;
                            expect.Add(t);
                            for (var c = t.childCount - 1; c >= 0; c--) st.Push(t.GetChild(c));
                        }
                        if (expect.Count != l.Count) { preorderAll = false; siblingOrderAll = false; }
                        else for (var j = 0; j < l.Count; j++) if (!ReferenceEquals(expect[j], l[j])) { preorderAll = false; break; }

                        // parent-before-child and sibling order (the order children appear among their parent's children equals GetChild order)
                        var pos = new Dictionary<Transform, int>();
                        for (var j = 0; j < l.Count; j++) pos[l[j]] = j;
                        var lastChildIndex = new Dictionary<Transform, int>();
                        for (var j = 1; j < l.Count; j++)
                        {
                            var pa = l[j].parent;
                            if (!pos.TryGetValue(pa, out var pj) || pj >= j) parentBeforeChildAll = false;
                            var sib = l[j].GetSiblingIndex();
                            if (lastChildIndex.TryGetValue(pa, out var prev) && sib <= prev) siblingOrderAll = false;
                            lastChildIndex[pa] = sib;
                        }
                    }
                    Log("n=" + n + " roots=" + roots + " order: exactPreOrderEqualsGetChildDfs=" + preorderAll + " parentBeforeChild=" + parentBeforeChildAll + " siblingOrderEqualsGetChildOrder=" + siblingOrderAll);
                }

                foreach (var r in rootList) UnityEngine.Object.Destroy(r.gameObject);
                yield return null;
                yield return null;
            }

            Log("done sink=" + _sink);
        }
    }
}
