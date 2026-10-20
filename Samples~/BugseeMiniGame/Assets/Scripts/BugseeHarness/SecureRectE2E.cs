using System.Globalization;
using Bugsee.WrapperPolicy;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Scenario "securerect": paints a green box at a Unity screen-space rectangle (bottom-left origin), registers the same
    /// rectangle with Bugsee.AddSecureScreenRectangle, and logs, tagged "[SECURERECT]", Unity's screen size, the orientation, the
    /// rectangle the wrapper registered in native pixels and where the Unity surface sits, again whenever any of them changes.
    /// Compare with a screenshot of the device: the box and the native rectangle must cover the same pixels (scaled by the
    /// surface over the screenshot). "bugsee_secure_scale" / "BUGSEE_SECURE_SCALE" = N renders at 1/N resolution first; "bugsee_secure_rotate_s" / "BUGSEE_SECURE_ROTATE_S" = S rotates after S seconds.
    /// </summary>
    public sealed class SecureRectE2E : MonoBehaviour
    {
        Texture2D _green;
        Rect _unityRect;
        string _last;
        float _next;
        bool _added;

        public static void Run(MonoBehaviour host, int scaleDown, float rotateAfterSeconds = 0f)
        {
            var go = new GameObject("SecureRectE2E");
            DontDestroyOnLoad(go);
            var e2e = go.AddComponent<SecureRectE2E>();
            if (scaleDown > 1)
            {
                int w = Screen.width / scaleDown, h = Screen.height / scaleDown;
                Debug.Log("[SECURERECT] SetResolution " + w + "x" + h + " (from " + Screen.width + "x" + Screen.height + ")");
                Screen.SetResolution(w, h, true);
            }
            e2e.StartCoroutine(e2e.ProbeBox());
            if (rotateAfterSeconds > 0f) e2e.StartCoroutine(e2e.RotateAfter(rotateAfterSeconds));
            e2e.Invoke(nameof(Register), scaleDown > 1 ? 1.5f : 0.1f);
        }

        void Awake()
        {
            _green = new Texture2D(1, 1);
            _green.SetPixel(0, 0, Color.green);
            _green.Apply();
        }

        // Fractions of the screen so the box means the same thing in any size and orientation.
        Rect Box() => new Rect(Screen.width * 0.1f, Screen.height * 0.15f, Screen.width * 0.5f, Screen.height * 0.2f);

        void Register()
        {
            _unityRect = Box();
            Bugsee.AddSecureScreenRectangle(_unityRect);
            _added = true;
            Debug.Log("[SECURERECT] added unityRect=" + F(_unityRect.xMin) + "," + F(_unityRect.yMin) + "," + F(_unityRect.xMax) + "," + F(_unityRect.yMax));
        }

        static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        void OnGUI()
        {
            // The box follows the screen like the registered rectangle does (re-added in Log when the screen changes).
            GUI.depth = -2000;
            var r = Box();
            GUI.DrawTexture(new Rect(r.xMin, Screen.height - r.yMax, r.width, r.height), _green);
        }

        int[] _rects;

        // Turns the interface to the other orientation (no sensor needed); the rectangle must follow without being added again.
        System.Collections.IEnumerator RotateAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            bool toPortrait = Screen.width > Screen.height;
            Screen.orientation = toPortrait ? ScreenOrientation.Portrait : ScreenOrientation.LandscapeLeft;
            Debug.Log("[SECURERECT] rotated to " + (toPortrait ? "portrait" : "landscape"));
        }
        string _lastBox;

        // Reads the rendered frame back and logs the bounding box of the green pixels (top-left origin, render pixels): where the
        // box really landed, to compare with nativeRects without a screenshot tool. Render pixels equal native pixels on iOS;
        // on Android they are scaled by the surface over Screen.
        System.Collections.IEnumerator ProbeBox()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                if (!_added) { yield return new WaitForSeconds(1f); continue; }
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                try
                {
                    var px = tex.GetPixels32();
                    int w = tex.width, h = tex.height, minX = w, minY = h, maxX = -1, maxY = -1;
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            var c = px[y * w + x];
                            if (c.g > 200 && c.r < 60 && c.b < 60)
                            {
                                if (x < minX) minX = x; if (x > maxX) maxX = x;
                                if (y < minY) minY = y; if (y > maxY) maxY = y;
                            }
                        }
                    // The texture's row 0 is the bottom; flip to a top-left origin.
                    string box = maxX < 0 ? "none" : minX + "," + (h - 1 - maxY) + "," + (maxX + 1) + "," + (h - minY);
                    if (box != _lastBox)
                    {
                        _lastBox = box;
                        Debug.Log("[SECURERECT] rendered green box (top-left, render px of " + w + "x" + h + ") = " + box);
                    }
                }
                finally { Destroy(tex); }
                yield return new WaitForSeconds(1.5f);
            }
        }

        void Update()
        {
            if (!_added || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            int n = SecureRectRegistry.Instance.CopyRects(0, ref _rects);
            string rects = "";
            for (int i = 0; i < n; i++)
                rects += "[" + _rects[i * 4] + "," + _rects[i * 4 + 1] + "," + _rects[i * 4 + 2] + "," + _rects[i * 4 + 3] + "]";
            string now = "screen=" + Screen.width + "x" + Screen.height + " orientation=" + Screen.orientation + " safeArea=" + Screen.safeArea
                + " nativeRects=" + rects;
            if (now == _last) return;
            _last = now;
            // Unity rect of the box at this size, to compare with nativeRects (top-left origin). The box is laid out by fractions of
            // the screen and the wrapper keeps the rectangle's fraction too, so they match after a rotation without adding again.
            Debug.Log("[SECURERECT] " + now + " boxUnity=" + Box());
        }
    }
}
