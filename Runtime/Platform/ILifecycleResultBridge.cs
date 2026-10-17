using System;
using System.Collections.Generic;

namespace Bugsee.Platform
{
    /// <summary>
    /// Optional bridge surface behind the Task-returning lifecycle calls. A bridge without it gets a fallback in
    /// the facade (the launch result read from the status right after the call returns).
    /// </summary>
    internal interface ILifecycleResultBridge
    {
        /// <summary><paramref name="started"/> gets the SDK's answer: false when it refused the launch, or when
        /// the launch never ran (a later Stop or Launch cancelled it, or the main thread did not take it).</summary>
        void Launch(string appToken, IDictionary<string, object> options, Action<bool> started);

        /// <inheritdoc cref="Launch"/>
        void Relaunch(IDictionary<string, object> options, Action<bool> started);

        /// <summary><paramref name="finished"/> runs once, when the stop is over: true when the stop ran (even
        /// if a later Launch dropped the ordinary Stop completions), false when the SDK had not reported it within the
        /// soft deadline (10 s from the call) or, on iOS, when the main thread did not take it. Android queues the call to
        /// the main thread and never refuses it: a stalled main thread leaves <paramref name="finished"/> pending.</summary>
        void Stop(Action<bool> finished);
    }
}
