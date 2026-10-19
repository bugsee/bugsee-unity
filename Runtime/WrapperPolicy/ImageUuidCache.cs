using System;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Reads the UUID of the running IL2CPP image through a probe and keeps it. Only a usable answer is kept: a probe
    /// that returned nothing (an empty native walk, a failure at a bad moment) is tried again on a later call, up to
    /// <c>maxAttempts</c> probes in all, so one early failure does not leave every payload without a UUID. Thread-safe;
    /// the probe never runs twice at once and never again after it succeeded.
    /// </summary>
    public sealed class ImageUuidCache
    {
        readonly Func<string> _probe;
        readonly int _maxAttempts;
        readonly object _gate = new object();
        volatile string _value;
        int _attempts;

        public ImageUuidCache(Func<string> probe, int maxAttempts = 3)
        {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _maxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
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

                _attempts++;
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
        /// running image; null (never "") when neither is known, so the field is omitted from the payload.
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
