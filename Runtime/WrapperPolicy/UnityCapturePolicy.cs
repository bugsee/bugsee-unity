using System;
using System.Collections.Generic;
using Bugsee.Contracts.Options;

namespace Bugsee.WrapperPolicy
{
    /// <summary>What the wrapper knows about this Unity player when a launch asks for VideoMode.DirectBuffers.</summary>
    public sealed class UnityCaptureEnvironment
    {
        public bool IsAndroidPlayer;
        public string UnityVersion;
        public bool ScreenCaptureModule;
        public bool AsyncReadbackSupported;
        public string GraphicsApi;
        /// <summary>The host set BugseeFrameCapturerOptions.RowOrder, which declares the row order of an unmeasured API verified.</summary>
        public bool RowOrderDeclared;
    }

    public sealed class VideoModeDecision
    {
        public VideoMode Effective;
        /// <summary>True when the Unity-side frame pusher must run (DirectBuffers stays and has a frame source).</summary>
        public bool UseUnityCapture;
        /// <summary>Why DirectBuffers was replaced by the default capture; null when it was not.</summary>
        public string FallbackReason;
    }

    /// <summary>
    /// DirectBuffers means "the host pushes the frames". In Unity the only frame source is the Unity-side capturer, so
    /// when it cannot run the wrapper launches the native SDK in its default capture mode instead, never in a
    /// DirectBuffers session without frames (which records no real video). Pure logic, no Unity types.
    /// </summary>
    public static class UnityCapturePolicy
    {
        /// <summary>First Unity with AndroidJNI.NewDirectByteBuffer: 2022.2.0b1 (all of 2022.3 LTS and 6000.x have it, 2021.3 and 2022.1 do not).</summary>
        public const string MinimumUnityVersion = "2022.2.0b1";
        public const int MinimumMajor = 2022;
        public const int MinimumMinor = 2;

        /// <summary>The mode the wrapper falls back to: the SDK's default capture engine.</summary>
        public const VideoMode FallbackMode = VideoMode.V2;

        public static bool IsSupportedUnityVersion(string unityVersion)
        {
            return FramePushPolicy.TryParseUnityVersion(unityVersion, out int major, out int minor, out _)
                && (major > MinimumMajor || (major == MinimumMajor && minor >= MinimumMinor));
        }

        public static VideoModeDecision Decide(VideoMode requested, UnityCaptureEnvironment env)
        {
            if (requested != VideoMode.DirectBuffers || env == null || !env.IsAndroidPlayer)
                return new VideoModeDecision { Effective = requested };

            string reason = null;
            if (!IsSupportedUnityVersion(env.UnityVersion))
                reason = "Unity " + env.UnityVersion + " has no AndroidJNI.NewDirectByteBuffer (needs " + MinimumUnityVersion + " or newer)";
            else if (!env.ScreenCaptureModule)
                reason = "the Screen Capture module (com.unity.modules.screencapture) is not in the project";
            else if (!env.AsyncReadbackSupported)
                reason = "this device does not support AsyncGPUReadback";
            else if (!FramePushPolicy.IsReadbackUsable(true, env.GraphicsApi == "Vulkan", env.UnityVersion))
                reason = "AsyncGPUReadback is not safe on Vulkan with this Unity version";
            else if (!env.RowOrderDeclared && !FramePushPolicy.TryMeasuredRowOrder(env.GraphicsApi, out _))
                reason = "the readback row order of " + (env.GraphicsApi ?? "this graphics API") + " is unverified (set BugseeFrameCapturer.DefaultOptions.RowOrder to declare it)";

            return reason == null
                ? new VideoModeDecision { Effective = VideoMode.DirectBuffers, UseUnityCapture = true }
                : new VideoModeDecision { Effective = FallbackMode, FallbackReason = reason };
        }

        public static VideoMode RequestedMode(IDictionary<string, object> options)
        {
            if (options == null || !options.TryGetValue(Options.CaptureVideoMode, out object value) || value == null)
                return VideoMode.V2;
            if (value is VideoMode mode) return mode;
            try
            {
                return VideoModeExtensions.FromIntValue(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                return VideoMode.V2;
            }
        }

        /// <summary>
        /// The options to hand to the native SDK: the same dictionary when nothing changes, else a copy with the video
        /// mode replaced by <see cref="FallbackMode"/>. Options that do not set a video mode never change.
        /// </summary>
        public static Dictionary<string, object> Apply(
            Dictionary<string, object> options, UnityCaptureEnvironment env, out VideoModeDecision decision)
        {
            if (options == null || !options.ContainsKey(Options.CaptureVideoMode))
            {
                decision = new VideoModeDecision { Effective = VideoMode.V2 };
                return options;
            }
            decision = Decide(RequestedMode(options), env);
            return decision.FallbackReason == null ? options : WithFallback(options);
        }

        /// <summary>A copy of the options with the default capture mode, for when the frame source could not start after the launch.</summary>
        public static Dictionary<string, object> WithFallback(Dictionary<string, object> options)
        {
            var copy = options == null ? new Dictionary<string, object>() : new Dictionary<string, object>(options);
            copy[Options.CaptureVideoMode] = (int)FallbackMode;
            return copy;
        }
    }
}
