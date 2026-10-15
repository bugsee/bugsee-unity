using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class AndroidSymbolPolicyTests
    {
        [TestCase(NativeSymbolLevel.Disabled, "Disabled")]
        [TestCase(NativeSymbolLevel.Public, "Public")]
        public void Upload_without_debugging_symbols_warns_with_what_is_lost_and_the_trade_off(NativeSymbolLevel level, string named)
        {
            var d = AndroidSymbolPolicy.Decide(useFullSymbols: false, uploadConfigured: true, current: level);

            Assert.That(d.Change, Is.False);
            Assert.That(d.Warning, Does.Contain("Create symbols.zip is " + named));
            Assert.That(d.Warning, Does.Contain("file:line"));
            Assert.That(d.Warning, Does.Contain("ten times larger"));
            Assert.That(d.Warning, Does.Contain("BUGSEE_ANDROID_FULL_SYMBOLS=1"));
        }

        [TestCase(NativeSymbolLevel.Disabled)]
        [TestCase(NativeSymbolLevel.Public)]
        public void No_upload_configured_means_no_warning_and_no_change(NativeSymbolLevel level)
        {
            var d = AndroidSymbolPolicy.Decide(false, false, level);
            Assert.That(d.Change, Is.False);
            Assert.That(d.Warning, Is.Null);
        }

        [TestCase(NativeSymbolLevel.Disabled)]
        [TestCase(NativeSymbolLevel.Public)]
        public void Opt_in_switches_to_debugging_without_warning(NativeSymbolLevel level)
        {
            foreach (bool upload in new[] { true, false })
            {
                var d = AndroidSymbolPolicy.Decide(true, upload, level);
                Assert.That(d.Change, Is.True);
                Assert.That(d.Warning, Is.Null);
            }
        }

        [Test]
        public void Debugging_is_left_alone()
        {
            foreach (bool opt in new[] { true, false })
            {
                var d = AndroidSymbolPolicy.Decide(opt, true, NativeSymbolLevel.Debugging);
                Assert.That(d.Change, Is.False);
                Assert.That(d.Warning, Is.Null);
            }
        }
    }
}
