using System;
using System.Threading;
using System.Threading.Tasks;
using Bugsee.Contracts.Options;
using Bugsee.Platform;
using UnityEngine;
using EchoOutcome = Bugsee.WrapperPolicy.LogExceptionEcho.Outcome;

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
                if (GetBoolOption(launchOptions, Options.CaptureUnobservedTaskExceptions, true))
                {
                    TaskScheduler.UnobservedTaskException += _instance.OnUnobservedTaskException;
                }
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
            var outcome = Report(exception, handled: !fatal, source: "LogException", syncFlush: fatal);
            // Unity echoes this exception into logMessageReceivedThreaded as text, on this thread, from inside the
            // next call. Expect it there so the callback can drop the second copy when this path delivered it, or
            // skipped it as a duplicate (the first call delivered it). If Report skipped a nested call or the send
            // failed or timed out, the echo is the only report left.
            if (!global::Bugsee.WrapperPolicy.LogExceptionEcho.ShouldExpect(outcome))
            {
                _inner.LogException(exception, context);
                return;
            }

            using (global::Bugsee.WrapperPolicy.LogExceptionEcho.Expect(exception))
            {
                _inner.LogException(exception, context);
            }
        }

        void OnLogMessageReceivedThreaded(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception)
            {
                return;
            }

            // ILogHandler.LogException already reported the Debug.LogException path; this is its echo.
            if (global::Bugsee.WrapperPolicy.LogExceptionEcho.IsEcho(condition))
            {
                return;
            }

            // Unity-routed uncaught exceptions that hit this callback more than once.
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

        // Per thread: a counter shared by all threads made a Debug.LogException on thread B skip Report while
        // thread A was inside it, and (with the echo below) the exception was then never reported at all.
        [ThreadStatic]
        static int _reportDepth;

        /// <summary>
        /// Delivered when <see cref="DispatchPayload"/> delivered it; Duplicate when the same exception (type,
        /// message and stack of the whole chain) was just reported; NotDelivered when it was skipped as a nested call, or the send failed or timed out.
        /// </summary>
        EchoOutcome Report(Exception exception, bool handled, string source, bool syncFlush = false)
        {
            if (exception == null)
            {
                return EchoOutcome.NotDelivered;
            }

            if (_reportDepth > 0)
            {
                return EchoOutcome.NotDelivered;
            }

            _reportDepth++;
            try
            {
                if (IsDuplicate(global::Bugsee.WrapperPolicy.LogExceptionEcho.DuplicateKey(exception, source)))
                {
                    return EchoOutcome.Duplicate;
                }

                var payload = ManagedExceptionPayload.FromException(exception);
                return DispatchPayload(payload, handled, syncFlush || !handled)
                    ? EchoOutcome.Delivered
                    : EchoOutcome.NotDelivered;
            }
            finally
            {
                _reportDepth--;
            }
        }

        /// <summary>
        /// True when the payload was sent, or queued for the main thread on the non-blocking path (where the echo
        /// would only be queued behind it). False when there was nothing to send, the send threw, or a sync flush
        /// timed out.
        /// </summary>
        bool DispatchPayload(ManagedExceptionPayload.Payload payload, bool handled, bool syncFlush)
        {
            if (payload == null || _bridge == null)
            {
                return false;
            }

            bool Send()
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
                    return true;
                }
                catch (Exception ex)
                {
                    // Never re-enter pipeline via Debug.LogException.
                    try { _inner.LogFormat(LogType.Warning, null, "Bugsee ExceptionPipeline: {0}", ex.Message); }
                    catch { /* ignore */ }
                    return false;
                }
            }

            if (MainThreadDispatcher.IsMainThread)
            {
                return Send();
            }

            if (syncFlush)
            {
                // On a timeout the queued Send may still run later; reporting twice beats losing the crash.
                var sent = false;
                var completed = MainThreadDispatcher.RunSync(() => { sent = Send(); }, 4000);
                return completed && sent;
            }

            MainThreadDispatcher.Run(() => Send());
            return true;
        }

        // Time.realtimeSinceStartup throws off the main thread, and log callbacks arrive on any thread.
        static readonly System.Diagnostics.Stopwatch DedupeClock = System.Diagnostics.Stopwatch.StartNew();

        bool IsDuplicate(string key)
        {
            var now = DedupeClock.Elapsed.TotalSeconds;
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
