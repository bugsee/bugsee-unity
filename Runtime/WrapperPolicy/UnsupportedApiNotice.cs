using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Says a message once per process (per distinct message text), for a wrapper call the platform cannot act on, so
    /// it is not a silent no-op and does not flood the log when called often.
    /// </summary>
    public sealed class UnsupportedApiNotice
    {
        /// <summary><c>Bugsee.ResetVideoCapturePermission</c> on iOS.</summary>
        public const string ResetVideoCapturePermissionMessage =
            "[Bugsee] ResetVideoCapturePermission does nothing on iOS: the iOS SDK keeps no video capture permission "
            + "(it records the app's own view without a system prompt). The call only has an effect on Android.";

        readonly Action<string> _log;
        readonly HashSet<string> _said = new HashSet<string>();

        public UnsupportedApiNotice(Action<string> log) { _log = log; }

        /// <summary>Logs <paramref name="message"/> the first time it is seen; true only when this call logged it (false: already said, no logger, or the logger threw, in which case a later call retries).</summary>
        public bool Once(string message)
        {
            if (_log == null) return false;
            var key = message ?? "";
            lock (_said) { if (!_said.Add(key)) return false; }
            try
            {
                _log(message);
                return true;
            }
            catch (Exception)
            {
                // A logger must not break the caller; the message was not said, so a later call may try again.
                lock (_said) { _said.Remove(key); }
                return false;
            }
        }
    }
}
