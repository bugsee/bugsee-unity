#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using Bugsee.Contracts.Apm;
using Bugsee.WrapperPolicy;

namespace Bugsee.Platform.IOS
{
    /// <summary>
    /// One <c>id&lt;BGSSpan&gt;</c> held by id in the native registry (<c>BugseeUnityBridge.mm</c>). <see cref="Release"/>
    /// removes the registry entry once <see cref="ManagedSpan"/> is done with the span; the finalizer does the same for
    /// a handle that was dropped without being released, so the registry never outlives the managed object.
    /// </summary>
    sealed class IosSpanHandle : ISpanHandle
    {
        long _id;

        public IosSpanHandle(long id)
        {
            _id = id;
            SpanId = Read(_bugsee_span_get_id(id));
            TraceId = Read(_bugsee_span_get_trace_id(id));
        }

        ~IosSpanHandle() => Release();

        public string SpanId { get; }
        public string TraceId { get; }

        public void SetName(string name) => _bugsee_span_set_name(_id, name);

        public void SetDescription(string description) => _bugsee_span_set_description(_id, description);

        public void SetAttribute(string key, object value) =>
            _bugsee_span_set_attribute(_id, key, AttributeWire.ToJson(value));

        public void SetStatus(SpanStatus status) => _bugsee_span_set_status(_id, (int)status);

        public ISpanHandle StartChild(string operation, string description)
        {
            long child = _bugsee_span_start_child(_id, operation, description);
            return child == 0 ? null : new IosSpanHandle(child);
        }

        public void Finish(SpanStatus status) => _bugsee_span_finish(_id, (int)status);

        public void Release()
        {
            long id = System.Threading.Interlocked.Exchange(ref _id, 0);
            if (id != 0) _bugsee_span_release(id);
            GC.SuppressFinalize(this);
        }

        static string Read(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return "";
            try
            {
                return Marshal.PtrToStringAnsi(ptr) ?? "";
            }
            finally
            {
                _bugsee_free(ptr);
            }
        }

        [DllImport("__Internal")] internal static extern long _bugsee_start_transaction(string name, string operation, string attributesJson);
        [DllImport("__Internal")] static extern long _bugsee_span_start_child(long spanId, string operation, string description);
        [DllImport("__Internal")] static extern IntPtr _bugsee_span_get_id(long spanId);
        [DllImport("__Internal")] static extern IntPtr _bugsee_span_get_trace_id(long spanId);
        [DllImport("__Internal")] static extern void _bugsee_span_set_name(long spanId, string name);
        [DllImport("__Internal")] static extern void _bugsee_span_set_description(long spanId, string description);
        [DllImport("__Internal")] static extern void _bugsee_span_set_attribute(long spanId, string key, string valueJson);
        [DllImport("__Internal")] static extern void _bugsee_span_set_status(long spanId, int status);
        [DllImport("__Internal")] static extern void _bugsee_span_finish(long spanId, int status);
        [DllImport("__Internal")] static extern void _bugsee_span_release(long spanId);
        [DllImport("__Internal")] static extern void _bugsee_free(IntPtr ptr);
    }
}
#endif
