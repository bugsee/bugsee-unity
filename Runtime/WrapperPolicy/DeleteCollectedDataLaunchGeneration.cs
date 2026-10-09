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
    }
}
