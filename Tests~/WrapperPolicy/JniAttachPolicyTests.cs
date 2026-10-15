using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class JniAttachPolicyTests
    {
        const JniAttachMode None = JniAttachMode.None;
        const JniAttachMode Detach = JniAttachMode.AttachDetach;
        const JniAttachMode Only = JniAttachMode.AttachOnly;

        [TestCase(false, true, false, 0, Detach, TestName = "a pool thread is attached for the scope and detached")]
        [TestCase(false, false, false, 0, Only, TestName = "a dedicated, Java-created or UI thread is attached and left attached")]
        [TestCase(true, false, false, 0, None, TestName = "the main thread is never touched")]
        [TestCase(true, true, false, 0, None, TestName = "the main thread wins over the pool flag")]
        [TestCase(false, true, true, 0, None, TestName = "a pool thread this wrapper already attached is not attached again")]
        [TestCase(false, false, true, 0, None, TestName = "a dedicated thread this wrapper already attached is not attached again")]
        [TestCase(false, true, false, 1, None, TestName = "a nested scope on a pool thread does nothing")]
        [TestCase(false, false, false, 2, None, TestName = "a nested scope on another thread does nothing")]
        public void Attach_decision(bool isMain, bool isPool, bool weAttachedBefore, int depth, JniAttachMode expected)
        {
            Assert.That(JniAttachPolicy.Decide(isMain, isPool, weAttachedBefore, depth), Is.EqualTo(expected));
        }

        [Test]
        public void Only_a_pool_thread_is_ever_detached()
        {
            foreach (bool isMain in new[] { false, true })
            foreach (bool isPool in new[] { false, true })
            foreach (bool before in new[] { false, true })
            foreach (int depth in new[] { 0, 1 })
            {
                var mode = JniAttachPolicy.Decide(isMain, isPool, before, depth);
                if (mode == JniAttachMode.AttachDetach)
                    Assert.That(isPool && !isMain && !before && depth == 0);
            }
        }
    }
}
