using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Callbacks handed to native code by an integer token. Each one is taken at most once, so whichever comes
    /// first, the SDK's answer or the bridge's timeout, wins and the other finds nothing.
    /// </summary>
    internal sealed class OneShotCallbacks<T>
    {
        readonly object _gate = new object();
        readonly Dictionary<int, Action<T>> _callbacks = new Dictionary<int, Action<T>>();
        int _lastToken;

        /// <summary>Returns the token for <paramref name="callback"/>; never 0, which stands for "no callback".</summary>
        public int Register(Action<T> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (_gate)
            {
                do { _lastToken++; } while (_lastToken == 0 || _callbacks.ContainsKey(_lastToken));
                _callbacks[_lastToken] = callback;
                return _lastToken;
            }
        }

        public bool TryTake(int token, out Action<T> callback)
        {
            lock (_gate)
            {
                if (!_callbacks.TryGetValue(token, out callback)) return false;
                _callbacks.Remove(token);
                return true;
            }
        }
    }
}
