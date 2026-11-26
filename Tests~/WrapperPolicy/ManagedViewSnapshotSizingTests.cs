using Bugsee.WrapperPolicy;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class ManagedViewSnapshotSizingTests
    {
        [Test]
        public void ASmallSceneAllocatesWhatItAlwaysDid()
        {
            Assert.That(ManagedViewSnapshotSizing.Capacity(0, 1500), Is.EqualTo(512));
            Assert.That(ManagedViewSnapshotSizing.Capacity(20, 1500), Is.EqualTo(512));
            Assert.That(ManagedViewSnapshotSizing.Capacity(455, 1500), Is.EqualTo(512));
            Assert.That(ManagedViewSnapshotSizing.Capacity(-5, 1500), Is.EqualTo(512));
        }

        [Test]
        public void ABigSceneStartsAtItsPreviousSizePlusAnEighth_NeverAboveTheBudgetCap()
        {
            Assert.That(ManagedViewSnapshotSizing.Capacity(800, 1500), Is.EqualTo(900));
            Assert.That(ManagedViewSnapshotSizing.Capacity(1500, 1500), Is.EqualTo(1502));
            Assert.That(ManagedViewSnapshotSizing.Capacity(1502, 1500), Is.EqualTo(1502));
            Assert.That(ManagedViewSnapshotSizing.Capacity(int.MaxValue, 1500), Is.EqualTo(1502));
        }

        [Test]
        public void ABudgetSmallerThanTheMinimumDoesNotLowerTheMinimum()
        {
            Assert.That(ManagedViewSnapshotSizing.Capacity(1000, 100), Is.EqualTo(512));
        }

        [Test]
        public void TheSeenSetIsPresizedOnlyWhenTheSnapshotIsAboveTheMinimum()
        {
            Assert.That(ManagedViewSnapshotSizing.PresizeSeenSet(ManagedViewSnapshotSizing.Capacity(0, 1500)), Is.False);
            Assert.That(ManagedViewSnapshotSizing.PresizeSeenSet(ManagedViewSnapshotSizing.Capacity(300, 1500)), Is.False);
            Assert.That(ManagedViewSnapshotSizing.PresizeSeenSet(ManagedViewSnapshotSizing.Capacity(1500, 1500)), Is.True);
        }

        [Test]
        public void ASnapshotSizedForTheBigSceneWritesTheSameTreeAsOneThatGrew()
        {
            var big = new ManagedViewSnapshot(ManagedViewSnapshotSizing.Capacity(1500, 1500));
            var small = new ManagedViewSnapshot();
            foreach (var s in new[] { big, small })
            {
                s.Add(ManagedViewSnapshot.NoParent, 0, null, "UnityPlayer", null);
                for (var i = 1; i < 1500; i++)
                {
                    var node = s.Add(i / 2, i, null, "GameObject", "n" + i);
                    s.AddComponent(node, "Transform");
                    s.AddComponent(node, "MeshRenderer");
                    if (i % 3 == 0) s.SetBounds(node, i, i * 2, 10, 20);
                }
            }

            Assert.That(big.Count, Is.EqualTo(small.Count));
            Assert.That(ManagedViewTreeJson.Write(big.ToTree(), null, 0, 1f), Is.EqualTo(ManagedViewTreeJson.Write(small.ToTree(), null, 0, 1f)));
        }
    }
}
