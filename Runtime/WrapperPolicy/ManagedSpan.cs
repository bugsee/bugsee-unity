using System;
using System.Collections.Generic;
using Bugsee.Contracts.Apm;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// The managed span: all lifetime and argument rules around one <see cref="ISpanHandle"/>. A span is finished
    /// once; that finishes its live children as Cancelled first (so the result does not depend on whether the native
    /// SDK cascades), calls the native finish, then releases the handle. After that no native call is made. A native
    /// call that throws is reported through the warning callback and never escapes: every child is finished
    /// independently, the parent is always finished, and each handle is released once. A child finishing on another
    /// thread while its parent finishes keeps whichever status reached it first (each span's state is flipped under
    /// its own lock). Thread-safe: the state is guarded and the handle is only used while the span is live.
    /// </summary>
    public sealed class ManagedSpan : ISpan
    {
        readonly object _gate = new object();
        readonly string _operation;
        readonly Action<string> _warn;
        readonly ManagedSpan _parent;
        readonly List<ManagedSpan> _children = new List<ManagedSpan>();
        ISpanHandle _handle;
        bool _finished;

        public ManagedSpan(ISpanHandle handle, string operation, Action<string> warn = null)
            : this(handle, operation, warn, null)
        {
        }

        ManagedSpan(ISpanHandle handle, string operation, Action<string> warn, ManagedSpan parent)
        {
            _handle = handle ?? throw new ArgumentNullException(nameof(handle));
            _operation = operation ?? "";
            _warn = warn;
            _parent = parent;
            SpanId = handle.SpanId ?? "";
            TraceId = handle.TraceId ?? "";
        }

        public string SpanId { get; }
        public string TraceId { get; }
        public string Operation => _operation;

        public bool IsFinished
        {
            get { lock (_gate) return _finished; }
        }

        public ISpan SetName(string name)
        {
            if (!SpanPolicy.HasText(name))
            {
                Warn("SetName ignored: a name is required.");
                return this;
            }
            return Guarded("SetName", h => h.SetName(name));
        }

        public ISpan SetDescription(string description) =>
            Guarded("SetDescription", h => h.SetDescription(SpanPolicy.Description(description)));

        public ISpan SetAttribute(string key, object value)
        {
            var decision = AttributePolicy.Evaluate(key, value);
            if (!decision.Accepted)
            {
                Warn("SetAttribute ignored: " + decision.Error + ".");
                return this;
            }
            return Guarded("SetAttribute", h => h.SetAttribute(key, AttributeWire.ToBoxable(value)));
        }

        public ISpan SetStatus(SpanStatus status) =>
            Guarded("SetStatus", h => h.SetStatus(SpanPolicy.Normalize(status)));

        public ISpan StartChild(string operation, string description = null)
        {
            if (!SpanPolicy.HasText(operation))
            {
                Warn("StartChild ignored: an operation is required.");
                return new NoopSpan();
            }
            lock (_gate)
            {
                if (_finished) return new NoopSpan();
                try
                {
                    var childHandle = _handle.StartChild(operation, description);
                    if (childHandle == null) return new NoopSpan();
                    var child = new ManagedSpan(childHandle, operation, _warn, this);
                    _children.Add(child);
                    return child;
                }
                catch (Exception e)
                {
                    Warn("StartChild failed: " + e.Message);
                    return new NoopSpan();
                }
            }
        }

        public void Finish() => Finish(SpanStatus.Ok);

        public void Finish(SpanStatus status)
        {
            var handle = Detach(out var children);
            if (handle == null) return;

            foreach (var child in children) child.FinishFromParent();
            FinishAndRelease(handle, SpanPolicy.Normalize(status));
            _parent?.Forget(this);
        }

        public void Dispose() => Finish();

        void FinishFromParent()
        {
            // The parent already detached this child, so it must not call back into the parent's list.
            var handle = Detach(out var grandChildren);
            if (handle == null) return;

            foreach (var child in grandChildren) child.FinishFromParent();
            FinishAndRelease(handle, SpanStatus.Cancelled);
        }

        // Flips the span to finished and hands back its handle and live children, or null when it was already finished.
        ISpanHandle Detach(out ManagedSpan[] children)
        {
            lock (_gate)
            {
                if (_finished)
                {
                    children = null;
                    return null;
                }
                _finished = true;
                var handle = _handle;
                _handle = null;
                children = _children.ToArray();
                _children.Clear();
                return handle;
            }
        }

        void FinishAndRelease(ISpanHandle handle, SpanStatus status)
        {
            try
            {
                handle.Finish(status);
            }
            catch (Exception e)
            {
                Warn("Finish failed: " + e.Message);
            }
            try
            {
                handle.Release();
            }
            catch (Exception e)
            {
                Warn("Release failed: " + e.Message);
            }
        }

        ISpan Guarded(string what, Action<ISpanHandle> call)
        {
            lock (_gate)
            {
                if (_finished) return this;
                try
                {
                    call(_handle);
                }
                catch (Exception e)
                {
                    Warn(what + " failed: " + e.Message);
                }
            }
            return this;
        }

        void Forget(ManagedSpan child)
        {
            lock (_gate) _children.Remove(child);
        }

        void Warn(string message)
        {
            try
            {
                _warn?.Invoke(message);
            }
            catch
            {
                // A logging callback must not break span handling.
            }
        }
    }

    /// <summary>
    /// The span returned when there is nothing behind it (the SDK is not launched, an argument was empty, the native
    /// SDK produced none): every call is accepted and does nothing, the ids are empty, and it reads as finished
    /// once <see cref="Finish()"/> or <see cref="Dispose"/> was called. (When performance monitoring is off the
    /// native SDKs return their own no-op span, whose ids read "0"; that one is wrapped like any other span.)
    /// </summary>
    public sealed class NoopSpan : ISpan
    {
        volatile bool _finished;

        public string SpanId => "";
        public string TraceId => "";
        public string Operation => "";
        public bool IsFinished => _finished;
        public ISpan SetName(string name) => this;
        public ISpan SetDescription(string description) => this;
        public ISpan SetAttribute(string key, object value) => this;
        public ISpan SetStatus(SpanStatus status) => this;
        public ISpan StartChild(string operation, string description = null) => new NoopSpan();
        public void Finish() => _finished = true;
        public void Finish(SpanStatus status) => _finished = true;
        public void Dispose() => _finished = true;
    }
}
