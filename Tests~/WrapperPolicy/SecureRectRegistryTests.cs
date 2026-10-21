using System;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class SecureRectRegistryTests
    {
        [Test]
        public void CopyRects_returns_the_rects_of_one_display_without_scaling_and_reuses_the_buffer()
        {
            var registry = new SecureRectRegistry();
            registry.Set("a", 0, 5, 6, 1, 2); // normalised to 1,2,5,6
            registry.Set("b", 1, 9, 9, 10, 10);
            int[] buffer = null;
            Assert.That(registry.CopyRects(0, ref buffer), Is.EqualTo(1));
            Assert.That(buffer[0], Is.EqualTo(1));
            Assert.That(buffer[3], Is.EqualTo(6));
            int[] same = buffer;
            Assert.That(registry.CopyRects(0, ref buffer), Is.EqualTo(1));
            Assert.That(buffer, Is.SameAs(same));
            Assert.That(registry.CopyRects(2, ref buffer), Is.EqualTo(0));
        }

        [Test]
        public void GetRects_returns_a_prefix_filtered_copy_of_one_display_ordered_by_owner()
        {
            var registry = new SecureRectRegistry();
            registry.Set("manual:9", 0, 9, 9, 10, 10);
            registry.Set("manual:1", 0, 5, 6, 1, 2); // normalised to 1,2,5,6
            registry.Set("screen:1", 0, 0, 0, 1, 1);
            registry.Set("manual:2", 1, 7, 7, 8, 8);
            var rects = registry.GetRects("manual:", 0);
            Assert.That(rects.Count, Is.EqualTo(2));
            Assert.That(rects[0], Is.EqualTo(new[] { 1, 2, 5, 6 }));
            Assert.That(rects[1], Is.EqualTo(new[] { 9, 9, 10, 10 }));
            rects[0][0] = 99;
            Assert.That(registry.GetRects("manual:", 0)[0][0], Is.EqualTo(1), "a copy, not the registry's own array");
            Assert.That(registry.GetRects("manual:", 3), Is.Empty);
        }

        [Test]
        public void CopyRects_is_safe_while_another_thread_registers_rects()
        {
            var registry = new SecureRectRegistry();
            var writer = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < 5000; i++) { registry.Set("o" + (i % 50), 0, 0, 0, i, i); if (i % 7 == 0) registry.RemoveOwner("o" + (i % 50)); }
            });
            int[] buffer = null;
            Assert.DoesNotThrow(() => { while (!writer.IsCompleted) registry.CopyRects(0, ref buffer); });
            writer.Wait();
        }

        [Test]
        public void Ios_scale_3_rounds_outward_after_convert()
        {
            var registry = new SecureRectRegistry();
            registry.Set("hud", 0, 0, 0, 10, 10);
            int[] snap = registry.Snapshot(0, 3f);
            Assert.That(snap, Is.EqualTo(new[] { 2, 1, 0, 0, 4, 4 }));
        }

        [Test]
        public void Same_rect_does_not_bump_version_and_other_owner_survives()
        {
            var registry = new SecureRectRegistry();
            registry.Set("a", 0, 0, 0, 2, 2);
            int version = registry.Snapshot(0, 1f)[0];
            registry.Set("a", 0, 0, 0, 2, 2);
            Assert.That(registry.Snapshot(0, 1f)[0], Is.EqualTo(version));

            registry.Set("b", 0, 5, 5, 8, 8);
            registry.RemoveOwner("a");
            int[] snap = registry.Snapshot(0, 1f);
            Assert.That(snap[0], Is.GreaterThan(version));
            Assert.That(snap[1], Is.EqualTo(1));
            Assert.That(snap[2], Is.EqualTo(5));
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void Non_finite_scale_throws(float scale)
        {
            var registry = new SecureRectRegistry();
            registry.Set("hud", 0, 0, 0, 10, 10);
            Assert.Throws<ArgumentOutOfRangeException>(() => registry.Snapshot(0, scale));
        }

        [Test]
        public void Inverted_edges_normalize_before_publish()
        {
            var registry = new SecureRectRegistry();
            registry.Set("hud", 0, 100, 200, 40, 80);
            int[] snap = registry.Snapshot(0, 1f);
            Assert.That(snap, Is.EqualTo(new[] { 2, 1, 40, 80, 100, 200 }));
        }

        [Test]
        public void Empty_snapshot_then_set_bumps_to_version_2()
        {
            var registry = new SecureRectRegistry();
            Assert.That(registry.Snapshot(0, 1f), Is.EqualTo(new[] { 1, 0 }));

            registry.Set("hud", 0, 0, 0, 10, 10);
            Assert.That(registry.Snapshot(0, 1f)[0], Is.EqualTo(2));
        }
    }
}
