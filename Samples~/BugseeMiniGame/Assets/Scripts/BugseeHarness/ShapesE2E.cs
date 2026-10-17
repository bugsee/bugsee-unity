using System;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using Bugsee.Internal;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Scenario "shapes": throws exceptions of the shapes that change how managed frames pair with native
    /// instruction pointers (sync, rethrow, ExceptionDispatchInfo, await, coroutine, delegate, cause chain, aggregate,
    /// throw in the first part of an async method) and logs, per shape, the stack text, the native IPs IL2CPP kept
    /// and the payload the SDK builds, tagged "[SHAPE]". The addresses can then be resolved with atos / addr2line
    /// and compared with the text. Every exception is also reported through Debug.LogException (the real path).
    /// </summary>
    public static class ShapesE2E
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Throw(string tag) { throw new InvalidOperationException("shape " + tag); }

        [MethodImpl(MethodImplOptions.NoInlining)] static void SyncOuter() { SyncMid(); }
        [MethodImpl(MethodImplOptions.NoInlining)] static void SyncMid() { Throw("1"); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void RethrowBare() { try { SyncMid(); } catch (Exception) { throw; } }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void RethrowEx() { try { SyncMid(); } catch (Exception ex) { throw ex; } }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void DispatchInfoThrow(ExceptionDispatchInfo info) { info.Throw(); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static async Task AwaitOwnThrow() { await Task.Yield(); Throw("4b"); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static async Task ThrowInFirstPart() { Throw("6"); await Task.Yield(); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static IEnumerator Coroutine()
        {
            yield return null;
            try { Throw("4c"); } catch (Exception ex) { Report("4c-coroutine", ex); }
        }

        public static void Run(MonoBehaviour host)
        {
            RunAsync(host);
        }

        static async void RunAsync(MonoBehaviour host)
        {
            try
            {
                try { SyncOuter(); } catch (Exception ex) { Report("1-sync-3deep", ex); }

                try { RethrowBare(); } catch (Exception ex) { Report("2a-rethrow-bare", ex); }
                try { RethrowEx(); } catch (Exception ex) { Report("2b-rethrow-ex", ex); }

                ExceptionDispatchInfo info = null;
                try { SyncMid(); } catch (Exception ex) { info = ExceptionDispatchInfo.Capture(ex); }
                try { DispatchInfoThrow(info); } catch (Exception ex) { Report("3-dispatch-info", ex); }

                try { await Task.Run(() => Throw("4a")); } catch (Exception ex) { Report("4a-await-task-run", ex); }
                try { await AwaitOwnThrow(); } catch (Exception ex) { Report("4b-await-own-throw", ex); }

                host.StartCoroutine(Coroutine());
                Action lambda = () => SyncMid();
                try { lambda(); } catch (Exception ex) { Report("4d-lambda-delegate", ex); }

                try
                {
                    try { Throw("5a"); }
                    catch (Exception inner) { throw new ApplicationException("outer 5a", inner); }
                }
                catch (Exception ex) { Report("5a-inner-exception", ex); }

                try { Task.Run(() => Throw("5b")).Wait(); } catch (Exception ex) { Report("5b-aggregate-single", ex); }
                try
                {
                    Task.WhenAll(Task.Run(() => Throw("5c-1")), Task.Run(() => Throw("5c-2"))).Wait();
                }
                catch (Exception ex) { Report("5c-aggregate-two", ex); }

                // 20 inner exceptions: the payload carries at most 8 nodes.
                var many = new System.Collections.Generic.List<Exception>();
                for (var i = 0; i < 20; i++)
                {
                    try { Throw("5d-" + i); } catch (Exception inner) { many.Add(inner); }
                }
                try { throw new AggregateException("many", many); } catch (Exception ex) { Report("5d-aggregate-20", ex); }

                var first = ThrowInFirstPart();
                if (first.Exception != null) Report("6a-first-part-task-exception", first.Exception.InnerException);
                try { await first; } catch (Exception ex) { Report("6b-first-part-awaited", ex); }
            }
            catch (Exception ex)
            {
                Debug.Log("[SHAPE] runner failed: " + ex);
            }
            Debug.Log("[SHAPE] done");
        }

        static void Report(string name, Exception ex)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("[SHAPE] ").Append(name).Append(" type=").Append(ex.GetType().Name);
                Debug.Log(sb);
                // One short line per item: logcat cuts long messages.
                var text = (ex.StackTrace ?? "").Replace("\r", "").Split('\n');
                for (var i = 0; i < text.Length; i++) Debug.Log("[SHAPE] " + name + " text" + i + "=" + text[i].Trim());
                var native = Il2CppNativeStack.TryCapture(ex);
                var ips = new StringBuilder();
                if (native != null)
                    foreach (var ip in native.Frames) ips.Append("0x").Append(ip.ToInt64().ToString("x")).Append(',');
                Debug.Log("[SHAPE] " + name + " native=" + (native == null ? 0 : native.Frames.Length) + " ips=" + ips
                    + " uuid=" + (native == null ? "-" : native.ImageUuid));
                var payload = ManagedExceptionPayload.FromException(ex);
                var json = ManagedExceptionPayload.ToJson(payload);
                Debug.Log("[SHAPE] " + name + " json cause=" + json.Contains("\"cause\":{") + " bytes=" + json.Length);
                // The whole JSON in chunks (logcat cuts long lines), to be parsed offline.
                for (var c = 0; c * 700 < json.Length; c++)
                    Debug.Log("[SHAPE] " + name + " jsonchunk" + c + "=" + json.Substring(c * 700, Math.Min(700, json.Length - c * 700)));
                var level = 0;
                for (var p = payload; p != null; p = p.cause, level++)
                {
                    Debug.Log("[SHAPE] " + name + " level" + level + " " + p.name + " module=" + p.moduleUUID + " frames=" + p.frames.Length);
                    for (var i = 0; i < p.frames.Length; i++)
                    {
                        var t = p.frames[i].trace ?? "";
                        var cut = t.IndexOf(" [0x", StringComparison.Ordinal);
                        Debug.Log("[SHAPE] " + name + " level" + level + "frame" + i + "=" + (p.frames[i].address ?? "-") + " " + (cut > 0 ? t.Substring(0, cut) : t));
                    }
                }
                Debug.LogException(ex);
            }
            catch (Exception e)
            {
                Debug.Log("[SHAPE] report failed " + name + ": " + e);
            }
        }
    }
}
