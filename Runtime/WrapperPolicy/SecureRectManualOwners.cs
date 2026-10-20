using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>Tracks imperative secure-rect owners keyed by pixel LTRB for the public facade.</summary>
    public sealed class SecureRectManualOwners
    {
        public const string OwnerIdPrefix = "manual:";

        readonly Dictionary<string, string> _ownerIdByRectKey = new Dictionary<string, string>();
        // Adding the same rectangle twice hides it until it was removed as often as it was added.
        readonly Dictionary<string, int> _countByRectKey = new Dictionary<string, int>();

        public void Add(int left, int top, int right, int bottom, SecureRectRegistry registry, int displayId)
        {
            string rectKey = RectKey(left, top, right, bottom);
            string ownerId = OwnerIdPrefix + rectKey;
            _ownerIdByRectKey[rectKey] = ownerId;
            _countByRectKey[rectKey] = _countByRectKey.TryGetValue(rectKey, out int n) ? n + 1 : 1;
            registry.Set(ownerId, displayId, left, top, right, bottom);
        }

        public bool Remove(int left, int top, int right, int bottom, SecureRectRegistry registry)
        {
            string rectKey = RectKey(left, top, right, bottom);
            if (!_ownerIdByRectKey.TryGetValue(rectKey, out string ownerId))
                return false;

            if (_countByRectKey.TryGetValue(rectKey, out int n) && n > 1)
            {
                _countByRectKey[rectKey] = n - 1;
                return true;
            }

            registry.RemoveOwner(ownerId);
            _ownerIdByRectKey.Remove(rectKey);
            _countByRectKey.Remove(rectKey);
            return true;
        }

        public void RemoveAll(SecureRectRegistry registry)
        {
            registry.RemoveOwnersWithPrefix(OwnerIdPrefix);
            _ownerIdByRectKey.Clear();
            _countByRectKey.Clear();
        }

        public static string RectKey(int left, int top, int right, int bottom) =>
            left + "," + top + "," + right + "," + bottom;
    }
}
