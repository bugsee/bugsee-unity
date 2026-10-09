# Unity managed exception signatures

Design for how the new Unity SDK computes, forwards, and uses **one** merge signature per managed error/crash.  
Status: **approved for implementation** (not yet implemented). Appserver merge remains `signatures: { $in: [...] }` per **application** (Android and iOS apps are separate).

## Scope (C′)

| In | Out |
|----|-----|
| Reports that carry a `UnityManagedException` payload (`LogException`, `LogUnhandledException`, ExceptionPipeline) | Pure native: `TestCrash`, NDK, ANR, OS kills — native SDK / native worker signatures unchanged |

## Problem

Today:

- Unity already hashes type + **raw** stack strings + `Application.buildGUID` (SHA-256) and sends it as JSON `signature` and Android `$$signature`.
- For `issue_type == error`, worker **replaces** issue signatures with SHA-1(`app_version` + processed frame strings), so Unity’s value is not the merge key.
- Raw strings and post-LNM file:line are not device-stable (IL offsets, absolute paths, call-site lines). Method-name-only hashes over-merge two throw sites in one method.
- Extra signatures on an issue are merge keys. The collection must stay **strongly bounded**; unbounded or noisy aliases stall `$in` merge.

## Rules

1. **Exactly one signature per event.** Worker always `replace_signatures` with a one-element array. Never append. Never keep a native wrapper hash beside the Unity key.
2. **Unity primary only when strong** (recipe 1 or 2 below). Weak Unity SHA-256 of raw stacks is discarded.
3. **Hash must not depend on worker symbolication.** LNM/MethodMap may improve display; they must not change the key.
4. **Handled vs unhandled** use the same recipe. Do not mix `"handled"` into the hash. Issue type stays a separate field.
5. **No cross-backend aliasing in v1** (Mono vs IL2CPP, Dev vs Release). Same store binary (same module UUID) should take the same strong recipe on every device of that platform.
6. **Later AI merge** may join those issues using weak markers (frames, type). It must **not** `$each` extra signatures onto the survivor. Survivor keeps a size-1 set (or a single chosen replacement key).

## Quality gate (client, user frames only)

Drop engine tails (`UnityEngine.`, `System.`, `Bugsee.`, JNI/`UnityPlayer`). Then pick **one** recipe, in order:

1. **Strong / addresses** — ≥1 user frame has a **module-relative** native offset (RVA) + module UUID. Convert `il2cpp_native_stack_trace` absolute PCs using image base; never hash raw `IntPtr`s (ASLR).
2. **Strong / file:line** — no usable RVAs, but ≥1 user frame has a file **name** (not a machine path) and line `> 0`.
3. **Weak** — method tokens only.

Empty user stack → weak, `type` only (optional app-version fence). Thrown-vs-allocated: no throw → no native IPs / empty `StackTrace` → weak.

Inner/`cause`: hash the **leaf payload already selected** (single-inner unwrap). Do not fold the cause chain into the hash.

## Hash inputs

| Recipe | Input |
|--------|--------|
| Addresses | `type` + `moduleUUID` + ordered RVAs of user frames that have offsets (cap ~8, throw-site first) |
| File:line | `type` + ordered `(filename, line)` for those user frames (cap ~8). No directory, no IL `[0x…]` |
| Weak fallback | `type` + ordered canonical user method tokens (`Type.Method` only). Same algorithm on client **or** worker, not both. Optional app **version** fence only — never `buildGUID`, never exception message, never raw stack strings |

## Propagation

- Client writes JSON `signature` when it computed the key (strong, or client-side weak).
- Native Android/iOS: use **that one** value as the request signature. Do **not** `ExceptionSignature.generate` on the wrapper throwable.
- If the client omitted a weak key: send no signature list; appserver must not synthesize a Java/ObjC wrapper hash. Worker fills the canonical weak key once.
- Worker: client key present → replace with that key; else compute the **same** weak recipe.

## Test matrix

| Case | Expect |
|------|--------|
| Two identical IL2CPP throws, same RVA recipe | One issue, `signatures.length == 1`, `total_count == 2` |
| Two throws in one method, name-only (weak) | One issue (over-merge accepted) |
| Two throws in one method, distinct RVAs or file:line | Two issues |
| Dev file:line vs Release RVA, same source site | Two issues (AI later, not signatures) |
| `LogException` vs `LogUnhandledException`, same throw site | Same signature; type may differ |
| Empty user stack | Type-only weak key, size 1 |
| Worker reprocess after LNM | Signature **unchanged** |
| `TestCrash` / NDK | Unchanged native signatures |
| Same throw on Android and iOS | Two issues (issues are per application/platform) |

## Decision log

| Decision | Choice | Alternatives | Why |
|----------|--------|--------------|-----|
| Scope | C′: Unity payload only | Full C including TestCrash / any process crash | Native dumps have no trustworthy Unity identity |
| Signature set | Exactly one per event | Dual Unity+worker; keep secondaries for compat | Extra keys are merge keys; unbounded `$in` stalls merge |
| Weak Unity raw SHA-256 | Discard | Use as sole key | Not stable across devices; tightening tokens over-merges throw sites |
| Weak path | One canonical fallback (user method tokens) | Union with Unity hash | Replace, don’t append |
| Strong path | RVA (preferred) then file:line | Absolute PCs; raw strings | ASLR; path/IL-offset splits |
| Cross-backend v1 | No alias | Dual signatures to glue Mono/IL2CPP | Different recipes; AI merge later without growing `signatures` |
| AI merge (future) | Join issues; survivor keeps size-1 keys | `$each` both signature lists | Weak markers ≠ merge keys |
| Handled bit | Not in hash | Native-style `"handled"` prefix | Same throw site must not split error vs crash |
| Create-time | Native uses Unity key only | Android SHA-1 of wrapper + worker replace | Avoided AU-21 dual hashes |
