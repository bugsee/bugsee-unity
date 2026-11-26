# Optimisation #4 / #10 evaluation: bulk subtree enumeration (HONOR WOD-LX1, Android, IL2CPP release, Unity 2021.3.45f1)

Benchmark: `EnumBench.cs` (scenario `enumbench`), plain GameObjects, 10 children per node, every 9th second-level group inactive
(about 10% of nodes inactive). Medians of 8 warm passes; two launches agree. Times are microseconds per returned (active) node.

| nodes | roots | walk (childCount+GetChild+gameObject+activeInHierarchy) | bulk GetComponentsInChildren<Transform>(false,list) | parent per node | bulk + parent + id lookup (tree rebuild) | FindObjectsByType<Transform>(Exclude, None) | + parent |
|---|---|---|---|---|---|---|---|
| 1000 | 1 | 2.88 | 1.24 | 0.54 | 1.95 | 4.79 | 6.25 |
| 1000 | 8 | 2.08 | 1.10 | 0.68 | 1.68 | 5.21 | 5.55 |
| 5000 | 1 | 1.80 | 0.96 | 0.61 | 1.77 | 2.31 | 3.75 |
| 5000 | 8 | 1.91 | 0.95 | 0.69 | 2.28 | 2.60 | 3.86 |
| 20000 | 1 | 1.72 | 0.90 | 0.57 | 1.79 | 1.65 | 2.33 |
| 20000 | 8 | 1.68 | 0.91 | 0.56 | 1.74 | 1.51 | 2.35 |

Worst single call (the unit the 5% slice cannot split; 5% of a 60 Hz frame is 0.83 ms):

| call | nodes returned | max single call |
|---|---|---|
| bulk, one root | 900 | 5.0 ms (first call; median pass 1.1 ms) |
| bulk, one root | 4889 | 5.5 ms |
| bulk, one root | 18889 | 20.2 ms |
| bulk, 8 roots (largest root) | 2389 | 2.7 ms |
| FindObjectsByType (whole process, 20618 returned) | 20618 | 32 to 37 ms |

Order (logged, not relied on): in every configuration the bulk order equals a depth-first pre-order that follows GetChild order,
parent before child, siblings in GetChild order (all three booleans true at n=1000/5000/20000, 1 and 8 roots).

## Reading

- The bulk call alone is about 0.9 to 1.2 us per node against the bare walk's 1.7 to 2.9. But the tree needs a parent for every node,
  and `parent` costs 0.55 to 0.7 us per node; with the hash lookup the rebuild costs 1.7 to 2.3 us, the same as the walk (1.7 to 2.9). No
  gain unless the code relies on the undocumented pre-order (then 0.9 to 1.2 against 1.7 to 2.9, saving roughly 1 us of a 17 to 25 us node).
- The walk is not only enumeration: each node still needs `.gameObject` (about 1 us) for name and components, which the bulk list does
  not remove. The real saving is therefore at most a few percent of the node cost, below the launch-to-launch noise of the vhscale numbers.
- A bulk call cannot be sliced: 20 ms for one 19000-node root is 24 times the 0.83 ms slice budget; even 900 nodes is above it on a cold call.
  Splitting by root only helps if the scene has many roots.
- `FindObjectsByType<Transform>(None)` returns every Transform in the process (the app's own and the SDK's objects too), costs 1.5 to 5.2 us
  per node and up to 37 ms in one call, and is 2021.3.18+ only.

## Recommendation

Reject #4 and #10 as a replacement of the walk: no measurable per-node win once the tree is rebuilt, and it breaks the sliced
main-thread budget (single calls of 5 to 37 ms against 0.83 ms). If a future need appears for scenes with very many small roots, a
per-root bulk call below a `hierarchyCount` threshold with the walk as a fallback is the only shape that keeps slicing, and the saving
would still be about 1 us per node.
