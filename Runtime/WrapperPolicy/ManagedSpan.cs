using System;
using System.Collections.Generic;
using Bugsee.Contracts.Apm;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// The managed span: all lifetime and argument rules around one <see cref="ISpanHandle"/>. A span is finished
    /// once; that finishes its live children as Cancelled first (so the result does not depend on whether the native
    /// SDK cascades), calls the native finish, then releases the handle. After that no native call is made. Both
    /// the finish and the release run even if a native call throws. Thread-safe: the state is guarded and the
    /// handle is only used while the span is live.
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
            lock (_gate)
            {
                if (!_finished) _handle.SetName(name);
            }
            return this;
        }

        public ISpan SetDescription(string description)
        {
            lock (_gate)
            {
                if (!_finished) _handle.SetDescription(SpanPolicy.Description(description));
            }
            return this;
        }

        public ISpan SetAttribute(string key, object value)
        {
            var decision = AttributePolicy.Evaluate(key, value);
            if (!decision.Accepted)
            {
                Warn("SetAttribute ignored: " + decision.Error + ".");
                return this;
            }
            lock (_gate)
            {
                if (!_finished) _handle.SetAttribute(key, AttributeWire.ToBoxable(value));
            }
            return this;
        }

        public ISpan SetStatus(SpanStatus status)
        {
            lock (_gate)
            {
                if (!_finished) _handle.SetStatus(SpanPolicy.Normalize(status));
            }
            return this;
        }

        public ISpan StartChild(string operation, string description = null)
        {
            if (!SpanPolicy.HasText(operation))
            {
                Warn("StartChild ignored: an operation is required.");
                return NoopSpan.Instance;
            }
            lock (_gate)
            {
                if (_finished) return NoopSpan.Instance;
                var childHandle = _handle.StartChild(operation, description);
                if (childHandle == null) return NoopSpan.Instance;
                var child = new ManagedSpan(childHandle, operation, _warn, this);
                _children.Add(child);
                return child;
            }
        }

        public void Finish() => Finish(SpanStatus.Ok);

        public void Finish(SpanStatus status)
        {
            ManagedSpan[] children;
            ISpanHandle handle;
            lock (_gate)
            {
                if (_finished) return;
                _finished = true;
                handle = _handle;
                _handle = null;
                children = _children.ToArray();
                _children.Clear();
            }

            try
            {
                // A child's own finish removes it from this list; the snapshot is already detached.
                foreach (var child in children) child.FinishFromParent();
                handle.Finish(SpanPolicy.Normalize(status));
            }
            finally
            {
                handle.Release();
                _parent?.Forget(this);
            }
        }

        public void Dispose() => Finish();

        void FinishFromParent()
        {
            // The parent already detached this child, so it must not call back into the parent's list.
            ISpanHandle handle;
            lock (_gate)
            {
                if (_finished) return;
                _finished = true;
                handle = _handle;
                _handle = null;
            }
            var grandChildren = DetachChildren();
            try
            {
                foreach (var child in grandChildren) child.FinishFromParent();
                handle.Finish(SpanStatus.Cancelled);
            }
            finally
            {
                handle.Release();
            }
        }

        ManagedSpan[] DetachChildren()
        {
            lock (_gate)
            {
                var children = _children.ToArray();
                _children.Clear();
                return children;
            }
        }

        void Forget(ManagedSpan child)
        {
            lock (_gate) _children.Remove(child);
        }

        void Warn(string message) => _warn?.Invoke(message);
    }

    /// <summary>The span returned when there is nothing native behind it: every call is accepted and does nothing.</summary>
    public sealed class NoopSpan : ISpan
    {
        public static readonly NoopSpan Instance = new NoopSpan();

        NoopSpan()
        {
        }

        public string SpanId => "";
        public string TraceId => "";
        public string Operation => "";
        public bool IsFinished => false;
        public ISpan SetName(string name) => this;
        public ISpan SetDescription(string description) => this;
        public ISpan SetAttribute(string key, object value) => this;
        public ISpan SetStatus(SpanStatus status) => this;
        public ISpan StartChild(string operation, string description = null) => this;
        public void Finish() { }
        public void Finish(SpanStatus status) { }
        public void Dispose() { }
    }
}
