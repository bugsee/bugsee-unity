#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

namespace Bugsee.Platform.Android
{
    /// <summary>
    /// Finds Unity's SurfaceView and reads its position on the screen and its size, with cached JNI handles: each read is
    /// three calls (getLocationOnScreen, getWidth, getHeight) plus isAttachedToWindow, so it can run every frame. The SDK
    /// takes secure rectangles in screen space (its own views are measured by their bounds on the screen), so the position is
    /// getLocationOnScreen, which also includes a multi-window offset. Main thread only; every JNI call is followed by a
    /// pending-exception check.
    /// </summary>
    sealed class SecureRectSurfaceProbe
    {
        const int ContentViewId = 16908290; // android.R.id.content

        IntPtr _viewClass, _surfaceClass, _groupClass, _rectClass, _playerClass, _activityClass, _holderClass;
        IntPtr _currentActivity, _findViewById, _getLocationOnScreen, _getWidth, _getHeight, _isAttached,
               _getChildCount, _getChildAt, _getHolder, _getSurfaceFrame, _rectWidth, _rectHeight;
        IntPtr _surface;            // global ref to the chosen SurfaceView
        IntPtr _location;           // global ref to an int[2]
        int _pickedForW, _pickedForH;
        bool _ready, _initFailed;

        public bool TryRead(int screenWidth, int screenHeight, out int left, out int top, out int width, out int height)
        {
            left = top = width = height = 0;
            try
            {
                if (_initFailed) return false;
                if (!_ready && !Init()) { ReleaseAll(); _initFailed = true; return false; }
                if (_surface == IntPtr.Zero || screenWidth != _pickedForW || screenHeight != _pickedForH || !IsAttached())
                {
                    DropSurface();
                    if (!Pick(screenWidth, screenHeight)) return false;
                }
                var args = new jvalue[1];
                args[0].l = _location;
                AndroidJNI.CallVoidMethod(_surface, _getLocationOnScreen, args);
                if (Pending()) { DropSurface(); return false; }
                left = AndroidJNI.GetIntArrayElement(_location, 0);
                top = AndroidJNI.GetIntArrayElement(_location, 1);
                var none = new jvalue[0];
                width = AndroidJNI.CallIntMethod(_surface, _getWidth, none);
                height = AndroidJNI.CallIntMethod(_surface, _getHeight, none);
                if (Pending()) { DropSurface(); return false; }
                return width > 0 && height > 0;
            }
            catch (Exception)
            {
                Pending();
                if (!_ready) { ReleaseAll(); _initFailed = true; }
                return false;
            }
        }

        bool Init()
        {
            _viewClass = Global(AndroidJNI.FindClass("android/view/View"));
            _surfaceClass = Global(AndroidJNI.FindClass("android/view/SurfaceView"));
            _groupClass = Global(AndroidJNI.FindClass("android/view/ViewGroup"));
            _rectClass = Global(AndroidJNI.FindClass("android/graphics/Rect"));
            _holderClass = Global(AndroidJNI.FindClass("android/view/SurfaceHolder"));
            _playerClass = Global(AndroidJNI.FindClass("com/unity3d/player/UnityPlayer"));
            _activityClass = Global(AndroidJNI.FindClass("android/app/Activity"));
            if (Pending() || _viewClass == IntPtr.Zero || _playerClass == IntPtr.Zero) return false;
            _currentActivity = AndroidJNI.GetStaticFieldID(_playerClass, "currentActivity", "Landroid/app/Activity;");
            _findViewById = AndroidJNI.GetMethodID(_activityClass, "findViewById", "(I)Landroid/view/View;");
            _getLocationOnScreen = AndroidJNI.GetMethodID(_viewClass, "getLocationOnScreen", "([I)V");
            _getWidth = AndroidJNI.GetMethodID(_viewClass, "getWidth", "()I");
            _getHeight = AndroidJNI.GetMethodID(_viewClass, "getHeight", "()I");
            _isAttached = AndroidJNI.GetMethodID(_viewClass, "isAttachedToWindow", "()Z");
            _getChildCount = AndroidJNI.GetMethodID(_groupClass, "getChildCount", "()I");
            _getChildAt = AndroidJNI.GetMethodID(_groupClass, "getChildAt", "(I)Landroid/view/View;");
            _getHolder = AndroidJNI.GetMethodID(_surfaceClass, "getHolder", "()Landroid/view/SurfaceHolder;");
            _getSurfaceFrame = AndroidJNI.GetMethodID(_holderClass, "getSurfaceFrame", "()Landroid/graphics/Rect;");
            _rectWidth = AndroidJNI.GetMethodID(_rectClass, "width", "()I");
            _rectHeight = AndroidJNI.GetMethodID(_rectClass, "height", "()I");
            IntPtr loc = AndroidJNI.ToIntArray(new int[2]);
            _location = Global(loc);
            if (Pending()) return false;
            _ready = true;
            return true;
        }

        // Releases every global ref created so far; after a failed Init nothing is left behind and Init is not tried again.
        void ReleaseAll()
        {
            foreach (IntPtr r in new[] { _viewClass, _surfaceClass, _groupClass, _rectClass, _holderClass, _playerClass, _activityClass, _location })
                if (r != IntPtr.Zero) AndroidJNI.DeleteGlobalRef(r);
            _viewClass = _surfaceClass = _groupClass = _rectClass = _holderClass = _playerClass = _activityClass = _location = IntPtr.Zero;
            DropSurface();
        }

        static IntPtr Global(IntPtr local)
        {
            if (local == IntPtr.Zero) return IntPtr.Zero;
            IntPtr g = AndroidJNI.NewGlobalRef(local);
            AndroidJNI.DeleteLocalRef(local);
            return g;
        }

        // Pending Java exceptions would poison the next JNI call: clear them and report that there was one.
        static bool Pending()
        {
            if (AndroidJNI.ExceptionOccurred() == IntPtr.Zero) return false;
            AndroidJNI.ExceptionClear();
            return true;
        }

        bool IsAttached()
        {
            bool attached = AndroidJNI.CallBooleanMethod(_surface, _isAttached, new jvalue[0]);
            return !Pending() && attached;
        }

        void DropSurface()
        {
            if (_surface != IntPtr.Zero) AndroidJNI.DeleteGlobalRef(_surface);
            _surface = IntPtr.Zero;
        }

        // Unity's view: the SurfaceView whose buffer has Unity's screen size (Resolution Scaling and a stretched buffer
        // both keep that equal), else the first one. A plugin's SurfaceView of another size is skipped.
        bool Pick(int screenWidth, int screenHeight)
        {
            IntPtr activity = AndroidJNI.GetStaticObjectField(_playerClass, _currentActivity);
            if (Pending() || activity == IntPtr.Zero) return false;
            IntPtr content = IntPtr.Zero;
            try
            {
                var args = new jvalue[1];
                args[0].i = ContentViewId;
                content = AndroidJNI.CallObjectMethod(activity, _findViewById, args);
                if (Pending() || content == IntPtr.Zero) return false;
                IntPtr first = IntPtr.Zero;
                IntPtr match = Find(content, screenWidth, screenHeight, 0, ref first);
                IntPtr chosen = match != IntPtr.Zero ? match : first;
                if (match != IntPtr.Zero && first != IntPtr.Zero && first != match) AndroidJNI.DeleteLocalRef(first);
                if (chosen == IntPtr.Zero) return false;
                _surface = AndroidJNI.NewGlobalRef(chosen);
                AndroidJNI.DeleteLocalRef(chosen);
                _pickedForW = screenWidth;
                _pickedForH = screenHeight;
                return true;
            }
            finally
            {
                if (content != IntPtr.Zero) AndroidJNI.DeleteLocalRef(content);
                AndroidJNI.DeleteLocalRef(activity);
            }
        }

        // Returns a local ref to the first SurfaceView with a buffer of the screen's size; the first SurfaceView seen at all
        // goes to `first` (a local ref, the caller releases it unless it is the one returned).
        IntPtr Find(IntPtr view, int screenWidth, int screenHeight, int depth, ref IntPtr first)
        {
            if (AndroidJNI.IsInstanceOf(view, _surfaceClass))
            {
                if (BufferMatches(view, screenWidth, screenHeight)) return AndroidJNI.NewLocalRef(view);
                if (first == IntPtr.Zero) first = AndroidJNI.NewLocalRef(view);
                return IntPtr.Zero;
            }
            if (depth > 8 || !AndroidJNI.IsInstanceOf(view, _groupClass)) return IntPtr.Zero;
            int count = AndroidJNI.CallIntMethod(view, _getChildCount, new jvalue[0]);
            if (Pending()) return IntPtr.Zero;
            for (int i = 0; i < count; i++)
            {
                var args = new jvalue[1];
                args[0].i = i;
                IntPtr child = AndroidJNI.CallObjectMethod(view, _getChildAt, args);
                if (Pending() || child == IntPtr.Zero) continue;
                IntPtr found = Find(child, screenWidth, screenHeight, depth + 1, ref first);
                AndroidJNI.DeleteLocalRef(child);
                if (found != IntPtr.Zero) return found;
            }
            return IntPtr.Zero;
        }

        bool BufferMatches(IntPtr surfaceView, int screenWidth, int screenHeight)
        {
            IntPtr holder = AndroidJNI.CallObjectMethod(surfaceView, _getHolder, new jvalue[0]);
            if (Pending() || holder == IntPtr.Zero) return false;
            IntPtr rect = AndroidJNI.CallObjectMethod(holder, _getSurfaceFrame, new jvalue[0]);
            AndroidJNI.DeleteLocalRef(holder);
            if (Pending() || rect == IntPtr.Zero) return false;
            int w = AndroidJNI.CallIntMethod(rect, _rectWidth, new jvalue[0]);
            int h = AndroidJNI.CallIntMethod(rect, _rectHeight, new jvalue[0]);
            AndroidJNI.DeleteLocalRef(rect);
            return !Pending() && w == screenWidth && h == screenHeight;
        }
    }
}
#endif
