using System;
using System.Threading;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Launch/Relaunch bumps the generation, so a Stop completion can tell that a new session started after it
    /// was queued. A delete does not use it: a delete is never skipped, it runs in request order.
    /// </summary>
    internal static class DeleteCollectedDataLaunchGeneration
    {
        static int _generation;

        public static int CaptureForPendingDelete() => Volatile.Read(ref _generation);

        public static void BumpForLaunch() => Interlocked.Increment(ref _generation);
    }
}
