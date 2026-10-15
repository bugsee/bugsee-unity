using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Internal
{
    /// <summary>Reads what UnityCapturePolicy needs from the running player.</summary>
    static class UnityCaptureProbe
    {
        public static UnityCaptureEnvironment Current()
        {
            return new UnityCaptureEnvironment
            {
                IsAndroidPlayer = Application.platform == RuntimePlatform.Android,
                UnityVersion = Application.unityVersion,
#if BUGSEE_HAS_SCREENCAPTURE
                ScreenCaptureModule = true,
#else
                ScreenCaptureModule = false,
#endif
                AsyncReadbackSupported = SystemInfo.supportsAsyncGPUReadback,
                GraphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                RowOrderDeclared = global::Bugsee.Capture.BugseeFrameCapturer.DefaultOptions.RowOrder.HasValue
            };
        }
    }
}
