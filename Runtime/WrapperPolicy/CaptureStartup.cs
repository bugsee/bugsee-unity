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
    /// <summary>
    /// What the wrapper knows about the running native session's pusher: whether the native SDK runs in DirectBuffers
    /// (as it reports it after an accepted launch) and the user's options of that launch, for a fallback relaunch.
    /// Cleared when the session stops or a later accepted launch is not DirectBuffers. Thread-safe.
    /// </summary>
    public sealed class PusherLaunchState
    {
        readonly object _gate = new object();
        bool _active;
        Dictionary<string, object> _lastOptions;
        bool _fallbackPending;
        Dictionary<string, object> _fallbackOptions;

        public bool Active { get { lock (_gate) return _active; } }
        public Dictionary<string, object> LastOptions { get { lock (_gate) return _lastOptions; } }

        public void OnAccepted(VideoMode? effectiveMode, bool startRequested, Dictionary<string, object> options)
        {
            bool direct = effectiveMode.HasValue ? effectiveMode.Value == VideoMode.DirectBuffers : startRequested;
            lock (_gate)
            {
                _active = direct;
                // A Relaunch without options keeps the previous ones; a session that is not DirectBuffers keeps none.
                _lastOptions = direct ? (options ?? _lastOptions) : null;
            }
        }

        public void OnStopped()
        {
            lock (_gate) { _active = false; _lastOptions = null; _fallbackPending = false; _fallbackOptions = null; }
        }

        /// <summary>The pusher failed while a newer request was waiting: remember that the session still needs the fallback.</summary>
        public void DeferFallback(Dictionary<string, object> options)
        {
            lock (_gate) { _fallbackPending = true; _fallbackOptions = options ?? _fallbackOptions; }
        }

        public bool FallbackPending { get { lock (_gate) return _fallbackPending; } }

        /// <summary>The deferred fallback's options (null options are possible), clearing it; false when none was deferred.</summary>
        public bool TakeDeferredFallback(out Dictionary<string, object> options)
        {
            lock (_gate)
            {
                options = _fallbackOptions;
                bool was = _fallbackPending;
                _fallbackPending = false;
                _fallbackOptions = null;
                return was;
            }
        }

        /// <summary>The pusher failed: the options for the fallback relaunch, or false when no DirectBuffers session is known. Consumes the state.</summary>
        public bool TakeFailure(out Dictionary<string, object> options)
        {
            lock (_gate)
            {
                options = _lastOptions;
                bool was = _active;
                _active = false;
                _lastOptions = null;
                return was;
            }
        }
    }

    public static class CaptureStartup
    {
        /// <summary>Call with the answer of a Launch/Relaunch request, once it is known.</summary>
        /// <param name="accepted">The native SDK accepted the request (it ran).</param>
        /// <param name="startRequested">The capture policy decided the request wants the pusher (DirectBuffers kept).</param>
        /// <param name="options">The options the request used (a fallback relaunch is these with the default mode).</param>
        /// <param name="state">Tracks the running session's DirectBuffers state and options.</param>
        /// <param name="isLatestRequest">No newer Launch/Relaunch is waiting: a fallback relaunch would otherwise replace its options, so it is skipped and that request decides.</param>
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
            PusherLaunchState state,
            Func<bool> isLatestRequest,
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
                    if (!isRunning())
                    {
                        state.OnStopped();
                        disable();
                    }
                    else if (state.FallbackPending && isLatestRequest())
                    {
                        // The pusher failed while this request was waiting, and the session it left is still in DirectBuffers.
                        state.TakeDeferredFallback(out Dictionary<string, object> deferred);
                        warn("the Unity-side frame pusher had stopped and the waiting launch request did not change the session; relaunching with the SDK's default capture.");
                        relaunch(UnityCapturePolicy.WithFallback(deferred ?? options));
                    }
                    return;
                }

                VideoMode? mode;
                try
                {
                    mode = effectiveMode();
                }
                catch (Exception ex)
                {
                    warn("could not read the effective video mode: " + ex.Message);
                    mode = null;
                }
                bool direct = mode.HasValue ? mode.Value == VideoMode.DirectBuffers : startRequested;

                if (state.FallbackPending)
                {
                    // The pusher failed while this request was waiting. If the session it produced is not DirectBuffers the
                    // failure is moot; if it is, the pusher is not restarted and the session goes to the default capture.
                    if (!direct)
                    {
                        state.TakeDeferredFallback(out _);
                        state.OnAccepted(mode, startRequested, options);
                        disable();
                        return;
                    }
                    if (isLatestRequest())
                    {
                        state.TakeDeferredFallback(out Dictionary<string, object> deferred);
                        warn("the Unity-side frame pusher had stopped; relaunching with the SDK's default capture.");
                        relaunch(UnityCapturePolicy.WithFallback(options ?? deferred));
                    }
                    return;
                }

                state.OnAccepted(mode, startRequested, options);
                if (!direct)
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
                state.TakeFailure(out Dictionary<string, object> fallbackOptions);
                fallbackOptions = fallbackOptions ?? options;
                if (!isLatestRequest())
                {
                    state.DeferFallback(fallbackOptions);
                    warn("the Unity-side frame pusher could not start, but a newer launch request is waiting; the fallback to the default capture runs when its answer arrives.");
                    return;
                }
                warn("the Unity-side frame pusher could not start; relaunching with the SDK's default capture.");
                relaunch(UnityCapturePolicy.WithFallback(fallbackOptions));
            });
        }

        /// <summary>The pusher stopped itself after launch (repeated readback failures): the same fallback, from the tracked session.</summary>
        public static void OnPusherFailed(
            PusherLaunchState state,
            Func<bool> isLatestRequest,
            Action<Dictionary<string, object>> relaunch,
            Action<string> warn)
        {
            if (!state.TakeFailure(out Dictionary<string, object> options)) return;
            if (!isLatestRequest())
            {
                // Never dropped: the waiting request's answer decides (see OnLaunchResult).
                state.DeferFallback(options);
                warn("the Unity-side frame pusher stopped, but a newer launch request is waiting; the fallback to the default capture runs when its answer arrives.");
                return;
            }
            warn("the Unity-side frame pusher stopped after repeated failures; relaunching with the SDK's default capture.");
            relaunch(UnityCapturePolicy.WithFallback(options));
        }
    }
}
