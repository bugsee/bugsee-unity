using System;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Reads the UUID of the running IL2CPP image through a probe and keeps it. Only a usable answer is kept: a probe
    /// that returned nothing (an empty native walk, a failure at a bad moment) is tried again on a later call, at least
    /// <c>retryAfterMs</c> after the previous probe and up to <c>maxAttempts</c> probes in all, so one early failure
    /// does not leave every payload without a UUID and the attempts are not all spent within one report. Thread-safe;
    /// the probe never runs twice at once and never again after it succeeded.
    /// </summary>
    public sealed class ImageUuidCache
    {
        readonly Func<string> _probe;
        readonly int _maxAttempts;
        readonly object _gate = new object();
        volatile string _value;
        readonly Func<long> _nowMs;
        readonly long _retryAfterMs;
        int _attempts;
        long _lastAttemptMs;

        /// <param name="nowMs">Monotonic milliseconds; defaults to the process stopwatch.</param>
        public ImageUuidCache(Func<string> probe, int maxAttempts = 3, long retryAfterMs = 2000, Func<long> nowMs = null)
        {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _maxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
            _retryAfterMs = retryAfterMs;
            _nowMs = nowMs ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000));
        }

        /// <summary>The UUID, or null when it is not known (yet).</summary>
        public string Get()
        {
            var cached = _value;
            if (cached != null)
            {
                return cached;
            }

            lock (_gate)
            {
                if (_value != null)
                {
                    return _value;
                }

                if (_attempts >= _maxAttempts)
                {
                    return null;
                }

                var now = _nowMs();
                if (_attempts > 0 && now - _lastAttemptMs < _retryAfterMs)
                {
                    return null;
                }

                _attempts++;
                _lastAttemptMs = now;
                string probed = null;
                try { probed = _probe(); }
                catch { /* a probe that throws is a probe that found nothing */ }

                if (string.IsNullOrEmpty(probed))
                {
                    return null;
                }

                _value = probed;
                return probed;
            }
        }
    }

    /// <summary>Which UUID the managed payload names as its module.</summary>
    public static class ModuleUuidChoice
    {
        /// <summary>
        /// The UUID the native stack of this very exception reported when it has one; otherwise the UUID of the
        /// running image; null (never "") when neither is known (JsonUtility writes it as an empty string, which the worker treats as missing).
        /// </summary>
        public static string Pick(string nativeUuid, string runtimeUuid)
        {
            if (!string.IsNullOrEmpty(nativeUuid))
            {
                return nativeUuid;
            }

            return string.IsNullOrEmpty(runtimeUuid) ? null : runtimeUuid;
        }
    }
}
