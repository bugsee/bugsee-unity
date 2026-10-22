using System;
using System.Collections.Generic;
using Bugsee.Contracts.Lifecycle;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// The results waiting for a native data wipe, by the wipe's token. A Delete of the same scope that arrives while
    /// a wipe is in flight is that wipe (its result is shared); one of another scope is not, as it would delete
    /// more or less than was asked for, and so waits for the wipe and starts its own.
    /// </summary>
    internal sealed class WipeCompletions
    {
        sealed class Wipe
        {
            public DeleteScope Scope;
            public readonly List<Action<bool>> Results = new List<Action<bool>>();
        }

        readonly object _gate = new object();
        readonly Dictionary<int, Wipe> _wipes = new Dictionary<int, Wipe>();

        public void Begin(int token, DeleteScope scope, Action<bool> result)
        {
            var wipe = new Wipe { Scope = scope };
            if (result != null) wipe.Results.Add(result);
            lock (_gate) _wipes[token] = wipe;
        }

        /// <summary>True when wipe <paramref name="token"/> is in flight with <paramref name="scope"/>; the result then joins it.</summary>
        public bool TryJoin(int token, DeleteScope scope, Action<bool> result)
        {
            lock (_gate)
            {
                if (!_wipes.TryGetValue(token, out var wipe) || wipe.Scope != scope) return false;
                if (result != null) wipe.Results.Add(result);
                return true;
            }
        }

        /// <summary>Answers every result of wipe <paramref name="token"/> once; a token with no wipe (or one already answered) does nothing.</summary>
        public void Settle(int token, bool wiped, Action<Action<bool>, bool> run)
        {
            Wipe wipe;
            lock (_gate)
            {
                if (!_wipes.TryGetValue(token, out wipe)) return;
                _wipes.Remove(token);
            }
            foreach (var result in wipe.Results) run(result, wiped);
        }
    }
}
