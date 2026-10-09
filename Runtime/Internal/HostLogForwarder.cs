using Bugsee.Contracts.Options;
using Bugsee.Platform;
using Bugsee.WrapperPolicy;
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

        [ThreadStatic]
        static int _forwardDepth;

        public static void InstallOnce(IBugseeNativeBridge bridge)
        {
            if (_installed || bridge == null || !bridge.IsSupported)
            {
                return;
            }

            _installed = true;
            Application.logMessageReceivedThreaded += (message, stackTrace, type) =>
            {
                ForwardLog(bridge, message, type);
            };
        }

        static void ForwardLog(IBugseeNativeBridge bridge, string message, LogType type)
        {
            if (type == LogType.Exception || string.IsNullOrEmpty(message))
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
                bridge.ChannelLog(message, LevelFor(type));
            }
            finally
            {
                _forwardDepth--;
            }
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
