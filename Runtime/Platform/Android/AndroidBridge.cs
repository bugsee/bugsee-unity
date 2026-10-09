#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using Bugsee.Contracts.Appearance;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Feedback;
using Bugsee.Contracts.Lifecycle;
using Bugsee.Contracts.Options;
using Bugsee.WrapperPolicy;
using Bugsee.Contracts.Reporting;
using Bugsee;
using Bugsee.Internal;
using Bugsee.Platform;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    sealed class AndroidBridge : IBugseeNativeBridge
    {
        const string BugseeClass = "com.bugsee.library.Bugsee";
        const string FeedbackClass = "com.bugsee.library.contracts.extensions.Feedback";

        readonly AndroidJavaClass _bugsee = new AndroidJavaClass(BugseeClass);
        bool _wrapperContextRefined;
        ReportHandlerProxy _reportHandlerProxy;
        LifecycleListenerProxy _lifecycleProxy;
        EventFilterProxy<INetworkEvent> _networkFilterProxy;
        EventFilterProxy<ILogEvent> _logFilterProxy;
        EventFilterProxy<IBreadcrumb> _breadcrumbFilterProxy;
        AndroidFeedback _feedback;
        AndroidAppearance _appearance;
        IBugseeExchangeFactory _exchangeFactory;
        static bool _loggedMissingNetworkFactory;

        public bool IsSupported => true;

        public IFeedback Feedback => _feedback ?? (_feedback = new AndroidFeedback(_bugsee));

        public void EnsureWrapperRegistered()
        {
            if (_wrapperContextRefined) return;
            _wrapperContextRefined = true;
#if ENABLE_IL2CPP
            const string scriptingBackend = "il2cpp";
#else
            const string scriptingBackend = "mono";
#endif
            using (var wrapper = new AndroidJavaClass("com.bugsee.unity.UnityWrapper"))
            {
                wrapper.CallStatic(
                    "refineContext",
                    Application.unityVersion ?? "unknown",
                    Application.platform.ToString(),
                    scriptingBackend,
                    Application.productName ?? "unknown",
                    BugseePackageVersion.Version);
            }
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
            ManagedExceptionPayload.EnsureBuildIdentity();
            EnsureWrapperRegistered();
            InstallDefaultListeners();
            using (var activity = CurrentActivity())
            using (var map = AndroidOptionsMapper.ToJavaMap(options))
            {
                _bugsee.CallStatic("launch", activity, appToken, map);
            }
            ExceptionPipeline.Install(this, options);
            HostLogForwarder.InstallOnce(this);
        }

        public void Relaunch(IDictionary<string, object> options)
        {
            using (var map = AndroidOptionsMapper.ToJavaMap(options))
            {
                _bugsee.CallStatic("relaunch", map);
            }
            ExceptionPipeline.Install(this, options);
            HostLogForwarder.InstallOnce(this);
        }

        public void Stop(Action completion = null)
        {
            HostLogForwarder.Uninstall();
            ExceptionPipeline.Uninstall();
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

        public void ChannelLog(string message, LogLevel level)
        {
            if (string.IsNullOrEmpty(message)) return;
            int source = WrapperLogSourcePolicy.Resolve(null);
            using (var wrapper = new AndroidJavaClass("com.bugsee.unity.UnityWrapper"))
            {
                wrapper.CallStatic("channelLog", message, (int)level, source);
            }
        }

        public IBugseeExchangeFactory GetExchangeFactory()
        {
            if (_exchangeFactory != null) return _exchangeFactory;
            var javaFactory = _bugsee.CallStatic<AndroidJavaObject>("getExchangeFactory");
            if (javaFactory == null) return null;
            _exchangeFactory = new AndroidExchangeFactory(javaFactory);
            return _exchangeFactory;
        }

        public void AddNetworkEvent(INetworkEvent networkEvent)
        {
            if (networkEvent == null) return;
            var androidEvent = networkEvent as AndroidNetworkEvent;
            if (androidEvent == null)
            {
                var factory = GetExchangeFactory();
                if (factory == null)
                {
                    if (!_loggedMissingNetworkFactory)
                    {
                        _loggedMissingNetworkFactory = true;
                        Debug.LogWarning("[Bugsee] network-factory-missing");
                    }
                    return;
                }

                androidEvent = (AndroidNetworkEvent)factory.CreateNetworkEvent(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    networkEvent.Stage,
                    string.IsNullOrEmpty(networkEvent.Id) ? null : networkEvent.Id,
                    networkEvent.Mechanism,
                    networkEvent.Method);
                if (androidEvent == null) return;
                CopyNetworkFields(networkEvent, androidEvent);
            }

            using (var wrapper = new AndroidJavaClass("com.bugsee.unity.UnityWrapper"))
            {
                wrapper.CallStatic("channelAddNetwork", androidEvent.Native);
            }
        }

        static void CopyNetworkFields(INetworkEvent source, INetworkEvent target)
        {
            if (source == null || target == null || ReferenceEquals(source, target)) return;
            target.Url = source.Url;
            target.Body = source.Body;
            target.Size = source.Size;
            target.ResponseCode = source.ResponseCode;
            target.StatusText = source.StatusText;
            target.ErrorShortMessage = source.ErrorShortMessage;
            target.ErrorDescription = source.ErrorDescription;
            target.Headers = source.Headers;
        }

        public void Log(string message, LogLevel level)
        {
            using (var clazz = new AndroidJavaClass("com.bugsee.library.contracts.options.LogLevel"))
            {
                var def = clazz.CallStatic<AndroidJavaObject>("fromRawValue", (sbyte)LogLevel.Info);
                var ll = clazz.CallStatic<AndroidJavaObject>("fromRawValue", (sbyte)level, def);
                try
                {
                    _bugsee.CallStatic("log", message, ll);
                }
                finally
                {
                    if (def != null && ll != null && def.GetRawObject() != ll.GetRawObject())
                        def.Dispose();
                    else if (def != null && ll == null)
                        def.Dispose();
                    ll?.Dispose();
                }
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
            LogExceptionPayload(ManagedExceptionPayload.FromException(exception), options);
        }

        public void LogUnhandledException(Exception exception, IDictionary<string, object> options = null)
        {
            if (exception == null) return;
            LogUnhandledExceptionPayload(ManagedExceptionPayload.FromException(exception), options);
        }

        public void LogExceptionPayload(
            ManagedExceptionPayload.Payload payload,
            IDictionary<string, object> options = null)
        {
            SendManagedPayload(payload, handled: true, options);
        }

        public void LogUnhandledExceptionPayload(
            ManagedExceptionPayload.Payload payload,
            IDictionary<string, object> options = null)
        {
            SendManagedPayload(payload, handled: false, options);
        }

        void SendManagedPayload(
            ManagedExceptionPayload.Payload payload,
            bool handled,
            IDictionary<string, object> options)
        {
            if (payload == null) return;

            var reasonJson = ManagedExceptionPayload.ToJson(payload);
            // Class name contains UnityManagedException → worker unity.py routing.
            using (var throwable = new AndroidJavaObject("com.bugsee.unity.UnityManagedException", reasonJson))
            using (var map = new AndroidJavaObject("java.util.HashMap"))
            {
                if (options != null)
                {
                    foreach (var kv in options)
                    {
                        if (kv.Key == null || kv.Value == null) continue;
                        map.Call<AndroidJavaObject>("put", kv.Key, Box(kv.Value));
                    }
                }
                map.Call<AndroidJavaObject>("put", "domain", "UnityManagedException");
                if (!string.IsNullOrEmpty(payload.signature))
                {
                    map.Call<AndroidJavaObject>("put", "$$signature", payload.signature);
                }
                if (!string.IsNullOrEmpty(payload.moduleUUID))
                {
                    map.Call<AndroidJavaObject>("put", "moduleUUID", payload.moduleUUID);
                }
                if (handled)
                {
                    _bugsee.CallStatic("logException", throwable, map);
                }
                else
                {
                    _bugsee.CallStatic("logUnhandledException", throwable, map);
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

        public void SetUserIdentifier(string userIdentifier)
        {
            var normalized = UserIdentifierPolicy.ForSet(userIdentifier);
            if (normalized == null)
                ClearUserIdentifier();
            else
                _bugsee.CallStatic("setUserIdentifier", normalized);
        }

        public string GetUserIdentifier() =>
            UserIdentifierPolicy.ForGet(_bugsee.CallStatic<string>("getUserIdentifier"));

        public void ClearUserIdentifier() => _bugsee.CallStatic("clearUserIdentifier");

        public void SetAttribute(string key, object value)
        {
            var decision = AttributePolicy.Evaluate(key, value);
            if (!decision.Accepted)
                throw new ArgumentException(decision.Error, nameof(value));

            _bugsee.CallStatic("setAttribute", key, Box(value));
            if (GetAttribute(key) == null)
                throw new ArgumentException("attribute '" + key + "' was dropped", nameof(value));
        }

        public object GetAttribute(string key)
        {
            using (var value = _bugsee.CallStatic<AndroidJavaObject>("getAttribute", key))
                return AndroidJavaConverters.Unbox(value);
        }

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
            if (filter == null)
            {
                _networkFilterProxy?.SetFilter(null);
                _bugsee.CallStatic("setNetworkEventFilter", (AndroidJavaObject)null);
                _networkFilterProxy = null;
                return;
            }

            if (_networkFilterProxy == null)
            {
                _networkFilterProxy = new EventFilterProxy<INetworkEvent>(
                    native => new AndroidNetworkEvent(native));
                _bugsee.CallStatic("setNetworkEventFilter", _networkFilterProxy);
            }
            _networkFilterProxy.SetFilter(filter);
        }

        public void SetLogEventFilter(EventFilter<ILogEvent> filter)
        {
            if (filter == null)
            {
                _logFilterProxy?.SetFilter(null);
                _bugsee.CallStatic("setLogEventFilter", (AndroidJavaObject)null);
                _logFilterProxy = null;
                return;
            }

            if (_logFilterProxy == null)
            {
                _logFilterProxy = new EventFilterProxy<ILogEvent>(
                    native => new AndroidLogEvent(native));
                _bugsee.CallStatic("setLogEventFilter", _logFilterProxy);
            }
            _logFilterProxy.SetFilter(filter);
        }

        public void SetBreadcrumbFilter(EventFilter<IBreadcrumb> filter)
        {
            if (filter == null)
            {
                _breadcrumbFilterProxy?.SetFilter(null);
                _bugsee.CallStatic("setBreadcrumbFilter", (AndroidJavaObject)null);
                _breadcrumbFilterProxy = null;
                return;
            }

            if (_breadcrumbFilterProxy == null)
            {
                _breadcrumbFilterProxy = new EventFilterProxy<IBreadcrumb>(
                    native => new AndroidBreadcrumb(native));
                _bugsee.CallStatic("setBreadcrumbFilter", _breadcrumbFilterProxy);
            }
            _breadcrumbFilterProxy.SetFilter(filter);
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

        static AndroidJavaObject Box(object value) => AndroidJavaConverters.Box(value);
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

        public IReadOnlyDictionary<string, object> ToMap()
        {
            using (var map = _appearance.Call<AndroidJavaObject>("toMap"))
                return AndroidJavaConverters.MapToDictionary(map);
        }
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
            // Do not qualify as Bugsee.Internal.* — Bugsee resolves to class Bugsee.Bugsee here.
            MainThreadDispatcher.Run(() => _listener.OnNewMessagesReceived(list));
        }

        public void onNewMessageSent(string message)
        {
            MainThreadDispatcher.Run(() => _listener.OnNewMessageSent(message));
        }
    }
}
#endif
