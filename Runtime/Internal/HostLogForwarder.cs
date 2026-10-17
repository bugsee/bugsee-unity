using System;
using Bugsee.Contracts.Options;
using Bugsee.Platform;
using UnityEngine;

namespace Bugsee.Internal
{
    /// <summary>
    /// Sends Unity <c>Debug</c> output through the wrapper channel as Custom.
    /// Public <c>Bugsee.Log</c> stays on the SDK log API.
    /// </summary>
    public static class HostLogForwarder
    {
        static bool _installed;
        static IBugseeNativeBridge _bridge;
        static Application.LogCallback _logHandler;
        static int _generation;

        [ThreadStatic]
        static int _forwardDepth;

        public static void InstallOnce(IBugseeNativeBridge bridge)
        {
            if (_installed || bridge == null || !bridge.IsSupported)
            {
                return;
            }

            _installed = true;
            _bridge = bridge;
            _generation++;
            _logHandler = (message, stackTrace, type) => ForwardLog(_bridge, message, stackTrace, type);
            Application.logMessageReceivedThreaded += _logHandler;
        }

        public static void Uninstall()
        {
            if (!_installed)
            {
                return;
            }

            _generation++;

            if (_logHandler != null)
            {
                Application.logMessageReceivedThreaded -= _logHandler;
                _logHandler = null;
            }

            _bridge = null;
            _installed = false;
        }

        static void ForwardLog(IBugseeNativeBridge bridge, string message, string stackTrace, LogType type)
        {
            if (type == LogType.Exception || string.IsNullOrEmpty(message))
            {
                return;
            }

            var payload = message;
            if (!string.IsNullOrEmpty(stackTrace))
            {
                payload = message + "\n" + stackTrace;
            }

            var generation = _generation;

            void Send()
            {
                if (generation != _generation)
                {
                    return;
                }

                if (_forwardDepth > 0)
                {
                    return;
                }

                _forwardDepth++;
                try
                {
                    bridge.ChannelLog(payload, LevelFor(type));
                }
                catch (Exception ex)
                {
                    try { Debug.LogWarning($"Bugsee HostLogForwarder: {ex.Message}"); }
                    catch { /* ignore */ }
                }
                finally
                {
                    _forwardDepth--;
                }
            }

            if (MainThreadDispatcher.IsMainThread)
            {
                Send();
                return;
            }

            MainThreadDispatcher.Run(Send);
        }

        static LogLevel LevelFor(LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Assert:
                    return LogLevel.Error;
                case LogType.Warning:
                    return LogLevel.Warning;
                default:
                    return LogLevel.Info;
            }
        }
    }
}
