using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class SymbolUploadPolicyTests
    {
        [TestCase(false, false, true)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        public void The_linemap_upload_follows_both_switches(bool noSymbolUpload, bool noMapping, bool allowed) =>
            Assert.That(SymbolUploadPolicy.LinemapUploadAllowed(noSymbolUpload, noMapping), Is.EqualTo(allowed));
    }
}
