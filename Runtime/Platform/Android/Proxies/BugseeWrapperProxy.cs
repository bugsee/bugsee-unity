#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Concurrent;
using System.Collections.Generic;
using Bugsee;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    /// <summary>
    /// Unity implementation of <c>com.bugsee.library.contracts.internal.BugseeWrapper</c>.
    /// Registered via <c>Bugsee.setWrapper</c> — not an application-facing API.
    /// </summary>
    sealed class BugseeWrapperProxy : AndroidJavaProxy
    {
        // Packed secure-rect buffer per display: [version, count, l,t,r,b, ...]
        readonly ConcurrentDictionary<int, int[]> _secureRectsByDisplay = new ConcurrentDictionary<int, int[]>();

        public BugseeWrapperProxy()
            : base("com.bugsee.library.contracts.internal.BugseeWrapper")
        {
        }

        public string getWrapperVersion() => BugseePackageVersion.Version;

        public string getWrapperBuild() => BugseePackageVersion.Build;

        public string getWrapperType() => "unity";

        // Cached on first main-thread access — getContext() is invoked off the main thread.
        static string _unityVersion = "";
        static string _platform = "";
        static string _productName = "";
        static bool _contextCached;

        internal static void CacheHostContext()
        {
            _unityVersion = Application.unityVersion ?? "";
            _platform = Application.platform.ToString();
            _productName = Application.productName ?? "";
            _contextCached = true;
        }

        public AndroidJavaObject getContext()
        {
            if (!_contextCached)
            {
                // Best-effort; may be empty if never warmed from main thread.
            }

            var map = new AndroidJavaObject("java.util.HashMap");
            map.Call<AndroidJavaObject>("put", "unity_version", _unityVersion);
            map.Call<AndroidJavaObject>("put", "unity_platform", _platform);
            map.Call<AndroidJavaObject>("put", "product_name", _productName);
            return map;
        }

        public void requestData(string dataType, AndroidJavaObject callback)
        {
            // No extra Unity data sources yet — always complete with null.
            callback?.Call("onResult", (string)null);
        }

        // Android SDK may pass Object as Map/null or as a JSON String; Unity JNI
        // resolves the exact overload, so both signatures are required.
        public void onLifecycleEvent(string eventType, AndroidJavaObject data)
        {
            // Lifecycle is forwarded via LifecycleListenerProxy → Bugsee.HandleNativeLifecycle.
            // Keep this hook for future wrapper-only side effects (do not double-dispatch).
        }

        public void onLifecycleEvent(string eventType, string data)
        {
            // See AndroidJavaObject overload — do not double-dispatch.
        }

        public int[] getSecureRectangles(int display)
        {
            if (_secureRectsByDisplay.TryGetValue(display, out int[] cached))
                return (int[])cached.Clone();

            return new[] { 1, 0 };
        }

        // ReportHandler defaults — no-op continue. App handler is separate via setReportHandler.
        public void onBeforeReportCreated(AndroidJavaObject report, bool isTerminating, AndroidJavaObject completion)
        {
            completion?.Call("run");
        }

        public void onAfterReportCreated(AndroidJavaObject report, bool isTerminating, AndroidJavaObject completion)
        {
            completion?.Call("run");
        }

        internal void SetSecureRectangles(IList<int> packedOrEmpty)
        {
            SetSecureRectangles(0, packedOrEmpty);
        }

        internal void SetSecureRectangles(int display, IList<int> packedOrEmpty)
        {
            if (packedOrEmpty == null || packedOrEmpty.Count < 2)
            {
                _secureRectsByDisplay[display] = new[] { 1, 0 };
                return;
            }

            var arr = new int[packedOrEmpty.Count];
            for (var i = 0; i < packedOrEmpty.Count; i++)
                arr[i] = packedOrEmpty[i];
            _secureRectsByDisplay[display] = arr;
        }
    }
}
#endif
