using System.Threading;
using Bugsee.WrapperPolicy;

namespace Bugsee.Internal
{
    /// <summary>
    /// What the last managed view hierarchy captures cost, for the sample's measurement scenario and tests of the wrapper. Not part of
    /// the SDK's API: it may change without notice. Records in every build; the wrapper's log line for each capture is written only in
    /// development builds unless <see cref="LogEveryCapture"/> is set.
    /// </summary>
    public static class ManagedViewDiagnostics
    {
        static ManagedViewCaptureStats _last;
        static int _count;

        /// <summary>Write the per-capture log line in a release build too (the sample's measurement run).</summary>
        public static bool LogEveryCapture;

        /// <summary>When above 0, the main thread's per-frame slice of the walk in ms instead of 5% of the frame (the sample compares slicings).</summary>
        public static double SliceMsOverride;

        /// <summary>True: the 3D scene read walks every transform itself instead of using what the tree walk found (the sample's comparison of the two on one scene: the replies must be identical).</summary>
        public static bool DisableSceneFeed;

        /// <summary>Whether the last 3D scene read used what the tree walk found (true) or walked every transform itself (false: the feed was off, or the tree skipped something it cannot resume from, e.g. a root object appeared during the walk). Written on the main thread.</summary>
        public static volatile bool LastSceneFeedUsed;

        /// <summary>The captures settled so far (replied or answered null).</summary>
        public static int Count => Volatile.Read(ref _count);

        public static ManagedViewCaptureStats Last => Volatile.Read(ref _last);

        /// <summary>Keep the JSON of every reply in <see cref="LastReply"/> (the sample's scenarios read it back; off by default, a reply is hundreds of KB).</summary>
        public static bool KeepReplies;

        static string _lastReply;
        static int _replyCount;

        /// <summary>The last reply given while <see cref="KeepReplies"/> was on: its JSON, or null when the wrapper answered null (no managed tree).</summary>
        public static string LastReply => Volatile.Read(ref _lastReply);

        /// <summary>Replies given (null ones too) while <see cref="KeepReplies"/> was on.</summary>
        public static int ReplyCount => Volatile.Read(ref _replyCount);

        internal static void NoteReply(string json)
        {
            if (!KeepReplies) return;
            Volatile.Write(ref _lastReply, json);
            Interlocked.Increment(ref _replyCount);
        }

        internal static void Record(ManagedViewCaptureStats stats)
        {
            Volatile.Write(ref _last, stats);
            Interlocked.Increment(ref _count);
        }
    }
}
