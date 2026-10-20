using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>
    /// Where Unity's rendered surface sits on its display, in the native pixels secure rectangles are registered in
    /// (Android window pixels, iOS points times the screen scale), and how big Unity's own screen is for that display.
    /// </summary>
    public readonly struct SecureRectView : IEquatable<SecureRectView>
    {
        public SecureRectView(int screenWidth, int screenHeight, int left, int top, int width, int height)
        {
            ScreenWidth = screenWidth;
            ScreenHeight = screenHeight;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        /// <summary>Unity's <c>Screen.width</c> / <c>Screen.height</c> (render pixels, possibly scaled down).</summary>
        public int ScreenWidth { get; }
        public int ScreenHeight { get; }

        /// <summary>The Unity surface in native pixels, top-left origin.</summary>
        public int Left { get; }
        public int Top { get; }
        public int Width { get; }
        public int Height { get; }

        public bool IsValid => ScreenWidth > 0 && ScreenHeight > 0 && Width > 0 && Height > 0;

        public bool Equals(SecureRectView o) =>
            ScreenWidth == o.ScreenWidth && ScreenHeight == o.ScreenHeight
            && Left == o.Left && Top == o.Top && Width == o.Width && Height == o.Height;

        public override bool Equals(object obj) => obj is SecureRectView o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = ScreenWidth;
                h = h * 397 + ScreenHeight;
                h = h * 397 + Left;
                h = h * 397 + Top;
                h = h * 397 + Width;
                return h * 397 + Height;
            }
        }
    }

    /// <summary>Converts a rectangle in Unity screen space (bottom-left origin, render pixels) to native pixels (top-left origin).</summary>
    public static class SecureRectSpace
    {
        /// <summary>
        /// Flips the y axis, scales by the surface size over Unity's screen size (Resolution Scaling, <c>Screen.SetResolution</c>),
        /// adds the surface offset in the window (status bar, cutout, multi-window), and rounds outwards so the native
        /// rectangle covers at least what was asked. The result is clamped to the surface; false when nothing of the
        /// rectangle is left (off screen, empty) or the view is unknown.
        /// </summary>
        public static bool TryToNative(
            float xMin, float yMin, float xMax, float yMax, SecureRectView view,
            out int left, out int top, out int right, out int bottom)
        {
            left = top = right = bottom = 0;
            if (!view.IsValid) return false;
            if (float.IsNaN(xMin) || float.IsNaN(yMin) || float.IsNaN(xMax) || float.IsNaN(yMax)) return false;

            double sx = (double)view.Width / view.ScreenWidth;
            double sy = (double)view.Height / view.ScreenHeight;
            // Unity's y grows upward; the native top edge is the Unity top (largest y).
            double topPx = (view.ScreenHeight - Math.Max(yMin, yMax)) * sy;
            double bottomPx = (view.ScreenHeight - Math.Min(yMin, yMax)) * sy;
            double leftPx = Math.Min(xMin, xMax) * sx;
            double rightPx = Math.Max(xMin, xMax) * sx;

            leftPx = Math.Max(0, Math.Min(view.Width, leftPx));
            rightPx = Math.Max(0, Math.Min(view.Width, rightPx));
            topPx = Math.Max(0, Math.Min(view.Height, topPx));
            bottomPx = Math.Max(0, Math.Min(view.Height, bottomPx));

            left = view.Left + (int)Math.Floor(leftPx);
            top = view.Top + (int)Math.Floor(topPx);
            right = view.Left + (int)Math.Ceiling(rightPx);
            bottom = view.Top + (int)Math.Ceiling(bottomPx);
            return right > left && bottom > top;
        }
    }

    /// <summary>
    /// Secure rectangles the app gave in Unity screen space. They are kept as given, with the screen size they were given
    /// for, and converted again whenever the view they convert through changes (resolution, the surface moving in its window).
    /// Adding the same rectangle twice counts twice: it is hidden until it was removed as often as it was added.
    /// </summary>
    public sealed class SecureRectScreenOwners
    {
        public const string OwnerIdPrefix = "screen:";

        struct Entry
        {
            public float XMin, YMin, XMax, YMax;
            public int DisplayId;
            public int Count;
            // Unity's screen size when it was added: the rectangle means that fraction of the screen from then on.
            public int AddedScreenWidth, AddedScreenHeight;
        }

        readonly object _sync = new object();
        readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        readonly Dictionary<int, SecureRectView> _appliedViews = new Dictionary<int, SecureRectView>();

        // + 0f turns -0 into +0, so equal rectangles have one key.
        static string Fmt(float v) => (v + 0f).ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        static string OwnerId(float xMin, float yMin, float xMax, float yMax, int displayId) =>
            OwnerIdPrefix + displayId + ":" + Fmt(xMin) + "," + Fmt(yMin) + "," + Fmt(xMax) + "," + Fmt(yMax);

        public bool HasEntries { get { lock (_sync) return _entries.Count > 0; } }

        public void Add(float xMin, float yMin, float xMax, float yMax, int displayId, SecureRectView view, SecureRectRegistry registry)
        {
            lock (_sync)
            {
                string id = OwnerId(xMin, yMin, xMax, yMax, displayId);
                int count = _entries.TryGetValue(id, out var existing) ? existing.Count : 0;
                var entry = new Entry
                {
                    XMin = xMin, YMin = yMin, XMax = xMax, YMax = yMax, DisplayId = displayId, Count = count + 1,
                    AddedScreenWidth = count > 0 ? existing.AddedScreenWidth : view.ScreenWidth,
                    AddedScreenHeight = count > 0 ? existing.AddedScreenHeight : view.ScreenHeight,
                };
                _entries[id] = entry;
                // A view that is not the one the other rectangles were converted with converts all of them: otherwise they
                // would keep the old conversion and the next refresh would see nothing to do.
                if (_appliedViews.TryGetValue(displayId, out var applied) && !applied.Equals(view))
                    ApplyDisplay(displayId, view, registry);
                else
                    Apply(id, entry, view, registry);
                _appliedViews[displayId] = view;
            }
        }

        public bool Remove(float xMin, float yMin, float xMax, float yMax, int displayId, SecureRectRegistry registry)
        {
            lock (_sync)
            {
                string id = OwnerId(xMin, yMin, xMax, yMax, displayId);
                if (!_entries.TryGetValue(id, out var entry)) return false;
                if (entry.Count > 1)
                {
                    entry.Count--;
                    _entries[id] = entry;
                    return true;
                }
                _entries.Remove(id);
                registry.RemoveOwner(id);
                return true;
            }
        }

        /// <summary>Removes every rectangle; returns the displays that had one.</summary>
        public List<int> RemoveAll(SecureRectRegistry registry)
        {
            lock (_sync)
            {
                var displays = DisplaysLocked();
                registry.RemoveOwnersWithPrefix(OwnerIdPrefix);
                _entries.Clear();
                _appliedViews.Clear();
                return displays;
            }
        }

        /// <summary>
        /// Converts the rectangles of <paramref name="displayId"/> again if <paramref name="view"/> is not the view they were
        /// last converted with. Returns true when the registry changed (the caller pushes a new buffer).
        /// </summary>
        public bool Refresh(int displayId, SecureRectView view, SecureRectRegistry registry)
        {
            lock (_sync)
            {
                if (_appliedViews.TryGetValue(displayId, out var applied) && applied.Equals(view)) return false;
                ApplyDisplay(displayId, view, registry);
                _appliedViews[displayId] = view;
                return true;
            }
        }

        public List<int> Displays()
        {
            lock (_sync) return DisplaysLocked();
        }

        List<int> DisplaysLocked()
        {
            var displays = new List<int>();
            foreach (var e in _entries.Values)
                if (!displays.Contains(e.DisplayId)) displays.Add(e.DisplayId);
            return displays;
        }

        void ApplyDisplay(int displayId, SecureRectView view, SecureRectRegistry registry)
        {
            foreach (var pair in _entries)
                if (pair.Value.DisplayId == displayId) Apply(pair.Key, pair.Value, view, registry);
        }

        static void Apply(string id, Entry e, SecureRectView view, SecureRectRegistry registry)
        {
            // The rectangle is in the pixels of the screen it was added on; against a new screen size it keeps its fraction of
            // the screen (Resolution Scaling, same aspect). Content that is anchored rather than laid out by fractions, or a
            // different aspect (rotation), needs the app to remove the rectangle and add it again.
            // Off screen or an unknown view registers nothing.
            var asAdded = new SecureRectView(e.AddedScreenWidth, e.AddedScreenHeight, view.Left, view.Top, view.Width, view.Height);
            if (SecureRectSpace.TryToNative(e.XMin, e.YMin, e.XMax, e.YMax, asAdded, out int l, out int t, out int r, out int b))
                registry.Set(id, e.DisplayId, l, t, r, b);
            else
                registry.RemoveOwner(id);
        }
    }
}
