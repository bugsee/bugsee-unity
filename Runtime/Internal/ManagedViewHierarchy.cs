using System;
using System.Collections.Generic;
using System.Diagnostics;
using Bugsee.WrapperPolicy;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace Bugsee.Internal
{
    /// <summary>
    /// Answers the native SDK's <c>requestData("vh")</c>: the Unity scene as the managed view hierarchy of the report
    /// (specs: sdk/wrapper-data-requests). The SDK asks on its main thread when it takes a report snapshot on that thread
    /// or Bugsee.CaptureViewHierarchy runs, and waits 500 ms; the walk needs Unity's main thread, so on Android (where the
    /// two differ) the reply is asynchronous, and on iOS (where they are the same) it is made inside the request. A
    /// request made while Unity's main thread is blocked in a synchronous SDK call (a logged exception) is answered null at
    /// once, because the reply could only come from the thread that is waiting for it.
    /// </summary>
    static class ManagedViewHierarchy
    {
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static readonly Vector3[] Corners = new Vector3[4];
        static readonly Vector3[] BoxCorners = new Vector3[8];
        static readonly List<Component> ComponentBuffer = new List<Component>();
        static int[] _secureRects;

        /// <summary>
        /// Called on whichever thread the native SDK asks on. <paramref name="reply"/> runs exactly once, with the JSON
        /// or null (nothing to contribute): immediately when this already is Unity's main thread, otherwise on it.
        /// <paramref name="pixelsPerNativeUnit"/> is read on the main thread, when the walk starts.
        /// </summary>
        public static void Handle(string dataType, Func<float> pixelsPerNativeUnit, Action<string> reply)
        {
            if (reply == null) return;
            var once = new ManagedViewRequests.Once(reply);
            if (dataType != ManagedViewTreeJson.DataType || ManagedViewRequests.IsMainThreadInBlockingNativeCall)
            {
                once.Reply(null);
                return;
            }

            var askedAt = Clock.ElapsedMilliseconds;
            MainThreadDispatcher.Run(() => once.Reply(Build(askedAt, pixelsPerNativeUnit)));
        }

        static string Build(long askedAt, Func<float> pixelsPerNativeUnit)
        {
            try
            {
                if (ManagedViewRequests.IsStale(askedAt, Clock.ElapsedMilliseconds)) return null;
                var startedAt = Clock.ElapsedMilliseconds;
                var budget = new ManagedViewBudget(
                    expired: () => Clock.ElapsedMilliseconds - startedAt > ManagedViewRequests.MaxBuildMs * 2 / 3);
                var root = Read(budget);
                var walkedAt = Clock.ElapsedMilliseconds;
                var count = SecureRectRegistry.Instance.CopyRects(0, ref _secureRects);
                var scale = pixelsPerNativeUnit != null ? pixelsPerNativeUnit() : 1f;
                var json = ManagedViewTreeJson.Write(root, _secureRects, count, scale);
                if (Debug.isDebugBuild)
                    LogLater(budget.Taken, json == null ? 0 : json.Length, walkedAt - startedAt, Clock.ElapsedMilliseconds - walkedAt, budget.Truncated);
                return json;
            }
            catch (Exception ex)
            {
                // Nothing to contribute: the report still carries the native half.
                LogLater(ex.Message);
                return null;
            }
        }

        // Logged on the next frame: a Debug.Log is captured by the SDK, and the SDK is inside requestData right now.
        static void LogLater(int nodes, int chars, long walkMs, long writeMs, bool truncated)
        {
            MainThreadDispatcher.Post(() => Debug.Log("[Bugsee] managed view hierarchy: " + nodes + " nodes, " + chars + " chars, walk "
                + walkMs + " ms, write " + writeMs + " ms" + (truncated ? ", truncated" : "")));
        }

        static void LogLater(string error)
        {
            MainThreadDispatcher.Post(() => Debug.LogWarning("[Bugsee] The managed view hierarchy could not be read: " + error));
        }

        static ManagedViewNode Read(ManagedViewBudget budget)
        {
            var width = Screen.width;
            var height = Screen.height;
            var root = new ManagedViewNode { Id = 0, ClassName = "UnityPlayer", HasBounds = true, Width = width, Height = height };
            budget.TryTake(1);
            var camera = Camera.main;

            // Scenes first, then the DontDestroyOnLoad scene (SceneManager never lists it; Bugsee's own dispatcher object lives
            // there, so its scene is found through that object, and the object itself is left out).
            var host = MainThreadDispatcher.Ensure();
            var scenes = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) scenes.Add(scene);
            }

            if (host != null && host.gameObject.scene.IsValid()) scenes.Add(host.gameObject.scene);

            for (var i = 0; i < scenes.Count; i++)
            {
                if (budget.IsExhausted) { root.Truncated = true; break; }
                if (!budget.TryTake(2)) { root.Truncated = true; break; }
                var scene = scenes[i];
                var sceneNode = new ManagedViewNode { Id = scene.handle, IdText = "scene-" + scene.handle, ClassName = "Scene", Name = scene.name };
                root.Children.Add(sceneNode);
                var roots = scene.GetRootGameObjects();
                for (var r = 0; r < roots.Length; r++)
                {
                    if (host != null && roots[r] == host.gameObject) continue;
                    if (budget.IsExhausted) { sceneNode.Truncated = true; break; }
                    if (ReadObject(roots[r], 3, default, camera, width, height, budget, sceneNode)) sceneNode.Truncated = true;
                }
            }

            return root;
        }

        // The canvas a uGUI element sits under: an overlay canvas lays out in screen pixels already, any other is projected by a camera.
        struct CanvasContext
        {
            public bool Valid;
            public Camera Camera; // null: the corners already are screen pixels
        }

        // Reads one object and what is below it. True when the budget cut this object off (the caller marks itself truncated).
        // One object that throws (destroyed under the walk, a missing script) loses its own geometry and children, not the tree.
        static bool ReadObject(
            GameObject go, int depth, CanvasContext canvas, Camera mainCamera, int width, int height,
            ManagedViewBudget budget, ManagedViewNode parent)
        {
            if (go == null || !go.activeInHierarchy) return false;
            if (!budget.TryTake(depth)) return true;

            ManagedViewNode node;
            try
            {
                node = new ManagedViewNode { Id = go.GetInstanceID(), Name = go.name };
                NameComponents(go, node);
            }
            catch (Exception)
            {
                return false;
            }

            parent.Children.Add(node);
            try
            {
                if (go.TryGetComponent<Canvas>(out var own)) canvas = CanvasOf(own, mainCamera);
                var transform = go.transform;
                var rect = transform as RectTransform;
                if (rect != null && canvas.Valid)
                {
                    rect.GetWorldCorners(Corners);
                    SetBounds(node, Corners, canvas.Camera, width, height);
                }
                else if (mainCamera != null && go.TryGetComponent<Renderer>(out var renderer) && renderer.enabled)
                {
                    var b = renderer.bounds;
                    var c = b.center;
                    var e = b.extents;
                    var k = 0;
                    for (var sx = -1; sx <= 1; sx += 2)
                    for (var sy = -1; sy <= 1; sy += 2)
                    for (var sz = -1; sz <= 1; sz += 2)
                        BoxCorners[k++] = new Vector3(c.x + sx * e.x, c.y + sy * e.y, c.z + sz * e.z);
                    SetBounds(node, BoxCorners, mainCamera, width, height);
                }

                var children = transform.childCount;
                for (var i = 0; i < children; i++)
                {
                    if (budget.IsExhausted) { node.Truncated = true; break; }
                    if (ReadObject(transform.GetChild(i).gameObject, depth + 1, canvas, mainCamera, width, height, budget, node))
                        node.Truncated = true;
                }
            }
            catch (Exception)
            {
                // Keep what this object has; its siblings are read by the caller.
            }

            return false;
        }

        // Screen Space - Overlay lays out in screen pixels. Screen Space - Camera with no camera assigned renders as overlay too, so it
        // is read as one. World Space is projected by its event camera, or the main camera when it has none.
        static CanvasContext CanvasOf(Canvas canvas, Camera mainCamera)
        {
            var root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            switch (root.renderMode)
            {
                case RenderMode.ScreenSpaceOverlay:
                    return new CanvasContext { Valid = true };
                case RenderMode.ScreenSpaceCamera:
                    return new CanvasContext { Valid = true, Camera = root.worldCamera };
                default:
                    var camera = root.worldCamera != null ? root.worldCamera : mainCamera;
                    return new CanvasContext { Valid = camera != null, Camera = camera };
            }
        }

        // Projects the corners to Unity screen pixels (origin bottom left) and stores their box with the origin at the top left.
        // A corner behind the camera, or one that is not a number, leaves the node without geometry rather than with a wrong box.
        static void SetBounds(ManagedViewNode node, Vector3[] corners, Camera camera, int width, int height)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (var i = 0; i < corners.Length; i++)
            {
                var p = corners[i];
                if (camera != null)
                {
                    p = camera.WorldToScreenPoint(p);
                    if (!(p.z > 0f)) return;
                }

                if (float.IsNaN(p.x) || float.IsNaN(p.y)) return;
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }

            if (!ManagedViewGeometry.TryBox(minX, minY, maxX, maxY, width, height, out var x, out var y, out var w, out var h)) return;
            node.HasBounds = true;
            node.X = x;
            node.Y = y;
            node.Width = w;
            node.Height = h;
        }

        // The type of the first component that says what the object is, and every type name. Type names only: no component is
        // asked for a value, so a Text, an input field or a TextMeshPro label contributes its class and never what it shows.
        static void NameComponents(GameObject go, ManagedViewNode node)
        {
            go.GetComponents(typeof(Component), ComponentBuffer);
            string components = null;
            var shown = 0;
            for (var i = 0; i < ComponentBuffer.Count; i++)
            {
                var component = ComponentBuffer[i];
                if (component == null || component is Transform || component is CanvasRenderer) continue;
                var name = component.GetType().Name;
                if (node.ClassName == null) node.ClassName = name;
                if (++shown > 8) continue;
                components = components == null ? name : components + "," + name;
            }

            ComponentBuffer.Clear();
            node.Components = components;
            if (node.ClassName == null) node.ClassName = go.transform is RectTransform ? "RectTransform" : "GameObject";
        }
    }
}
