using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Bugsee.WrapperPolicy
{
    /// <summary>One node of the managed (Unity) view hierarchy, geometry in Unity screen pixels with the origin at the top left.</summary>
    public sealed class ManagedViewNode
    {
        public long Id;
        /// <summary>Overrides <see cref="Id"/> on the wire, for nodes whose id comes from another counter (a scene handle can equal an instance id).</summary>
        public string IdText;
        public string ClassName;
        /// <summary>The GameObject's name. A free-text label, so it is withheld for protected nodes.</summary>
        public string Name;
        /// <summary>The component type names on the object, comma separated. Code identifiers, never content.</summary>
        public string Components;
        public bool HasBounds;
        public float X, Y, Width, Height;
        /// <summary>The walk stopped at this node: some of its children (or deeper levels) were left out. Written as the viewer's node-level <c>truncated</c>.</summary>
        public bool Truncated;
        public readonly List<ManagedViewNode> Children = new List<ManagedViewNode>();

        // Filled once by the writer, bottom-up: the node's own box or the union of its children's.
        internal bool ResolvedHas;
        internal float RX, RY, RW, RH;
    }

    /// <summary>
    /// What one hierarchy walk may cost: a node count, a depth and a deadline. The walk stops taking nodes once any of
    /// them is spent, and the result says it was cut short. Pure so it can be unit-tested.
    /// </summary>
    public sealed class ManagedViewBudget
    {
        public const int DefaultMaxNodes = 1500;
        public const int DefaultMaxDepth = 24;

        readonly int _maxNodes;
        readonly int _maxDepth;
        readonly Func<bool> _expired;
        int _taken;

        public ManagedViewBudget(int maxNodes = DefaultMaxNodes, int maxDepth = DefaultMaxDepth, Func<bool> expired = null)
        {
            _maxNodes = maxNodes;
            _maxDepth = maxDepth;
            _expired = expired;
        }

        /// <summary>True once something was left out because of the budget.</summary>
        public bool Truncated { get; private set; }

        public int Taken => _taken;

        /// <summary>
        /// True when no further node can be taken; the walk should stop looking at siblings. Asked while a node is waiting to
        /// be visited, so a true answer means that node is left out, and it is recorded as <see cref="Truncated"/>.
        /// </summary>
        public bool IsExhausted
        {
            get
            {
                if (_taken < _maxNodes && (_expired == null || !_expired())) return false;
                Truncated = true;
                return true;
            }
        }

        /// <summary>Takes one node at <paramref name="depth"/> (the root is 1), or records that the budget cut it off.</summary>
        public bool TryTake(int depth)
        {
            if (depth > _maxDepth || _taken >= _maxNodes || (_expired != null && _expired()))
            {
                Truncated = true;
                return false;
            }

            _taken++;
            return true;
        }
    }

    /// <summary>
    /// Writes the <c>vh</c> reply: the root node of the managed hierarchy as one JSON object (class_name, bounds
    /// [x, y, w, h], optional id, options, subitems), the shape the viewer reads (specs: sdk/wrapper-data-requests).
    /// The SDK stores the reply byte for byte and never looks inside it, so the privacy rules are applied here: no
    /// text content ever reaches a node, and a node whose box overlaps a secure rectangle loses its name and component
    /// list. Each node is judged by its own box (a full-screen container overlaps, its far-away children do not). The
    /// bugsee_protected_view flag is deliberately not written: the viewer draws it as a secured region, and a container
    /// that merely overlaps a rectangle is not one.
    /// </summary>
    public static class ManagedViewTreeJson
    {
        /// <summary>The wire name of the flow this reply belongs to.</summary>
        public const string DataType = "vh";

        // A walk never nests deeper than the budget allows; the writer holds its own bound so a hand-built tree cannot overflow it.
        const int MaxWriteDepth = 64;

        /// <param name="root">The tree; geometry in Unity screen pixels, top-left origin.</param>
        /// <param name="secureRects">[left, top, right, bottom, ...] in the same pixels (SecureRectRegistry.CopyRects).</param>
        /// <param name="secureCount">How many rectangles of <paramref name="secureRects"/> are in use.</param>
        /// <param name="pixelsPerNativeUnit">1 on Android (the native tree is in pixels), the screen scale on iOS (points).</param>
        public static string Write(
            ManagedViewNode root, int[] secureRects, int secureCount, float pixelsPerNativeUnit) =>
            Write(root, secureRects, secureCount, pixelsPerNativeUnit, out _);

        /// <param name="protectedNodes">How many nodes lost their name and component list to a secure rectangle.</param>
        public static string Write(
            ManagedViewNode root, int[] secureRects, int secureCount, float pixelsPerNativeUnit, out int protectedNodes)
        {
            protectedNodes = 0;
            if (root == null) return null;
            if (!(pixelsPerNativeUnit > 0f) || float.IsInfinity(pixelsPerNativeUnit)) pixelsPerNativeUnit = 1f;
            if (secureRects == null) secureCount = 0;
            else if (secureCount * 4 > secureRects.Length) secureCount = secureRects.Length / 4;

            Resolve(root, 1);
            var sb = new StringBuilder(4096);
            WriteNode(sb, root, secureRects, secureCount, pixelsPerNativeUnit, 1, ref protectedNodes);
            return sb.ToString();
        }

        /// <summary>
        /// The bounds a node reports: its own, or the union of its children's when it has none (an empty container).
        /// False when nothing below it has geometry either.
        /// </summary>
        public static bool TryResolveBounds(ManagedViewNode node, out float x, out float y, out float w, out float h)
        {
            Resolve(node, 1);
            x = node.RX; y = node.RY; w = node.RW; h = node.RH;
            return node.ResolvedHas;
        }

        // Post-order, once per node: a walk of n nodes costs n, not n times the depth.
        static void Resolve(ManagedViewNode node, int depth)
        {
            if (node.HasBounds)
            {
                node.ResolvedHas = true;
                node.RX = node.X; node.RY = node.Y; node.RW = node.Width; node.RH = node.Height;
            }

            var found = false;
            float minX = 0, minY = 0, maxX = 0, maxY = 0;
            if (depth < MaxWriteDepth)
            {
                for (var i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i];
                    if (child == null) continue;
                    Resolve(child, depth + 1);
                    if (node.HasBounds || !child.ResolvedHas) continue;
                    var cx = child.RX; var cy = child.RY; var cr = cx + child.RW; var cb = cy + child.RH;
                    if (!found) { minX = cx; minY = cy; maxX = cr; maxY = cb; found = true; continue; }
                    if (cx < minX) minX = cx;
                    if (cy < minY) minY = cy;
                    if (cr > maxX) maxX = cr;
                    if (cb > maxY) maxY = cb;
                }
            }

            if (node.HasBounds) return;
            node.ResolvedHas = found;
            node.RX = minX; node.RY = minY; node.RW = maxX - minX; node.RH = maxY - minY;
        }

        static void WriteNode(
            StringBuilder sb, ManagedViewNode node, int[] rects, int rectCount, float scale, int depth, ref int protectedNodes)
        {
            var hasBounds = node.ResolvedHas;
            float x = node.RX, y = node.RY, w = node.RW, h = node.RH;
            // Geometry that cannot be placed cannot be proven clear of a secure rectangle, so with any registered it counts as covered.
            var isProtected = rectCount > 0 && (!hasBounds || Overlaps(rects, rectCount, x, y, w, h));
            if (isProtected && (!string.IsNullOrEmpty(node.Name) || !string.IsNullOrEmpty(node.Components))) protectedNodes++;

            sb.Append('{');
            sb.Append("\"id\":").Append(JsonWire.String(node.IdText ?? node.Id.ToString(CultureInfo.InvariantCulture)));
            sb.Append(",\"class_name\":").Append(JsonWire.String(string.IsNullOrEmpty(node.ClassName) ? "GameObject" : node.ClassName));
            sb.Append(",\"bounds\":[");
            if (hasBounds)
            {
                Number(sb, x / scale); sb.Append(',');
                Number(sb, y / scale); sb.Append(',');
                Number(sb, w / scale); sb.Append(',');
                Number(sb, h / scale);
            }
            else
            {
                sb.Append("0,0,0,0");
            }

            sb.Append(']');

            var withName = !isProtected && !string.IsNullOrEmpty(node.Name);
            var withComponents = !isProtected && !string.IsNullOrEmpty(node.Components);
            if (withName || withComponents)
            {
                sb.Append(",\"options\":{");
                if (withName) sb.Append("\"name\":").Append(JsonWire.String(node.Name));
                if (withComponents)
                {
                    if (withName) sb.Append(',');
                    sb.Append("\"components\":").Append(JsonWire.String(node.Components));
                }

                sb.Append('}');
            }

            if (node.Truncated) sb.Append(",\"truncated\":true");

            if (depth < MaxWriteDepth && node.Children.Count > 0)
            {
                var first = true;
                for (var i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i];
                    if (child == null) continue;
                    sb.Append(first ? ",\"subitems\":[" : ",");
                    first = false;
                    WriteNode(sb, child, rects, rectCount, scale, depth + 1, ref protectedNodes);
                }

                if (!first) sb.Append(']');
            }

            sb.Append('}');
        }

        static bool Overlaps(int[] rects, int count, float x, float y, float w, float h)
        {
            for (var i = 0; i < count; i++)
            {
                float left = rects[i * 4], top = rects[i * 4 + 1], right = rects[i * 4 + 2], bottom = rects[i * 4 + 3];
                if (x < right && x + w > left && y < bottom && y + h > top) return true;
            }

            return false;
        }

        static void Number(StringBuilder sb, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
            var text = Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
            sb.Append(text == "-0" ? "0" : text);
        }
    }

    /// <summary>Turns a projected screen box (Unity pixels, origin bottom left) into the node box (origin top left).</summary>
    public static class ManagedViewGeometry
    {
        /// <summary>
        /// False when the box is not a number (a projection through a degenerate matrix gives NaN, and NaN compares false with
        /// everything). Otherwise the box is clamped to one display of margin on every side, so a sky dome or ground plane keeps
        /// its place in the tree without reporting a box thousands of screens wide, and the Y axis is flipped.
        /// </summary>
        public static bool TryBox(
            float minX, float minY, float maxX, float maxY, float screenWidth, float screenHeight,
            out float x, out float y, out float width, out float height)
        {
            x = y = width = height = 0f;
            if (float.IsNaN(minX) || float.IsNaN(minY) || float.IsNaN(maxX) || float.IsNaN(maxY)) return false;
            if (!(screenWidth > 0f) || !(screenHeight > 0f)) return false;
            minX = Clamp(minX, -screenWidth, 2f * screenWidth);
            maxX = Clamp(maxX, -screenWidth, 2f * screenWidth);
            minY = Clamp(minY, -screenHeight, 2f * screenHeight);
            maxY = Clamp(maxY, -screenHeight, 2f * screenHeight);
            if (maxX < minX || maxY < minY) return false;
            x = minX;
            y = screenHeight - maxY;
            width = maxX - minX;
            height = maxY - minY;
            return true;
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    /// <summary>
    /// When the wrapper may answer a <c>vh</c> request, and the one-reply rule. The SDK holds a report for the reply, so a
    /// request must never wait on a thread the SDK itself is blocking, and a reply must never be given twice.
    /// </summary>
    public static class ManagedViewRequests
    {
        /// <summary>The SDK gives up 500 ms after the request; a walk that starts later than this cannot be used.</summary>
        public const long MaxQueueDelayMs = 250;
        /// <summary>The walk and the JSON writing share this much time.</summary>
        public const long MaxBuildMs = 120;

        static int _blockingNativeCalls;

        /// <summary>
        /// True while Unity's main thread is inside a native call that the SDK completes synchronously (a logged exception):
        /// the SDK then waits for the reply on that very thread, which would be waiting for the main thread. It is answered null
        /// at once instead, and the report carries the native tree only.
        /// </summary>
        public static bool IsMainThreadInBlockingNativeCall => System.Threading.Volatile.Read(ref _blockingNativeCalls) > 0;

        /// <summary>Call on Unity's main thread before such a native call; pair with <see cref="ExitBlockingNativeCall"/> in a finally.</summary>
        public static void EnterBlockingNativeCall() => System.Threading.Interlocked.Increment(ref _blockingNativeCalls);

        public static void ExitBlockingNativeCall() => System.Threading.Interlocked.Decrement(ref _blockingNativeCalls);

        /// <summary>True when the request waited too long for the main thread for its answer to still count.</summary>
        public static bool IsStale(long askedAtMs, long nowMs) => nowMs - askedAtMs > MaxQueueDelayMs;

        /// <summary>The first call of <see cref="Reply"/> reaches <paramref name="reply"/>; later ones are ignored.</summary>
        public sealed class Once
        {
            readonly Action<string> _reply;
            int _done;

            public Once(Action<string> reply) { _reply = reply; }

            public bool Reply(string json)
            {
                if (_reply == null || System.Threading.Interlocked.Exchange(ref _done, 1) != 0) return false;
                _reply(json);
                return true;
            }
        }
    }
}
