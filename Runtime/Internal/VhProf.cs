using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace Bugsee.Internal
{
    /// <summary>SPIKE ONLY (spike/vh-stage-attribution, never merged): chained per-stage Stopwatch accounting for the managed view walker.</summary>
    static class VhProf
    {
        public const int Step_Pre = 0, SliceChk = 1, SF_Count = 2, SF_Budget = 3, SF_GetChild = 4, V_Active = 5, V_Id = 6, V_Seen = 7, V_Budget = 8,
            V_Name = 9, V_CompsIcall = 10, V_CompsLoop = 11, V_SnapAdd = 12, V_Canvas = 13, V_RectRead = 14, V_RectMath = 15, V_RectSet = 16,
            V_RendRead = 17, V_RendSet = 18, V_Push = 19, Sc_Cameras = 20, Sc_Seed = 21, Sc_UnitFirst = 22, Sc_UnitChild = 23, Sc_RendRead = 24,
            Sc_RendGeom = 25, Sc_Add = 26, Sc_Canvas = 27, Sc_SliceChk = 28, Sc_Budget = 29, Step_Post = 30, Other = 31, CmpNull = 32, CmpIs = 33, CmpName = 34, Count = 35;

        public static readonly string[] Names =
        {
            "step_pre(secure rects,Screen)", "sliceExpired(tree)", "sf.childCount", "sf.IsExhausted", "sf.GetChild+gameObject", "v.activeInHierarchy",
            "v.GetInstanceID", "v._seen.Add", "v.TryTake", "v.go.name", "v.GetComponents icall", "v.components managed loop", "v.snapshot.Add+AddComponent",
            "v.CanvasOf", "v.rect+matrix read", "v.corners math", "v.SetBounds(rect)", "v.renderer.enabled+bounds", "v.SetBounds(renderer)", "v.childCount+Push",
            "sc.cameras+seed phase0", "sc.SeedUnits", "sc.unit first(go,active,TryGetComp)", "sc.unit child(childCount,GetChild,lists)", "sc.rend isVisible+go+id+layer",
            "sc.rend bounds/matrix", "sc.scene.AddObject", "sc.canvases", "sc.sliceExpired", "sc.SceneExpired", "step_post", "other", "v.cmp Unity-null check", "v.cmp is-type tests", "v.cmp GetType+dict+NameBuffer"
        };

        public static readonly long[] Ticks = new long[Count];
        public static readonly long[] Calls = new long[Count];
        static long _last;
        public static bool On = true;
        public static int Nodes, RectNodes, RendNodes, Units, SceneRends, Steps;
        public static long OverheadTicksPerCall;
        static long _heapAtBegin;
        static long _allocAccum;
        static long _stepAlloc;
        static Unity.Profiling.ProfilerRecorder _rec;
        static int _gc0;
        public static long TreeTicks, SceneTicks;

        public static void Calibrate()
        {
            if (OverheadTicksPerCall != 0) return;
            try { _rec = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Memory, "GC Allocated In Frame"); } catch (Exception) { }
            long best = long.MaxValue;
            for (var rep = 0; rep < 5; rep++)
            {
                var a = Stopwatch.GetTimestamp();
                _last = a;
                for (var i = 0; i < 20000; i++) T(Other);
                var b = Stopwatch.GetTimestamp();
                var per = (b - a) / 20000;
                if (per < best) best = per;
            }

            OverheadTicksPerCall = Math.Max(best, 1);
            Array.Clear(Ticks, 0, Count);
            Array.Clear(Calls, 0, Count);
        }

        public static void Reset()
        {
            Array.Clear(Ticks, 0, Count);
            Array.Clear(Calls, 0, Count);
            Nodes = RectNodes = RendNodes = Units = SceneRends = Steps = 0;
            _allocAccum = 0;
            _gc0 = GC.CollectionCount(0);
            TreeTicks = SceneTicks = 0;
        }

        public static void StepBegin()
        {
            Steps++;
            _stepAlloc = _rec.Valid ? _rec.LastValue : 0;
            _last = Stopwatch.GetTimestamp();
        }

        public static void StepEnd()
        {
            T(Step_Post);
            if (_rec.Valid) { var v = _rec.LastValue; if (v > _stepAlloc) _allocAccum += v - _stepAlloc; }
        }

        public static void T(int stage)
        {
            var now = Stopwatch.GetTimestamp();
            Ticks[stage] += now - _last;
            Calls[stage]++;
            _last = now;
        }

        public static string[] Dump()
        {
            var f = 1e6 / Stopwatch.Frequency; // ticks -> us
            var sb = new StringBuilder();
            double total = 0;
            for (var i = 0; i < Count; i++)
            {
                var t = Ticks[i] - Calls[i] * OverheadTicksPerCall;
                total += t * f;
            }

            sb.Append("[VHSTAGE] nodes=").Append(Nodes).Append(" rectNodes=").Append(RectNodes).Append(" rendNodes=").Append(RendNodes)
              .Append(" sceneUnits=").Append(Units).Append(" sceneRends=").Append(SceneRends).Append(" steps=").Append(Steps)
              .Append(" ovhNsPerT=").Append((OverheadTicksPerCall * f * 1000).ToString("F0"))
              .Append(" sumUs=").Append(total.ToString("F0")).Append(" allocBytes(heapDelta)=").Append(_allocAccum)
              .Append(" gc0=").Append(GC.CollectionCount(0) - _gc0);
            var parts = new string[2];
            for (var i = 0; i < Count; i++)
            {
                if (i == 20) { parts[0] = sb.ToString(); sb.Length = 0; sb.Append("[VHSTAGE2]"); }
                if (Calls[i] == 0) continue;
                var t = (Ticks[i] - Calls[i] * OverheadTicksPerCall) * f;
                sb.Append(" | ").Append(i).Append(' ').Append(Names[i]).Append(" us=").Append(t.ToString("F0")).Append(" n=").Append(Calls[i]);
            }

            parts[1] = sb.ToString();
            return parts;
        }
    }
}
