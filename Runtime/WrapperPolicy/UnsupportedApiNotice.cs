using System;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// <c>Bugsee.ResetVideoCapturePermission</c> on iOS: the SDK has no capture permission to reset (video capture of
    /// the app's own view needs none), so the call does nothing. It says so once per process instead of staying silent.
    /// </summary>
    public sealed class UnsupportedApiNotice
    {
        public const string ResetVideoCapturePermissionMessage =
            "[Bugsee] ResetVideoCapturePermission does nothing on iOS: the iOS SDK keeps no video capture permission "
            + "(it records the app's own view without a system prompt). The call only has an effect on Android.";

        readonly Action<string> _log;
        int _said;

        public UnsupportedApiNotice(Action<string> log) { _log = log; }

        /// <summary>Logs <paramref name="message"/> the first time only; true when this call logged it.</summary>
        public bool Once(string message)
        {
            if (System.Threading.Interlocked.Exchange(ref _said, 1) != 0) return false;
            try { _log?.Invoke(message); } catch (Exception) { /* a logger must not break the caller */ }
            return true;
        }
    }
}
