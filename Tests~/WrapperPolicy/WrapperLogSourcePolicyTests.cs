using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class WrapperLogSourcePolicyTests
    {
        [Test]
        public void Missing_source_is_Custom()
        {
            Assert.That(WrapperLogSourcePolicy.Resolve(null), Is.EqualTo(98));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(5)]
        [TestCase(98)]
        public void Allow_list_is_unchanged(int source)
        {
            Assert.That(WrapperLogSourcePolicy.Resolve(source), Is.EqualTo(source));
        }

        [TestCase(3)]
        [TestCase(4)]
        [TestCase(99)]
        [TestCase(-1)]
        public void Anything_else_is_Custom(int source)
        {
            Assert.That(WrapperLogSourcePolicy.Resolve(source), Is.EqualTo(98));
        }
    }
}
