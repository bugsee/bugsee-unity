# Managed view walker: per-stage main-thread attribution (spike, not for merge)

Branch `spike/vh-stage-attribution` (base: origin/main a9db47ba, which has #295 and #296). Instrumentation is `Runtime/Internal/VhProf.cs`
plus `VhProf.T(stage)` calls in `ManagedViewWalker` (chained `Stopwatch.GetTimestamp`, one timestamp per stage boundary, so every
nanosecond of `Step` falls into exactly one stage). It is a measurement patch and is never shipped.

## Setup

- HONOR WOD-LX1, Android, Unity 2021.3.45f1, IL2CPP **release**, arm64. iPhone XS (KRSFT), iOS 18.7.9, Unity 2022.3.62f3, IL2CPP **release**
  (reached with `xcrun devicectl`, installed and launched through `devicectl device process launch --console`; it was connected and
  needed no retry beyond the first attempt).
- Scenario `vhscale` (sliced, the production 5 % slices) and `vhscaleunsliced` (one 1 s slice, so no frame scheduling is involved), N=1000
  and N=5000, 8 captures per N per launch, 3 launches per device and mode, count order alternated (1000,5000 / 5000,1000 / 1000,5000).
  The first capture of every N is dropped (cold). Table values are medians over the 21 warm captures.
- The timer itself costs about 100 ns per `T()` call (calibrated in-process at start and subtracted per call). 64 000 calls per N=1000 capture
  is 6.4 ms: the instrumented `mainMs` minus the corrected stage sum is 6.4 ms on HONOR (47.4 vs 41.0) and 6.2 ms on the iPhone, i.e. the corrected
  stage sum explains the main-thread time with a residual of under 1 %.
- Raw logs: `/Volumes/External2TB/tmp-claude/kept-evidence/vh-stage/out/{U3?,S3?,IU3?,I3?}.log`; scripts `agg.py`, `table.py` in the same folder.
  (An earlier v2 build, which also used `GC.GetTotalMemory`, is in `S?`/`U?`/`I?` logs; the numbers agree.)

## What the old "14-20 us per node" was

`mainMs / nodes` divides the main-thread time of the **tree walk and the 3D scene pass** by the number of **tree** nodes. With the scene block on,
the scene pass adds 5.4-6.1 us per scene unit (HONOR), and there are more scene units (every transform: 2732 at N=1000, 6732 at N=5000) than
tree nodes (capped at 1500). Unsliced `mainMs` per tree node is therefore 31.7 (N=1000) and 50 (N=5000) us with the scene pass,
and the **tree alone is 15.3-16.4 us per node** (HONOR). The spike's bare walk (8.5-10 us) makes the Unity calls only; the difference
(about 6-7 us) is managed work in the walker, itemised below.

## Stage table, unsliced (us per tree node; scene stages per scene unit)

| stage (us per tree node; scene stages per scene unit) | HONOR unsl N=1000 | HONOR unsl N=5000 | iPhone unsl N=1000 | iPhone unsl N=5000 |
|---|---|---|---|---|
| 1 sliceExpired(tree) | 0.39 | 0.36 | 0.26 | 0.23 |
| 3 sf.IsExhausted | 0.15 | 0.15 | 0.20 | 0.18 |
| 7 v._seen.Add | 0.54 | 0.48 | 0.12 | 0.10 |
| 8 v.TryTake | 0.18 | 0.18 | 0.19 | 0.18 |
| 0 step_pre(secure rects,Screen) | 0.01 | 0.01 | 0.00 | 0.00 |
| 2 sf.childCount | 0.22 | 0.22 | 0.09 | 0.08 |
| 4 sf.GetChild+gameObject | 1.18 | 1.14 | 0.30 | 0.28 |
| 5 v.activeInHierarchy | 0.44 | 0.44 | 0.12 | 0.12 |
| 6 v.GetInstanceID | 0.10 | 0.10 | 0.03 | 0.03 |
| 9 v.go.name | 2.61 | 2.60 | 0.94 | 0.58 |
| 10 v.GetComponents icall | 2.88 | 2.85 | 0.91 | 0.84 |
| 32 v.cmp Unity-null check | 0.76 | 0.76 | 0.09 | 0.10 |
| 33 v.cmp is-type tests | 0.33 | 0.32 | 0.07 | 0.07 |
| 34 v.cmp GetType+dict+NameBuffer | 1.31 | 1.30 | 0.42 | 0.39 |
| 11 v.components managed loop | 0.17 | 0.17 | 0.09 | 0.08 |
| 12 v.snapshot.Add+AddComponent | 2.37 | 0.94 | 0.21 | 0.45 |
| 13 v.CanvasOf | 0.05 | 0.05 | 0.03 | 0.03 |
| 17 v.renderer.enabled+bounds | 1.27 | 1.25 | 0.34 | 0.31 |
| 18 v.SetBounds(renderer) | 0.86 | 0.82 | 0.24 | 0.21 |
| 19 v.childCount+Push | 0.37 | 0.38 | 0.08 | 0.08 |
| 20 sc.cameras+seed phase0 [per scene unit] | 0.04 | 0.02 | 0.01 | 0.00 |
| 21 sc.SeedUnits [per scene unit] | 0.03 | 0.01 | 0.01 | 0.00 |
| 22 sc.unit first(go,active,TryGetComp) [per scene unit] | 2.23 | 2.31 | 0.36 | 0.31 |
| 23 sc.unit child(childCount,GetChild,lists) [per scene unit] | 1.16 | 1.15 | 0.11 | 0.08 |
| 24 sc.rend isVisible+go+id+layer [per scene unit] | 0.54 | 0.67 | 0.09 | 0.12 |
| 25 sc.rend bounds/matrix [per scene unit] | 0.27 | 0.53 | 0.04 | 0.07 |
| 26 sc.scene.AddObject [per scene unit] | 0.12 | 0.55 | 0.02 | 0.12 |
| 27 sc.canvases [per scene unit] | 0.01 | 0.01 | 0.00 | 0.00 |
| 28 sc.sliceExpired [per scene unit] | 0.45 | 0.46 | 0.28 | 0.21 |
| 29 sc.SceneExpired [per scene unit] | 0.40 | 0.40 | 0.27 | 0.22 |
| 30 step_post [per scene unit] | 0.02 | 0.01 | 0.00 | 0.00 |
| TREE stages total us/node | 16.41 | 15.33 | 4.93 | 4.29 |
| SCENE stages total us/scene unit | 5.35 | 6.08 | 1.33 | 1.19 |
| engine mainMs (instrumented run), ms | 47.4 | 75.3 | 19.4 | 30.1 |
| sum of stages, timer cost removed, ms | 41.0 | 64.3 | 13.2 | 19.2 |
| tree nodes / scene units | 1498 / 2732 | 1498 / 6732 | 1498 / 2732 | 1498 / 6732 |
| T() calls per capture x calibrated ns | 64060 x 100 ns | 109120 x 100 ns | 64072 x 100 ns | 109162 x 100 ns |

Unity calls per tree node by stage (code reading; every row is one native call unless noted):
`sf.childCount` 1 (`childCount`), `sf.GetChild+gameObject` 2, `v.activeInHierarchy` 1, `v.GetInstanceID` 1 (no native call in a player
build), `v.go.name` 1 (+ a managed string), `v.GetComponents` 1, `v.renderer.enabled+bounds` 0.76 x 2, `v.childCount+Push` 1,
`CanvasOf` 0 (only for nodes with a Canvas). About 9.5 Unity calls per node in all: HONOR 9.5 us, i.e. the bare walk's 8.5-10 us.
Scene unit: `t.gameObject`, `activeInHierarchy`, `TryGetComponent<Renderer>` (1.63 per unit), `childCount`, `GetChild` (about 2 per unit), and per renderer
`isVisible`, `r.gameObject`, `GetInstanceID`, `layer`, `localBounds`/`bounds`, `localToWorldMatrix`.

## Where the 6-7 us of non-Unity time per tree node goes (HONOR, unsliced)

| managed cost | us per node | share of the tree | note |
|---|---|---|---|
| component loop: `GetType()` + `Dictionary<Type,string>` + name list | 1.3 | 8 % | 1.76 components per node |
| component loop: `component == null` (Unity null check) | 0.76 | 5 % | 2.76 per node, 0.28 each |
| component loop: `is Transform / CanvasRenderer / Canvas / Renderer` | 0.33 | 2 % | |
| `snapshot.Add` + `AddComponent` | 0.9 (N=5000) to 2.4 (N=1000) | 6-14 % | nine array stores plus a list add; the spread is array growth (512 -> 1024 -> 2048 doubling, ten `Array.Resize`) and the GC it triggers |
| `SetBounds` (8-corner projection) | 0.85 | 5 % | renderer nodes only (76 %) |
| `sliceExpired` delegate + clock read | 0.39 | 2.5 % | 1.23 calls per node |
| `_seen` `HashSet<int>.Add` | 0.5 | 3 % | |
| `TryTake`, `IsExhausted` | 0.33 | 2 % | `IsExhausted` also reads the clock |
| **total** | **about 6-7** | **38-44 %** | |

iPhone XS: the same managed work is about 2.0 us of 4.3-4.9 us per node (component loop 0.67, snapshot 0.2-0.45, SetBounds 0.22, delegate 0.23,
`TryTake`/`IsExhausted` 0.35, seen-set 0.1).

## Findings

1. The Unity calls cost 9.5 us per node on HONOR and 2.4-2.9 on the iPhone (3.3-3.9 times faster, everything).
2. The biggest non-Unity costs are the component-type loop (2.6 us per node on HONOR, 17 % of the tree walk), `snapshot.Add` with its
   array growth (1-2.4 us), the 8-corner projection (0.85) and the per-node `sliceExpired` delegate with its clock read (0.39, plus 0.33 for the
   budget checks). JSON building and string concatenation are not on the main thread at all (the snapshot is plain arrays; the tree, escaping and
   JSON run on the worker). The only per-node managed allocation is the name string.
3. Allocation per capture: `GC.GetTotalMemory` deltas (v2 build) were 0.33-0.46 MB per sliced capture and 0.37-0.75 MB per unsliced capture. That is an upper
   bound (heap chunk growth and the game's own allocations in the same window are in it); `GC.CollectionCount(0)` rose by 3-5 per capture on HONOR and 5-11 on
   the iPhone. The Profiler counter `GC Allocated In Frame` returned nothing in a release player, so there is no exact figure. By reading the code the walk's own
   allocation is the name string per node (about 40-50 B) plus the snapshot arrays (about 60 B per node, doubling twice for 1500 nodes). The spread of
   `snapshot.Add` between N=1000 (2.4 us) and N=5000 (0.9 us) is the allocation/GC cost landing on whichever call allocates.
4. In the production (sliced) mode on HONOR the capture is limited by the 250 ms deadline, not by the 1500-node cap: 570-680 nodes are read in
   15-16 slices (5 % of a 16.7-33 ms frame is 0.8-1.7 ms) and the scene block usually has no time left (`sceneObjects` median 0, "time_budget"). Per node
   the sliced walk is slower (17-21 us, cold caches after every frame) than the unsliced one (15-16 us).
5. Time between slices on the main thread: none from Bugsee beyond `MainThreadDispatcher.Update` and `Pump` (a `Time.frameCount` read and
   one delegate); the wall time of a capture (250-300 ms) is the frames in between, not main-thread cost.

## Cheap production fixes that this suggests (not part of the scene-feed PR)

- Component loop (2.6 us per node, 17 %): cache by `Type` with a reference comparer or an `int` key (`type.TypeHandle`/ `RuntimeTypeHandle.Value`), drop the `component == null`
  check for entries `GetComponents` just returned (a destroyed component cannot be in a list filled this call), and test `Renderer`/`Canvas`
  with one `GetType()` switch instead of four `is` casts. Estimated saving 1-1.5 us per node on HONOR (7-10 %). Needs a run to confirm.
- Pre-size the snapshot (`new ManagedViewSnapshot(ManagedViewBudget.DefaultMaxNodes + 2)`) and the `_seen` set: removes the two array
  doublings (and their garbage) from the walk. Estimated 0.3-1 us per node, mostly on HONOR.
- Call `sliceExpired` every 4-8 nodes instead of every node (0.4-0.7 us per node, 2.5-4 %), as the check is a delegate plus a clock read; the slice
  overshoots by at most a few microseconds.

## Sliced mode (production slices), warm captures, medians

| device | N | tree nodes read | slices | tree us per node (stage sum) | scene us per unit | sceneObjects |
|---|---|---|---|---|---|---|
| HONOR | 1000 | 571 | 16 | 20.6 | 6.7 | 0 (time budget) |
| HONOR | 5000 | 679 | 7 | 16.9 | 5.9 | 0 (time budget) |
| iPhone XS | 1000 | 1103 | 18 | 8.6 | n/a (scene not reached) | 0 (time budget) |
| iPhone XS | 5000 | 1498 | 18 | 4.0 | n/a | 0 (time budget) |

Sliced walks are slower per node than unsliced ones (HONOR 17-21 vs 15-16 us; iPhone N=1000 8.6 vs 4.9): every slice starts with cold caches and
the per-frame work (secure rectangles, `Screen` reads) is paid per slice.
