using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    public enum NetworkLaunchPhase
    {
        BeforeLaunched,
        Launched,
        Stopped
    }

    /// <summary>
    /// Buffers wrapper-channel network submits on iOS until lifecycle reaches Launched.
    /// </summary>
    public sealed class NetworkEventLaunchBuffer<T>
    {
        readonly object _gate = new object();
        readonly List<T> _pending = new List<T>();
        NetworkLaunchPhase _phase = NetworkLaunchPhase.BeforeLaunched;
        bool _lifecycleStoppedPendingAfterNewLaunch;
        Action<T> _submit;

        /// <summary>
        /// Call when Launch/Relaunch begins so a late native Stopped does not wipe pre-launch buffers.
        /// </summary>
        public void BeginNewLaunchCycle() => _lifecycleStoppedPendingAfterNewLaunch = true;

        public NetworkLaunchPhase Phase
        {
            get
            {
                lock (_gate)
                    return _phase;
            }
        }

        public void SetSubmitHandler(Action<T> submit) => _submit = submit;

        public void SetPhase(NetworkLaunchPhase phase)
        {
            lock (_gate)
            {
                if (phase == NetworkLaunchPhase.Launched)
                {
                    if (_phase == NetworkLaunchPhase.Launched)
                        return;
                    _lifecycleStoppedPendingAfterNewLaunch = false;
                    _phase = NetworkLaunchPhase.Launched;
                    FlushPendingLocked();
                    return;
                }

                if (phase == NetworkLaunchPhase.Stopped)
                {
                    _lifecycleStoppedPendingAfterNewLaunch = false;
                    _phase = NetworkLaunchPhase.Stopped;
                    _pending.Clear();
                    return;
                }

                if (phase == NetworkLaunchPhase.BeforeLaunched)
                    _phase = NetworkLaunchPhase.BeforeLaunched;
            }
        }

        public void SetPhaseFromLifecycle(NetworkLaunchPhase phase)
        {
            lock (_gate)
            {
                if (phase == NetworkLaunchPhase.Launched)
                {
                    if (_phase == NetworkLaunchPhase.Launched)
                        return;
                    _lifecycleStoppedPendingAfterNewLaunch = false;
                    _phase = NetworkLaunchPhase.Launched;
                    FlushPendingLocked();
                    return;
                }

                if (phase == NetworkLaunchPhase.Stopped)
                {
                    if (_lifecycleStoppedPendingAfterNewLaunch && _phase == NetworkLaunchPhase.BeforeLaunched)
                        return;
                    _lifecycleStoppedPendingAfterNewLaunch = false;
                    _phase = NetworkLaunchPhase.Stopped;
                    _pending.Clear();
                    return;
                }

                if (phase == NetworkLaunchPhase.BeforeLaunched)
                    _phase = NetworkLaunchPhase.BeforeLaunched;
            }
        }

        public void Enqueue(T item)
        {
            if (item == null)
                return;

            lock (_gate)
            {
                if (_phase == NetworkLaunchPhase.Stopped)
                    return;

                if (_phase == NetworkLaunchPhase.Launched)
                {
                    var submit = _submit;
                    if (submit != null)
                        submit(item);
                    return;
                }

                _pending.Add(item);
            }
        }

        void FlushPendingLocked()
        {
            if (_submit == null || _pending.Count == 0)
            {
                _pending.Clear();
                return;
            }

            var copy = new List<T>(_pending);
            _pending.Clear();
            var submit = _submit;
            for (var i = 0; i < copy.Count; i++)
                submit(copy[i]);
        }
    }
}
