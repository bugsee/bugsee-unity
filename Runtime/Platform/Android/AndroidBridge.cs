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
    sealed class AndroidBridge : IBugseeNativeBridge, ILifecycleResultBridge
    {
        const string BugseeClass = "com.bugsee.library.Bugsee";
        const string FeedbackClass = "com.bugsee.library.contracts.extensions.Feedback";

        readonly AndroidJavaClass _bugsee = new AndroidJavaClass(BugseeClass);
        bool _wrapperContextRefined;
        ReportHandlerProxy _reportHandlerProxy;
        LifecycleListenerProxy _lifecycleProxy;
        DataRequestProviderProxy _dataRequestProxy;
        EventFilterProxy<INetworkEvent> _networkFilterProxy;
        EventFilterProxy<ILogEvent> _logFilterProxy;
        EventFilterProxy<IBreadcrumb> _breadcrumbFilterProxy;
        AndroidFeedback _feedback;
        AndroidAppearance _appearance;
        IBugseeExchangeFactory _exchangeFactory;
        AndroidManagedReport _openReport;
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

        // Launch, Relaunch, Stop and DeleteCollectedDataOnDevice all go through one state machine on the main
        // thread (AndroidLifecycleMachine: ordering, deferral behind a stop, stale results, deadlines).
        AndroidLifecycleMachine _lifecycleMachine;
        static readonly System.Diagnostics.Stopwatch LifecycleClock = System.Diagnostics.Stopwatch.StartNew();
        readonly HashSet<System.Threading.Timer> _lifecycleTimers = new HashSet<System.Threading.Timer>();

        AndroidLifecycleMachine Lifecycle =>
            _lifecycleMachine ?? (_lifecycleMachine = new AndroidLifecycleMachine(new LifecycleNative(this), () => LifecycleClock.Elapsed));

        public void Launch(string appToken, IDictionary<string, object> options) => Launch(appToken, options, null);

        // completion: true when the SDK reports a launch (or was already running), false when it refused, the
        // request was superseded, or no result came within AndroidLifecycleMachine.OperationTimeout.
        internal void Launch(string appToken, IDictionary<string, object> options, Action<bool> completion)
        {
            // All of it on the main thread: build identity reads Application.buildGUID/version and Resources, which throw
            // UnityException when Launch is called from another thread.
            MainThreadDispatcher.Run(() =>
            {
                ManagedExceptionPayload.EnsureBuildIdentity();
                ManagedExceptionPayload.WarmModuleUuid();
                EnsureWrapperRegistered();
                InstallDefaultListeners();
                ReportHandlerProxy.InvalidateBudget();
                // The SDK applies the options when it answers: drop a value cached from the old ones in between.
                Lifecycle.Launch(appToken, options, ok =>
                {
                    ReportHandlerProxy.InvalidateBudget();
                    completion?.Invoke(ok);
                });
            });
        }

        public void Relaunch(IDictionary<string, object> options) => Relaunch(options, null);

        internal void Relaunch(IDictionary<string, object> options, Action<bool> completion) =>
            MainThreadDispatcher.Run(() =>
            {
                ReportHandlerProxy.InvalidateBudget();
                Lifecycle.Relaunch(options, ok =>
                {
                    ReportHandlerProxy.InvalidateBudget();
                    completion?.Invoke(ok);
                });
            });

        void ILifecycleResultBridge.Launch(string appToken, IDictionary<string, object> options, Action<bool> started) =>
            Launch(appToken, options, started);

        void ILifecycleResultBridge.Relaunch(IDictionary<string, object> options, Action<bool> started) =>
            Relaunch(options, started);

        // finished (StopAsync): true when the SDK reported the stop, false at the machine's 10 s soft deadline. The
        // stop is queued to the main thread like every lifecycle call here, so it is never skipped.
        void ILifecycleResultBridge.Stop(Action<bool> finished) => Stop(null, finished);

        public void Stop(Action completion = null) => Stop(completion, null);

        void Stop(Action completion, Action<bool> finished)
        {
            MainThreadDispatcher.Run(() =>
            {
                AndroidManagedReportUploadFence.Invalidate();
                _openReport?.ReleaseSnapshotFiles();
                _openReport = null;
                Lifecycle.Stop(completion, finished);
            });
        }

        public bool GetLaunched()
        {
            // Read before every StartTransaction, which may run on a thread-pool thread (see AndroidJniScope).
            using (AndroidJniScope.Enter())
                return _bugsee.CallStatic<bool>("getLaunched");
        }

        public IReadOnlyDictionary<string, object> GetLaunchOptions()
        {
            using (var options = _bugsee.CallStatic<AndroidJavaObject>("getLaunchOptions"))
            {
                if (options == null) return null;
                using (var map = options.Call<AndroidJavaObject>("toMap"))
                    return map == null ? null : AndroidJavaConverters.MapToDictionary(map);
            }
        }

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
            target.IsOverride = source.IsOverride;
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
                // Nothing more goes in the map: the SDK reads only the signature from the reason JSON (the worker reads the module UUID), and
                // ignores other keys (they only end up in the report's logging options). The SDK's signature does not vary with
                // the Java call site because UnityManagedException has no Java stack, see Plugins/Android/UnityManagedException.java.
                // The SDK files the report on this thread and, for the managed view hierarchy, waits for a reply that only this
                // thread could give (Unity's main thread, now inside this call). Telling the wrapper lets it answer "none" at once
                // instead of making the SDK time out and the game stall. Only when this is Unity's thread: from any other the reply
                // can still be made.
                var onUnityThread = MainThreadDispatcher.IsMainThread;
                if (onUnityThread) ManagedViewRequests.EnterBlockingNativeCall();
                try
                {
                    if (handled)
                    {
                        _bugsee.CallStatic("logException", throwable, map);
                    }
                    else
                    {
                        _bugsee.CallStatic("logUnhandledException", throwable, map);
                    }
                }
                finally
                {
                    if (onUnityThread) ManagedViewRequests.ExitBlockingNativeCall();
                }
            }
        }

        // Not Bugsee.testCrash() directly: thrown over JNI on Unity's thread it never goes uncaught.
        public void TestCrash()
        {
            using (var wrapper = new AndroidJavaClass("com.bugsee.unity.UnityWrapper"))
            {
                wrapper.CallStatic("testCrash");
            }
        }

        public void ShowReportDialog(string summary, string description, IssueSeverity? severity, IList<string> labels)
        {
            using (var sev = OptionalSeverity(severity))
            using (var javaLabels = ToJavaStringList(labels))
            {
                _bugsee.CallStatic("showReportDialog", summary ?? "", description ?? "", sev, javaLabels);
            }
        }

        public void Upload(string summary, string description, IssueSeverity? severity, IList<string> labels)
        {
            using (var sev = OptionalSeverity(severity))
            using (var javaLabels = ToJavaStringList(labels))
            {
                _bugsee.CallStatic("upload", summary ?? "", description ?? "", sev, javaLabels);
            }
        }

        public void Notify(string title, string body, IssueSeverity severity, IDictionary<string, string> fields, bool urgent)
        {
            using (var sev = Severity(severity))
            using (var javaFields = AndroidJavaConverters.StringDictionaryToMap(fields))
            {
                // A null cannot select a JNI overload, so no fields is an empty map (and no body an empty string).
                _bugsee.CallStatic("notify", title, NotifyPolicy.Body(body), sev, javaFields, urgent);
            }
        }

        public ISpanHandle StartTransaction(string name, string operation, IDictionary<string, object> attributes)
        {
            // The three-argument overload always takes a map (a null cannot select an overload), empty when there are none.
            using (AndroidJniScope.Enter())
            using (var javaAttributes = AndroidJavaConverters.ObjectDictionaryToMap(attributes))
            {
                var transaction = _bugsee.CallStatic<AndroidJavaObject>("startTransaction", name, operation, javaAttributes);
                return transaction == null ? null : new AndroidSpanHandle(transaction);
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

            _bugsee.CallStatic("setAttribute", key, Box(AttributeWire.ToBoxable(value)));
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

        public void SetSecureBuffer(int display, int[] packed)
        {
            if (packed == null) return;
            using (var wrapper = new AndroidJavaClass("com.bugsee.unity.UnityWrapper"))
            {
                wrapper.CallStatic("setSecureBuffer", display, packed);
            }
        }

        public float GetSecureRectSnapshotScale() => 1f;

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
                    native => new AndroidNetworkEvent(native)); // the SDK never times the network filter out (7.4.1/7.4.2): no reply window
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
                    native => new AndroidLogEvent(native), FilterReplyWindow.LogFilterSdkTimeoutMs);
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

        public void NotifyLifecycle(string eventType) { }

        public void DeleteCollectedDataOnDevice() => DeleteCollectedDataOnDevice(null);

        // Stops the SDK if needed, then deletes with includingBundles=true: the two-argument overload keeps finished
        // report bundles, including ones not uploaded yet, so a "delete collected data" request would leave data
        // behind. Runs in request order with Launch/Relaunch/Stop, so a later Launch never cancels it.
        internal void DeleteCollectedDataOnDevice(Action<bool> completion)
        {
            MainThreadDispatcher.Run(() =>
            {
                AndroidManagedReportUploadFence.Invalidate();
                _openReport?.ReleaseSnapshotFiles();
                _openReport = null;
                Lifecycle.DeleteCollectedData(completion);
            });
        }

        // The native side of the lifecycle machine. Every SDK callback is moved to the main thread.
        sealed class LifecycleNative : IAndroidLifecycleNative
        {
            readonly AndroidBridge _bridge;
            public LifecycleNative(AndroidBridge bridge) { _bridge = bridge; }

            static Action<bool> OnMain(Action<bool> done) => ok => MainThreadDispatcher.Run(() => done(ok));

            public BugseeStatus Status() => _bridge.GetStatus();

            public void Launch(string appToken, IDictionary<string, object> options, Action<bool> done)
            {
                using (var activity = _bridge.CurrentActivity())
                using (var map = AndroidOptionsMapper.ToJavaMap(OptionPlatformGate.ForAndroid(options)))
                    _bridge._bugsee.CallStatic("launch", activity, appToken, map, new BooleanCallback1Proxy(OnMain(done)));
            }

            public void Relaunch(IDictionary<string, object> options, Action<bool> done)
            {
                using (var map = AndroidOptionsMapper.ToJavaMap(OptionPlatformGate.ForAndroid(options)))
                    _bridge._bugsee.CallStatic("relaunch", map, new BooleanCallback1Proxy(OnMain(done)));
            }

            public void Stop(Action done) =>
                _bridge._bugsee.CallStatic("stop", new RunnableProxy(() => MainThreadDispatcher.Run(done)));

            public void DeleteCollectedData(Action<bool> done) =>
                _bridge._bugsee.CallStatic("deleteCollectedDataOnDevice", true, true, new BooleanCallback1Proxy(OnMain(done)));

            public void InstallHooks(IDictionary<string, object> options)
            {
                ExceptionPipeline.Install(_bridge, options);
                HostLogForwarder.InstallOnce(_bridge);
            }

            public void UninstallHooks()
            {
                HostLogForwarder.Uninstall();
                ExceptionPipeline.Uninstall();
            }

            public void ScheduleTick(TimeSpan delay)
            {
                var timers = _bridge._lifecycleTimers;
                System.Threading.Timer timer = null;
                timer = new System.Threading.Timer(_ => MainThreadDispatcher.Run(() =>
                {
                    timers.Remove(timer);
                    timer.Dispose();
                    _bridge.Lifecycle.Tick();
                }), null, delay + TimeSpan.FromMilliseconds(50), System.Threading.Timeout.InfiniteTimeSpan);
                timers.Add(timer); // keeps it alive until it fires; only touched on the main thread
            }

            public void Warn(string message) => Debug.LogWarning("[Bugsee] " + message);
            public void Error(Exception exception) => Debug.LogException(exception);
        }

        public IReport CreateReport()
        {
            if (_openReport != null)
                throw new InvalidOperationException("a report is already open");
            _openReport = new AndroidManagedReport();
            return _openReport;
        }

        public void DiscardReport(IReport report)
        {
            if (report == null)
                throw new ArgumentNullException(nameof(report));
            if (!ReferenceEquals(report, _openReport))
                throw new ArgumentException("Report was not created by CreateReport.", nameof(report));
            _openReport?.ReleaseSnapshotFiles();
            _openReport = null;
        }

        public void UploadReport(IReport report)
        {
            if (report == null)
                throw new ArgumentNullException(nameof(report));
            if (!ReferenceEquals(report, _openReport))
                throw new ArgumentException("Report was not created by CreateReport.", nameof(report));
            if (_openReport == null)
                throw new InvalidOperationException("CreateReport failed.");
            var snapshot = _openReport;
            _openReport = null;
            var uploadFence = AndroidManagedReportUploadFence.Current;
            _bugsee.CallStatic(
                "createReport",
                new ReportCreationListenerProxy(snapshot, _bugsee, uploadFence));
        }

        public void AddBreadcrumb(string category, string message, string levelName)
        {
            var level = BreadcrumbLevelMap.ParseOrThrow(levelName);
            if (GetExchangeFactory() == null)
                return;

            using (var levelClass = new AndroidJavaClass(
                       "com.bugsee.library.contracts.exchange.Breadcrumb$Level"))
            using (var javaLevel = levelClass.CallStatic<AndroidJavaObject>(
                       "fromValue",
                       (sbyte)BreadcrumbLevelMap.ToAndroid(level)))
            using (var javaFactory = _bugsee.CallStatic<AndroidJavaObject>("getExchangeFactory"))
            using (var dataMap = new AndroidJavaObject("java.util.HashMap"))
            {
                if (javaFactory == null)
                    return;
                var breadcrumb = javaFactory.Call<AndroidJavaObject>(
                    "createBreadcrumb",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    category ?? "",
                    message ?? "",
                    javaLevel,
                    "manual",
                    dataMap);
                if (breadcrumb == null)
                    return;
                _bugsee.CallStatic("addBreadcrumb", breadcrumb);
            }
        }

        void InstallDefaultListeners()
        {
            if (_lifecycleProxy != null) return;
            // Lifecycle events reach C# only through the wrapper: it holds the ones fired before this point
            // (auto-launch, crash recovery), replays them in order, then forwards every later event. Also
            // registering with Bugsee.setLifecycleEventsListener would deliver events twice or lose some.
            _lifecycleProxy = new LifecycleListenerProxy();
            using (var wrapper = new AndroidJavaClass("com.bugsee.unity.UnityWrapper"))
            {
                wrapper.CallStatic("attachLifecycleListener", _lifecycleProxy);
            }

            // The wrapper answers requestData("vh") with the Unity scene once this is in place (null until then).
            _dataRequestProxy = new DataRequestProviderProxy();
            using (var wrapper = new AndroidJavaClass("com.bugsee.unity.UnityWrapper"))
            {
                wrapper.CallStatic("setManagedDataProvider", _dataRequestProxy);
            }
        }

        static AndroidJavaObject Severity(IssueSeverity severity)
        {
            using (var clazz = new AndroidJavaClass("com.bugsee.library.contracts.options.IssueSeverity"))
            {
                return clazz.CallStatic<AndroidJavaObject>("fromIntValue", (int)severity);
            }
        }

        // A null severity is the native "not given": the report takes the ReportingDefaultBugPriority option.
        // (IssueSeverity)0 is no severity, as on iOS.
        static AndroidJavaObject OptionalSeverity(IssueSeverity? severity) =>
            severity.HasValue && IssueSeverityWire.ToWire(severity) > 0 ? Severity(severity.Value) : null;

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
