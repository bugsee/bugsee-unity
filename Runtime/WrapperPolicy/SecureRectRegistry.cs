using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    public sealed class SecureRectRegistry
    {
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

        public void RemoveOwner(string ownerId)
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

        public int[] Snapshot(int displayId, float pixelsPerNativeUnit)
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
