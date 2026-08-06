#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using Bugsee.Contracts.Appearance;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Feedback;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using Bugsee.Contracts.Reporting;
using Bugsee.Platform;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    sealed class AndroidBridge : IBugseeNativeBridge
    {
        const string BugseeClass = "com.bugsee.library.Bugsee";
        const string FeedbackClass = "com.bugsee.library.contracts.extensions.Feedback";

        readonly AndroidJavaClass _bugsee = new AndroidJavaClass(BugseeClass);
        BugseeWrapperProxy _wrapper;
        ReportHandlerProxy _reportHandlerProxy;
        LifecycleListenerProxy _lifecycleProxy;
        AndroidFeedback _feedback;
        AndroidAppearance _appearance;

        public bool IsSupported => true;

        public IFeedback Feedback => _feedback ?? (_feedback = new AndroidFeedback(_bugsee));

        public void EnsureWrapperRegistered()
        {
            if (_wrapper != null) return;
            BugseeWrapperProxy.CacheHostContext();
            _wrapper = new BugseeWrapperProxy();
            _bugsee.CallStatic("setWrapper", _wrapper);
        }

        AndroidJavaObject CurrentActivity()
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            {
                return unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            }
        }

        public void Launch(string appToken, IDictionary<string, object> options)
        {
            EnsureWrapperRegistered();
            InstallDefaultListeners();
            using (var activity = CurrentActivity())
            using (var map = AndroidOptionsMapper.ToJavaMap(options))
            {
                _bugsee.CallStatic("launch", activity, appToken, map);
            }
        }

        public void Relaunch(IDictionary<string, object> options)
        {
            using (var map = AndroidOptionsMapper.ToJavaMap(options))
            {
                _bugsee.CallStatic("relaunch", map);
            }
        }

        public void Stop(Action completion = null)
        {
            if (completion == null)
            {
                _bugsee.CallStatic("stop");
                return;
            }

            _bugsee.CallStatic("stop", new RunnableProxy(completion));
        }

        public bool GetLaunched() => _bugsee.CallStatic<bool>("getLaunched");

        public BugseeStatus GetStatus()
        {
            using (var status = _bugsee.CallStatic<AndroidJavaObject>("getStatus"))
            {
                var name = status?.Call<string>("name");
                if (Enum.TryParse(name, out BugseeStatus parsed)) return parsed;
                return BugseeStatus.Stopped;
            }
        }

        public void StartBlackout() => _bugsee.CallStatic("startBlackout");
        public void EndBlackout() => _bugsee.CallStatic("endBlackout");
        public bool IsBlackout() => _bugsee.CallStatic<bool>("isBlackout");

        public void Log(string message, LogLevel level)
        {
            using (var clazz = new AndroidJavaClass("com.bugsee.library.contracts.options.LogLevel"))
            using (var def = clazz.CallStatic<AndroidJavaObject>("fromRawValue", (byte)LogLevel.Info))
            using (var ll = clazz.CallStatic<AndroidJavaObject>("fromRawValue", (byte)level, def))
            {
                _bugsee.CallStatic("log", message, ll);
            }
        }

        public void Trace(string name, object value)
        {
            _bugsee.CallStatic("trace", name, Box(value));
        }

        public void Event(string name, IDictionary<string, object> parameters)
        {
            if (parameters == null || parameters.Count == 0)
            {
                _bugsee.CallStatic("event", name);
                return;
            }

            using (var map = new AndroidJavaObject("java.util.HashMap"))
            {
                foreach (var kv in parameters)
                {
                    if (kv.Key == null || kv.Value == null) continue;
                    map.Call<AndroidJavaObject>("put", kv.Key, Box(kv.Value));
                }
                _bugsee.CallStatic("event", name, map);
            }
        }

        public void LogException(Exception exception, IDictionary<string, object> options = null)
        {
            if (exception == null) return;
            var message = exception.GetType().FullName + ": " + exception.Message + "\n" + exception.StackTrace;
            using (var throwable = new AndroidJavaObject("java.lang.RuntimeException", message))
            {
                if (options == null || options.Count == 0)
                {
                    _bugsee.CallStatic("logException", throwable);
                    return;
                }

                using (var map = new AndroidJavaObject("java.util.HashMap"))
                {
                    foreach (var kv in options)
                    {
                        if (kv.Key == null || kv.Value == null) continue;
                        map.Call<AndroidJavaObject>("put", kv.Key, Box(kv.Value));
                    }
                    _bugsee.CallStatic("logException", throwable, map);
                }
            }
        }

        public void TestCrash() => _bugsee.CallStatic("testCrash");

        public void ShowReportDialog(string summary, string description, IssueSeverity severity, IList<string> labels)
        {
            using (var sev = Severity(severity))
            using (var javaLabels = ToJavaStringList(labels))
            {
                _bugsee.CallStatic("showReportDialog", summary ?? "", description ?? "", sev, javaLabels);
            }
        }

        public void Upload(string summary, string description, IssueSeverity severity, IList<string> labels)
        {
            using (var sev = Severity(severity))
            using (var javaLabels = ToJavaStringList(labels))
            {
                _bugsee.CallStatic("upload", summary ?? "", description ?? "", sev, javaLabels);
            }
        }

        public void SetUserIdentifier(string userIdentifier) =>
            _bugsee.CallStatic("setUserIdentifier", userIdentifier);

        public string GetUserIdentifier() => _bugsee.CallStatic<string>("getUserIdentifier");

        public void ClearUserIdentifier() => _bugsee.CallStatic("clearUserIdentifier");

        public void SetAttribute(string key, object value) =>
            _bugsee.CallStatic("setAttribute", key, Box(value));

        public object GetAttribute(string key) => _bugsee.CallStatic<AndroidJavaObject>("getAttribute", key);

        public void ClearAttribute(string key) => _bugsee.CallStatic("clearAttribute", key);

        public void ClearAllAttributes() => _bugsee.CallStatic("clearAllAttributes");

        public void AddSecureRectangle(int left, int top, int right, int bottom)
        {
            using (var rect = new AndroidJavaObject("android.graphics.Rect", left, top, right, bottom))
            {
                _bugsee.CallStatic("addSecureRectangle", rect);
            }
        }

        public void RemoveSecureRectangle(int left, int top, int right, int bottom)
        {
            using (var rect = new AndroidJavaObject("android.graphics.Rect", left, top, right, bottom))
            {
                _bugsee.CallStatic("removeSecureRectangle", rect);
            }
        }

        public void RemoveAllSecureRectangles() => _bugsee.CallStatic("removeAllSecureRectangles");

        public void CaptureViewHierarchy() => _bugsee.CallStatic("captureViewHierarchy");

        public void ResetVideoCapturePermission() => _bugsee.CallStatic("resetVideoCapturePermission");

        public IAppearance GetAppearance()
        {
            if (_appearance != null) return _appearance;
            var native = _bugsee.CallStatic<AndroidJavaObject>("getAppearance");
            _appearance = new AndroidAppearance(native);
            return _appearance;
        }

        public void SetNetworkEventFilter(EventFilter<INetworkEvent> filter)
        {
            // Full NetworkEvent wrap comes next; for now install passthrough unless null.
            if (filter == null)
            {
                _bugsee.CallStatic("setNetworkEventFilter", (AndroidJavaObject)null);
                return;
            }

            Debug.LogWarning("[Bugsee] Network event filter is registered; detailed field mapping is still being expanded.");
            // Keep events by default until full wrapper lands.
            _bugsee.CallStatic("setNetworkEventFilter", (AndroidJavaObject)null);
        }

        public void SetLogEventFilter(EventFilter<ILogEvent> filter)
        {
            if (filter == null)
            {
                _bugsee.CallStatic("setLogEventFilter", (AndroidJavaObject)null);
                return;
            }

            Debug.LogWarning("[Bugsee] Log event filter is registered; detailed field mapping is still being expanded.");
            _bugsee.CallStatic("setLogEventFilter", (AndroidJavaObject)null);
        }

        public void SetBreadcrumbFilter(EventFilter<IBreadcrumb> filter)
        {
            if (filter == null)
            {
                _bugsee.CallStatic("setBreadcrumbFilter", (AndroidJavaObject)null);
                return;
            }

            Debug.LogWarning("[Bugsee] Breadcrumb filter is registered; detailed field mapping is still being expanded.");
            _bugsee.CallStatic("setBreadcrumbFilter", (AndroidJavaObject)null);
        }

        public void SetReportHandler(IReportHandler handler)
        {
            if (_reportHandlerProxy == null)
            {
                _reportHandlerProxy = new ReportHandlerProxy();
                _bugsee.CallStatic("setReportHandler", _reportHandlerProxy);
            }
            _reportHandlerProxy.SetHandler(handler);
        }

        public void SetLifecycleEventListener(ILifecycleEventListener listener)
        {
            // App listener is held on the Bugsee facade; native proxy always fans out there.
            InstallDefaultListeners();
        }

        void InstallDefaultListeners()
        {
            if (_lifecycleProxy == null)
            {
                _lifecycleProxy = new LifecycleListenerProxy();
                _bugsee.CallStatic("setLifecycleEventsListener", _lifecycleProxy);
            }
        }

        static AndroidJavaObject Severity(IssueSeverity severity)
        {
            using (var clazz = new AndroidJavaClass("com.bugsee.library.contracts.options.IssueSeverity"))
            {
                return clazz.CallStatic<AndroidJavaObject>("fromIntValue", (int)severity);
            }
        }

        static AndroidJavaObject ToJavaStringList(IList<string> labels)
        {
            var list = new AndroidJavaObject("java.util.ArrayList");
            if (labels == null) return list;
            foreach (var label in labels)
            {
                if (label != null) list.Call<bool>("add", label);
            }
            return list;
        }

        static AndroidJavaObject Box(object value)
        {
            if (value == null) return null;
            if (value is string s) return new AndroidJavaObject("java.lang.String", s);
            if (value is bool b) return new AndroidJavaObject("java.lang.Boolean", b);
            if (value is int i) return new AndroidJavaObject("java.lang.Integer", i);
            if (value is long l) return new AndroidJavaObject("java.lang.Long", l);
            if (value is float f) return new AndroidJavaObject("java.lang.Float", f);
            if (value is double d) return new AndroidJavaObject("java.lang.Double", d);
            return new AndroidJavaObject("java.lang.String", value.ToString());
        }
    }

    sealed class AndroidAppearance : IAppearance
    {
        readonly AndroidJavaObject _appearance;

        public AndroidAppearance(AndroidJavaObject appearance)
        {
            _appearance = appearance;
        }

        public IAppearance SetColor(string propertyName, Color32? color)
        {
            if (color == null)
            {
                _appearance.Call<AndroidJavaObject>("setColor", propertyName, (AndroidJavaObject)null);
            }
            else
            {
                var c = color.Value;
                int argb = (c.a << 24) | (c.r << 16) | (c.g << 8) | c.b;
                using (var boxed = new AndroidJavaObject("java.lang.Integer", argb))
                {
                    _appearance.Call<AndroidJavaObject>("setColor", propertyName, boxed);
                }
            }
            return this;
        }

        public Color32? GetColor(string propertyName)
        {
            using (var boxed = _appearance.Call<AndroidJavaObject>("getColor", propertyName))
            {
                if (boxed == null) return null;
                var argb = boxed.Call<int>("intValue");
                return new Color32(
                    (byte)((argb >> 16) & 0xff),
                    (byte)((argb >> 8) & 0xff),
                    (byte)(argb & 0xff),
                    (byte)((argb >> 24) & 0xff));
            }
        }

        public IAppearance SetString(string propertyName, string value)
        {
            _appearance.Call<AndroidJavaObject>("setString", propertyName, value);
            return this;
        }

        public string GetString(string propertyName) =>
            _appearance.Call<string>("getString", propertyName);

        public IReadOnlyDictionary<string, object> ToMap() => new Dictionary<string, object>();
    }

    sealed class AndroidFeedback : IFeedback
    {
        readonly AndroidJavaClass _bugsee;
        AndroidJavaObject _ext;

        public AndroidFeedback(AndroidJavaClass bugsee)
        {
            _bugsee = bugsee;
        }

        AndroidJavaObject Ext()
        {
            if (_ext != null) return _ext;
            using (var feedbackClass = new AndroidJavaClass("com.bugsee.library.contracts.extensions.Feedback"))
            {
                // Same pattern as legacy Unity / Flutter: pass AndroidJavaClass as Class<T>.
                _ext = _bugsee.CallStatic<AndroidJavaObject>("ext", feedbackClass);
            }
            return _ext;
        }

        public void ShowFeedbackUi()
        {
            try { Ext()?.Call("showFeedbackActivity"); }
            catch (Exception ex) { Debug.LogWarning("[Bugsee] Feedback extension unavailable: " + ex.Message); }
        }

        public void SetDefaultGreeting(string greeting)
        {
            try { Ext()?.Call("setDefaultFeedbackGreeting", greeting); }
            catch (Exception ex) { Debug.LogWarning("[Bugsee] Feedback extension unavailable: " + ex.Message); }
        }

        public void SetListener(IFeedbackListener listener)
        {
            try
            {
                if (listener == null)
                {
                    Ext()?.Call("setOnNewFeedbackListener", (AndroidJavaObject)null);
                    return;
                }
                Ext()?.Call("setOnNewFeedbackListener", new FeedbackListenerProxy(listener));
            }
            catch (Exception ex) { Debug.LogWarning("[Bugsee] Feedback extension unavailable: " + ex.Message); }
        }
    }

    sealed class FeedbackListenerProxy : AndroidJavaProxy
    {
        readonly IFeedbackListener _listener;

        public FeedbackListenerProxy(IFeedbackListener listener)
            : base("com.bugsee.library.contracts.feedback.FeedbackListener")
        {
            _listener = listener;
        }

        public void onNewMessagesReceived(AndroidJavaObject newMessages)
        {
            var list = new List<string>();
            if (newMessages != null)
            {
                var size = newMessages.Call<int>("size");
                for (var i = 0; i < size; i++)
                    list.Add(newMessages.Call<string>("get", i));
            }
            Bugsee.Internal.MainThreadDispatcher.Run(() => _listener.OnNewMessagesReceived(list));
        }

        public void onNewMessageSent(string message)
        {
            Bugsee.Internal.MainThreadDispatcher.Run(() => _listener.OnNewMessageSent(message));
        }
    }
}
#endif
