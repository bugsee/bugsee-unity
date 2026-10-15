using System;
using System.Collections.Generic;
using Bugsee.Contracts.Options;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Keeps the Unity-side frame pusher in step with the native SDK. The pusher must run exactly when the native SDK
    /// is in VideoMode.DirectBuffers, so its state follows what the native launch actually did, not what was asked
    /// for: a Launch the SDK ignored (already running) or rejected leaves the pusher alone, and an accepted one is
    /// read back from the effective launch options. If the pusher cannot start, the session is relaunched with the
    /// default capture. Written against delegates so it can be tested without Unity.
    /// </summary>
    public static class CaptureStartup
    {
        /// <summary>Call with the answer of a Launch/Relaunch request, once it is known.</summary>
        /// <param name="accepted">The native SDK accepted the request (it ran).</param>
        /// <param name="startRequested">The capture policy decided the request wants the pusher (DirectBuffers kept).</param>
        /// <param name="options">The options the request used (a fallback relaunch is these with the default mode).</param>
        /// <param name="isRunning">The native SDK is launching or launched.</param>
        /// <param name="effectiveMode">The video mode the native SDK reports in its launch options, or null when unreadable.</param>
        /// <param name="enable">Starts the pusher; main thread only; false when it could not start.</param>
        /// <param name="disable">Stops the pusher.</param>
        /// <param name="relaunch">Relaunches the native SDK with the given options (the SDK is launched when this runs).</param>
        /// <param name="warn">Logs a warning.</param>
        /// <param name="runOnMainThread">Runs the action on the main thread (inline when already there, else queued).</param>
        public static void OnLaunchResult(
            bool accepted,
            bool startRequested,
            Dictionary<string, object> options,
            Func<bool> isRunning,
            Func<VideoMode?> effectiveMode,
            Func<bool> enable,
            Action disable,
            Action<Dictionary<string, object>> relaunch,
            Action<string> warn,
            Action<Action> runOnMainThread)
        {
            runOnMainThread(() =>
            {
                if (!accepted)
                {
                    // Ignored or refused: the native session, if any, is unchanged, so the pusher is too.
                    if (!isRunning()) disable();
                    return;
                }

                VideoMode? mode = effectiveMode();
                bool wantPusher = mode.HasValue ? mode.Value == VideoMode.DirectBuffers : startRequested;
                if (!wantPusher)
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
