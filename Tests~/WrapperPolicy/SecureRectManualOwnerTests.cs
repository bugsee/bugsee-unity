using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class SecureRectManualOwnerTests
    {
        [Test]
        public void Facade_manual_owner_flow_pushes_snapshot_after_each_mutation()
        {
            var registry = new SecureRectRegistry();
            var owners = new SecureRectManualOwners();
            int[] lastPush = null;

            void PushDisplay0()
            {
                lastPush = registry.Snapshot(0, 1f);
            }

            owners.Add(0, 0, 2, 2, registry, 0);
            PushDisplay0();
            Assert.That(lastPush, Is.Not.Null);
            Assert.That(lastPush[1], Is.EqualTo(1));

            owners.Add(5, 5, 8, 8, registry, 0);
            PushDisplay0();
            Assert.That(lastPush[1], Is.EqualTo(2));
            int lastNonEmptyVersion = lastPush[0];

            Assert.That(owners.Remove(0, 0, 2, 2, registry), Is.True);
            PushDisplay0();
            Assert.That(lastPush[1], Is.EqualTo(1));
            Assert.That(lastPush[2], Is.EqualTo(5));
            Assert.That(lastPush[3], Is.EqualTo(5));
            Assert.That(lastPush[4], Is.EqualTo(8));
            Assert.That(lastPush[5], Is.EqualTo(8));

            Assert.That(owners.Remove(5, 5, 8, 8, registry), Is.True);
            PushDisplay0();
            Assert.That(lastPush[1], Is.EqualTo(0));
            Assert.That(lastPush[0], Is.GreaterThan(lastNonEmptyVersion));
            Assert.That(lastPush[0], Is.Not.EqualTo(1));
        }

        [Test]
        public void Facade_remove_all_manual_owners_then_push_empty_union()
        {
            var registry = new SecureRectRegistry();
            var owners = new SecureRectManualOwners();
            int[] lastPush = null;

            void PushDisplay0()
            {
                lastPush = registry.Snapshot(0, 1f);
            }

            owners.Add(1, 2, 3, 4, registry, 0);
            registry.Set("hud", 0, 10, 10, 20, 20);
            PushDisplay0();
            Assert.That(lastPush[1], Is.EqualTo(2));
            int lastNonEmptyVersion = lastPush[0];

            owners.RemoveAll(registry);
            PushDisplay0();
            Assert.That(lastPush[1], Is.EqualTo(1));
            Assert.That(lastPush[2], Is.EqualTo(10));

            registry.RemoveOwner("hud");
            PushDisplay0();
            Assert.That(lastPush[1], Is.EqualTo(0));
            Assert.That(lastPush[0], Is.GreaterThan(lastNonEmptyVersion));
        }
    }
}
