using System;
using System.Collections.Generic;

namespace Bugsee.WrapperPolicy
{
    /// <summary>What a bridge read about where Unity's surface sits, plus the sizes the fallback needs.</summary>
    public struct SecureRectViewRead
    {
        public int ScreenWidth, ScreenHeight;
        /// <summary>The surface could be read.</summary>
        public bool Ok;
        /// <summary>The surface agrees with Unity's screen (false: a view caught mid-rotation, so the numbers are not trusted).</summary>
        public bool Settled;
        public int Left, Top, Width, Height;
        /// <summary>The display's real size in native pixels: the conservative surface when none was ever read.</summary>
        public int FallbackWidth, FallbackHeight;
    }

    public enum SecureRectViewState { Ok, Unsettled, Unreadable }

    /// <summary>
    /// Keeps the last surface that was read and settled, so a failed or unsettled read never turns into "no scale, no offset":
    /// it keeps the last good surface, or the display's real size when none was read yet.
    /// </summary>
    public sealed class SecureRectViewResolver
    {
        bool _hasGood;
        int _gl, _gt, _gw, _gh;

        public SecureRectViewState State { get; private set; } = SecureRectViewState.Ok;

        public SecureRectView Resolve(SecureRectViewRead read, out bool stateChanged)
        {
            SecureRectViewState next;
            SecureRectView view;
            if (read.Ok && read.Settled)
            {
                _hasGood = true;
                _gl = read.Left; _gt = read.Top; _gw = read.Width; _gh = read.Height;
                next = SecureRectViewState.Ok;
                view = new SecureRectView(read.ScreenWidth, read.ScreenHeight, _gl, _gt, _gw, _gh);
            }
            else
            {
                next = read.Ok ? SecureRectViewState.Unsettled : SecureRectViewState.Unreadable;
                // Nothing good yet: a surface that was read but looks mid-rotation still beats "no scale" (it is the right
                // scale whenever the render buffer is merely stretched); only an unreadable one falls back to the display size.
                view = _hasGood
                    ? new SecureRectView(read.ScreenWidth, read.ScreenHeight, _gl, _gt, _gw, _gh)
                    : read.Ok && read.Width > 0 && read.Height > 0
                    ? new SecureRectView(read.ScreenWidth, read.ScreenHeight, read.Left, read.Top, read.Width, read.Height)
                    : new SecureRectView(read.ScreenWidth, read.ScreenHeight, 0, 0,
                        read.FallbackWidth > 0 ? read.FallbackWidth : read.ScreenWidth,
                        read.FallbackHeight > 0 ? read.FallbackHeight : read.ScreenHeight);
            }
            stateChanged = next != State;
            State = next;
            return view;
        }
    }

    /// <summary>
    /// The screen-space secure rectangle facade without Unity: owners, the per-display view resolution, and pushing the buffer
    /// when something changed. All calls run on the Unity main thread.
    /// </summary>
    public sealed class SecureRectScreenSync
    {
        readonly SecureRectRegistry _registry;
        readonly SecureRectScreenOwners _owners = new SecureRectScreenOwners();
        readonly Dictionary<int, SecureRectViewResolver> _resolvers = new Dictionary<int, SecureRectViewResolver>();
        readonly Func<int, SecureRectViewRead> _read;
        readonly Action<int> _push;
        readonly Action<string> _warn;

        public SecureRectScreenSync(SecureRectRegistry registry, Func<int, SecureRectViewRead> read, Action<int> push, Action<string> warn)
        {
            _registry = registry;
            _read = read;
            _push = push;
            _warn = warn;
        }

        public bool HasEntries => _owners.HasEntries;

        SecureRectView Resolve(int displayId, out SecureRectViewState state)
        {
            if (!_resolvers.TryGetValue(displayId, out var resolver))
                _resolvers[displayId] = resolver = new SecureRectViewResolver();
            var view = resolver.Resolve(_read(displayId), out bool changed);
            state = resolver.State;
            if (changed && state != SecureRectViewState.Ok)
                _warn("Could not read where Unity's view sits on display " + displayId
                    + " (" + (state == SecureRectViewState.Unreadable ? "unreadable" : "does not match Unity's screen")
                    + "); screen-space secure rectangles use the last surface read, or the display's full size, until it can be read.");
            return view;
        }

        /// <param name="screenWidth">Unity's screen size when the app made the call, when the call was made on the main thread; 0 reads it now
        /// (a call queued from another thread). Without it a resolution change between the call and the frame that runs it would be
        /// taken for the size the rectangle was given for.</param>
        public void Add(float xMin, float yMin, float xMax, float yMax, int displayId, int screenWidth = 0, int screenHeight = 0)
        {
            if (float.IsNaN(xMin) || float.IsNaN(yMin) || float.IsNaN(xMax) || float.IsNaN(yMax))
            {
                _warn("AddSecureScreenRectangle ignored: the rectangle has a NaN component.");
                return;
            }
            var view = Resolve(displayId, out _);
            if (!view.IsValid)
            {
                _warn("AddSecureScreenRectangle ignored: display " + displayId + " has no usable size (it does not exist, or is not active).");
                return;
            }
            if (screenWidth > 0 && screenHeight > 0)
                view = new SecureRectView(screenWidth, screenHeight, view.Left, view.Top, view.Width, view.Height);
            _owners.Add(xMin, yMin, xMax, yMax, displayId, view, _registry);
            _push(displayId);
        }

        public void Remove(float xMin, float yMin, float xMax, float yMax, int displayId)
        {
            if (_owners.Remove(xMin, yMin, xMax, yMax, displayId, _registry)) _push(displayId);
        }

        public void RemoveAll()
        {
            foreach (int display in _owners.RemoveAll(_registry)) _push(display);
        }

        /// <summary>Per frame: convert again when the surface or the screen changed.</summary>
        public void Tick()
        {
            if (!_owners.HasEntries) return;
            foreach (int display in _owners.Displays())
            {
                var view = Resolve(display, out var state);
                // A view caught mid-rotation: keep what is registered; the next frame reads it again.
                if (state == SecureRectViewState.Unsettled || !view.IsValid) continue;
                if (_owners.Refresh(display, view, _registry)) _push(display);
            }
        }
    }
}
