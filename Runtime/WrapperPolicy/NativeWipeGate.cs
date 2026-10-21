using System;
using Bugsee.Contracts.Lifecycle;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// The iOS rule for native data wipes, over <see cref="StopWaitList"/> and <see cref="WipeCompletions"/>: one wipe
    /// in flight at a time. A Delete of the same scope as the wipe in flight shares it and its result; a Delete of
    /// another scope is queued behind it and starts its own when that one is reported, in request order. Main thread
    /// only.
    /// </summary>
    internal sealed class NativeWipeGate
    {
        readonly StopWaitList _waiters;
        readonly WipeCompletions _results;

        public NativeWipeGate(StopWaitList waiters, WipeCompletions results)
        {
            _waiters = waiters;
            _results = results;
        }

        /// <summary>
        /// True when <paramref name="done"/> is taken care of: it joined the wipe in flight (same scope), or
        /// <paramref name="beginAgain"/> was queued to run when that wipe is over. False when no wipe is in flight:
        /// the caller begins its own (<see cref="Begin"/>).
        /// </summary>
        public bool TryJoinOrQueue(DeleteScope scope, Action<bool> done, Action beginAgain)
        {
            if (_waiters.TryCoalesceWipe(out var wipe) && _results.TryJoin(wipe, scope, done)) return true;
            if (!_waiters.TryQueueBehindWipe(beginAgain, out var cancelledLaunch)) return false;
            cancelledLaunch?.Invoke(false);
            return true;
        }

        /// <summary>Records a wipe of <paramref name="scope"/> as in flight and returns the token to hand to native.</summary>
        public int Begin(DeleteScope scope, Action<bool> done, int generation)
        {
            var token = _waiters.Begin(null, generation, holdsLaunch: true);
            _results.Begin(token, scope, done);
            return token;
        }
    }
}
