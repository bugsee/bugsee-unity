using System;

namespace Bugsee.Contracts.Apm
{
    /// <summary>
    /// A unit of work in a performance trace, started with <c>Bugsee.StartTransaction</c> or <see cref="StartChild"/>.
    /// It wraps a native span: finish it (or dispose it) to release the native object. Finishing a span first
    /// finishes its still-live children as <see cref="SpanStatus.Cancelled"/>. Everything is safe to call after
    /// <see cref="Finish()"/> (it does nothing), and a span started while the SDK is not launched or without a
    /// valid name is an inert span (empty ids) that accepts every call. Nothing here throws: invalid input and
    /// native failures log a warning and leave the span unchanged.
    /// </summary>
    public interface ISpan : IDisposable
    {
        /// <summary>The native span id ("0" for the native no-op span used when performance monitoring is off), or an empty string for an inert span.</summary>
        string SpanId { get; }

        /// <summary>The id of the trace the span belongs to, or an empty string for an inert span.</summary>
        string TraceId { get; }

        string Operation { get; }

        /// <summary>True once the span was finished, directly or because its parent finished.</summary>
        bool IsFinished { get; }

        ISpan SetName(string name);

        ISpan SetDescription(string description);

        /// <summary>The value must be a string (up to 1024 UTF-16 units), a bool or a number in the signed 64-bit range.</summary>
        ISpan SetAttribute(string key, object value);

        ISpan SetStatus(SpanStatus status);

        /// <summary>Starts a child span. A finished parent returns an inert span.</summary>
        ISpan StartChild(string operation, string description = null);

        /// <summary>Finishes the span with <see cref="SpanStatus.Ok"/>.</summary>
        void Finish();

        void Finish(SpanStatus status);
    }
}
