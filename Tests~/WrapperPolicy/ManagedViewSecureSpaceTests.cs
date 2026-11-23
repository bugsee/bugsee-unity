using System.Text.Json;
using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// The managed view hierarchy compares node boxes (Unity render pixels, top-left origin) with secure rectangles that the registry
    /// holds in native pixels. These tests run the real chain: a rectangle in Unity screen space goes through the real
    /// <see cref="SecureRectScreenOwners"/> into the real <see cref="SecureRectRegistry"/>, comes out through
    /// <see cref="SecureRectSpace.NativeToRender"/> (what <c>ManagedViewHierarchy.CollectSecureRects</c> runs) and meets the nodes in
    /// <see cref="ManagedViewTreeJson"/>.
    /// </summary>
    public class ManagedViewSecureSpaceTests
    {
        const string Hidden = "CardNumberField";

        // The Unity screen rectangle (bottom-left origin) and the node that draws exactly there (top-left origin, render pixels).
        static (Rect2 screen, ManagedViewNode node) Covered(int screenHeight, float x, float yFromTop, float w, float h)
        {
            var node = Node(5, Hidden, x, yFromTop, w, h);
            return (new Rect2(x, screenHeight - yFromTop - h, x + w, screenHeight - yFromTop), node);
        }

        struct Rect2
        {
            public float XMin, YMin, XMax, YMax;
            public Rect2(float xMin, float yMin, float xMax, float yMax) { XMin = xMin; YMin = yMin; XMax = xMax; YMax = yMax; }
        }

        static ManagedViewNode Node(long id, string name, float x, float y, float w, float h)
        {
            var node = new ManagedViewNode
            {
                Id = id, ClassName = "InputField", Name = name, Components = "Image,InputField", HasBounds = true, X = x, Y = y, Width = w, Height = h,
            };
            return node;
        }

        static ManagedViewNode Root(SecureRectView view, params ManagedViewNode[] children)
        {
            var root = new ManagedViewNode { Id = 0, ClassName = "UnityPlayer", HasBounds = true, X = 0, Y = 0, Width = view.ScreenWidth, Height = view.ScreenHeight };
            root.Children.AddRange(children);
            return root;
        }

        // What the wrapper does: registered native pixels -> render pixels -> the overlap test of the writer.
        static string Report(ManagedViewNode root, SecureRectRegistry registry, SecureRectView view, bool convert, out int secured)
        {
            int[] rects = null;
            var count = registry.CopyRects(0, ref rects);
            if (convert) SecureRectSpace.NativeToRender(rects, count, view);
            return ManagedViewTreeJson.Write(root, rects, count, 1f, out secured);
        }

        static bool HasName(string json, string name) => json.Contains("\"" + name + "\"");

        static void AssertHiddenAfterConversion(SecureRectView view, float x, float yFromTop, float w, float h, bool alsoLeaksWithoutConversion)
        {
            var (screen, covered) = Covered(view.ScreenHeight, x, yFromTop, w, h);
            var far = Node(6, "Logo", 0, 0, 4, 4); // top-left corner, clear of the rectangle
            var registry = new SecureRectRegistry();
            new SecureRectScreenOwners().Add(screen.XMin, screen.YMin, screen.XMax, screen.YMax, 0, view, registry);
            var root = Root(view, covered, far);

            var json = Report(root, registry, view, true, out var secured);
            Assert.That(HasName(json, Hidden), Is.False, "the covered node keeps no name");
            var coveredJson = JsonDocument.Parse(json).RootElement.GetProperty("subitems")[0];
            Assert.That(coveredJson.TryGetProperty("options", out _), Is.False, "nor a component list");
            Assert.That(HasName(json, "Logo"), Is.True, "a node away from the rectangle is untouched");
            Assert.That(secured, Is.EqualTo(1));

            // The path before the fix: the native-pixel rectangle against render-pixel boxes.
            var before = Report(Root(view, Node(5, Hidden, x, yFromTop, w, h)), registry, view, false, out _);
            Assert.That(HasName(before, Hidden), Is.EqualTo(alsoLeaksWithoutConversion));
        }

        [Test]
        public void NativeResolutionBehavesAsBefore()
        {
            var view = new SecureRectView(1080, 2400, 0, 0, 1080, 2400);
            AssertHiddenAfterConversion(view, 100, 600, 400, 80, alsoLeaksWithoutConversion: false);
        }

        [Test]
        public void ResolutionScalingBelowOneStillCoversTheNode()
        {
            // Render at 0.5: 540x1200 on a 1080x2400 surface. The registry holds twice the render pixels.
            var view = new SecureRectView(540, 1200, 0, 0, 1080, 2400);
            AssertHiddenAfterConversion(view, 50, 300, 200, 40, alsoLeaksWithoutConversion: true);
        }

        [Test]
        public void ScreenSetResolutionSmallerThanNativeStillCoversTheNode()
        {
            // Screen.SetResolution(720, 1280) on a 1080x1920 display.
            var view = new SecureRectView(720, 1280, 0, 0, 1080, 1920);
            AssertHiddenAfterConversion(view, 400, 900, 200, 60, alsoLeaksWithoutConversion: true);
        }

        [Test]
        public void ASurfaceOffsetStillCoversTheNode()
        {
            // Native resolution, but the surface sits below a 96 px status bar and a 40 px left inset.
            var view = new SecureRectView(1000, 2000, 40, 96, 1000, 2000);
            AssertHiddenAfterConversion(view, 20, 20, 300, 30, alsoLeaksWithoutConversion: true);
        }

        [Test]
        public void ScaledAndOffsetTogether()
        {
            var view = new SecureRectView(540, 1000, 24, 72, 1080, 2000);
            AssertHiddenAfterConversion(view, 100, 500, 200, 50, alsoLeaksWithoutConversion: true);
        }

        [TestCase(2, 390, 844)]
        [TestCase(3, 390, 844)]
        public void IosNativePixelsAreScreenPointsTimesTheScale(int scale, int pointsW, int pointsH)
        {
            // Unity renders at the native scale: the surface is points * scale, Unity's screen is the same size.
            var view = new SecureRectView(pointsW * scale, pointsH * scale, 0, 0, pointsW * scale, pointsH * scale);
            AssertHiddenAfterConversion(view, 30, 200, 300, 60, alsoLeaksWithoutConversion: false);
        }

        [TestCase(2, 3)]
        [TestCase(3, 2)]
        public void IosRenderScaleDifferentFromTheScreenScale(int renderScale, int nativeScale)
        {
            // Unity renders 390x844 points at renderScale, the screen is nativeScale: the registry holds points * nativeScale.
            var view = new SecureRectView(390 * renderScale, 844 * renderScale, 0, 0, 390 * nativeScale, 844 * nativeScale);
            AssertHiddenAfterConversion(view, 40, 150, 200, 50, alsoLeaksWithoutConversion: true);
        }

        [Test]
        public void AManuallyRegisteredNativeRectangleIsConvertedToo()
        {
            // AddSecureRectangle gives native pixels directly; they go through the same conversion.
            var view = new SecureRectView(540, 1200, 0, 96, 1080, 2304);
            var registry = new SecureRectRegistry();
            registry.Set("manual:1", 0, 100, 296, 500, 396); // native
            var node = Node(5, Hidden, 50, 100, 200, 50);    // the same area in render pixels: (296-96)*1200/2304 = 104 to (396-96)*1200/2304 = 156
            var json = Report(Root(view, node, Node(6, "Logo", 400, 900, 20, 20)), registry, view, true, out _);
            Assert.That(HasName(json, Hidden), Is.False);
            Assert.That(HasName(json, "Logo"), Is.True);
        }

        [Test]
        public void ARectanglePartlyOutsideTheSurfaceStillCoversTheNodeInside()
        {
            var view = new SecureRectView(540, 1200, 0, 0, 1080, 2400);
            var registry = new SecureRectRegistry();
            // Unity screen space: hangs over the left, top and right edges.
            new SecureRectScreenOwners().Add(-100, 1100, 700, 1500, 0, view, registry);
            var topLeft = Node(5, Hidden, 0, 0, 60, 60);
            var rightEdge = Node(6, "AtRightEdge", 520, 10, 20, 20);
            var below = Node(7, "Below", 0, 150, 540, 20);
            var json = Report(Root(view, topLeft, rightEdge, below), registry, view, true, out _);
            Assert.That(HasName(json, Hidden), Is.False);
            Assert.That(HasName(json, "AtRightEdge"), Is.False);
            Assert.That(HasName(json, "Below"), Is.True);
        }

        [Test]
        public void ARectangleEntirelyOffTheSurfaceHidesNothing()
        {
            var view = new SecureRectView(540, 1200, 0, 0, 1080, 2400);
            var registry = new SecureRectRegistry();
            new SecureRectScreenOwners().Add(1000, 0, 1200, 100, 0, view, registry); // right of the screen: not registered at all
            var json = Report(Root(view, Node(5, "Anywhere", 10, 10, 50, 50)), registry, view, true, out var secured);
            Assert.That(HasName(json, "Anywhere"), Is.True);
            Assert.That(secured, Is.EqualTo(0));
        }

        [Test]
        public void NoRectanglesLeaveEveryNodeAlone()
        {
            var view = new SecureRectView(540, 1200, 0, 0, 1080, 2400);
            var json = Report(Root(view, Node(5, "Anywhere", 10, 10, 50, 50)), new SecureRectRegistry(), view, true, out var secured);
            Assert.That(HasName(json, "Anywhere"), Is.True);
            Assert.That(secured, Is.EqualTo(0));
        }

        [Test]
        public void ANodeWithoutBoundsStaysProtectedWhileARectangleExists()
        {
            var view = new SecureRectView(540, 1200, 0, 0, 1080, 2400);
            var registry = new SecureRectRegistry();
            registry.Set("manual:1", 0, 0, 0, 10, 10);
            var unplaced = new ManagedViewNode { Id = 5, ClassName = "GameObject", Name = "Unplaced", Components = "Foo" };
            var json = Report(Root(view, unplaced), registry, view, true, out var secured);
            Assert.That(HasName(json, "Unplaced"), Is.False, "geometry that cannot be placed counts as covered");
            Assert.That(secured, Is.EqualTo(1));
        }

        [Test]
        public void CoverScreenMakesEveryRectangleTheWholeScreen()
        {
            var view = new SecureRectView(540, 1200, 0, 0, 1080, 2400);
            var registry = new SecureRectRegistry();
            registry.Set("manual:1", 0, 900, 2000, 910, 2010);
            int[] rects = null;
            var count = registry.CopyRects(0, ref rects);
            SecureRectSpace.CoverScreen(rects, count, 540, 1200);
            var json = ManagedViewTreeJson.Write(Root(view, Node(5, "Anywhere", 500, 1100, 10, 10)), rects, count, 1f);
            Assert.That(HasName(json, "Anywhere"), Is.False);
        }

        [Test]
        public void TheConversionRoundsOutwardsAndKeepsAnUnknownViewAsIs()
        {
            var view = new SecureRectView(3, 3, 0, 0, 10, 10); // 0.3 render pixels per native pixel
            var rects = new[] { 4, 4, 5, 5 };
            Assert.That(SecureRectSpace.NativeToRender(rects, 1, view), Is.True);
            Assert.That(rects, Is.EqualTo(new[] { 1, 1, 2, 2 })); // [1.2, 1.5] floors and ceils to [1, 2]

            var untouched = new[] { 4, 4, 5, 5 };
            Assert.That(SecureRectSpace.NativeToRender(untouched, 1, default), Is.False);
            Assert.That(untouched, Is.EqualTo(new[] { 4, 4, 5, 5 }));
            Assert.That(SecureRectSpace.NativeToRender(null, 1, view), Is.False);
            Assert.That(SecureRectSpace.NativeToRender(new[] { 1, 2 }, 5, view), Is.True, "a count past the array is clamped");
        }
    }
}
