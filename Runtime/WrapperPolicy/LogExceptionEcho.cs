using System;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// <c>Debug.LogException</c> reaches the SDK twice: through <c>ILogHandler.LogException</c> (a real
    /// <see cref="Exception"/>) and again through <c>Application.logMessageReceivedThreaded</c>, which only has
    /// Unity's rendered text. Unity raises that callback synchronously, on the logging thread, from inside the
    /// default handler's <c>LogException</c> (checked in a Unity 2021.3.45 Editor, main and background thread). So
    /// the echo is recognised by being raised on this thread while <see cref="Expect"/>'s scope is open around that
    /// call - not by a time window, which let another thread's exception with the same text be taken for it.
    ///
    /// The text is still compared, so a different exception logged from inside that call (a nested log callback)
    /// is not dropped. Unity renders the INNERMOST exception of a chain as exactly "TypeName: message" (the outer
    /// ones go into the trace as "Rethrow as ..."), and as the bare "TypeName" when the message is blank. The
    /// expectation is consumed by the echo it explains.
    /// </summary>
    public static class LogExceptionEcho
    {
        /// <summary>What the <c>ILogHandler.LogException</c> path did with the exception.</summary>
        public enum Outcome
        {
            /// <summary>Sent, or queued for the main thread.</summary>
            Delivered,
            /// <summary>Skipped as a repeat of the same exception (see <see cref="DuplicateKey"/>) just reported.</summary>
            Duplicate,
            /// <summary>Skipped (nested call), the send threw, or a sync flush timed out.</summary>
            NotDelivered,
        }

        /// <summary>
        /// Whether to <see cref="Expect"/> (and so drop) the echo. A duplicate was already reported by the first
        /// call, so its echo would file the same exception again from the text; only when nothing was delivered is
        /// the echo the report.
        /// </summary>
        public static bool ShouldExpect(Outcome outcome) => outcome != Outcome.NotDelivered;

        /// <summary>
        /// Key for the short-window repeat check behind <see cref="Outcome.Duplicate"/>. Since a duplicate's echo
        /// is dropped, it must name the same exception: type, message and stack of the whole chain (inner and
        /// aggregate ones too), plus the source. Type + stack alone merged a per-item message thrown from one line,
        /// and every unthrown <c>new Exception(msg)</c> (null stack), and their echoes were dropped with them.
        /// </summary>
        public static string DuplicateKey(Exception exception, string source)
        {
            var key = new System.Text.StringBuilder();
            AppendIdentity(key, exception, 0);
            return key.Append('\n').Append(source).ToString();
        }

        static void AppendIdentity(System.Text.StringBuilder key, Exception exception, int depth)
        {
            if (exception == null) return;
            if (depth > 16) { key.Append("\n..."); return; }
            key.Append(exception.GetType().FullName).Append('\n')
                .Append(exception.Message).Append('\n')
                .Append(exception.StackTrace).Append('\n');
            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    key.Append("--inner--\n");
                    AppendIdentity(key, inner, depth + 1);
                }
                return;
            }
            if (exception.InnerException != null)
            {
                key.Append("--inner--\n");
                AppendIdentity(key, exception.InnerException, depth + 1);
            }
        }

        internal sealed class Entry
        {
            public string Name;
            public string FullName;
            public string Message;
            public bool Consumed;
            public Entry Outer;
        }

        [ThreadStatic]
        static Entry _expected;

        /// <summary>Open around the call that raises the echo; dispose when it returns.</summary>
        public static Scope Expect(Exception exception)
        {
            if (exception == null) return default;
            var inner = exception;
            while (inner.InnerException != null) inner = inner.InnerException;
            var type = inner.GetType();
            var entry = new Entry
            {
                Name = type.Name,
                FullName = type.FullName,
                Message = inner.Message ?? "",
                Outer = _expected,
            };
            _expected = entry;
            return new Scope(entry);
        }

        /// <summary>True for the echo of the exception expected on this thread, once.</summary>
        public static bool IsEcho(string condition)
        {
            var entry = _expected;
            if (string.IsNullOrEmpty(condition) || entry == null || entry.Consumed) return false;
            if (!Matches(condition, entry)) return false;
            entry.Consumed = true;
            return true;
        }

        public readonly struct Scope : IDisposable
        {
            readonly Entry _entry;

            internal Scope(Entry entry) { _entry = entry; }

            public void Dispose()
            {
                if (_entry != null && _expected == _entry) _expected = _entry.Outer;
            }
        }

        static bool Matches(string condition, Entry entry)
        {
            foreach (var name in new[] { entry.Name, entry.FullName })
            {
                if (name == null) continue;
                // Unity prints ": message" only when the message is not blank.
                var expected = string.IsNullOrWhiteSpace(entry.Message) ? name : name + ": " + entry.Message;
                if (condition == expected
                    || condition.StartsWith(expected + "\n", StringComparison.Ordinal)
                    || condition.StartsWith(expected + "\r", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
