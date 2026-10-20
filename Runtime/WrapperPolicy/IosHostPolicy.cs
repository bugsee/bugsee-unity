using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>Decisions about the iOS host that the launch options depend on.</summary>
    public static class IosHostPolicy
    {
        /// <summary>
        /// The frame rate to tell the SDK: the target when it is set, 30 (Unity's mobile default) when it is not,
        /// and never more than the screen can show, since the SDK uses it as a frame-skip divisor.
        /// </summary>
        public static int CaptureFrameRate(int targetFrameRate, int screenRefreshRate)
        {
            if (targetFrameRate <= 0) return 30;
            return screenRefreshRate > 0 ? Math.Min(targetFrameRate, screenRefreshRate) : targetFrameRate;
        }

        /// <summary>
        /// The wrapper build to report: null for none. A build that is empty or the "dev" placeholder (what every
        /// checkout and release carries until a build id is stamped) is not a build id.
        /// </summary>
        public static string WrapperBuild(string build) =>
            string.IsNullOrEmpty(build) || build == "dev" ? null : build;

        /// <summary>
        /// Whether a caller-supplied wrapper_info can be passed through: the SDK reads it as a map and indexes
        /// into it (a string there is an unrecognised-selector crash at launch), it compares "type" with
        /// isEqualToString: (anything but a string there crashes the same way), and a map without a "type" silently
        /// turns the Unity capture path off.
        /// </summary>
        public static bool IsUsableWrapperInfo(object supplied) =>
            supplied is IDictionary<string, object> map
            && map.TryGetValue("type", out var type)
            && type is string name
            && name.Length > 0;
    }
}
