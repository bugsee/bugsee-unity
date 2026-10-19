using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Bugsee.Contracts.Options;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// How long the native SDK waits for a report handler before it carries on without it: the
    /// <c>ReportHandlerCallbackTimeout</c> option (30 s by default). <c>0</c> or less disables the per-handler timer,
    /// but the SDK still ends the chain at 60 s. iOS (7.0.0-beta7) has the same 30 s but does not read the option.
    /// </summary>
    public static class ReportHandlerDeadline
    {
        public const long DefaultBudgetMs = 30000;
        public const long ChainCapMs = 60000;
        const long MaxSeconds = 24 * 60 * 60;

        public static long BudgetMillis(IReadOnlyDictionary<string, object> launchOptions, bool optionHonoured)
        {
            if (!optionHonoured || launchOptions == null
                || !launchOptions.TryGetValue(Options.ReportHandlerCallbackTimeout, out object value) || value == null)
                return DefaultBudgetMs;

            long seconds;
            try { seconds = Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return DefaultBudgetMs; }

            if (seconds <= 0) return ChainCapMs;
            return Math.Min(seconds, MaxSeconds) * 1000;
        }
    }

    /// <summary>
    /// Decides what a report-handler callback may still do once the native SDK has stopped waiting for it, which
    /// happens when the Unity main loop is paused (an Android app in the background) or the handler answers
    /// slowly. The SDK has already carried on with the report unchanged, so a handler that would only start now is
    /// skipped and a reply that arrives now is only acknowledged, never applied to the report. The completion
    /// runs exactly once, whichever of the reply, the skip or a failure comes first; the flag it receives says
    /// whether the handler's edits may be applied.
    /// </summary>
    public sealed class LateReplyGuard
    {
        readonly object _gate = new object();
        readonly long _deadlineMs;
        readonly Func<long> _nowMs;
        readonly Action<bool> _finish;
        bool _finished;

        public LateReplyGuard(long budgetMs, Func<long> nowMs, Action<bool> finish)
        {
            _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
            _finish = finish ?? throw new ArgumentNullException(nameof(finish));
            _deadlineMs = nowMs() + budgetMs;
        }

        public static long StopwatchMillis() => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

        public bool IsExpired => _nowMs() >= _deadlineMs;

        /// <summary>True when the handler may run. When it may not, the report is completed unchanged.</summary>
        public bool TryBegin()
        {
            if (!IsExpired) return true;
            Finish(false);
            return false;
        }

        /// <summary>The handler's completion callback.</summary>
        public void Done() => Finish(!IsExpired);

        /// <summary>The handler failed or there is none: complete the report unchanged.</summary>
        public void Abandon() => Finish(false);

        void Finish(bool applyEdits)
        {
            lock (_gate)
            {
                if (_finished) return;
                _finished = true;
            }
            _finish(applyEdits);
        }
    }
}
