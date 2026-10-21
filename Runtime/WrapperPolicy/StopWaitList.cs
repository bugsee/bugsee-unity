using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// One native stop at a time, and the actions waiting for it to finish.
    /// A Stop or Delete that arrives while a native stop of the same launch generation is in flight waits on
    /// that stop instead of issuing another one. Waiters run when the SDK reports that stop finished (or the
    /// bridge gives up waiting for the report), matched by token, not by order. They are dropped if a
    /// Launch/Relaunch has started a new session since they were queued, including one made by an earlier
    /// waiter: they belong to the session that stopped and must not act on the new one.
    /// <para>
    /// A Launch/Relaunch that arrives while a native stop is in flight is deferred until it finishes, because
    /// the SDK keeps a launch that completes after a stop stopped. The latest request wins: a later
    /// Launch/Relaunch replaces a deferred one, and a later Stop or Delete cancels it. When the stop finishes the
    /// waiters run in arrival order (first waiters ahead of the rest), then the deferred launch, which is by construction the latest request; it
    /// is cancelled instead if a waiter has made a newer request meanwhile (a completion that launches or stops
    /// again was called later still). A cancelled launch is told so (<c>false</c>), so nothing waits on it.
    /// </para>
    /// <para>
    /// A Delete joins as a <c>first</c> waiter: it runs before the other waiters and is never dropped, because a
    /// wipe must happen in request order and not silently not happen. A completion that relaunches (a Stop
    /// requested before the Delete) was by request order a later request, so the wipe goes ahead of it.
    /// </para>
    /// </summary>
    internal sealed class StopWaitList
    {
        sealed class Waiter
        {
            public Action Run;
            public Action Dropped;
            public bool First;
        }

        sealed class Pending
        {
            public int Generation;
            public bool HoldsLaunch;
            public readonly List<Waiter> Waiters = new List<Waiter>();
        }

        readonly object _gate = new object();
        readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();
        int _lastToken;
        int _inFlightToken;
        // Bumped by every lifecycle request, so a deferred launch can tell whether it is still the latest one.
        int _intent;
        Action<bool> _deferredLaunch;
        int _deferredLaunchIntent;

        /// <summary>
        /// Queues <paramref name="waiter"/> on the native stop in flight in <paramref name="generation"/>.
        /// False when there is none; the caller then issues a native stop with <see cref="Begin"/>.
        /// <paramref name="dropped"/>, if any, runs in its place when the waiter is dropped.
        /// A deferred launch is cancelled either way (it comes back in <paramref name="cancelledLaunch"/>, for
        /// the caller to call with <c>false</c> outside any lock): this request is newer.
        /// </summary>
        public bool TryJoin(Action waiter, int generation, out Action<bool> cancelledLaunch, Action dropped = null,
            bool first = false)
        {
            lock (_gate)
            {
                _intent++;
                cancelledLaunch = TakeDeferredLaunch();
                if (_inFlightToken == 0
                    || !_pending.TryGetValue(_inFlightToken, out var pending)
                    || pending.Generation != generation)
                    return false;
                Add(pending, waiter, dropped, first);
                return true;
            }
        }

        /// <summary>
        /// Records a new native operation in flight (a stop, or with <paramref name="holdsLaunch"/> a data wipe)
        /// and returns the token to pass to it. A wipe is the same request as the Delete that started it, so it
        /// does not count as a newer one: a launch deferred behind it stays deferred, and runs when the wipe is
        /// reported (<see cref="Complete"/>), not when the stop before it finished.
        /// </summary>
        public int Begin(Action waiter, int generation, Action dropped = null, bool first = false,
            bool holdsLaunch = false)
        {
            lock (_gate)
            {
                if (!holdsLaunch) _intent++;
                var token = ++_lastToken;
                var pending = new Pending { Generation = generation, HoldsLaunch = holdsLaunch };
                Add(pending, waiter, dropped, first);
                _pending[token] = pending;
                _inFlightToken = token;
                return token;
            }
        }

        /// <summary>
        /// True when a wipe begun with <c>holdsLaunch</c> is the operation in flight and no launch is waiting on
        /// it: a consecutive Delete is then the same wipe (Android coalesces them too), so the caller starts no
        /// second one. Not a new request, so the intent is unchanged. False when a launch is deferred behind the
        /// wipe: a Delete after that launch is a newer request and joins as usual, cancelling it.
        /// </summary>
        public bool TryCoalesceWipe() => TryCoalesceWipe(out _);

        /// <inheritdoc cref="TryCoalesceWipe()"/>
        /// <param name="token">The token of that wipe, for the caller to join its result to.</param>
        public bool TryCoalesceWipe(out int token)
        {
            lock (_gate)
            {
                token = _inFlightToken;
                return _inFlightToken != 0
                    && _deferredLaunch == null
                    && _pending.TryGetValue(_inFlightToken, out var pending)
                    && pending.HoldsLaunch;
            }
        }

        /// <summary>
        /// Queues <paramref name="waiter"/> as a first waiter on the wipe in flight (one begun with
        /// <c>holdsLaunch</c>), for a Delete that must not run beside it. False when no wipe is in flight. A wipe
        /// waiter is not a new request (the intent is unchanged), so a launch deferred behind the wipe stays
        /// deferred, and runs after the waiter's own wipe; except that a launch already waiting is older than this
        /// Delete and is cancelled (it comes back in <paramref name="cancelledLaunch"/>, for the caller to call with
        /// <c>false</c> outside any lock), as <see cref="TryJoin"/> does.
        /// </summary>
        public bool TryQueueBehindWipe(Action waiter, out Action<bool> cancelledLaunch)
        {
            lock (_gate)
            {
                cancelledLaunch = null;
                if (_inFlightToken == 0
                    || !_pending.TryGetValue(_inFlightToken, out var pending)
                    || !pending.HoldsLaunch)
                    return false;
                if (_deferredLaunch != null)
                {
                    _intent++;
                    cancelledLaunch = TakeDeferredLaunch();
                }
                Add(pending, waiter, null, first: true);
                return true;
            }
        }

        /// <summary>
        /// Defers <paramref name="launch"/> until the native stop in flight finishes. False when none is in
        /// flight: the caller launches now. A launch deferred earlier is replaced and comes back in
        /// <paramref name="cancelledLaunch"/>. <paramref name="launch"/> is later called with <c>true</c> to
        /// run it, or <c>false</c> when a newer request cancelled it.
        /// </summary>
        public bool TryDeferLaunch(Action<bool> launch, out Action<bool> cancelledLaunch)
        {
            lock (_gate)
            {
                _intent++;
                cancelledLaunch = TakeDeferredLaunch();
                if (_inFlightToken == 0) return false;
                _deferredLaunch = launch;
                _deferredLaunchIntent = _intent;
                return true;
            }
        }

        /// <summary>
        /// Stop <paramref name="token"/> finished, or the caller has given up waiting for it: runs its waiters,
        /// then any deferred launch, in order through <paramref name="run"/>, outside the lock. Nothing runs
        /// for an unknown or already completed token, so a report that arrives after a timeout is ignored.
        /// <paramref name="currentGeneration"/> is read again before each waiter, so a Launch/Relaunch, whether
        /// made before this call or by an earlier waiter, drops the rest (their <c>dropped</c> actions run
        /// instead). Returns true when waiters were dropped.
        /// </summary>
        public bool Complete(int token, Func<int> currentGeneration, Action<Action> run)
        {
            Pending pending;
            Action<bool> launch = null;
            int launchIntent = 0;
            lock (_gate)
            {
                if (!_pending.TryGetValue(token, out pending)) return false;
                _pending.Remove(token);
                if (_inFlightToken == token)
                {
                    _inFlightToken = 0;
                    launch = TakeDeferredLaunch();
                    launchIntent = _deferredLaunchIntent;
                }
            }

            var dropped = false;
            foreach (var waiter in pending.Waiters)
            {
                if (!waiter.First) continue;
                if (waiter.Run != null) run(waiter.Run);
            }
            foreach (var waiter in pending.Waiters)
            {
                if (waiter.First) continue;
                if (!dropped && currentGeneration() != pending.Generation) dropped = true;
                var action = dropped ? waiter.Dropped : waiter.Run;
                if (action != null) run(action);
            }

            if (launch != null)
            {
                bool latest;
                lock (_gate)
                {
                    if (_intent == launchIntent && _inFlightToken != 0
                        && _pending.TryGetValue(_inFlightToken, out var next) && next.HoldsLaunch)
                    {
                        // A waiter started the wipe: the launch waits for it.
                        _deferredLaunch = launch;
                        _deferredLaunchIntent = launchIntent;
                        return dropped;
                    }
                    latest = _intent == launchIntent && _inFlightToken == 0;
                }
                run(() => launch(latest));
            }
            return dropped;
        }

        /// <summary>True while stop <paramref name="token"/> has neither been reported nor given up on.</summary>
        public bool IsPending(int token)
        {
            lock (_gate)
            {
                return _pending.ContainsKey(token);
            }
        }

        static void Add(Pending pending, Action waiter, Action dropped, bool first)
        {
            if (waiter != null || dropped != null)
                pending.Waiters.Add(new Waiter { Run = waiter, Dropped = dropped, First = first });
        }

        Action<bool> TakeDeferredLaunch()
        {
            var launch = _deferredLaunch;
            _deferredLaunch = null;
            return launch;
        }
    }
}
