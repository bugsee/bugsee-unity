using System.Collections;
using Bugsee.Internal;
using UnityEngine;
using UnityEngine.UI;

namespace Bugsee.Sample
{
    /// <summary>
    /// Device check for issue #291 (BUGSEE_E2E_SCENARIO=vhsecurescale): the managed view hierarchy compared node boxes in Unity render
    /// pixels with secure rectangles in native pixels. It builds an overlay canvas with a named card, puts a secure rectangle exactly over
    /// the card (<c>AddSecureScreenRectangle</c>), captures the hierarchy at the native resolution and again after
    /// <c>Screen.SetResolution</c> to half, and compares the number of nodes that lost their name or components (the wrapper's
    /// <see cref="ManagedViewDiagnostics"/> record, so release builds work too). With the fix the two counts are equal; before it the
    /// scaled capture missed the card. Logs <c>[Sample] vhsecurescale PASS|FAIL</c> lines and <c>[VHSECURESCALE]</c> numbers.
    /// </summary>
    static class VhSecureScaleE2E
    {
        public static IEnumerator Run()
        {
            var nativeW = Screen.width;
            var nativeH = Screen.height;
            var fullscreen = Screen.fullScreen;
            Rect? rect = null;
            GameObject canvasGo = null;
            var failures = 0;
            try
            {
                Debug.Log("[VHSECURESCALE] start launched=" + Bugsee.IsLaunched + " screen=" + nativeW + "x" + nativeH);
                canvasGo = new GameObject("VhSecureScaleCanvas", typeof(Canvas));
                canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                canvasGo.GetComponent<Canvas>().sortingOrder = -100;
                var card = new GameObject("CardNumberSecret", typeof(RectTransform), typeof(Image));
                card.transform.SetParent(canvasGo.transform, false);
                var rt = (RectTransform)card.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.25f, 0.4f); // a fraction of the screen, so it scales with the resolution
                rt.sizeDelta = new Vector2(nativeW * 0.2f, nativeH * 0.05f);
                yield return null;

                // 1: native resolution.
                var native = new int[1];
                yield return Measure(card, rt, "native", native, r => rect = r);
                Remove(ref rect);

                // 2: half the resolution (the display stays native, Unity renders smaller).
                Screen.SetResolution(nativeW / 2, nativeH / 2, fullscreen);
                for (var i = 0; i < 10; i++) yield return null;
                Debug.Log("[VHSECURESCALE] screen now " + Screen.width + "x" + Screen.height);
                var scaled = new int[1];
                yield return Measure(card, rt, "half", scaled, r => rect = r);
                Remove(ref rect);

                if (native[0] < 1) { failures++; Debug.Log("[Sample] vhsecurescale FAIL the native capture secured nothing: secured=" + native[0]); }
                if (scaled[0] != native[0]) { failures++; Debug.Log("[Sample] vhsecurescale FAIL the half-resolution capture secured " + scaled[0] + " nodes, the native one " + native[0]); }
                else Debug.Log("[Sample] vhsecurescale PASS half-resolution secured=" + scaled[0] + " equals native secured=" + native[0]);
            }
            finally
            {
                Remove(ref rect);
                Screen.SetResolution(nativeW, nativeH, fullscreen);
                if (canvasGo != null) Object.Destroy(canvasGo);
                Debug.Log("[Sample] vhsecurescale done failures=" + failures);
            }
        }

        static void Remove(ref Rect? rect)
        {
            if (rect == null) return;
            Bugsee.RemoveSecureScreenRectangle(rect.Value);
            rect = null;
        }

        static IEnumerator Measure(GameObject card, RectTransform rt, string label, int[] secured, System.Action<Rect> remember)
        {
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners); // an overlay canvas: screen pixels, origin bottom left
            var rect = Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
            Bugsee.AddSecureScreenRectangle(rect);
            remember(rect);
            yield return null;
            yield return null;
            var before = ManagedViewDiagnostics.Count;
            Bugsee.CaptureViewHierarchy();
            var waited = 0f;
            while (ManagedViewDiagnostics.Count == before && waited < 10f) { waited += Time.unscaledDeltaTime; yield return null; }
            var last = ManagedViewDiagnostics.Last;
            secured[0] = ManagedViewDiagnostics.Count > before && last != null ? last.Secured : -1;
            Debug.Log("[VHSECURESCALE] " + label + " screen=" + Screen.width + "x" + Screen.height + " rect=" + rect + " nodes=" + (last != null ? last.Nodes : -1)
                + " secured=" + secured[0] + " outcome=" + (last != null ? last.Outcome : "none"));
            yield return new WaitForSecondsRealtime(1f);
        }
    }
}
