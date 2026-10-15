using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class JniAttachPolicyTests
    {
        [TestCase(false, true, false, 0, true, TestName = "an unattached pool thread is attached")]
        [TestCase(true, false, false, 0, false, TestName = "the main thread is never attached")]
        [TestCase(true, true, false, 0, false, TestName = "the main thread wins over the pool flag")]
        [TestCase(false, false, false, 0, false, TestName = "a non-pool thread (Unity's, a Java-owned one) is left alone")]
        [TestCase(false, true, true, 0, false, TestName = "an already attached thread is not attached again")]
        [TestCase(false, true, false, 1, false, TestName = "a nested scope does nothing")]
        public void Attach_decision(bool isMain, bool isPool, bool alreadyAttached, int depth, bool expected)
        {
            Assert.That(JniAttachPolicy.ShouldAttach(isMain, isPool, alreadyAttached, depth), Is.EqualTo(expected));
        }
    }
}
