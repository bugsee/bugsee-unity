using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class SecureRectSpaceTests
    {
        static SecureRectView Full(int w, int h) => new SecureRectView(w, h, 0, 0, w, h);

        [Test]
        public void FlipsYAndKeepsPixelsWhenViewIsUnscaled()
        {
            // Unity rect at the bottom-left of a 1000x2000 screen, 100x200.
            Assert.That(SecureRectSpace.TryToNative(0, 0, 100, 200, Full(1000, 2000), out var l, out var t, out var r, out var b));
            Assert.That((l, t, r, b), Is.EqualTo((0, 1800, 100, 2000)));
        }

        [Test]
        public void ScalesWhenRenderIsSmallerThanSurface()
        {
            // Resolution scaling: Unity renders 540x960 into a 1080x1920 surface.
            var view = new SecureRectView(540, 960, 0, 0, 1080, 1920);
            Assert.That(SecureRectSpace.TryToNative(10, 900, 110, 960, view, out var l, out var t, out var r, out var b));
            Assert.That((l, t, r, b), Is.EqualTo((20, 0, 220, 120)));
        }

        [Test]
        public void AddsSurfaceOffsetInWindow()
        {
            // Status bar of 80 px above the surface.
            var view = new SecureRectView(1000, 1000, 0, 80, 1000, 1000);
            Assert.That(SecureRectSpace.TryToNative(0, 900, 100, 1000, view, out var l, out var t, out var r, out var b));
            Assert.That((l, t, r, b), Is.EqualTo((0, 80, 100, 180)));
        }

        [Test]
        public void RoundsOutwards()
        {
            var view = new SecureRectView(3, 3, 0, 0, 10, 10);
            Assert.That(SecureRectSpace.TryToNative(1, 1, 2, 2, view, out var l, out var t, out var r, out var b));
            // x 1..2 -> 3.33..6.67 ; y flipped 1..2 -> 3.33..6.67
            Assert.That((l, t, r, b), Is.EqualTo((3, 3, 7, 7)));
        }

        [Test]
        public void ClampsToSurfaceAndRejectsOffScreen()
        {
            Assert.That(SecureRectSpace.TryToNative(-50, -50, 50, 50, Full(100, 100), out var l, out var t, out var r, out var b));
            Assert.That((l, t, r, b), Is.EqualTo((0, 50, 50, 100)));
            Assert.That(SecureRectSpace.TryToNative(200, 0, 300, 10, Full(100, 100), out _, out _, out _, out _), Is.False);
        }

        [Test]
        public void UnknownViewOrNaNIsRejected()
        {
            Assert.That(SecureRectSpace.TryToNative(0, 0, 1, 1, default, out _, out _, out _, out _), Is.False);
            Assert.That(SecureRectSpace.TryToNative(float.NaN, 0, 1, 1, Full(10, 10), out _, out _, out _, out _), Is.False);
        }

        [Test]
        public void OwnersFollowViewChange()
        {
            var registry = new SecureRectRegistry();
            var owners = new SecureRectScreenOwners();
            owners.Add(0, 0, 100, 100, 0, Full(1000, 2000), registry);
            int[] rects = null;
            Assert.That(registry.CopyRects(0, ref rects), Is.EqualTo(1));
            Assert.That(new[] { rects[0], rects[1], rects[2], rects[3] }, Is.EqualTo(new[] { 0, 1900, 100, 2000 }));

            // The surface moves down by a status bar: same screen, new offset.
            Assert.That(owners.Refresh(0, new SecureRectView(1000, 2000, 0, 50, 1000, 2000), registry), Is.True);
            registry.CopyRects(0, ref rects);
            Assert.That(new[] { rects[0], rects[1], rects[2], rects[3] }, Is.EqualTo(new[] { 0, 1950, 100, 2050 }));

            // The same view again changes nothing.
            Assert.That(owners.Refresh(0, new SecureRectView(1000, 2000, 0, 50, 1000, 2000), registry), Is.False);
        }

        [Test]
        public void OwnersKeepTheirFractionWhenResolutionScales()
        {
            var registry = new SecureRectRegistry();
            var owners = new SecureRectScreenOwners();
            // Added at 500x1000 on a 1000x2000 surface, then Unity renders at 250x500 on the same surface.
            owners.Add(50, 100, 150, 300, 0, new SecureRectView(500, 1000, 0, 0, 1000, 2000), registry);
            int[] rects = null;
            registry.CopyRects(0, ref rects);
            var before = new[] { rects[0], rects[1], rects[2], rects[3] };
            Assert.That(owners.Refresh(0, new SecureRectView(250, 500, 0, 0, 1000, 2000), registry), Is.True);
            registry.CopyRects(0, ref rects);
            Assert.That(new[] { rects[0], rects[1], rects[2], rects[3] }, Is.EqualTo(before));
            Assert.That(before, Is.EqualTo(new[] { 100, 1400, 300, 1800 }));
        }

        [Test]
        public void OwnersRemoveAndRemoveAll()
        {
            var registry = new SecureRectRegistry();
            var owners = new SecureRectScreenOwners();
            owners.Add(0, 0, 10, 10, 0, Full(100, 100), registry);
            owners.Add(20, 20, 30, 30, 1, Full(100, 100), registry);
            int[] rects = null;
            Assert.That(registry.CopyRects(0, ref rects), Is.EqualTo(1));
            Assert.That(registry.CopyRects(1, ref rects), Is.EqualTo(1));
            Assert.That(owners.Remove(0, 0, 10, 10, 0, registry), Is.True);
            Assert.That(owners.Remove(0, 0, 10, 10, 0, registry), Is.False);
            Assert.That(registry.CopyRects(0, ref rects), Is.EqualTo(0));
            Assert.That(owners.RemoveAll(registry), Is.EqualTo(new[] { 1 }));
            Assert.That(registry.CopyRects(1, ref rects), Is.EqualTo(0));
            Assert.That(owners.HasEntries, Is.False);
        }

        [Test]
        public void OffScreenRectRegistersNothing()
        {
            var registry = new SecureRectRegistry();
            var owners = new SecureRectScreenOwners();
            owners.Add(150, 0, 200, 10, 0, Full(100, 100), registry);
            int[] rects = null;
            Assert.That(registry.CopyRects(0, ref rects), Is.EqualTo(0));
            Assert.That(owners.HasEntries, Is.True);
            Assert.That(owners.Remove(150, 0, 200, 10, 0, registry), Is.True);
        }

        [Test]
        public void ManualAndScreenOwnersDoNotShareAPrefix()
        {
            Assert.That(SecureRectScreenOwners.OwnerIdPrefix, Does.Not.StartWith(SecureRectManualOwners.OwnerIdPrefix));
            Assert.That(SecureRectManualOwners.OwnerIdPrefix, Does.Not.StartWith(SecureRectScreenOwners.OwnerIdPrefix));
        }
    }
}
