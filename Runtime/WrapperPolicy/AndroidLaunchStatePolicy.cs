using Bugsee.Contracts.Lifecycle;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// The Android 7.4.1 SDK's launch/relaunch gates (BugseeInternal), so the wrapper only claims what the SDK will
    /// actually do.
    /// </summary>
    internal static class AndroidLaunchStatePolicy
    {
        public static bool IsRunning(BugseeStatus status) =>
            status == BugseeStatus.Launching || status == BugseeStatus.Launched;

        /// <summary>
        /// relaunch() proceeds only when the SDK is Launched. While it is Launching, Stopping or Stopped it logs and
        /// returns WITHOUT calling the completion, so nothing may be claimed for it.
        /// </summary>
        public static bool RelaunchAccepted(BugseeStatus status) => status == BugseeStatus.Launched;

        /// <summary>
        /// The status read right after relaunch() returned. An accepted relaunch moves Launched -> Stopping inside the
        /// call (under the SDK's lock) and always calls back later: Stopping, then Stopped, then its continuation
        /// launch() (Launching, Launched). Still Launched therefore means the SDK did not take it (someone stopped it
        /// between our status check and the call, and it is running again), so the claim is released now. Every
        /// other status is ambiguous - a refusal because another thread was stopping looks the same as an accepted
        /// relaunch in its stop phase - so the claim is kept and released by the callback or by the deadline.
        /// </summary>
        public static bool RelaunchClaimReleasedAfterCall(BugseeStatus statusAfterCall) =>
            statusAfterCall == BugseeStatus.Launched;

        /// <summary>
        /// Whether THIS launch() call started a session. launch() while already Launching/Launched is a no-op that
        /// ignores the token and options and still reports true, so "running afterwards" alone is not enough.
        /// </summary>
        public static bool LaunchStartedSession(BugseeStatus before, BugseeStatus after) =>
            !IsRunning(before) && IsRunning(after);
    }
}
