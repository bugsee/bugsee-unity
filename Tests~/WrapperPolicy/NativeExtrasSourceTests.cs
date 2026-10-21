using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Bugsee.WrapperPolicy.Tests
{
    /// <summary>
    /// Source checks for the native extras exposed in #103: the default network sanitizer (Android) and the WebView secure
    /// views. A drift between the bridges and the SDK names is otherwise only visible on a device.
    /// </summary>
    public class NativeExtrasSourceTests
    {
        static string Read(string relative)
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "package.json")))
                    return File.ReadAllText(Path.Combine(dir, relative));
                dir = Directory.GetParent(dir)?.FullName;
            }
            throw new InvalidOperationException("Could not locate repo root (package.json).");
        }

        [Test]
        public void Android_sanitizer_goes_through_the_SDKs_default_sanitizer_and_is_kept_by_R8()
        {
            var android = Read("Runtime/Platform/Android/AndroidBridge.cs");
            Assert.That(android, Does.Contain("\"getDefaultNetworkSanitizer\""));
            Assert.That(android, Does.Contain("sanitizer.Call(\"sanitize\", androidEvent.Native)"));
            Assert.That(Read("Plugins/Android/bugsee-unity-wrapper.proguard"),
                Does.Contain("-keep interface com.bugsee.library.contracts.exchange.NetworkDataSanitizer { *; }"));
        }

        [Test]
        public void Android_webview_calls_run_on_the_UI_thread_and_name_the_View_overload_of_remove()
        {
            var android = Read("Runtime/Platform/Android/AndroidBridge.cs");
            Assert.That(android, Does.Contain("\"addSecureWebView\""));
            Assert.That(android, Does.Contain("runOnUiThread"));
            // removeSecureView(View) and removeSecureView(Object) both exist; the Object one ignores a WebView.
            Assert.That(android, Does.Contain("\"removeSecureView\", \"(Landroid/view/View;)V\""));
            Assert.That(android, Does.Contain("ExceptionClear"));
            Assert.That(android, Does.Contain("android.webkit.WebView"));
        }

        [Test]
        public void Ios_webview_add_masks_the_view_and_both_exports_exist_in_the_real_and_stub_sections()
        {
            var mm = Read("Plugins/iOS/BugseeUnityBridge.mm");
            foreach (var name in new[] { "_bugsee_add_secure_web_view", "_bugsee_remove_secure_web_view" })
            {
                Assert.That(Regex.Matches(mm, @"int " + name + @"\(void \*view\)").Count, Is.EqualTo(2),
                    name + " must exist in the SDK section and in the no-SDK stub");
            }
            // +addSecureWebView: only protects the page; the view is hidden by +addSecureView: (#103).
            var op = mm.Substring(mm.IndexOf("BugseeSecureWebViewOp(void *view, BOOL add)", StringComparison.Ordinal));
            op = op.Substring(0, op.IndexOf("int _bugsee_add_secure_web_view", StringComparison.Ordinal));
            Assert.That(op, Does.Contain("[Bugsee addSecureWebView:"));
            Assert.That(op, Does.Contain("[Bugsee addSecureView:"));
            Assert.That(op, Does.Contain("[Bugsee removeSecureView:"));
            Assert.That(op, Does.Contain("dispatch_get_main_queue"));
            Assert.That(op, Does.Contain("isKindOfClass:"));
        }

        [Test]
        public void Public_api_delegates_to_the_bridge()
        {
            var api = Read("Runtime/Bugsee.cs");
            Assert.That(api, Does.Contain("Bridge.ApplyDefaultNetworkSanitizer("));
            Assert.That(Regex.Matches(api, @"Bridge\.AddSecureWebView\(").Count, Is.EqualTo(2));
            Assert.That(Regex.Matches(api, @"Bridge\.RemoveSecureWebView\(").Count, Is.EqualTo(2));
        }
    }
}
