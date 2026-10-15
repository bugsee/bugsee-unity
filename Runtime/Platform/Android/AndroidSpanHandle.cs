#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using Bugsee.Contracts.Apm;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    /// <summary>
    /// One <c>com.bugsee.library.contracts.performance.Span</c>. The handle is the held <see cref="AndroidJavaObject"/>
    /// (a JNI global reference), disposed by <see cref="Release"/> once <see cref="ManagedSpan"/> is done with it.
    /// </summary>
    sealed class AndroidSpanHandle : ISpanHandle
    {
        const string StatusClass = "com.bugsee.library.contracts.performance.SpanStatus";

        AndroidJavaObject _span;

        public AndroidSpanHandle(AndroidJavaObject span)
        {
            _span = span ?? throw new ArgumentNullException(nameof(span));
            try
            {
                SpanId = span.Call<string>("getSpanId") ?? "";
                TraceId = span.Call<string>("getTraceId") ?? "";
            }
            catch
            {
                Release();
                throw;
            }
        }

        public string SpanId { get; }
        public string TraceId { get; }

        public void SetName(string name) => Chain("setName", name);

        public void SetDescription(string description) => Chain("setDescription", description);

        public void SetAttribute(string key, object value)
        {
            using (var boxed = AndroidJavaConverters.Box(value))
                Chain("setAttribute", key, boxed);
        }

        public void SetStatus(SpanStatus status)
        {
            using (var native = NativeStatus(status))
                Chain("setStatus", native);
        }

        public ISpanHandle StartChild(string operation, string description)
        {
            AndroidJNI.AttachCurrentThread();
            // A null cannot select a JNI overload, so no description uses the one-argument method.
            var child = description == null
                ? _span.Call<AndroidJavaObject>("startChildSpan", operation)
                : _span.Call<AndroidJavaObject>("startChildSpan", operation, description);
            return child == null ? null : new AndroidSpanHandle(child);
        }

        public void Finish(SpanStatus status)
        {
            AndroidJNI.AttachCurrentThread();
            using (var native = NativeStatus(status))
                _span.Call("finish", native);
        }

        public void Release()
        {
            _span?.Dispose();
            _span = null;
        }

        // The setters return the span for chaining; each call returns a new reference that must be disposed.
        void Chain(string method, params object[] args)
        {
            AndroidJNI.AttachCurrentThread();
            using (_span.Call<AndroidJavaObject>(method, args)) { }
        }

        static AndroidJavaObject NativeStatus(SpanStatus status)
        {
            using (var clazz = new AndroidJavaClass(StatusClass))
                return clazz.GetStatic<AndroidJavaObject>(SpanPolicy.AndroidStatusName(status));
        }
    }
}
#endif
