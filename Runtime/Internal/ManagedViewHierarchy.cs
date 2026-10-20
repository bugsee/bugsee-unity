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
    /// (specs: sdk/wrapper-data-requests). The SDK asks on its main thread, once per capture pass (a report snapshot or
    /// Bugsee.CaptureViewHierarchy), and waits 500 ms; the walk needs Unity's main thread, so on Android (where the two
    /// differ) the reply is asynchronous, and on iOS (where they are the same) it is made inside the request.
    /// </summary>
    static class ManagedViewHierarchy
    {
        // The SDK gives up 500 ms after the request; a walk that starts later than this, or runs longer, cannot be used.
        const long MaxQueueDelayMs = 250;
        const long MaxWalkMs = 150;

        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static readonly Vector3[] Corners = new Vector3[4];
        static readonly List<Component> ComponentBuffer = new List<Component>();
        static int[] _secureRects;

        /// <summary>
        /// Called on whichever thread the native SDK asks on. <paramref name="reply"/> runs exactly once, with the JSON
        /// or null (nothing to contribute): immediately when this already is Unity's main thread, otherwise on it.
        /// </summary>
        public static void Handle(string dataType, float pixelsPerNativeUnit, Action<string> reply)
        {
            if (reply == null) return;
            if (dataType != ManagedViewTreeJson.DataType)
            {
                reply(null);
                return;
            }

            var askedAt = Clock.ElapsedMilliseconds;
            MainThreadDispatcher.Run(() => reply(Build(askedAt, pixelsPerNativeUnit)));
        }

        static string Build(long askedAt, float pixelsPerNativeUnit)
        {
            try
            {
                if (Clock.ElapsedMilliseconds - askedAt > MaxQueueDelayMs) return null;
                var startedAt = Clock.ElapsedMilliseconds;
                var budget = new ManagedViewBudget(expired: () => Clock.ElapsedMilliseconds - startedAt > MaxWalkMs);
                var root = Read(budget);
                var count = SecureRectRegistry.Instance.CopyRects(0, ref _secureRects);
                return ManagedViewTreeJson.Write(root, _secureRects, count, pixelsPerNativeUnit, budget.Truncated);
            }
            catch (Exception ex)
            {
                // Nothing to contribute: the report still carries the native half.
                Debug.LogWarning("[Bugsee] The managed view hierarchy could not be read: " + ex.Message);
                return null;
            }
        }

        static ManagedViewNode Read(ManagedViewBudget budget)
        {
            var screenHeight = Screen.height;
            var root = new ManagedViewNode
            {
                Id = 0,
                ClassName = "UnityPlayer",
                HasBounds = true,
                Width = Screen.width,
                Height = screenHeight,
            };
            budget.TryTake(1);
            var camera = Camera.main;
            var scenes = SceneManager.sceneCount;
            for (var i = 0; i < scenes && !budget.IsExhausted; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                if (!budget.TryTake(2)) break;
                var sceneNode = new ManagedViewNode { Id = scene.handle, ClassName = "Scene", Name = scene.name };
                root.Children.Add(sceneNode);
                var roots = scene.GetRootGameObjects();
                for (var r = 0; r < roots.Length && !budget.IsExhausted; r++)
                    ReadObject(roots[r], 3, default, camera, screenHeight, budget, sceneNode);
            }

            return root;
        }

        // The canvas a uGUI element sits under: an overlay canvas lays out in screen pixels already, any other is projected by a camera.
        struct CanvasContext
        {
            public bool Valid;
            public Camera Camera; // null for ScreenSpaceOverlay
        }

        static void ReadObject(
            GameObject go, int depth, CanvasContext canvas, Camera mainCamera, int screenHeight,
            ManagedViewBudget budget, ManagedViewNode parent)
        {
            if (go == null || !go.activeInHierarchy || !budget.TryTake(depth)) return;

            var node = new ManagedViewNode { Id = go.GetInstanceID(), Name = go.name };
            NameComponents(go, node);
            parent.Children.Add(node);

            if (go.TryGetComponent<Canvas>(out var own))
            {
                var rootCanvas = own.rootCanvas != null ? own.rootCanvas : own;
                canvas.Valid = true;
                canvas.Camera = rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null
                    : (rootCanvas.worldCamera != null ? rootCanvas.worldCamera : mainCamera);
                if (rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay && canvas.Camera == null) canvas.Valid = false;
            }

            var transform = go.transform;
            var rect = transform as RectTransform;
            if (rect != null && canvas.Valid)
            {
                rect.GetWorldCorners(Corners);
                SetBounds(node, Corners, canvas.Camera, screenHeight);
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
                {
                    BoxCorners[k++] = new Vector3(c.x + sx * e.x, c.y + sy * e.y, c.z + sz * e.z);
                }

                SetBounds(node, BoxCorners, mainCamera, screenHeight);
            }

            var children = transform.childCount;
            for (var i = 0; i < children && !budget.IsExhausted; i++)
                ReadObject(transform.GetChild(i).gameObject, depth + 1, canvas, mainCamera, screenHeight, budget, node);
        }

        static readonly Vector3[] BoxCorners = new Vector3[8];

        // Projects the corners to Unity screen pixels (origin bottom left) and stores their box with the origin at the top left.
        // A corner behind the camera leaves the node without geometry rather than with a wrong box.
        static void SetBounds(ManagedViewNode node, Vector3[] corners, Camera camera, int screenHeight)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (var i = 0; i < corners.Length; i++)
            {
                var p = corners[i];
                if (camera != null)
                {
                    p = camera.WorldToScreenPoint(p);
                    if (p.z <= 0f) return;
                }

                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }

            if (float.IsNaN(minX) || float.IsNaN(minY) || float.IsNaN(maxX) || float.IsNaN(maxY)) return;
            node.HasBounds = true;
            node.X = minX;
            node.Y = screenHeight - maxY;
            node.Width = maxX - minX;
            node.Height = maxY - minY;
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
