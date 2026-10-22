using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    public sealed class SecureRectRegistry
    {
        static readonly SecureRectRegistry SharedInstance = new SecureRectRegistry();

        public static SecureRectRegistry Instance => SharedInstance;

        // Bugsee.AddSecureRectangle can run on any thread while the frame pusher reads every frame.
        readonly object _sync = new object();

        readonly Dictionary<(string ownerId, int displayId), int[]> _rectsByOwnerDisplay =
            new Dictionary<(string ownerId, int displayId), int[]>();

        readonly Dictionary<int, DisplaySnapshotState> _displayStates =
            new Dictionary<int, DisplaySnapshotState>();

        sealed class DisplaySnapshotState
        {
            public int Version;
            public int[] LastPublishedRects;
        }

        public void Set(string ownerId, int displayId, int left, int top, int right, int bottom)
        {
            lock (_sync)
            {
                int normalizedLeft = Math.Min(left, right);
                int normalizedRight = Math.Max(left, right);
                int normalizedTop = Math.Min(top, bottom);
                int normalizedBottom = Math.Max(top, bottom);
                _rectsByOwnerDisplay[(ownerId, displayId)] = new[]
                {
                    normalizedLeft,
                    normalizedTop,
                    normalizedRight,
                    normalizedBottom
                };
            }
        }

        public void RemoveOwner(string ownerId)
        {
            lock (_sync)
            {
                var keysToRemove = new List<(string ownerId, int displayId)>();
                foreach (var key in _rectsByOwnerDisplay.Keys)
                {
                    if (key.ownerId == ownerId)
                        keysToRemove.Add(key);
                }

                for (int i = 0; i < keysToRemove.Count; i++)
                    _rectsByOwnerDisplay.Remove(keysToRemove[i]);
            }
        }

        public void RemoveOwnersWithPrefix(string ownerIdPrefix)
        {
            lock (_sync)
            {
                if (string.IsNullOrEmpty(ownerIdPrefix))
                    return;

                var keysToRemove = new List<(string ownerId, int displayId)>();
                foreach (var key in _rectsByOwnerDisplay.Keys)
                {
                    if (key.ownerId != null && key.ownerId.StartsWith(ownerIdPrefix, StringComparison.Ordinal))
                        keysToRemove.Add(key);
                }

                for (int i = 0; i < keysToRemove.Count; i++)
                    _rectsByOwnerDisplay.Remove(keysToRemove[i]);
            }
        }

        public int[] Snapshot(int displayId, float pixelsPerNativeUnit)
        {
            lock (_sync)
            {
                ValidatePixelsPerNativeUnit(pixelsPerNativeUnit);

                int[] converted = BuildConvertedRects(displayId, pixelsPerNativeUnit);
                DisplaySnapshotState state = GetOrCreateDisplayState(displayId);

                if (state.LastPublishedRects == null)
                {
                    state.Version = 1;
                    state.LastPublishedRects = Array.Empty<int>();
                }

                if (!RectBuffersEqual(state.LastPublishedRects, converted))
                {
                    int next = state.Version + 1;
                    state.Version = next == 1 ? 2 : next;
                    state.LastPublishedRects = (int[])converted.Clone();
                }

                return PackSnapshot(state.Version, converted);
            }
        }

        /// <summary>
        /// Copies the rectangles of a display as [l, t, r, b, ...] (registered pixels, no scaling) into
        /// <paramref name="dest"/>, growing it when too small, and returns the rectangle count. Thread-safe; allocates
        /// only when the buffer has to grow, so it can run every frame.
        /// </summary>
        public int CopyRects(int displayId, ref int[] dest)
        {
            lock (_sync)
            {
                int count = 0;
                foreach (var entry in _rectsByOwnerDisplay)
                {
                    if (entry.Key.displayId != displayId) continue;
                    if (dest == null || dest.Length < (count + 1) * 4)
                        Array.Resize(ref dest, Math.Max(16, (count + 1) * 8));
                    Array.Copy(entry.Value, 0, dest, count * 4, 4);
                    count++;
                }
                return count;
            }
        }

        /// <summary>
        /// The rectangles of a display whose owner id starts with <paramref name="ownerIdPrefix"/>, each as
        /// [l, t, r, b] (registered pixels, no scaling), ordered by owner id.
        /// </summary>
        public List<int[]> GetRects(string ownerIdPrefix, int displayId)
        {
            lock (_sync)
            {
                var owners = new List<string>();
                foreach (var key in _rectsByOwnerDisplay.Keys)
                {
                    if (key.displayId == displayId && key.ownerId != null &&
                        key.ownerId.StartsWith(ownerIdPrefix, StringComparison.Ordinal))
                        owners.Add(key.ownerId);
                }
                owners.Sort(StringComparer.Ordinal);
                var result = new List<int[]>(owners.Count);
                foreach (var owner in owners)
                    result.Add((int[])_rectsByOwnerDisplay[(owner, displayId)].Clone());
                return result;
            }
        }

        static int[] PackSnapshot(int version, int[] convertedRects)
        {
            int rectCount = convertedRects.Length / 4;
            var result = new int[2 + convertedRects.Length];
            result[0] = version;
            result[1] = rectCount;
            Array.Copy(convertedRects, 0, result, 2, convertedRects.Length);
            return result;
        }

        int[] BuildConvertedRects(int displayId, float pixelsPerNativeUnit)
        {
            var owners = new List<string>();
            foreach (var key in _rectsByOwnerDisplay.Keys)
            {
                if (key.displayId == displayId)
                    owners.Add(key.ownerId);
            }

            owners.Sort(StringComparer.Ordinal);

            var converted = new int[owners.Count * 4];
            for (int i = 0; i < owners.Count; i++)
            {
                int[] rect = _rectsByOwnerDisplay[(owners[i], displayId)];
                int offset = i * 4;
                converted[offset] = (int)Math.Floor(rect[0] / pixelsPerNativeUnit);
                converted[offset + 1] = (int)Math.Floor(rect[1] / pixelsPerNativeUnit);
                converted[offset + 2] = (int)Math.Ceiling(rect[2] / pixelsPerNativeUnit);
                converted[offset + 3] = (int)Math.Ceiling(rect[3] / pixelsPerNativeUnit);
            }

            return converted;
        }

        DisplaySnapshotState GetOrCreateDisplayState(int displayId)
        {
            if (!_displayStates.TryGetValue(displayId, out DisplaySnapshotState state))
            {
                state = new DisplaySnapshotState();
                _displayStates[displayId] = state;
            }

            return state;
        }

        static void ValidatePixelsPerNativeUnit(float pixelsPerNativeUnit)
        {
            if (pixelsPerNativeUnit <= 0f || float.IsNaN(pixelsPerNativeUnit) || float.IsInfinity(pixelsPerNativeUnit))
                throw new ArgumentOutOfRangeException(nameof(pixelsPerNativeUnit));
        }

        static bool RectBuffersEqual(int[] left, int[] right)
        {
            if (left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }
    }
}
