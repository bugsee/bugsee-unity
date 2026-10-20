using Bugsee.Contracts.Lifecycle;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// The Android 7.4.2 SDK's launch/relaunch gates (BugseeInternal), so the wrapper only claims what the SDK will
    /// actually do. Every launch(), relaunch() and stop() of 7.4.2 calls back exactly once, so none of the gates needs
    /// a deadline to stand in for a missing callback.
    /// </summary>
    internal static class AndroidLaunchStatePolicy
    {
        public static bool IsRunning(BugseeStatus status) =>
            status == BugseeStatus.Launching || status == BugseeStatus.Launched;

        /// <summary>
        /// relaunch() starts a new relaunch only when the SDK is Launched. While it is Launching, Stopping or Stopped
        /// the SDK refuses it and calls back false (REFUSED_*), so the wrapper answers false at once, without
        /// touching the managed hooks, and does not call the SDK. A relaunch already in flight is joined instead (see
        /// <see cref="AndroidLifecycleMachine.Relaunch"/>): the SDK does that before it looks at the status.
        /// </summary>
        public static bool RelaunchAccepted(BugseeStatus status) => status == BugseeStatus.Launched;
    }
}
