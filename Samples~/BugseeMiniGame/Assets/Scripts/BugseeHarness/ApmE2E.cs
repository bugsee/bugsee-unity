using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bugsee.Contracts.Apm;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// APM e2e (scenarios "apm" and "apmoff"): drives transactions and spans from the main thread and from thread-pool
    /// threads, with odd inputs, and logs every id with the "[ApmE2E]" tag so a run can be matched to what the backend
    /// received. "apmoff" runs the same calls with performance monitoring off and then a long create/finish loop that would
    /// overflow the JNI global reference table if a span leaked its reference.
    /// </summary>
    static class ApmE2E
    {
        static void Log(string message) => Debug.Log("[ApmE2E] " + message);

        static string Describe(ISpan span) =>
            "span=" + span.SpanId + " trace=" + span.TraceId + " op=" + span.Operation + " finished=" + span.IsFinished;

        static T Try<T>(string what, Func<T> call)
        {
            try
            {
                return call();
            }
            catch (Exception e)
            {
                Log("THREW " + what + ": " + e.GetType().Name + ": " + e.Message);
                return default(T);
            }
        }

        public static void Run(string runId, bool monitoringOff, bool leak = false, bool pool = false)
        {
            Log("begin run=" + runId + " monitoringOff=" + monitoringOff + " thread=" + Thread.CurrentThread.ManagedThreadId);
            if (pool)
            {
                PoolStress(runId);
                Log("end run=" + runId);
                return;
            }
            if (leak)
            {
                Loop(runId, 54000);
                Log("end run=" + runId);
                return;
            }
            MainThread(runId);
            PoolThreads(runId);
            OddInputs(runId);
            Loop(runId, monitoringOff ? 54000 : 1500);
            Log("end run=" + runId);
        }

        static void MainThread(string runId)
        {
            var txn = Bugsee.StartTransaction("apm-main-" + runId, "e2e.main", new Dictionary<string, object>
            {
                ["t_double"] = 1.0, ["t_int"] = 7, ["t_string"] = "x", ["t_bool"] = true
            });
            Log("main txn " + Describe(txn));
            var db = txn.StartChild("db.query", "select 1");
            Log("main child " + Describe(db));
            db.SetAttribute("c_double", 2.5).SetAttribute("c_int", 42L).SetAttribute("c_string", "abc").SetAttribute("c_bool", false);
            db.SetName("db-renamed").SetDescription("select 2");
            var inner = db.StartChild("db.row");
            Log("main grandchild " + Describe(inner));
            inner.Finish(SpanStatus.Timeout);
            db.SetStatus(SpanStatus.Error);
            db.Finish(SpanStatus.DeadlineExceeded);
            var open = txn.StartChild("never.finished", "cancelled by parent");
            Log("main open child " + Describe(open));
            txn.SetStatus(SpanStatus.Ok);
            txn.Finish();
            Log("main after finish txn=" + txn.IsFinished + " open=" + open.IsFinished);
        }

        static void PoolThreads(string runId)
        {
            // Everything on one pool thread: create, child, attributes, finish.
            Task.Run(() =>
            {
                Log("pool-all thread=" + Thread.CurrentThread.ManagedThreadId + " isPool=" + Thread.CurrentThread.IsThreadPoolThread);
                var txn = Try("pool StartTransaction", () => Bugsee.StartTransaction("apm-pool-" + runId, "e2e.pool"));
                if (txn == null) return;
                Log("pool txn " + Describe(txn));
                var c = txn.StartChild("pool.child", "from pool");
                c.SetAttribute("p_double", 3.25).SetAttribute("p_int", 5);
                c.Finish();
                txn.Finish(SpanStatus.Ok);
                Log("pool done txn=" + txn.IsFinished + " child=" + c.IsFinished);
            }).Wait();

            // Created on main, children and finish on pool threads, concurrently with the parent finishing.
            var shared = Bugsee.StartTransaction("apm-cross-" + runId, "e2e.cross");
            Log("cross txn " + Describe(shared));
            var kids = new ISpan[20];
            for (int i = 0; i < kids.Length; i++) kids[i] = shared.StartChild("cross.child." + i);
            var tasks = new Task[kids.Length];
            for (int i = 0; i < kids.Length; i++)
            {
                var k = kids[i];
                tasks[i] = Task.Run(() => { k.SetAttribute("n", 1); k.Finish(SpanStatus.Error); });
            }
            shared.Finish(SpanStatus.Ok);
            Task.WaitAll(tasks);
            int finished = 0;
            foreach (var k in kids) if (k.IsFinished) finished++;
            Log("cross done finishedKids=" + finished + "/" + kids.Length + " parent=" + shared.IsFinished);
        }

        static void OddInputs(string runId)
        {
            Log("odd empty name -> " + Describe(Bugsee.StartTransaction("", "op")));
            Log("odd null op -> " + Describe(Bugsee.StartTransaction("n", null)));
            var txn = Bugsee.StartTransaction("apm-odd-" + runId, "e2e.odd", new Dictionary<string, object>
            {
                ["ok"] = "fine", ["bad_obj"] = new object(), ["bad_nan"] = double.NaN, ["bad_big"] = ulong.MaxValue, [""] = "empty-key"
            });
            Log("odd txn " + Describe(txn));
            txn.SetAttribute("long", new string('x', 2000)).SetAttribute(null, 1).SetAttribute("k", null);
            txn.SetName(null).SetName("");
            txn.SetDescription(null);
            txn.SetStatus((SpanStatus)99);
            Log("odd empty child -> " + Describe(txn.StartChild("")));
            txn.Finish((SpanStatus)99);
            txn.Finish();
            txn.Dispose();
            Log("odd after finish child -> " + Describe(txn.StartChild("late")) + " txnFinished=" + txn.IsFinished);

            ISpan disposed;
            using (disposed = Bugsee.StartTransaction("apm-dispose-" + runId, "e2e.dispose"))
            {
                disposed.SetAttribute("in_using", true);
                disposed.StartChild("dispose.child");
            }
            Log("odd dispose without finish finished=" + disposed.IsFinished);
        }

        static void Loop(string runId, int count)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var txn = Bugsee.StartTransaction("apm-loop-" + runId, "e2e.loop");
            for (int i = 0; i < count; i++)
            {
                var c = txn.StartChild("loop.child");
                c.SetAttribute("i", i);
                c.Finish();
            }
            txn.Finish();
            for (int i = 0; i < count / 2; i++)
                Bugsee.StartTransaction("apm-loop-txn", "e2e.loop").Dispose();
            Log("loop done count=" + count + " ms=" + sw.ElapsedMilliseconds);
        }

        // Thousands of spans created and finished on thread-pool threads, plus spans dropped without finishing (their
        // native references must be released by the finalizer, not leak).
        static void PoolStress(string runId)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int Workers = 8, PerWorker = 3000;
            var tasks = new Task[Workers];
            for (int w = 0; w < Workers; w++)
            {
                tasks[w] = Task.Run(() =>
                {
                    for (int i = 0; i < PerWorker; i++)
                    {
                        var txn = Bugsee.StartTransaction("apm-pool-stress", "e2e.stress");
                        var c = txn.StartChild("stress.child");
                        c.SetAttribute("i", i);
                        if (i % 3 == 0) continue;           // dropped unfinished
                        c.Finish();
                        txn.Finish();
                    }
                });
            }
            Task.WaitAll(tasks);
            Log("pool stress done ms=" + sw.ElapsedMilliseconds + " spans=" + (Workers * PerWorker * 2));
            for (int g = 0; g < 3; g++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            Log("gc done");
        }
    }
}
