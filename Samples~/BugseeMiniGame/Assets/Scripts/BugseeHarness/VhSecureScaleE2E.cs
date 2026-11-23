using System.Collections;
using System.Collections.Generic;
using Bugsee.Internal;
using UnityEngine;

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
        sealed class Runner : MonoBehaviour { }

        /// <summary>Starts the scenario on the canvas object itself: the scene's own objects (the bootstrap among them) are switched off while it runs.</summary>
        public static void Start()
        {
            var canvasGo = new GameObject("VhSecureScaleCanvas", typeof(Canvas));
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.GetComponent<Canvas>().sortingOrder = -100;
            Object.DontDestroyOnLoad(canvasGo); // a scene load must not stop the coroutine before its finally restores everything
            canvasGo.AddComponent<Runner>().StartCoroutine(Run(canvasGo));
        }

        static IEnumerator Run(GameObject canvasGo)
        {
            var nativeW = Screen.width;
            var nativeH = Screen.height;
            var fullscreen = Screen.fullScreen;
            Rect? rect = null;
            var hidden = new List<GameObject>();
            var failures = 0;
            try
            {
                Debug.Log("[VHSECURESCALE] start launched=" + Bugsee.IsLaunched + " screen=" + nativeW + "x" + nativeH);
                // The walk is sliced and capped, so the nodes it reaches vary between captures of a busy scene: the scene's own objects are
                // switched off for the run, leaving the canvas and the card, so the counts below are stable (4 on the HONOR WOD-LX1: the canvas, the card and what the host adds).
                for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                    foreach (var go in UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).GetRootGameObjects())
                        if (go != canvasGo && go.activeSelf) { go.SetActive(false); hidden.Add(go); }
                var card = new GameObject("CardNumberSecret", typeof(RectTransform));
                card.transform.SetParent(canvasGo.transform, false);
                // A named node in the opposite corner, clear of the rectangle: it must keep its name at both resolutions, so a PASS cannot
                // come from covering everything.
                var clear = new GameObject("ClearCornerNode", typeof(RectTransform));
                clear.transform.SetParent(canvasGo.transform, false);
                var crt = (RectTransform)clear.transform;
                crt.anchorMin = crt.anchorMax = new Vector2(0.9f, 0.9f);
                crt.sizeDelta = new Vector2(nativeW * 0.05f, nativeH * 0.02f);
                var rt = (RectTransform)card.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.25f, 0.4f); // a fraction of the screen, so it scales with the resolution
                rt.sizeDelta = new Vector2(nativeW * 0.2f, nativeH * 0.05f);
                yield return null;

                // 1: native resolution.
                var native = new int[2];
                yield return Measure(card, rt, "native", native, r => rect = r);
                Remove(ref rect);

                // 2: half the resolution (the display stays native, Unity renders smaller).
                Screen.SetResolution(nativeW / 2, nativeH / 2, fullscreen);
                var waitedFrames = 0;
                while (Screen.width == nativeW && Screen.height == nativeH && waitedFrames++ < 120) yield return null;
                for (var i = 0; i < 10; i++) yield return null;
                Debug.Log("[VHSECURESCALE] screen now " + Screen.width + "x" + Screen.height);
                if (Screen.width == nativeW && Screen.height == nativeH)
                {
                    Debug.Log("[Sample] vhsecurescale SKIP the resolution did not change (" + Screen.width + "x" + Screen.height + "), nothing to compare");
                    yield break;
                }
                var scaled = new int[2];
                yield return Measure(card, rt, "half", scaled, r => rect = r);
                Remove(ref rect);

                if (native[0] < 2) { failures++; Debug.Log("[Sample] vhsecurescale FAIL the native capture should secure at least the canvas and the card: secured=" + native[0]); }
                // Nodes: the unnamed root plus the clear corner node (named, not under the rectangle) never count as secured.
                foreach (var r in new[] { native, scaled })
                    if (r[0] > r[1] - 2) { failures++; Debug.Log("[Sample] vhsecurescale FAIL the clear corner node was secured too: secured=" + r[0] + " nodes=" + r[1]); }
                if (scaled[0] != native[0]) { failures++; Debug.Log("[Sample] vhsecurescale FAIL the half-resolution capture secured " + scaled[0] + " nodes, the native one " + native[0]); }
                else Debug.Log("[Sample] vhsecurescale PASS half-resolution secured=" + scaled[0] + " equals native secured=" + native[0]);
            }
            finally
            {
                Remove(ref rect);
                Screen.SetResolution(nativeW, nativeH, fullscreen);
                Object.Destroy(canvasGo);
                foreach (var go in hidden) if (go != null) go.SetActive(true);
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
            secured[1] = last != null ? last.Nodes : -1;
            Debug.Log("[VHSECURESCALE] " + label + " screen=" + Screen.width + "x" + Screen.height + " rect=" + rect + " nodes=" + (last != null ? last.Nodes : -1)
                + " secured=" + secured[0] + " outcome=" + (last != null ? last.Outcome : "none"));
            yield return new WaitForSecondsRealtime(1f);
        }
    }
}
