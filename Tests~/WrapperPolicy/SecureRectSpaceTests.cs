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

        static int[] Rect(SecureRectRegistry r, int display, int index)
        {
            int[] rects = null;
            int n = r.CopyRects(display, ref rects);
            Assert.That(n, Is.GreaterThan(index));
            return new[] { rects[index * 4], rects[index * 4 + 1], rects[index * 4 + 2], rects[index * 4 + 3] };
        }

        [Test]
        public void AddWithANewViewConvertsTheOlderRectanglesToo()
        {
            var registry = new SecureRectRegistry();
            var owners = new SecureRectScreenOwners();
            var v1 = new SecureRectView(100, 100, 0, 0, 100, 100);
            var v2 = new SecureRectView(100, 100, 0, 0, 200, 200); // the surface doubled
            owners.Add(0, 0, 10, 10, 0, v1, registry);
            owners.Add(50, 50, 60, 60, 0, v2, registry);
            int[] rects = null;
            registry.CopyRects(0, ref rects);
            var all = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 2; i++) all.Add(string.Join(",", Rect(registry, 0, i)));
            Assert.That(all, Is.EquivalentTo(new[] { "0,180,20,200", "100,80,120,100" }));
            // Nothing is left to refresh: both already have v2.
            Assert.That(owners.Refresh(0, v2, registry), Is.False);
        }

        [Test]
        public void DuplicateAddsNeedTwoRemovals()
        {
            var registry = new SecureRectRegistry();
            var owners = new SecureRectScreenOwners();
            owners.Add(0, 0, 10, 10, 0, Full(100, 100), registry);
            owners.Add(0, 0, 10, 10, 0, Full(100, 100), registry);
            int[] rects = null;
            Assert.That(owners.Remove(0, 0, 10, 10, 0, registry), Is.True);
            Assert.That(registry.CopyRects(0, ref rects), Is.EqualTo(1));
            Assert.That(owners.Remove(0, 0, 10, 10, 0, registry), Is.True);
            Assert.That(registry.CopyRects(0, ref rects), Is.EqualTo(0));
        }

        [Test]
        public void ADuplicateAddMeansTheFractionOfTheScreenItWasAddedOnLast()
        {
            var registry = new SecureRectRegistry();
            var owners = new SecureRectScreenOwners();
            owners.Add(0, 0, 10, 10, 0, Full(100, 100), registry);
            owners.Add(0, 0, 10, 10, 0, Full(200, 200), registry); // same values on a bigger screen: a smaller fraction
            Assert.That(Rect(registry, 0, 0), Is.EqualTo(new[] { 0, 190, 10, 200 }));
        }

        [Test]
        public void NegativeZeroIsTheSameRectangle()
        {
            var registry = new SecureRectRegistry();
            var owners = new SecureRectScreenOwners();
            owners.Add(-0f, 0, 10, 10, 0, Full(100, 100), registry);
            Assert.That(owners.Remove(0f, 0, 10, 10, 0, registry), Is.True);
        }

        [Test]
        public void ManualDuplicateAddsNeedTwoRemovals()
        {
            var registry = new SecureRectRegistry();
            var owners = new SecureRectManualOwners();
            owners.Add(1, 2, 3, 4, registry, 0);
            owners.Add(1, 2, 3, 4, registry, 0);
            int[] rects = null;
            Assert.That(owners.Remove(1, 2, 3, 4, registry), Is.True);
            Assert.That(registry.CopyRects(0, ref rects), Is.EqualTo(1));
            Assert.That(owners.Remove(1, 2, 3, 4, registry), Is.True);
            Assert.That(registry.CopyRects(0, ref rects), Is.EqualTo(0));
            Assert.That(owners.Remove(1, 2, 3, 4, registry), Is.False);
        }

        [Test]
        public void ResolverKeepsTheLastGoodSurface()
        {
            var resolver = new SecureRectViewResolver();
            // Nothing read yet: the display's real size, never "no scale".
            var v = resolver.Resolve(new SecureRectViewRead { ScreenWidth = 360, ScreenHeight = 806, FallbackWidth = 720, FallbackHeight = 1612 }, out bool changed);
            Assert.That((v.Left, v.Top, v.Width, v.Height), Is.EqualTo((0, 0, 720, 1612)));
            Assert.That(changed, Is.True);
            Assert.That(resolver.State, Is.EqualTo(SecureRectViewState.Unreadable));

            v = resolver.Resolve(new SecureRectViewRead { ScreenWidth = 360, ScreenHeight = 806, Ok = true, Settled = true, Left = 0, Top = 50, Width = 720, Height = 1500 }, out changed);
            Assert.That((v.Top, v.Height), Is.EqualTo((50, 1500)));
            Assert.That(resolver.State, Is.EqualTo(SecureRectViewState.Ok));

            // Unreadable and unsettled reads keep the good surface; only the screen size follows.
            v = resolver.Resolve(new SecureRectViewRead { ScreenWidth = 806, ScreenHeight = 360, Ok = true, Settled = false, Width = 1, Height = 1 }, out changed);
            Assert.That((v.Top, v.Width, v.Height, v.ScreenWidth), Is.EqualTo((50, 720, 1500, 806)));
            Assert.That(resolver.State, Is.EqualTo(SecureRectViewState.Unsettled));
            resolver.Resolve(new SecureRectViewRead { ScreenWidth = 806, ScreenHeight = 360 }, out changed);
            Assert.That(changed, Is.True);
            resolver.Resolve(new SecureRectViewRead { ScreenWidth = 806, ScreenHeight = 360 }, out changed);
            Assert.That(changed, Is.False, "the warning is for a state change, not every frame");
        }

        [Test]
        public void AnUnsettledFirstReadStillBeatsNoScale()
        {
            var resolver = new SecureRectViewResolver();
            var v = resolver.Resolve(new SecureRectViewRead { ScreenWidth = 540, ScreenHeight = 900, Ok = true, Settled = false, Width = 1125, Height = 2436, FallbackWidth = 540, FallbackHeight = 900 }, out _);
            Assert.That((v.Width, v.Height), Is.EqualTo((1125, 2436)));
        }

        sealed class Harness
        {
            public readonly SecureRectRegistry Registry = new SecureRectRegistry();
            public SecureRectViewRead Read;
            public readonly System.Collections.Generic.List<int> Pushes = new System.Collections.Generic.List<int>();
            public readonly System.Collections.Generic.List<string> Warnings = new System.Collections.Generic.List<string>();
            public readonly SecureRectScreenSync Sync;
            public Harness(SecureRectViewRead read)
            {
                Read = read;
                Sync = new SecureRectScreenSync(Registry, d => Read, Pushes.Add, Warnings.Add);
            }
        }

        static SecureRectViewRead Good(int sw, int sh, int l, int t, int w, int h) =>
            new SecureRectViewRead { ScreenWidth = sw, ScreenHeight = sh, Ok = true, Settled = true, Left = l, Top = t, Width = w, Height = h, FallbackWidth = w, FallbackHeight = h };

        [Test]
        public void TickConvertsAgainOnlyWhenTheViewChanged()
        {
            var h = new Harness(Good(500, 1000, 0, 0, 1000, 2000));
            h.Sync.Add(50, 100, 150, 300, 0);
            Assert.That(h.Pushes, Is.EqualTo(new[] { 0 }));
            h.Sync.Tick();
            Assert.That(h.Pushes.Count, Is.EqualTo(1), "an unchanged view pushes nothing");
            h.Read = Good(250, 500, 0, 40, 1000, 2000); // render halved, surface moved down
            h.Sync.Tick();
            Assert.That(h.Pushes.Count, Is.EqualTo(2));
            Assert.That(Rect(h.Registry, 0, 0), Is.EqualTo(new[] { 100, 1440, 300, 1840 }));
        }

        [Test]
        public void TickSkipsAnUnsettledViewAndKeepsWhatIsRegistered()
        {
            var h = new Harness(Good(500, 1000, 0, 0, 1000, 2000));
            h.Sync.Add(50, 100, 150, 300, 0);
            var before = Rect(h.Registry, 0, 0);
            h.Read = new SecureRectViewRead { ScreenWidth = 1000, ScreenHeight = 500, Ok = true, Settled = false, Width = 1000, Height = 2000 };
            h.Sync.Tick();
            Assert.That(h.Pushes.Count, Is.EqualTo(1));
            Assert.That(Rect(h.Registry, 0, 0), Is.EqualTo(before));
        }

        [Test]
        public void AnUnreadableSurfaceFallsBackToTheDisplaySizeAndWarnsOnce()
        {
            // Resolution scaling, surface never readable: the rectangle must still land at display scale, not at 1/N.
            var h = new Harness(new SecureRectViewRead { ScreenWidth = 360, ScreenHeight = 806, FallbackWidth = 720, FallbackHeight = 1612 });
            h.Sync.Add(36, 120.9f, 216, 282.1f, 0);
            Assert.That(Rect(h.Registry, 0, 0), Is.EqualTo(new[] { 72, 1047, 432, 1371 }));
            h.Sync.Tick();
            h.Sync.Tick();
            Assert.That(h.Warnings.Count, Is.EqualTo(1));
        }

        [Test]
        public void ADifferentAspectRenderSizeScalesEachAxisByItsOwnRatio()
        {
            // Display 720x1612 rendered at 540x600: the surface stretches it, x by 4/3 and y by 2.69.
            var h = new Harness(Good(540, 600, 0, 0, 720, 1612));
            h.Sync.Add(54, 60, 270, 300, 0);
            var r = Rect(h.Registry, 0, 0);
            Assert.That(r[0], Is.EqualTo(72));
            Assert.That(r[2], Is.EqualTo(360));
            Assert.That(r[1], Is.EqualTo((int)System.Math.Floor((600 - 300) * 1612.0 / 600)));
            Assert.That(r[3], Is.EqualTo((int)System.Math.Ceiling((600 - 60) * 1612.0 / 600)));
        }

        [Test]
        public void NaNAndMissingDisplaysAreIgnoredWithAWarning()
        {
            var h = new Harness(Good(100, 100, 0, 0, 100, 100));
            h.Sync.Add(float.NaN, 0, 1, 1, 0);
            Assert.That(h.Warnings.Count, Is.EqualTo(1));
            h.Read = default; // a display that does not exist
            h.Sync.Add(0, 0, 1, 1, 3);
            Assert.That(h.Warnings.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(h.Pushes, Is.Empty);
            Assert.That(h.Sync.HasEntries, Is.False);
        }

        [Test]
        public void RemoveAllClearsAndPushes()
        {
            var h = new Harness(Good(100, 100, 0, 0, 100, 100));
            h.Sync.Add(0, 0, 10, 10, 0);
            h.Sync.RemoveAll();
            int[] rects = null;
            Assert.That(h.Registry.CopyRects(0, ref rects), Is.EqualTo(0));
            Assert.That(h.Pushes.Count, Is.EqualTo(2));
        }
    }
}
