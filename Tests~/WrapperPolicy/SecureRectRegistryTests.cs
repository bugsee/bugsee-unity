using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class SecureRectRegistryTests
    {
        [Test]
        public void Ios_scale_3_rounds_outward_after_convert()
        {
            var registry = new SecureRectRegistry();
            registry.Set("hud", 0, 0, 0, 10, 10);
            int[] snap = registry.Snapshot(0, 3f);
            Assert.That(snap, Is.EqualTo(new[] { 1, 1, 0, 0, 4, 4 }));
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
    }
}
