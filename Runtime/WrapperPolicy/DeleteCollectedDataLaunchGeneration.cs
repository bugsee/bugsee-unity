using System;
using System.Threading;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Launch/Relaunch bumps generation so an in-flight GDPR delete can skip wiping a newer session.
    /// </summary>
    internal static class DeleteCollectedDataLaunchGeneration
    {
        static int _generation;

        public static int CaptureForPendingDelete() => Volatile.Read(ref _generation);

        public static void BumpForLaunch() => Interlocked.Increment(ref _generation);

        public static bool ShouldRunDelete(int capturedGeneration) =>
            capturedGeneration == Volatile.Read(ref _generation);

        /// <summary>
        /// Runs a delete's teardown only if no Launch/Relaunch has happened since it was requested; false when it
        /// was skipped. Main thread, where launches bump the generation, so the check and the body are not split.
        /// </summary>
        public static bool RunIfCurrent(int capturedGeneration, Action body)
        {
            if (!ShouldRunDelete(capturedGeneration)) return false;
            body();
            return true;
        }
    }
}
