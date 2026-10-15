using System;
using Bugsee.Contracts.Apm;

namespace Bugsee.WrapperPolicy
{
    /// <summary>Failure-safe construction of spans, so a native failure never escapes and never leaks a handle.</summary>
    public static class SpanStart
    {
        /// <summary>
        /// Starts a transaction through <paramref name="start"/> and wraps the handle, once <paramref name="isLaunched"/>
        /// says the SDK is launched (otherwise an inert span, and <paramref name="start"/> is not called). Any failure (the
        /// launched check, the native call, or building the wrapper, in which case the handle is released) is reported
        /// through <paramref name="warn"/> and yields an inert span.
        /// </summary>
        public static ISpan Transaction(Func<bool> isLaunched, Func<ISpanHandle> start, string operation, Action<string> warn)
        {
            ISpanHandle handle = null;
            try
            {
                if (!isLaunched()) return new NoopSpan();
                handle = start();
                return handle == null ? new NoopSpan() : new ManagedSpan(handle, operation, warn);
            }
            catch (Exception e)
            {
                try { handle?.Release(); } catch { }
                try { warn?.Invoke("StartTransaction failed: " + e.Message); } catch { }
                return new NoopSpan();
            }
        }

        /// <summary>
        /// Reads the ids of a freshly registered native span. If a read throws, <paramref name="release"/> runs (once)
        /// before the exception propagates, so the registry entry is not leaked by a constructor that never completes.
        /// </summary>
        public static void ReadIds(Func<string> spanId, Func<string> traceId, Action release, out string span, out string trace)
        {
            try
            {
                span = spanId() ?? "";
                trace = traceId() ?? "";
            }
            catch
            {
                release();
                throw;
            }
        }
    }
}
