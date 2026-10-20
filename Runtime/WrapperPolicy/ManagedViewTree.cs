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
        public string ClassName;
        /// <summary>The GameObject's name. A free-text label, so it is withheld for protected nodes.</summary>
        public string Name;
        /// <summary>The component type names on the object, comma separated. Code identifiers, never content.</summary>
        public string Components;
        public bool HasBounds;
        public float X, Y, Width, Height;
        public readonly List<ManagedViewNode> Children = new List<ManagedViewNode>();
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

        /// <summary>True when no further node can be taken; the walk should stop looking at siblings.</summary>
        public bool IsExhausted => _taken >= _maxNodes || (_expired != null && _expired());

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
        /// <param name="truncated">Marks the root: the tree is incomplete because the walk hit its budget.</param>
        public static string Write(
            ManagedViewNode root, int[] secureRects, int secureCount, float pixelsPerNativeUnit, bool truncated)
        {
            if (root == null) return null;
            if (!(pixelsPerNativeUnit > 0f) || float.IsInfinity(pixelsPerNativeUnit)) pixelsPerNativeUnit = 1f;
            if (secureRects == null) secureCount = 0;
            else if (secureCount * 4 > secureRects.Length) secureCount = secureRects.Length / 4;

            var sb = new StringBuilder(4096);
            WriteNode(sb, root, secureRects, secureCount, pixelsPerNativeUnit, truncated, 1);
            return sb.ToString();
        }

        /// <summary>
        /// The bounds a node reports: its own, or the union of its children's when it has none (an empty container).
        /// False when nothing below it has geometry either.
        /// </summary>
        public static bool TryResolveBounds(ManagedViewNode node, out float x, out float y, out float w, out float h)
        {
            return TryResolveBounds(node, 1, out x, out y, out w, out h);
        }

        static bool TryResolveBounds(ManagedViewNode node, int depth, out float x, out float y, out float w, out float h)
        {
            if (node.HasBounds)
            {
                x = node.X; y = node.Y; w = node.Width; h = node.Height;
                return true;
            }

            var found = false;
            float minX = 0, minY = 0, maxX = 0, maxY = 0;
            if (depth < MaxWriteDepth)
            {
                for (var i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i];
                    if (child == null || !TryResolveBounds(child, depth + 1, out var cx, out var cy, out var cw, out var ch)) continue;
                    if (!found)
                    {
                        minX = cx; minY = cy; maxX = cx + cw; maxY = cy + ch;
                        found = true;
                        continue;
                    }

                    if (cx < minX) minX = cx;
                    if (cy < minY) minY = cy;
                    if (cx + cw > maxX) maxX = cx + cw;
                    if (cy + ch > maxY) maxY = cy + ch;
                }
            }

            x = minX; y = minY; w = maxX - minX; h = maxY - minY;
            return found;
        }

        static void WriteNode(
            StringBuilder sb, ManagedViewNode node, int[] rects, int rectCount, float scale, bool truncated, int depth)
        {
            var hasBounds = TryResolveBounds(node, out var x, out var y, out var w, out var h);
            // Geometry that cannot be placed cannot be proven clear of a secure rectangle, so with any registered it counts as covered.
            var isProtected = rectCount > 0 && (!hasBounds || Overlaps(rects, rectCount, x, y, w, h));

            sb.Append('{');
            sb.Append("\"id\":").Append(JsonWire.String(node.Id.ToString(CultureInfo.InvariantCulture)));
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

            var options = new StringBuilder();
            if (!isProtected && !string.IsNullOrEmpty(node.Name))
                options.Append("\"name\":").Append(JsonWire.String(node.Name));
            if (!isProtected && !string.IsNullOrEmpty(node.Components))
            {
                if (options.Length > 0) options.Append(',');
                options.Append("\"components\":").Append(JsonWire.String(node.Components));
            }

            if (truncated)
            {
                if (options.Length > 0) options.Append(',');
                options.Append("\"unity_truncated\":true");
            }

            if (options.Length > 0) sb.Append(",\"options\":{").Append(options).Append('}');

            if (depth < MaxWriteDepth && node.Children.Count > 0)
            {
                var first = true;
                for (var i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i];
                    if (child == null) continue;
                    sb.Append(first ? ",\"subitems\":[" : ",");
                    first = false;
                    WriteNode(sb, child, rects, rectCount, scale, false, depth + 1);
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
}
