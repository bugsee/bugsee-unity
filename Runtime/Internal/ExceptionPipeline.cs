using System;
using System.Threading;
using System.Threading.Tasks;
using Bugsee.Contracts.Options;
using Bugsee.Platform;
using UnityEngine;

namespace Bugsee.Internal
{
    /// <summary>
    /// Unity-layer managed exception capture. Exclusive ownership of Unity log
    /// exceptions via <see cref="ILogHandler"/> (primary) with deduped
    /// <c>logMessageReceivedThreaded</c> fallback, plus AppDomain / UnobservedTask.
    /// Separate from native <c>DetectAndReportCrash</c>.
    /// </summary>
    public sealed class ExceptionPipeline : ILogHandler
    {
        const string CaptureOptionKey = Options.CaptureManagedExceptions;

        static ExceptionPipeline _instance;
        static readonly object Gate = new object();

        readonly IBugseeNativeBridge _bridge;
        readonly ILogHandler _inner;
        readonly bool _treatUnityLogExceptionAsFatal;
        int _reentrancy;
        string _lastDedupeKey;
        double _lastDedupeTime;

        ExceptionPipeline(IBugseeNativeBridge bridge, ILogHandler inner, bool treatUnityLogExceptionAsFatal)
        {
            _bridge = bridge;
            _inner = inner ?? Debug.unityLogger.logHandler;
            _treatUnityLogExceptionAsFatal = treatUnityLogExceptionAsFatal;
        }

        /// <summary>
        /// Install pipeline after native Launch. No-op when capture is disabled
        /// via launch option <see cref="Options.CaptureManagedExceptions"/> = false.
        /// Default when key omitted: enabled.
        /// </summary>
        public static void Install(IBugseeNativeBridge bridge, System.Collections.Generic.IDictionary<string, object> launchOptions)
        {
            if (bridge == null || !bridge.IsSupported)
            {
                return;
            }

            if (!IsCaptureEnabled(launchOptions))
            {
                Uninstall();
                return;
            }

            lock (Gate)
            {
                if (_instance != null)
                {
                    return;
                }

                var treatFatal = GetBoolOption(launchOptions, Options.TreatUnityLogExceptionsAsFatal, false);
                var inner = Debug.unityLogger.logHandler;
                _instance = new ExceptionPipeline(bridge, inner, treatFatal);
                Debug.unityLogger.logHandler = _instance;

                Application.logMessageReceivedThreaded += _instance.OnLogMessageReceivedThreaded;
                AppDomain.CurrentDomain.UnhandledException += _instance.OnUnhandledException;
#if !NET_LEGACY
                TaskScheduler.UnobservedTaskException += _instance.OnUnobservedTaskException;
#endif
            }
        }

        public static void Uninstall()
        {
            lock (Gate)
            {
                if (_instance == null)
                {
                    return;
                }

                if (Debug.unityLogger.logHandler == _instance)
                {
                    Debug.unityLogger.logHandler = _instance._inner;
                }

                Application.logMessageReceivedThreaded -= _instance.OnLogMessageReceivedThreaded;
                AppDomain.CurrentDomain.UnhandledException -= _instance.OnUnhandledException;
#if !NET_LEGACY
                TaskScheduler.UnobservedTaskException -= _instance.OnUnobservedTaskException;
#endif
                _instance = null;
            }
        }

        static bool IsCaptureEnabled(System.Collections.Generic.IDictionary<string, object> options)
        {
            if (options == null || !options.ContainsKey(CaptureOptionKey))
            {
                return true;
            }
            return GetBoolOption(options, CaptureOptionKey, true);
        }

        static bool GetBoolOption(System.Collections.Generic.IDictionary<string, object> options, string key, bool fallback)
        {
            if (options == null || !options.TryGetValue(key, out var raw) || raw == null)
            {
                return fallback;
            }
            if (raw is bool b)
            {
                return b;
            }
            if (bool.TryParse(raw.ToString(), out var parsed))
            {
                return parsed;
            }
            return fallback;
        }

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            _inner.LogFormat(logType, context, format, args);
        }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            // Unity routes both Debug.LogException and many uncaught main-thread
            // exceptions here. Default non-fatal; opt into fatal via launch option.
            var fatal = _treatUnityLogExceptionAsFatal;
            Report(exception, handled: !fatal, source: "LogException", syncFlush: fatal);
            _inner.LogException(exception, context);
        }

        void OnLogMessageReceivedThreaded(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception)
            {
                return;
            }

            // ILogHandler.LogException already reported Debug.LogException path —
            // dedupe Unity-routed uncaught exceptions that also hit this callback.
            var key = (condition ?? "") + "\n" + (stackTrace ?? "");
            if (IsDuplicate(key))
            {
                return;
            }

            var payload = ManagedExceptionPayload.FromUnityLog(condition, stackTrace);
            var fatal = _treatUnityLogExceptionAsFatal;
            DispatchPayload(payload, handled: !fatal, syncFlush: fatal);
        }

        void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e?.ExceptionObject as Exception;
            if (ex == null)
            {
                return;
            }
            // IL2CPP may continue after this; still treat as fatal for reporting.
            Report(ex, handled: false, source: "AppDomain", syncFlush: true);
        }

#if !NET_LEGACY
        void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e?.Exception == null)
            {
                return;
            }
            Report(e.Exception, handled: true, source: "UnobservedTask");
        }
#endif

        void Report(Exception exception, bool handled, string source, bool syncFlush = false)
        {
            if (exception == null)
            {
                return;
            }

            if (Interlocked.Increment(ref _reentrancy) > 1)
            {
                Interlocked.Decrement(ref _reentrancy);
                return;
            }

            try
            {
                var key = exception.GetType().FullName + "\n" + (exception.StackTrace ?? "") + "\n" + source;
                if (IsDuplicate(key))
                {
                    return;
                }

                var payload = ManagedExceptionPayload.FromException(exception);
                DispatchPayload(payload, handled, syncFlush || !handled);
            }
            finally
            {
                Interlocked.Decrement(ref _reentrancy);
            }
        }

        void DispatchPayload(ManagedExceptionPayload.Payload payload, bool handled, bool syncFlush)
        {
            if (payload == null || _bridge == null)
            {
                return;
            }

            void Send()
            {
                try
                {
                    if (handled)
                    {
                        _bridge.LogExceptionPayload(payload, null);
                    }
                    else
                    {
                        _bridge.LogUnhandledExceptionPayload(payload, null);
                    }
                }
                catch (Exception ex)
                {
                    // Never re-enter pipeline via Debug.LogException.
                    try { _inner.LogFormat(LogType.Warning, null, "Bugsee ExceptionPipeline: {0}", ex.Message); }
                    catch { /* ignore */ }
                }
            }

            if (MainThreadDispatcher.IsMainThread)
            {
                Send();
                return;
            }

            if (syncFlush)
            {
                MainThreadDispatcher.RunSync(Send, 4000);
            }
            else
            {
                MainThreadDispatcher.Run(Send);
            }
        }

        bool IsDuplicate(string key)
        {
            var now = (double)Time.realtimeSinceStartup;
            lock (Gate)
            {
                if (_lastDedupeKey == key && (now - _lastDedupeTime) < 0.75)
                {
                    return true;
                }
                _lastDedupeKey = key;
                _lastDedupeTime = now;
                return false;
            }
        }
    }
}
