using System;
using System.Collections.Generic;
using Bugsee.Contracts.Lifecycle;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// What <see cref="AndroidLifecycleMachine"/> needs from the native SDK. Every callback (done) must be delivered
    /// on the thread that drives the machine (the Unity main thread); a synchronous refusal may deliver it inline,
    /// from inside the call.
    /// </summary>
    internal interface IAndroidLifecycleNative
    {
        BugseeStatus Status();
        void Launch(string appToken, IDictionary<string, object> options, Action<bool> done);
        void Relaunch(IDictionary<string, object> options, Action<bool> done);
        /// <summary>stop(Runnable): starts a stop, joins one in progress, or runs done at once when Stopped.</summary>
        void Stop(Action done);
        void DeleteCollectedData(Action<bool> done);
        void InstallHooks(IDictionary<string, object> options);
        void UninstallHooks();
        /// <summary>Call <see cref="AndroidLifecycleMachine.Tick"/> (on the machine's thread) after this delay.</summary>
        void ScheduleTick(TimeSpan delay);
        void Warn(string message);
        void Error(Exception exception);
    }

    /// <summary>
    /// One state machine for the Android SDK lifecycle: Launch, Relaunch, Stop and DeleteCollectedDataOnDevice, all
    /// driven from one thread.
    ///
    /// Rules (modelled on the Android 7.4.2 SDK, which calls back exactly once from every launch(), relaunch() and
    /// stop(): a refused relaunch calls back false, a relaunch cancelled by stop() calls back false):
    /// - Launch, Relaunch and Delete run one at a time, in request order. One that arrives while a Stop, another of
    ///   them, or an SDK stop the wrapper did not start (status Stopping) is in flight is queued and started once
    ///   that settles - nothing blocks the caller. The latest intent wins: a queued Launch replaces queued
    ///   Launch/Relaunch requests after the last queued Delete, a queued Relaunch folds its options into the queued
    ///   Launch/Relaunch before it, and Stop drops every queued Launch/Relaunch. A Delete is never dropped or skipped
    ///   by a later Launch: it runs first, so it can only wipe data collected before the Launch.
    /// - A Relaunch requested while a Relaunch is in flight (and no Stop came after it) JOINS it, as the SDK does:
    ///   it gets that relaunch's outcome and its own options are IGNORED (a warning says so). The SDK answers a joiner
    ///   true once the relaunch launched, also when the joiner's options were dropped.
    /// - Stop is never queued: it starts (or joins) a native stop at once, and bumps the stop epoch. A Stop that
    ///   arrives while a relaunch is stopping, or between its stop and its launch, CANCELS the relaunch in the SDK:
    ///   the SDK stays stopped and the relaunch calls back false; a Stop while the relaunch is launching is a plain
    ///   stop (the launch then calls back false). A Launch or Relaunch whose result belongs to an older stop epoch is
    ///   stale: it never installs the managed hooks. The Stop's completion waits for that result (it follows the
    ///   stop at once), so a Stop always ends with the SDK stopped and nothing else in flight.
    /// - The managed hooks are installed before launch() (a refusal is reported synchronously, from inside it) and,
    ///   for a current result, reconciled with the SDK's status: running installs them, a refusal removes them.
    /// - Two-stage deadline, as a guard against a native call that never answers (a slow stop, a stalled capture
    ///   start) - the SDK itself always calls back:
    ///   - Soft, <see cref="OperationTimeout"/> after an operation started, ONLY answers its callers, with a warning,
    ///     so nobody hangs: Stop completions run, Launch/Relaunch completions get true when the SDK is already running
    ///     (Launching/Launched: it accepted the launch, only the callback is missing) and false otherwise, Delete
    ///     completions get false. It releases nothing
    ///     else - the operation stays in flight, the queue stays blocked behind it, and a Launch/Relaunch requested
    ///     meanwhile (also from inside such a Stop completion) stays deferred until the SDK actually reports (the
    ///     stop finished, the relaunch called back) or the hard deadline passes. The native operation
    ///     may still finish after its caller got false (a Delete then still deletes); its result is applied as usual.
    ///   - Hard, <see cref="HardTimeout"/>: the operation is reset - forgotten, its stop gate released - and the
    ///     queue moves on. A callback after that is ignored (by token). Residual race: a result after the hard reset
    ///     is ignored, so the managed hooks can be out of step with that session until the next lifecycle call; and a
    ///     Delete started after the hard reset of a relaunch can overlap a continuation that launch()es later still
    ///     (the Delete re-checks the status after its stop, but not during the native delete).
    /// </summary>
    internal sealed class AndroidLifecycleMachine
    {
        public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(60);

        enum Kind { Launch, Relaunch, Delete }

        sealed class Op
        {
            public Kind Kind;
            public string AppToken;
            public IDictionary<string, object> Options;
            public IDictionary<string, object> PreviousHookOptions;
            public readonly List<Action<bool>> Completions = new List<Action<bool>>();
            public int Token;
            public int StopEpoch;
            public TimeSpan Deadline;
            public TimeSpan HardDeadline;
        }

        // A Stop caller. Result (StopAsync): true when the stop really finished, false when the deadline answered
        // first. The deadline counts from the call, also for a Stop that joins a stop already in flight.
        sealed class StopWaiter
        {
            public Action Completion;
            public Action<bool> Result;
            public TimeSpan Deadline;
        }

        sealed class StopOp
        {
            // Callers' Stop waiters (each answered at its own soft deadline at the latest) and the machine's own
            // continuations (run only when the stop really finished, or at the hard reset).
            public readonly List<StopWaiter> Waiters = new List<StopWaiter>();
            public readonly List<Action> Continuations = new List<Action>();
            public int Token;
            public bool NativeDone;
            public TimeSpan Deadline;
            public TimeSpan HardDeadline;
        }

        readonly IAndroidLifecycleNative _native;
        readonly Func<TimeSpan> _now;
        readonly LinkedList<Op> _queue = new LinkedList<Op>();
        Op _active;
        StopOp _stop;
        int _nextToken;
        int _stopEpoch;
        bool _skipStoppingJoin;
        IDictionary<string, object> _hookOptions;

        public AndroidLifecycleMachine(IAndroidLifecycleNative native, Func<TimeSpan> now)
        {
            _native = native;
            _now = now;
        }

        public void Launch(string appToken, IDictionary<string, object> options, Action<bool> completion = null)
        {
            var op = new Op { Kind = Kind.Launch, AppToken = appToken, Options = options };
            Add(op.Completions, completion);
            // A queued Launch replaces the Launch/Relaunch requests queued after the last Delete.
            while (_queue.Last != null && _queue.Last.Value.Kind != Kind.Delete)
            {
                Complete(_queue.Last.Value.Completions, false);
                _queue.RemoveLast();
            }
            _queue.AddLast(op);
            Pump();
        }

        public void Relaunch(IDictionary<string, object> options, Action<bool> completion = null)
        {
            var tail = _queue.Last?.Value;
            if (tail != null && tail.Kind != Kind.Delete)
            {
                // Launch(a) then Relaunch(b), both waiting: launch with b.
                tail.Options = options;
                Add(tail.Completions, completion);
                return;
            }

            // The SDK joins a relaunch in flight: the joiner gets that relaunch's outcome and its options are ignored.
            // Not after a Stop (the SDK cancelled that relaunch, nothing can join it) and not in front of a queued
            // Launch/Relaunch (handled above: those run after the active operation and take these options).
            if (_active != null && _active.Kind == Kind.Relaunch && _active.StopEpoch == _stopEpoch)
            {
                if (options != null && options.Count > 0)
                    _native.Warn("Bugsee.Relaunch: a relaunch is already in progress, so these options are IGNORED; " +
                                 "the options of the call that started it apply.");
                Add(_active.Completions, completion);
                // Joined after the soft deadline already answered the callers: answer this one the same way.
                if (completion != null && _now() >= _active.Deadline) Tick();
                return;
            }

            var op = new Op { Kind = Kind.Relaunch, Options = options };
            Add(op.Completions, completion);
            _queue.AddLast(op);
            Pump();
        }

        public void DeleteCollectedData(Action<bool> completion = null)
        {
            var tail = _queue.Last?.Value;
            if (tail != null && tail.Kind == Kind.Delete)
            {
                Add(tail.Completions, completion);
                return;
            }

            var op = new Op { Kind = Kind.Delete };
            Add(op.Completions, completion);
            _queue.AddLast(op);
            Pump();
        }

        /// <param name="result">Runs once: true when the SDK reported the stop finished (also when a Launch came
        /// meanwhile, unlike <paramref name="completion"/> in other bridges), false when the soft deadline answered
        /// first. Never dropped.</param>
        public void Stop(Action completion = null, Action<bool> result = null)
        {
            _stopEpoch++;
            var dropped = new List<Op>();
            for (var node = _queue.First; node != null;)
            {
                var next = node.Next;
                if (node.Value.Kind != Kind.Delete)
                {
                    dropped.Add(node.Value);
                    _queue.Remove(node);
                }
                node = next;
            }

            _native.UninstallHooks();
            StartStop(completion, result: result);
            foreach (var op in dropped) Complete(op.Completions, false);
        }

        /// <summary>Applies every deadline that has passed. Safe to call at any time.</summary>
        public void Tick()
        {
            var now = _now();

            // Soft: answer the callers only. Hard: reset. The operation first, so a Delete waiting on a stop that
            // is reset in the same tick does not start another one.
            if (_active != null && now >= _active.Deadline && _active.Completions.Count > 0)
            {
                // A Launch/Relaunch the SDK already started (Launching/Launched) is answered true: it was accepted,
                // only its final callback is missing. A Delete has no such state.
                var accepted = _active.Kind != Kind.Delete && AndroidLaunchStatePolicy.IsRunning(_native.Status());
                _native.Warn("Bugsee." + _active.Kind + " got no result from the SDK within " +
                             OperationTimeout.TotalSeconds + " s; it is reported as " +
                             (accepted ? "started (the SDK is running)" : "failed") + ", but the SDK may still " +
                             "complete it.");
                var answered = _active.Completions.ToArray();
                _active.Completions.Clear();
                foreach (var c in answered) Run(() => c(accepted));
            }

            if (_active != null && now >= _active.HardDeadline)
            {
                _native.Warn("Bugsee." + _active.Kind + " got no result from the SDK within " +
                             HardTimeout.TotalSeconds + " s; the wrapper stops waiting for it.");
                Settle(_active, false);
            }

            if (_stop != null && _stop.Waiters.Exists(w => now >= w.Deadline))
            {
                _native.Warn("Bugsee.Stop did not complete within " + OperationTimeout.TotalSeconds +
                             " s; its completion runs now. The SDK may still be stopping: a Launch requested now " +
                             "waits until it has.");
                var nativeDone = _stop.NativeDone;
                var late = _stop.Waiters.FindAll(w => now >= w.Deadline);
                _stop.Waiters.RemoveAll(w => now >= w.Deadline);
                foreach (var w in late) Answer(w, nativeDone);
            }

            if (_stop != null && now >= _stop.HardDeadline)
            {
                _native.Warn("Bugsee.Stop did not complete within " + HardTimeout.TotalSeconds +
                             " s; the wrapper stops waiting for it.");
                _skipStoppingJoin = true;
                FinishStop(finished: false);
            }

            Pump();
        }

        void Pump()
        {
            while (_active == null && _stop == null && _queue.Count > 0)
            {
                // The SDK refuses launch() and relaunch() while Stopping. A stop the wrapper did not start is joined first, unless a
                // join already timed out (the next operation then runs and reports what the SDK says).
                if (!_skipStoppingJoin && _native.Status() == BugseeStatus.Stopping)
                {
                    StartStop(null);
                    return;
                }

                _skipStoppingJoin = false;
                var op = _queue.First.Value;
                _queue.RemoveFirst();
                op.Token = ++_nextToken;
                op.StopEpoch = _stopEpoch;
                op.Deadline = _now() + OperationTimeout;
                op.HardDeadline = _now() + HardTimeout;
                op.PreviousHookOptions = _hookOptions;
                _active = op;
                _native.ScheduleTick(OperationTimeout);
                _native.ScheduleTick(HardTimeout);
                switch (op.Kind)
                {
                    case Kind.Launch: StartLaunch(op); break;
                    case Kind.Relaunch: StartRelaunch(op); break;
                    default: DeleteStep(op); break;
                }
            }
        }

        void StartLaunch(Op op)
        {
            var before = _native.Status();
            if (!AndroidLaunchStatePolicy.IsRunning(before))
            {
                // Hooks first: a refusal calls back inline, and the reconcile then removes them.
                _hookOptions = op.Options;
                _native.InstallHooks(_hookOptions);
            }
            else
            {
                _native.Warn("Bugsee.Launch: the SDK is already running, so this token and these options are " +
                             "ignored. Call Stop and launch from its completion to change them.");
            }

            var token = op.Token;
            Call(op, () => _native.Launch(op.AppToken, op.Options, ok => OnResult(token, ok)));
        }

        void StartRelaunch(Op op)
        {
            if (!AndroidLaunchStatePolicy.RelaunchAccepted(_native.Status()))
            {
                _native.Warn("Bugsee.Relaunch refused: the SDK only relaunches once it is Launched.");
                Settle(op, false);
                return;
            }

            _hookOptions = op.Options;
            _native.InstallHooks(_hookOptions);
            var token = op.Token;
            Call(op, () => _native.Relaunch(op.Options, ok => OnResult(token, ok)));
            // A relaunch the SDK refused after our status check (it was stopped meanwhile) has already called back
            // false, inline: Settle rolled the hooks back.
        }

        // The SDK must be fully Stopped before the delete, and stays stopped after it. Stop (or join the stop in
        // progress), then decide again from the stop's completion: something else may have launched meanwhile.
        void DeleteStep(Op op)
        {
            if (_active != op) return;
            if (_native.Status() != BugseeStatus.Stopped)
            {
                _stopEpoch++;
                _native.UninstallHooks();
                StartStop(null, continuation: () => DeleteStep(op));
                return;
            }

            var token = op.Token;
            Call(op, () => _native.DeleteCollectedData(ok =>
            {
                if (_active == null || _active.Token != token) return;
                if (!ok) _native.Warn("deleteCollectedDataOnDevice failed.");
                Settle(_active, ok);
                Pump();
            }));
        }

        void Call(Op op, Action nativeCall)
        {
            try
            {
                nativeCall();
            }
            catch (Exception e)
            {
                _native.Error(e);
                if (_active == op)
                {
                    Settle(op, false);
                    Pump();
                }
            }
        }

        void OnResult(int token, bool ok)
        {
            if (_active != null && _active.Token == token)
            {
                Settle(_active, ok);
                Pump();
                return;
            }

            // A result after the operation's hard reset is ignored.
        }

        // Ends the active operation: reconcile the hooks, complete it, then finish a Stop that was waiting on it.
        void Settle(Op op, bool ok)
        {
            if (_active != op) return;
            _active = null;

            if (op.Kind != Kind.Delete)
            {
                var running = AndroidLaunchStatePolicy.IsRunning(_native.Status());
                if (op.StopEpoch != _stopEpoch)
                {
                    // Stale: a Stop came after this started (the SDK cancelled it). Never re-install the hooks.
                }
                else if (running)
                {
                    _native.InstallHooks(_hookOptions);
                }
                else if (!ok)
                {
                    _hookOptions = op.PreviousHookOptions;
                    _native.UninstallHooks();
                    if (op.Kind == Kind.Launch)
                        _native.Warn("The SDK reported that it did not launch (an empty or invalid token, no context, a " +
                                     "launch made before a previous Stop finished, or an exception it rolled back). " +
                                     "Managed capture is switched off.");
                }

            }

            // In request order: this operation's waiters, then those of a Stop requested after it.
            Complete(op.Completions, ok);
            if (op.Kind != Kind.Delete && _stop != null && _stop.NativeDone) FinishStop();
        }

        void StartStop(Action completion, Action continuation = null, Action<bool> result = null)
        {
            if (_stop != null)
            {
                AddWaiter(completion, result);
                Add(_stop.Continuations, continuation);
                return;
            }

            _stop = new StopOp { Deadline = _now() + OperationTimeout, HardDeadline = _now() + HardTimeout };
            _native.ScheduleTick(OperationTimeout);
            _native.ScheduleTick(HardTimeout);

            AddWaiter(completion, result);
            Add(_stop.Continuations, continuation);
            var stop = _stop;
            stop.NativeDone = false;
            var token = stop.Token = ++_nextToken;
            try
            {
                _native.Stop(() => OnStopDone(token));
            }
            catch (Exception e)
            {
                _native.Error(e);
                OnStopDone(token);
            }
        }

        void AddWaiter(Action completion, Action<bool> result)
        {
            if (completion == null && result == null) return;
            // The SDK already reported this stop (FinishStop only waits for a Launch/Relaunch result): StopAsync is
            // answered now, it must not time out on a stop that is over.
            if (_stop.NativeDone && result != null)
            {
                Run(() => result(true));
                result = null;
                if (completion == null) return;
            }
            _stop.Waiters.Add(new StopWaiter { Completion = completion, Result = result, Deadline = _now() + OperationTimeout });
            // A waiter that joins later than the stop's first one has a deadline of its own.
            _native.ScheduleTick(OperationTimeout);
        }

        void Answer(StopWaiter waiter, bool finished)
        {
            if (waiter.Completion != null) Run(waiter.Completion);
            if (waiter.Result != null) Run(() => waiter.Result(finished));
        }

        void OnStopDone(int token)
        {
            if (_stop == null || _stop.Token != token) return;
            _stop.NativeDone = true;
            // StopAsync waiters are told now: the SDK reported the stop, even when FinishStop waits for a Launch/Relaunch
            // result below (that wait is about the machine's continuations and plain completions).
            var told = _stop.Waiters.FindAll(w => w.Result != null);
            foreach (var w in told)
            {
                var result = w.Result;
                w.Result = null;
                if (w.Completion == null) _stop.Waiters.Remove(w);
                Run(() => result(true));
            }
            // A Launch/Relaunch still in flight may launch() after this stop (a relaunch's continuation): wait for
            // its result, which stops it again if needed.
            if (_active != null && _active.Kind != Kind.Delete) return;
            FinishStop();
            Pump();
        }

        void FinishStop(bool finished = true)
        {
            var stop = _stop;
            _stop = null;
            foreach (var w in stop.Waiters) Answer(w, finished);
            foreach (var c in stop.Continuations) Run(c);
        }

        void Complete(List<Action<bool>> completions, bool ok)
        {
            foreach (var c in completions)
            {
                try { c(ok); }
                catch (Exception e) { _native.Error(e); }
            }
        }

        void Run(Action action)
        {
            try { action(); }
            catch (Exception e) { _native.Error(e); }
        }

        static void Add<T>(List<T> list, T item) where T : class
        {
            if (item != null) list.Add(item);
        }
    }
}
