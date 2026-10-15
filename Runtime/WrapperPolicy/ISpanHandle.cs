using System;
using Bugsee.Contracts.Apm;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// One native span as a platform bridge exposes it (a Java object on Android, an id into the native registry on
    /// iOS). <see cref="ManagedSpan"/> owns it: it calls <see cref="Finish"/> at most once and <see cref="Release"/>
    /// exactly once, in that order, and never touches it afterwards.
    /// </summary>
    public interface ISpanHandle
    {
        string SpanId { get; }
        string TraceId { get; }
        void SetName(string name);
        void SetDescription(string description);
        void SetAttribute(string key, object value);
        void SetStatus(SpanStatus status);

        /// <summary>A new child handle, or null when the native SDK produced none. The description may be null.</summary>
        ISpanHandle StartChild(string operation, string description);

        void Finish(SpanStatus status);

        /// <summary>Drops the wrapper's reference to the native span (a JNI global ref, a registry entry).</summary>
        void Release();

        /// <summary>
        /// A scope that keeps the calling thread usable for native calls until it is disposed (an Android thread-pool thread
        /// is attached once for a whole <see cref="ManagedSpan"/> finish instead of once per call). Never null.
        /// </summary>
        IDisposable EnterScope();
    }
}
