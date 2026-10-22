# Unity managed exception signatures

Design for how the new Unity SDK computes, forwards, and uses **one** merge signature per managed error/crash.  
Status: **approved for implementation**; the #107 part (Android: the wrapper's Java stack no longer feeds the SDK signature) is implemented, see *What the wrapper sends today*. The client recipe (rules 2 and 3, the client hash) is implemented in `Runtime/WrapperPolicy/ManagedSignatureRecipe.cs` (#217); the SDK and worker parts are not implemented. Appserver merge remains `signatures: { $in: [...] }` per **application** (Android and iOS apps are separate).

## Scope (C′)

| In | Out |
|----|-----|
| Reports that carry a `UnityManagedException` payload (`LogException`, `LogUnhandledException`, ExceptionPipeline) | Pure native: `TestCrash`, NDK, ANR, OS kills — native SDK / native worker signatures unchanged |

## Problem

Today:

- Before #217 the client hashed type + **raw** stack strings + `Application.buildGUID` (SHA-256) and sent it as the JSON `signature` in the reason; it now sends the recipe below (see *Client recipe as implemented*). (Android used to also send it as a `$$signature` option; nothing reads that, and it is no longer sent.)
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
| Addresses | `type` + `moduleUUID` + ordered RVAs of user frames that have offsets (cap ~8, throw-site first). Payloads built from Unity's log text (`FromUnityLog`) carry no addresses, so they cannot qualify for this recipe. |
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
| Create-time | Native uses Unity key only (target; not true today, see below) | Android SHA-1 of wrapper + worker replace | Avoided AU-21 dual hashes |

## Client recipe as implemented (#217)

`ManagedSignatureRecipe.Compute` returns SHA-256 hex of `A|type|uuid|tokens`, `F|type|tokens` or `W|type|tokens`.

- **Type:** normalised to the short name with no generic arguments or assembly versions (`System.NullReferenceException` and Unity's log text `NullReferenceException` hash alike).
- **User frames:** the first 8 frames (throw site first) that are not `UnityEngine.`, `UnityEditor.`, `System.`, `Mono.`, `Microsoft.`, `Bugsee.` (the sample's `Bugsee.Sample.` counts as user code), `Unity.Collections.`, `Unity.Jobs.`, `Unity.Burst.`, `(wrapper ...)`, Java/Android frames, and not separator lines (`--- End of stack trace ...`, Unity's `Rethrow as X: ...`). Other `Unity.*` packages are user frames. The cap is 8 user frames in total, not per recipe.
- **Per-frame token:** the module-relative address (`r:`) when the frame has one, else `file:line` (`f:`, file name only, line above 0), else the method token (`m:`, `Type.Method`, no arguments).
- **Which recipe:** `A` (addresses) when the throw site (first user frame) has an address and the image UUID is known, tokens as above; else `F` when any user frame has a file and line (frames without one use their method token); else `W` (method tokens only, the type alone for an empty user stack). Requiring the throw site's address keeps IL2CPP frames that carry a line (and so get no address) from being skipped, which would merge different throw sites.
- **Mono caller tail:** `FromException` captures the live stack once per reported exception and `TrimCallerTail` drops the frames the trace shares with it, keeping the catch frame. Matching is by `Type.Method` only, so same-named overloads match each other (a few extra frames may be cut). Nothing is cut when the whole trace is a tail of the live stack and the live stack is longer (an exception filter or `UnhandledException` runs before the unwind, the throw frames are still live), and matching stops at a Bugsee frame. An exception stored and logged from elsewhere has no shared tail and keeps every frame.
- **Unity log path (`FromUnityLog`):** no live stack and no addresses, so no trim and only the `F` and `W` recipes. Unity's log stack text of a caught exception carries the caller chain too, so a throw site logged through this path can differ from the same throw reported through `LogException`. This path runs only when no exception object is available.
- **Worker:** the worker cannot recompute this key: the trim needs the live stack and the engine filter is client-side. The client key is sent as is; a worker that wants to replace it needs its own recipe over the frames it receives.
- **Deviations from the design above:** the weak recipe is computed on the client, the optional version fence is not used, and the engine list and per-frame tokens are as listed here.

## What the wrapper sends today (Android, issue #107)

Audited against Android SDK 7.4.2 and the worker:

- The SDK ignores every key of the exception options map except `domain` and `skipFrames`, which only feed the client signature (`skipFrames` trimmed the Java stack of the throwable; with the wrapper's throwable now stackless it has no effect on Android). `domain` is also written to the report's `crash.json` exception. The wrapper no longer sends `$$signature` or `moduleUUID` as options. The SDK reads only `signature` from the reason JSON (for its client signature; the worker writes it to `additional_signature`, which nothing reads); the worker reads only `moduleUUID` (and `il2cppLinemapId`); `buildID` is read by nothing.
- The client signature the SDK files a report under is SHA-1 over: `handled` (only for `logException`), the domain, the throwable class name, the JSON `signature` from the reason, then the Java stack of the throwable. The Java stack used to be the one of the wrapper's `UnityManagedException`, built where the wrapper calls the SDK, so it varied with the calling thread. `UnityManagedException` now captures no Java stack, so the SDK's signature no longer depends on the wrapper's Java call site. The payload `signature` it hashes in used to vary per thread on Mono; the #217 recipe above removes that. Checked on a device by comparing the SDK's own signature function for the same payload handed over from the main thread and from a worker (sample scenario `sigthreads`).
- Still different from this design, and not fixable in the wrapper: the SDK mixes `handled` into the hash (`LogException` and `LogUnhandledException` of one throw site get different client signatures, rule 4), and for issue type `error` the worker replaces the signatures while for `crash` it appends its own (rule 1). Those need the worker (and for `handled`, the SDK) to follow this design; the client key is only the create-time key.
- **iOS differs:** bugsee-cocoa builds a `logException` signature from a live report (signal name and the calling thread's native frames), not from the domain or the reason-JSON `signature`, so the "Create-time: native uses Unity key only" row does not hold for iOS today. This needs a bugsee-cocoa/worker change; the wrapper cannot supply the key there. Not re-verified at 7.0.0-beta7 in this change.
