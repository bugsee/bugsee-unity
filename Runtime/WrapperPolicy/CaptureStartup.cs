using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// What happens after a launch that asked for VideoMode.DirectBuffers: start the Unity-side pusher on the main thread,
    /// and if it cannot start, relaunch with the default capture, so the session never keeps DirectBuffers without a
    /// frame source. Written against delegates so it can be tested without Unity.
    /// </summary>
    public static class CaptureStartup
    {
        /// <param name="startPusher">The policy decided the pusher must run.</param>
        /// <param name="options">The options the launch used (the fallback relaunch is these with the default mode).</param>
        /// <param name="enable">Starts the pusher; must be called on the main thread; false when it could not start.</param>
        /// <param name="disable">Stops a pusher a previous launch started.</param>
        /// <param name="relaunch">Relaunches the native SDK with the given options.</param>
        /// <param name="warn">Logs a warning.</param>
        /// <param name="runOnMainThread">Runs the action on the main thread (inline when already there, else queued).</param>
        public static void Run(
            bool startPusher,
            Dictionary<string, object> options,
            Func<bool> enable,
            Action disable,
            Action<Dictionary<string, object>> relaunch,
            Action<string> warn,
            Action<Action> runOnMainThread)
        {
            runOnMainThread(() =>
            {
                if (!startPusher)
                {
                    disable();
                    return;
                }
                bool started;
                try
                {
                    started = enable();
                }
                catch (Exception ex)
                {
                    warn("the Unity-side frame pusher threw while starting: " + ex.Message);
                    started = false;
                }
                if (started) return;
                warn("the Unity-side frame pusher could not start; relaunching with the SDK's default capture.");
                relaunch(UnityCapturePolicy.WithFallback(options));
            });
        }

        /// <summary>The pusher stopped itself after launch (repeated readback failures): same fallback.</summary>
        public static void OnPusherFailed(
            Dictionary<string, object> options,
            Action<Dictionary<string, object>> relaunch,
            Action<string> warn)
        {
            warn("the Unity-side frame pusher stopped after repeated failures; relaunching with the SDK's default capture.");
            relaunch(UnityCapturePolicy.WithFallback(options));
        }
    }
}
