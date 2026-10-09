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

        public static void InstallOnce(IBugseeNativeBridge bridge)
        {
            if (_installed || bridge == null || !bridge.IsSupported)
            {
                return;
            }

            _installed = true;
            Application.logMessageReceived += (message, stackTrace, type) =>
            {
                if (type == LogType.Exception || string.IsNullOrEmpty(message))
                {
                    return;
                }

                bridge.ChannelLog(message, LevelFor(type));
            };
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
