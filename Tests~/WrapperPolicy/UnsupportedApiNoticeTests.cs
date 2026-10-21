using System.Collections.Generic;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    public class UnsupportedApiNoticeTests
    {
        [Test]
        public void The_message_is_logged_on_the_first_call_only()
        {
            var lines = new List<string>();
            var notice = new UnsupportedApiNotice(lines.Add);

            Assert.That(notice.Once(UnsupportedApiNotice.ResetVideoCapturePermissionMessage), Is.True);
            Assert.That(notice.Once(UnsupportedApiNotice.ResetVideoCapturePermissionMessage), Is.False);
            Assert.That(lines, Is.EqualTo(new[] { UnsupportedApiNotice.ResetVideoCapturePermissionMessage }));
        }

        [Test]
        public void Each_distinct_message_is_logged_once()
        {
            var lines = new List<string>();
            var notice = new UnsupportedApiNotice(lines.Add);
            notice.Once("a"); notice.Once("b"); notice.Once("a"); notice.Once("b");
            Assert.That(lines, Is.EqualTo(new[] { "a", "b" }));
        }

        [Test]
        public void A_throwing_logger_does_not_reach_the_caller()
        {
            var notice = new UnsupportedApiNotice(_ => throw new System.InvalidOperationException());
            Assert.DoesNotThrow(() => notice.Once("x"));
        }

        [Test]
        public void The_message_names_the_api_and_the_platform_that_acts_on_it()
        {
            Assert.That(UnsupportedApiNotice.ResetVideoCapturePermissionMessage, Does.Contain("ResetVideoCapturePermission does nothing on iOS"));
            Assert.That(UnsupportedApiNotice.ResetVideoCapturePermissionMessage, Does.Contain("Android"));
        }

        [Test]
        public void The_iOS_bridge_goes_through_the_notice_rather_than_an_empty_body()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "Runtime", "Platform", "IOS", "IOSBridge.cs")))
                dir = System.IO.Directory.GetParent(dir)?.FullName;
            Assert.That(dir, Is.Not.Null, "could not find the package root (Runtime/Platform/IOS/IOSBridge.cs) above the test directory");
            var src = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "Runtime", "Platform", "IOS", "IOSBridge.cs"));
            Assert.That(src, Does.Contain("_resetPermissionNotice.Once(UnsupportedApiNotice.ResetVideoCapturePermissionMessage)"));
            Assert.That(src, Does.Not.Contain("ResetVideoCapturePermission() { }"));
        }
    }
}
