namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// How big the managed view walk's snapshot and seen-set start. A snapshot that starts at 512 nodes and doubles reallocates its ten
    /// parallel arrays twice for a 1500 node tree (about 210 KB allocated for 121 KB used) and rehashes the seen-set about a dozen times,
    /// all on the main thread inside the walk's slices. Measured on a HONOR WOD-LX1 and an iPhone XS this was the one part of the walk's
    /// managed overhead that was worth removing (see the changelog entry).
    /// </summary>
    public static class ManagedViewSnapshotSizing
    {
        /// <summary>The snapshot never starts below the size it always had, so a small scene allocates exactly what it did before.</summary>
        public const int MinCapacity = 512;

        /// <summary>
        /// The size to start with: the node count of the previous capture plus an eighth (a scene grows a little between captures), at
        /// least <see cref="MinCapacity"/> and at most the budget's cap plus the root and a scene node. The first capture after launch has
        /// no previous count and starts at the minimum.
        /// </summary>
        public static int Capacity(int previousCount, int maxNodes)
        {
            var cap = maxNodes + 2;
            if (cap < MinCapacity) cap = MinCapacity;
            if (previousCount <= 0) return MinCapacity;
            var want = (long)previousCount + previousCount / 8;
            if (want < MinCapacity) return MinCapacity;
            return want > cap ? cap : (int)want;
        }

        /// <summary>
        /// True when the seen-set is created with a capacity. Below the minimum it stays the default empty set (a small scene pays nothing);
        /// above it, the set would otherwise rehash about a dozen times on the way to a big tree.
        /// </summary>
        public static bool PresizeSeenSet(int capacity) => capacity > MinCapacity;
    }
}
