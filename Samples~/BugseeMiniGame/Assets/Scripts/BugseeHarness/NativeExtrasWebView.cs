using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Makes the native views <see cref="NativeExtrasE2E"/> hands to <c>Bugsee.AddSecureWebView</c>: a real android.webkit.WebView
    /// (AndroidJavaObject) or WKWebView (IntPtr, from Plugins/iOS/BugseeSampleNativeViews.mm), and a view that is not a WebView.
    /// </summary>
    static class NativeExtrasWebView
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern IntPtr _sample_create_native_view(int webView);
        [DllImport("__Internal")] static extern void _sample_destroy_native_view(IntPtr view);
#endif

        public static object Create() => CreateView(true);

        public static object CreateNotAWebView() => CreateView(false);

        static object CreateView(bool webView)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJavaObject view = null;
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                var done = new ManualResetEvent(false);
                activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
                {
                    try
                    {
                        view = webView
                            ? new AndroidJavaObject("android.webkit.WebView", activity)
                            : new AndroidJavaObject("android.view.View", activity);
                        using (var lp = new AndroidJavaObject("android.widget.FrameLayout$LayoutParams", 120, 120))
                            activity.Call("addContentView", view, lp);
                    }
                    catch (Exception ex) { Debug.Log("[Sample] extras native view failed: " + ex); }
                    finally { done.Set(); }
                }));
                done.WaitOne(5000);
            }
            return view;
#elif UNITY_IOS && !UNITY_EDITOR
            var ptr = _sample_create_native_view(webView ? 1 : 0);
            return ptr == IntPtr.Zero ? null : (object)ptr;
#else
            return null;
#endif
        }

        public static void Destroy(object view)
        {
            if (view == null) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            var native = view as AndroidJavaObject;
            if (native == null) return;
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                var done = new ManualResetEvent(false);
                activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
                {
                    try
                    {
                        using (var parent = native.Call<AndroidJavaObject>("getParent"))
                            if (parent != null) parent.Call("removeView", native);
                    }
                    catch (Exception ex) { Debug.Log("[Sample] extras native view removal failed: " + ex.Message); }
                    finally { done.Set(); }
                }));
                done.WaitOne(5000);
            }
#elif UNITY_IOS && !UNITY_EDITOR
            if (view is IntPtr ptr) _sample_destroy_native_view(ptr);
#endif
        }

        public static string Describe(object view)
        {
            if (view == null) return "no native view on this platform";
#if UNITY_ANDROID && !UNITY_EDITOR
            var native = view as AndroidJavaObject;
            return native == null ? view.GetType().Name : native.Call<string>("toString");
#else
            return view.ToString();
#endif
        }
    }
}
