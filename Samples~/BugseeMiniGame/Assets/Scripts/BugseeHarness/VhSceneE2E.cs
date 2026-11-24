using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Bugsee.Contracts.Options;
using Bugsee.Internal;
using Bugsee.WrapperPolicy;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Bugsee.Sample
{
    /// <summary>
    /// Device check for the 3D <c>scene</c> block of the managed view hierarchy (BUGSEE_E2E_SCENARIO=vhscene). It switches the scene's own objects
    /// off and builds the handedness fixture of the spec (sdk/viewtree-scene) out of real engine objects: a perspective camera at the origin and
    /// an orthographic one drawing into a quarter of the screen, a 1 x 2 x 3 box turned 30 degrees, a cube beside it; and also a box under a
    /// rotated, non-uniformly scaled parent, a mirrored box, a sprite, and a canvas in each render mode (world space, screen space with a
    /// camera, overlay) with named children. It asks the SDK for the managed view hierarchy, reads the JSON the wrapper replied
    /// (<see cref="ManagedViewDiagnostics.LastReply"/>) and checks the cameras, the boxes (against the values the spec prescribes and against
    /// the engine's own transforms), that a box projected through the emitted camera lies inside its node's <c>bounds</c>, that a
    /// screen-sized secure rectangle withholds every name and leaves the geometry, and the launch option: the level comes from the launch options
    /// (<c>bugsee_scene_graph</c> / <c>BUGSEE_E2E_SCENE_GRAPH</c> = none | namesonly | all; unset = the default, All): None answers nothing,
    /// NamesOnly answers a tree with names and no <c>bounds</c> and no <c>scene</c>. The reply is also written to
    /// <c>persistentDataPath/vh-scene-&lt;level&gt;.json</c> (a development build: <c>run-as</c> on Android). Logs <c>[Sample] vhscene PASS|FAIL</c>
    /// lines and <c>done failures=N</c>; also works in a release build.
    /// </summary>
    static class VhSceneE2E
    {
        sealed class Runner : MonoBehaviour { }

        static int _failures;

        /// <param name="configured">What the launch was given for the option (the intent extra or environment variable), null for the default: the
        /// native SDKs do not echo the wrapper's own <c>unity.*</c> keys back, so the scenario is told what it launched with.</param>
        /// <param name="uploadReport">Also upload a manual report titled <c>e2e-vhscene-&lt;runId&gt;</c> while the scene stands (bugsee_scene_report = 1), so the stored <c>viewtree</c> can be read on the dashboard.</param>
        public static void Start(string runId, string configured, bool uploadReport = false)
        {
            var host = new GameObject("VhSceneRunner");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<Runner>().StartCoroutine(Run(host, runId, configured, uploadReport));
        }

        static bool Check(string name, bool ok, string detail = null)
        {
            if (!ok) _failures++;
            Debug.Log("[Sample] vhscene " + (ok ? "PASS " : "FAIL ") + name + (ok || detail == null ? "" : " :: " + detail));
            return ok;
        }

        static IEnumerator Run(GameObject host, string runId, string configured, bool uploadReport)
        {
            _failures = 0;
            var created = new List<GameObject>();
            var hidden = new List<GameObject>();
            Rect? rect = null;
            var level = SceneGraphCapture.All;
            try
            {
                level = SceneGraphCaptureOption.Parse(configured, out var recognized);
                Debug.Log("[Sample] vhscene start level=" + level + " option=" + (configured ?? "(unset: the default)") + " recognized=" + recognized
                    + " screen=" + Screen.width + "x" + Screen.height + " dev=" + Debug.isDebugBuild + " unity=" + Application.unityVersion);
                var vh = Bugsee.GetLaunchOptions() != null && Bugsee.GetLaunchOptions().TryGetValue(Options.CaptureViewHierarchy, out var enabled)
                    && enabled != null && string.Equals(enabled.ToString(), "false", StringComparison.OrdinalIgnoreCase);
                if (vh) { Debug.Log("[Sample] vhscene SKIP CaptureViewHierarchy is off in this launch"); yield break; }

                for (var i = 0; i < SceneManager.sceneCount; i++)
                    foreach (var go in SceneManager.GetSceneAt(i).GetRootGameObjects())
                        if (go != host && go.activeSelf) { go.SetActive(false); hidden.Add(go); }
                var fx = Build(created);
                for (var i = 0; i < 4; i++) yield return null; // drawn at least once: a renderer is only in a "visible" scene after a frame
                ManagedViewDiagnostics.KeepReplies = true;

                var json = default(string);
                yield return Capture(r => json = r);
                File.WriteAllText(Path.Combine(Application.persistentDataPath, "vh-scene-" + level.ToString().ToLowerInvariant() + ".json"), json ?? "null");
                Debug.Log("[Sample] vhscene reply chars=" + (json == null ? -1 : json.Length) + " file=" + Application.persistentDataPath + "/vh-scene-" + level.ToString().ToLowerInvariant() + ".json");

                if (level == SceneGraphCapture.None)
                {
                    Check("None: the wrapper answered no managed tree", json == null, "chars=" + (json == null ? 0 : json.Length));
                    yield break;
                }

                var root = json == null ? null : JsonRead.Object(json);
                if (!Check("a managed tree was answered and is valid JSON", root != null, "chars=" + (json == null ? 0 : json.Length))) yield break;
                Check("a named node is in the tree with its name", json.Contains("\"VhCubeA\""), "no VhCubeA");
                Check("the overlay canvas card is in the tree", json.Contains("\"VhOverlayCard\""), "no VhOverlayCard");

                if (level == SceneGraphCapture.NamesOnly)
                {
                    Check("NamesOnly: no node carries bounds", !json.Contains("\"bounds\""), "found \"bounds\"");
                    Check("NamesOnly: no scene block", !root.ContainsKey("scene") && !json.Contains("\"scene\""), "found scene");
                    Check("NamesOnly: no zeroed bounds", !json.Contains("[0,0,0,0]"), "found [0,0,0,0]");
                }
                else
                {
                    CheckScene(root, json, fx);
                }

                // A secure rectangle over the whole screen: every name goes, in both levels, and the geometry of the scene stays.
                var count = level == SceneGraphCapture.All ? SceneObjects(root).Count : -1;
                rect = new Rect(0, 0, Screen.width, Screen.height);
                Bugsee.AddSecureScreenRectangle(rect.Value);
                yield return null;
                yield return null;
                var secured = default(string);
                yield return Capture(r => secured = r);
                var securedRoot = secured == null ? null : JsonRead.Object(secured);
                if (Check("the capture with a secure rectangle answered", securedRoot != null))
                {
                    // A node whose box lies wholly outside the screen is not under the rectangle and keeps its name (a cube beside the view on a
                    // portrait screen): the engine says which fixture objects those are, from their own renderer bounds, and nothing else may keep one.
                    var kept = new List<string>();
                    CollectNames(securedRoot, kept);
                    var leaked = new List<string>();
                    foreach (var name in kept)
                        if (!fx.ByName.TryGetValue(name, out var go) || OnScreen(go)) leaked.Add(name);
                    Check("a screen-sized secure rectangle withholds the name of every node on the screen", kept.Count < fx.ByName.Count && leaked.Count == 0,
                        "kept=" + string.Join(",", kept) + " on-screen among them=" + string.Join(",", leaked));
                    if (level == SceneGraphCapture.All)
                    {
                        var after = SceneObjects(securedRoot);
                        Check("the scene keeps its geometry under the rectangle (same objects)", after.Count == count && count > 0, "before=" + count + " after=" + after.Count);
                        foreach (var o in after)
                            Check("scene object " + o["id"] + " carries no label", !o.ContainsKey("name") && !o.ContainsKey("options"), "has a label");
                    }
                }
                if (uploadReport)
                {
                    // Last: the report's own work (video, files) slows the captures that follow it, which the checks above must not see.
                    if (rect != null) { Bugsee.RemoveSecureScreenRectangle(rect.Value); rect = null; }
                    yield return null;
                    yield return null;
                    var title = "e2e-vhscene-" + runId + "-" + level.ToString().ToLowerInvariant();
                    Bugsee.Upload(title, "managed view hierarchy with the 3D scene block");
                    Debug.Log("[Sample] vhscene upload requested " + title);
                    yield return new WaitForSecondsRealtime(8f);
                }
            }
            finally
            {
                if (rect != null) Bugsee.RemoveSecureScreenRectangle(rect.Value);
                ManagedViewDiagnostics.KeepReplies = false;
                foreach (var go in created) if (go != null) Object.Destroy(go);
                foreach (var go in hidden) if (go != null) go.SetActive(true);
                Debug.Log("[Sample] vhscene done level=" + level + " failures=" + _failures);
                Object.Destroy(host);
            }
        }

        // ---- the fixture ----

        sealed class Fixture
        {
            public Camera Cam1, Cam2;
            public GameObject BoxA, BoxB, Nested, Mirrored, Sprite, WorldCanvas, CameraCanvas, OverlayCanvas;
            public readonly Dictionary<string, GameObject> ByName = new Dictionary<string, GameObject>();
        }

        static GameObject Cube(string name, Vector3 position, Vector3 scale, Quaternion rotation, Transform parent, List<GameObject> created)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var collider = go.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);
            go.name = name;
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            go.transform.localScale = scale;
            if (parent == null) created.Add(go);
            return go;
        }

        static Fixture Build(List<GameObject> created)
        {
            var f = new Fixture();
            var c1 = new GameObject("VhCam1");
            c1.tag = "MainCamera";
            f.Cam1 = c1.AddComponent<Camera>();
            f.Cam1.fieldOfView = 60f; f.Cam1.nearClipPlane = 0.3f; f.Cam1.farClipPlane = 1000f; f.Cam1.depth = 0f;
            f.Cam1.clearFlags = CameraClearFlags.SolidColor; f.Cam1.backgroundColor = new Color(0.1f, 0.1f, 0.15f);
            created.Add(c1);

            var c2 = new GameObject("VhCam2");
            c2.transform.position = new Vector3(-10f, 0f, 5f);
            c2.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            f.Cam2 = c2.AddComponent<Camera>();
            f.Cam2.orthographic = true; f.Cam2.orthographicSize = 2f; f.Cam2.nearClipPlane = 0.3f; f.Cam2.farClipPlane = 100f; f.Cam2.depth = 1f;
            f.Cam2.rect = new Rect(0f, 0f, 0.5f, 0.5f);
            f.Cam2.clearFlags = CameraClearFlags.SolidColor; f.Cam2.backgroundColor = new Color(0.2f, 0.1f, 0.1f);
            created.Add(c2);

            f.BoxA = Cube("VhCubeA", new Vector3(0f, 0f, 5f), new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 30f, 0f), null, created);
            f.BoxB = Cube("VhCubeB", new Vector3(2f, 0f, 5f), Vector3.one, Quaternion.identity, null, created);

            var parent = new GameObject("VhParent");
            parent.transform.position = new Vector3(-3f, 1f, 7f);
            parent.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
            parent.transform.localScale = new Vector3(2f, 1f, 0.5f);
            created.Add(parent);
            f.Nested = Cube("VhNestedCube", new Vector3(0.5f, 0f, 0f), Vector3.one, Quaternion.identity, parent.transform, created);

            f.Mirrored = Cube("VhMirroredCube", new Vector3(3f, -1f, 6f), new Vector3(-1.5f, 1f, 1f), Quaternion.Euler(0f, 20f, 0f), null, created);

            f.Sprite = new GameObject("VhSprite");
            f.Sprite.transform.position = new Vector3(0f, 2f, 6f);
            var tex = new Texture2D(4, 4);
            var pixels = new Color32[16];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 200, 0, 255);
            tex.SetPixels32(pixels);
            tex.Apply();
            f.Sprite.AddComponent<SpriteRenderer>().sprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            created.Add(f.Sprite);

            f.WorldCanvas = new GameObject("VhWorldCanvas", typeof(Canvas));
            var wc = f.WorldCanvas.GetComponent<Canvas>();
            wc.renderMode = RenderMode.WorldSpace;
            var wrt = (RectTransform)f.WorldCanvas.transform;
            wrt.sizeDelta = new Vector2(200f, 100f);
            wrt.localScale = Vector3.one * 0.01f;
            wrt.position = new Vector3(1f, 1.5f, 4f);
            wrt.rotation = Quaternion.Euler(0f, 20f, 0f);
            var label = new GameObject("VhWorldLabel", typeof(RectTransform));
            label.transform.SetParent(wrt, false);
            created.Add(f.WorldCanvas);

            f.CameraCanvas = new GameObject("VhCameraCanvas", typeof(Canvas));
            var cc = f.CameraCanvas.GetComponent<Canvas>();
            cc.renderMode = RenderMode.ScreenSpaceCamera;
            cc.worldCamera = f.Cam1;
            cc.planeDistance = 2f;
            created.Add(f.CameraCanvas);

            f.OverlayCanvas = new GameObject("VhOverlayCanvas", typeof(Canvas));
            f.OverlayCanvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var card = new GameObject("VhOverlayCard", typeof(RectTransform));
            card.transform.SetParent(f.OverlayCanvas.transform, false);
            var crt = (RectTransform)card.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0.2f);
            crt.sizeDelta = new Vector2(Screen.width * 0.3f, Screen.height * 0.05f);
            created.Add(f.OverlayCanvas);
            foreach (var go in new[] { c1, c2, f.BoxA, f.BoxB, parent, f.Nested, f.Mirrored, f.Sprite, f.WorldCanvas, label, f.CameraCanvas, f.OverlayCanvas, card })
                f.ByName[go.name] = go;
            return f;
        }

        // One capture: waits for the wrapper's reply (null included).
        static IEnumerator Capture(Action<string> done)
        {
            var before = ManagedViewDiagnostics.ReplyCount;
            Bugsee.CaptureViewHierarchy();
            var waited = 0f;
            while (ManagedViewDiagnostics.ReplyCount == before && waited < 10f) { waited += Time.unscaledDeltaTime; yield return null; }
            done(ManagedViewDiagnostics.ReplyCount > before ? ManagedViewDiagnostics.LastReply : null);
            yield return new WaitForSecondsRealtime(0.5f);
        }

        // ---- reading the reply ----

        static double D(object v) => v is long l ? l : (v is double d ? d : double.NaN);

        static double[] Arr(object v)
        {
            if (!(v is List<object> list)) return null;
            var r = new double[list.Count];
            for (var i = 0; i < r.Length; i++) r[i] = D(list[i]);
            return r;
        }

        static List<Dictionary<string, object>> SceneObjects(Dictionary<string, object> root)
        {
            var r = new List<Dictionary<string, object>>();
            if (root.TryGetValue("scene", out var s) && s is Dictionary<string, object> scene && scene.TryGetValue("objects", out var o) && o is List<object> list)
                foreach (var e in list) if (e is Dictionary<string, object> d) r.Add(d);
            return r;
        }

        static Dictionary<string, object> ById(Dictionary<string, object> scene, string array, string id)
        {
            if (scene.TryGetValue(array, out var o) && o is List<object> list)
                foreach (var e in list) if (e is Dictionary<string, object> d && d.TryGetValue("id", out var x) && (x as string) == id) return d;
            return null;
        }

        static Dictionary<string, object> Node(Dictionary<string, object> node, string id)
        {
            if (node.TryGetValue("id", out var x) && (x as string) == id) return node;
            if (node.TryGetValue("subitems", out var kids) && kids is List<object> list)
                foreach (var k in list)
                    if (k is Dictionary<string, object> kd) { var found = Node(kd, id); if (found != null) return found; }
            return null;
        }

        static bool Near(double[] v, double x, double y, double z, double tol = 0.01) =>
            v != null && v.Length >= 3 && Math.Abs(v[0] - x) < tol && Math.Abs(v[1] - y) < tol && Math.Abs(v[2] - z) < tol;

        // q and -q are the same rotation.
        static bool SameRotation(double[] q, double x, double y, double z, double w, double tol = 0.002)
        {
            if (q == null || q.Length != 4) return false;
            var dot = q[0] * x + q[1] * y + q[2] * z + q[3] * w;
            var s = dot < 0 ? -1 : 1;
            return Math.Abs(q[0] * s - x) < tol && Math.Abs(q[1] * s - y) < tol && Math.Abs(q[2] * s - z) < tol && Math.Abs(q[3] * s - w) < tol;
        }

        static string Id(GameObject go) => go.GetInstanceID().ToString(CultureInfo.InvariantCulture);

        static void CheckScene(Dictionary<string, object> root, string json, Fixture f)
        {
            var bounds = root.TryGetValue("bounds", out var b) ? Arr(b) : null;
            Check("the root has positive bounds", bounds != null && bounds.Length == 4 && bounds[2] > 0 && bounds[3] > 0, "bounds=" + (bounds == null ? "none" : string.Join(",", bounds)));
            if (!Check("a scene block is on the root", root.TryGetValue("scene", out var so) && so is Dictionary<string, object>, "no scene")) return;
            var scene = (Dictionary<string, object>)so;
            Check("scene version 1, selection visible", D(scene["version"]) == 1 && (scene["selection"] as string) == "visible");
            var sent = (int)D(scene["objects_sent"]);
            Check("objects_sent equals the array length", sent == SceneObjects(root).Count, "sent=" + sent + " count=" + SceneObjects(root).Count);
            Check("nothing was truncated or skipped in this small scene", !scene.ContainsKey("truncated") && !scene.ContainsKey("skipped"), "truncated/skipped present");
            Check("objects_qualified equals objects_sent", scene.ContainsKey("objects_qualified") && (int)D(scene["objects_qualified"]) == sent);

            // The cameras: the spec's fixture values.
            var cam1 = ById(scene, "cameras", Id(f.Cam1.gameObject));
            var cam2 = ById(scene, "cameras", Id(f.Cam2.gameObject));
            if (Check("both cameras are in the scene with the camera objects' ids", cam1 != null && cam2 != null, "cam1=" + (cam1 != null) + " cam2=" + (cam2 != null)))
            {
                Check("camera 1: position 0,0,0, no rotation, perspective 60, near 0.3, far 1000, main, screen, full viewport",
                    Near(Arr(cam1["position"]), 0, 0, 0) && !cam1.ContainsKey("rotation") && (cam1["projection"] as string) == "perspective" && Math.Abs(D(cam1["fov_y"]) - 60) < 0.01
                    && Math.Abs(D(cam1["near"]) - 0.3) < 0.001 && Math.Abs(D(cam1["far"]) - 1000) < 0.01 && cam1.ContainsKey("main") && (cam1["target"] as string) == "screen" && !cam1.ContainsKey("viewport"),
                    "cam1=" + Describe(cam1));
                Check("camera 2: position -10,0,-5, rotation 0,-0.7071,0,0.7071, orthographic height 4, viewport 0,0.5,0.5,0.5, not main",
                    Near(Arr(cam2["position"]), -10, 0, -5) && SameRotation(Arr(cam2["rotation"]), 0, -0.7071, 0, 0.7071) && (cam2["projection"] as string) == "orthographic"
                    && Math.Abs(D(cam2["ortho_height"]) - 4) < 0.01 && !cam2.ContainsKey("main") && Arr(cam2["viewport"]) != null
                    && Near(Arr(cam2["viewport"]), 0, 0.5, 0.5, 0.001) && Math.Abs(Arr(cam2["viewport"])[3] - 0.5) < 0.001,
                    "cam2=" + Describe(cam2));
                Check("camera order follows depth", D(cam2["order"]) > D(cam1["order"]));
            }

            // The boxes of the fixture.
            var a = ById(scene, "objects", Id(f.BoxA));
            if (Check("box A is in the scene", a != null))
                Check("box A: center 0,0,-5, rotation 0,-0.2588,0,0.9659, size 1,2,3, kind mesh",
                    Near(Arr(a["center"]), 0, 0, -5) && SameRotation(Arr(a["rotation"]), 0, -0.2588, 0, 0.9659) && Near(Arr(a["size"]), 1, 2, 3) && (a["kind"] as string) == "mesh", "a=" + Describe(a));
            var bb = ById(scene, "objects", Id(f.BoxB));
            if (Check("box B is in the scene", bb != null))
                Check("box B: center 2,0,-5, no rotation, size 1,1,1", Near(Arr(bb["center"]), 2, 0, -5) && !bb.ContainsKey("rotation") && Near(Arr(bb["size"]), 1, 1, 1), "b=" + Describe(bb));

            // A box under a rotated, non-uniformly scaled parent: against the engine's own transform, converted by the spec's rule (mirror Z).
            var n = ById(scene, "objects", Id(f.Nested));
            if (Check("the nested box is in the scene", n != null))
            {
                var t = f.Nested.transform;
                var p = t.position;
                var q = t.rotation;
                var ls = t.lossyScale;
                Check("nested box: center, size and rotation equal the engine's transform converted to the wire",
                    Near(Arr(n["center"]), p.x, p.y, -p.z) && Near(Arr(n["size"]), ls.x, ls.y, ls.z) && SameRotation(Arr(n["rotation"]), -q.x, -q.y, q.z, q.w),
                    "n=" + Describe(n) + " engine pos=" + p + " scale=" + ls + " rot=" + q);
            }

            var m = ById(scene, "objects", Id(f.Mirrored));
            if (Check("the mirrored box is in the scene", m != null))
            {
                var q = Arr(m["rotation"]);
                Check("mirrored box: absolute size 1.5,1,1 and a unit quaternion", Near(Arr(m["size"]), 1.5, 1, 1) && q != null && Math.Abs(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3] - 1) < 0.001, "m=" + Describe(m));
            }

            var sp = ById(scene, "objects", Id(f.Sprite));
            if (Check("the sprite is in the scene", sp != null))
                Check("sprite: kind sprite and no thickness", (sp["kind"] as string) == "sprite" && Math.Abs(Arr(sp["size"])[2]) < 0.001, "sprite=" + Describe(sp));

            // Canvases: world space and screen space with a camera are planes in the scene; overlay is not.
            var wc = ById(scene, "objects", Id(f.WorldCanvas));
            if (Check("the world-space canvas is a ui plane", wc != null && (wc["kind"] as string) == "ui", "wc=" + Describe(wc)))
                Check("world canvas: size 2 x 1 x 0 and its place", Near(Arr(wc["size"]), 2, 1, 0) && Near(Arr(wc["center"]), f.WorldCanvas.transform.position.x, f.WorldCanvas.transform.position.y, -f.WorldCanvas.transform.position.z), "wc=" + Describe(wc));
            var cc = ById(scene, "objects", Id(f.CameraCanvas));
            if (Check("the camera canvas is a ui plane drawn by its camera", cc != null && (cc["kind"] as string) == "ui" && (cc.TryGetValue("camera", out var cam) ? cam as string : null) == Id(f.Cam1.gameObject), "cc=" + Describe(cc)))
                Check("camera canvas: two metres in front of its camera, flat", Math.Abs(Arr(cc["center"])[2] - -2) < 0.05 && Math.Abs(Arr(cc["size"])[2]) < 0.001, "cc=" + Describe(cc));
            Check("the overlay canvas is not a scene object", ById(scene, "objects", Id(f.OverlayCanvas)) == null);

            // The tree keeps the overlay card with its bounds; the boxes project inside their nodes.
            var card = FindByName(root, "VhOverlayCard");
            Check("the overlay card has bounds in the tree", card != null && card.ContainsKey("bounds"));
            if (cam1 != null && bounds != null && bounds.Length == 4)
            {
                CheckReprojection("box A", root, bounds, cam1, a, f.BoxA);
                CheckReprojection("box B", root, bounds, cam1, bb, f.BoxB);
                CheckReprojection("nested box", root, bounds, cam1, n, f.Nested);
            }
        }

        static void CollectNames(Dictionary<string, object> node, List<string> into)
        {
            if (node.TryGetValue("options", out var o) && o is Dictionary<string, object> od && od.TryGetValue("name", out var n) && n is string name) into.Add(name);
            if (node.TryGetValue("subitems", out var kids) && kids is List<object> list)
                foreach (var k in list)
                    if (k is Dictionary<string, object> kd) CollectNames(kd, into);
        }

        // Whether the engine puts the object's renderer bounds on the screen (the main camera's view of its 8 corners); an object without a
        // renderer or with a corner behind the camera has no box and counts as on the screen (unknown position: protected).
        static bool OnScreen(GameObject go)
        {
            var cam = Camera.main;
            var r = go.GetComponentInChildren<Renderer>();
            if (cam == null || r == null) return true;
            var b = r.bounds;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (var i = 0; i < 8; i++)
            {
                var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = cam.WorldToScreenPoint(corner);
                if (p.z <= 0f) return true;
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }

            return minX < Screen.width && maxX > 0 && minY < Screen.height && maxY > 0;
        }

        static string Describe(Dictionary<string, object> o) => o == null ? "null" : "{" + string.Join(", ", ToStrings(o)) + "}";

        static IEnumerable<string> ToStrings(Dictionary<string, object> o)
        {
            foreach (var kv in o)
                yield return kv.Key + "=" + (kv.Value is List<object> l ? "[" + string.Join(",", l) + "]" : kv.Value);
        }

        static Dictionary<string, object> FindByName(Dictionary<string, object> node, string name)
        {
            if (node.TryGetValue("options", out var o) && o is Dictionary<string, object> od && od.TryGetValue("name", out var n) && (n as string) == name) return node;
            if (node.TryGetValue("subitems", out var kids) && kids is List<object> list)
                foreach (var k in list)
                    if (k is Dictionary<string, object> kd) { var found = FindByName(kd, name); if (found != null) return found; }
            return null;
        }

        // The spec's reprojection rule, in the consumer's terms: the 8 corners of the emitted box, through the emitted camera (fov, aspect,
        // pose, viewport, all in the wire's convention), mapped onto the root's bounds, lie inside the tree node's bounds within one unit.
        static void CheckReprojection(string label, Dictionary<string, object> root, double[] screen, Dictionary<string, object> cam, Dictionary<string, object> obj, GameObject go)
        {
            if (obj == null) return;
            var node = Node(root, Id(go));
            if (node == null || !node.ContainsKey("bounds")) { Check(label + " reprojects inside its node's bounds", false, "no node bounds"); return; }
            var nb = Arr(node["bounds"]);
            var c = Arr(obj["center"]);
            var s = Arr(obj["size"]);
            var q = obj.ContainsKey("rotation") ? Arr(obj["rotation"]) : new[] { 0.0, 0.0, 0.0, 1.0 };
            var cp = Arr(cam["position"]);
            var cq = cam.ContainsKey("rotation") ? Arr(cam["rotation"]) : new[] { 0.0, 0.0, 0.0, 1.0 };
            var vp = cam.ContainsKey("viewport") ? Arr(cam["viewport"]) : new[] { 0.0, 0.0, 1.0, 1.0 };
            var tanHalf = Math.Tan(D(cam["fov_y"]) * Math.PI / 360.0);
            var aspect = D(cam["aspect"]);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (var i = 0; i < 8; i++)
            {
                var local = new[] { (i & 1) == 0 ? -s[0] / 2 : s[0] / 2, (i & 2) == 0 ? -s[1] / 2 : s[1] / 2, (i & 4) == 0 ? -s[2] / 2 : s[2] / 2 };
                var w = Add(Rotate(q, local), c);
                var rel = Sub(w, cp);
                var inCam = Rotate(new[] { -cq[0], -cq[1], -cq[2], cq[3] }, rel); // the inverse rotation
                if (!(inCam[2] < -1e-6)) { Check(label + " reprojects inside its node's bounds", false, "a corner is behind the camera"); return; }
                var nx = inCam[0] / (-inCam[2] * tanHalf * aspect);
                var ny = inCam[1] / (-inCam[2] * tanHalf);
                var px = screen[0] + (vp[0] + (nx + 1) / 2 * vp[2]) * screen[2];
                var py = screen[1] + (vp[1] + (1 - (ny + 1) / 2) * vp[3]) * screen[3];
                minX = Math.Min(minX, px); maxX = Math.Max(maxX, px); minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
            }

            var ok = minX >= nb[0] - 1 && minY >= nb[1] - 1 && maxX <= nb[0] + nb[2] + 1 && maxY <= nb[1] + nb[3] + 1;
            Check(label + " reprojects inside its node's bounds", ok, "projected=[" + minX.ToString("0.0") + "," + minY.ToString("0.0") + "," + maxX.ToString("0.0") + "," + maxY.ToString("0.0")
                + "] node bounds=[" + string.Join(",", nb) + "]");
        }

        static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

        // v rotated by the unit quaternion (x, y, z, w).
        static double[] Rotate(double[] q, double[] v)
        {
            double x = q[0], y = q[1], z = q[2], w = q[3];
            var tx = 2 * (y * v[2] - z * v[1]);
            var ty = 2 * (z * v[0] - x * v[2]);
            var tz = 2 * (x * v[1] - y * v[0]);
            return new[] { v[0] + w * tx + (y * tz - z * ty), v[1] + w * ty + (z * tx - x * tz), v[2] + w * tz + (x * ty - y * tx) };
        }
    }
}
