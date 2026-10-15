using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Internal
{
    /// <summary>
    /// Reads what UnityCapturePolicy needs from the running player. SystemInfo may only be used on the main thread, so the
    /// values are read once on it at startup (and lazily on the main thread if that did not run) and a launch from any
    /// other thread uses the cached copy.
    /// </summary>
    static class UnityCaptureProbe
    {
        static readonly object Gate = new object();
        static UnityCaptureEnvironment _cached;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            // RuntimeInitializeOnLoadMethod runs on the main thread; MainThreadDispatcher's own bootstrap may not have run
            // yet, so do not go through IsMainThread here.
            var env = Read();
            lock (Gate) _cached = env;
        }

        public static UnityCaptureEnvironment Current()
        {
            var env = _cached;
            if (env == null && MainThreadDispatcher.IsMainThread)
            {
                env = Read();
                lock (Gate) _cached = env;
            }
            // Off the main thread before the startup read ran: report nothing usable, so the launch falls back.
            env = env ?? _cached ?? Unavailable();
            // The one value that can change after startup, and is a plain field read.
            env.RowOrderDeclared = global::Bugsee.Capture.BugseeFrameCapturer.DefaultOptions.RowOrder.HasValue;
            return env;
        }

        static UnityCaptureEnvironment Read()
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
                GraphicsApi = SystemInfo.graphicsDeviceType.ToString()
            };
        }

        static UnityCaptureEnvironment Unavailable()
        {
            return new UnityCaptureEnvironment { IsAndroidPlayer = true, NotRead = true };
        }
    }
}
