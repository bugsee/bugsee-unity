#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using Bugsee.Contracts.Exchange;
using Bugsee.Contracts.Options;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    /// <summary>
    /// Thin wrappers over Java exchange events. Mutate in place so filter
    /// callbacks write through to the native instance the SDK owns.
    /// </summary>
    sealed class AndroidNetworkEvent : INetworkEvent
    {
        readonly AndroidJavaObject _native;

        public AndroidNetworkEvent(AndroidJavaObject native)
        {
            _native = native;
        }

        public string Id => _native.Call<string>("getId");
        public string Mechanism => _native.Call<string>("getMechanism");
        public string Method => _native.Call<string>("getMethod");

        public string Url
        {
            get => _native.Call<string>("getUrl");
            set => _native.Call("setUrl", value);
        }

        public string Body
        {
            get => _native.Call<string>("getBody");
            set => _native.Call("setBody", value);
        }

        public long Size
        {
            get => _native.Call<long>("getSize");
            set => _native.Call("setSize", value);
        }

        public int ResponseCode
        {
            get => _native.Call<int>("getResponseCode");
            set => _native.Call("setResponseCode", value);
        }

        public string StatusText
        {
            get => _native.Call<string>("getStatusText");
            set => _native.Call("setStatusText", value);
        }

        public string ErrorShortMessage
        {
            get => _native.Call<string>("getErrorShortMessage");
            set => _native.Call("setErrorShortMessage", value);
        }

        public string ErrorDescription
        {
            get => _native.Call<string>("getErrorDescription");
            set => _native.Call("setErrorDescription", value);
        }

        public IDictionary<string, string> Headers
        {
            get
            {
                using (var map = _native.Call<AndroidJavaObject>("getHeaders"))
                    return AndroidJavaConverters.MapToStringDictionary(map);
            }
            set
            {
                using (var map = AndroidJavaConverters.StringDictionaryToMap(value))
                    _native.Call("setHeaders", map);
            }
        }

        public NetworkEventStage Stage
        {
            get
            {
                using (var stage = _native.Call<AndroidJavaObject>("getNetworkEventType"))
                {
                    var name = stage?.Call<string>("name");
                    if (Enum.TryParse(name, out NetworkEventStage parsed))
                        return parsed;
                    return NetworkEventStage.RequestCompleted;
                }
            }
        }
    }

    sealed class AndroidLogEvent : ILogEvent
    {
        readonly AndroidJavaObject _native;

        public AndroidLogEvent(AndroidJavaObject native)
        {
            _native = native;
        }

        public string Message
        {
            get => _native.Call<string>("getMessage");
            set => _native.Call("setMessage", value);
        }

        public LogLevel Level
        {
            get
            {
                using (var level = _native.Call<AndroidJavaObject>("getLevel"))
                {
                    var raw = level?.Call<sbyte>("getValue") ?? (sbyte)LogLevel.Info;
                    return LogLevelExtensions.FromRawValue(raw);
                }
            }
            set
            {
                using (var clazz = new AndroidJavaClass("com.bugsee.library.contracts.options.LogLevel"))
                {
                    var def = clazz.CallStatic<AndroidJavaObject>("fromRawValue", (sbyte)LogLevel.Info);
                    var javaLevel = clazz.CallStatic<AndroidJavaObject>("fromRawValue", (sbyte)value, def);
                    try
                    {
                        _native.Call("setLevel", javaLevel);
                    }
                    finally
                    {
                        if (def != null && javaLevel != null
                            && def.GetRawObject() != javaLevel.GetRawObject())
                            def.Dispose();
                        else if (def != null && javaLevel == null)
                            def.Dispose();
                        javaLevel?.Dispose();
                    }
                }
            }
        }
    }

    sealed class AndroidBreadcrumb : IBreadcrumb
    {
        readonly AndroidJavaObject _native;

        public AndroidBreadcrumb(AndroidJavaObject native)
        {
            _native = native;
        }

        public long Timestamp
        {
            get => _native.Call<long>("getTimestamp");
            set => _native.Call("setTimestamp", value);
        }

        public string Category
        {
            get => _native.Call<string>("getCategory");
            set => _native.Call("setCategory", value);
        }

        public string Message
        {
            get => _native.Call<string>("getMessage");
            set => _native.Call("setMessage", value);
        }

        public string Type
        {
            get => _native.Call<string>("getType");
            set => _native.Call("setType", value);
        }

        public LogLevel? Level
        {
            get
            {
                using (var level = _native.Call<AndroidJavaObject>("getLevel"))
                {
                    if (level == null) return null;
                    var name = level.Call<string>("name");
                    switch (name)
                    {
                        case "DEBUG": return LogLevel.Debug;
                        case "INFO": return LogLevel.Info;
                        case "WARNING": return LogLevel.Warning;
                        case "ERROR":
                        case "FATAL": return LogLevel.Error;
                        default: return null;
                    }
                }
            }
            set
            {
                if (value == null)
                {
                    _native.Call("setLevel", (AndroidJavaObject)null);
                    return;
                }

                string enumName;
                switch (value.Value)
                {
                    case LogLevel.Debug:
                    case LogLevel.Verbose:
                        enumName = "DEBUG";
                        break;
                    case LogLevel.Info:
                        enumName = "INFO";
                        break;
                    case LogLevel.Warning:
                        enumName = "WARNING";
                        break;
                    case LogLevel.Error:
                        enumName = "ERROR";
                        break;
                    default:
                        enumName = "INFO";
                        break;
                }

                using (var clazz = new AndroidJavaClass(
                           "com.bugsee.library.contracts.exchange.Breadcrumb$Level"))
                using (var javaLevel = clazz.CallStatic<AndroidJavaObject>("valueOf", enumName))
                {
                    _native.Call("setLevel", javaLevel);
                }
            }
        }

        public IDictionary<string, object> Data
        {
            get
            {
                using (var map = _native.Call<AndroidJavaObject>("getData"))
                    return AndroidJavaConverters.MapToDictionary(map);
            }
            set
            {
                using (var map = AndroidJavaConverters.ObjectDictionaryToMap(value))
                    _native.Call("setData", map);
            }
        }
    }
}
#endif
