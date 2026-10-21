// Sample only: a native WKWebView (or a plain UIView) on screen, handed to C# as a retained pointer, so the native-extras e2e scenario
// has a real WebView to pass to Bugsee.AddSecureWebView. Unity calls this on the main thread. WebKit is loaded at run time, so the
// sample needs no framework added to the Xcode project.
#import <UIKit/UIKit.h>
#include <dlfcn.h>

extern "C" {

void *_sample_create_native_view(int webView)
{
    CGRect frame = CGRectMake(0, 0, 120, 120);
    UIView *view = nil;
    if (webView)
    {
        dlopen("/System/Library/Frameworks/WebKit.framework/WebKit", RTLD_LAZY);
        Class webKitClass = NSClassFromString(@"WKWebView");
        if (webKitClass == Nil) return NULL;
        view = [(UIView *)[webKitClass alloc] initWithFrame:frame];
    }
    else
    {
        view = [[UIView alloc] initWithFrame:frame];
    }
    UIWindow *window = [UIApplication sharedApplication].keyWindow;
    [window.rootViewController.view addSubview:view];
    return (void *)CFBridgingRetain(view);
}

void _sample_destroy_native_view(void *ptr)
{
    if (ptr == NULL) return;
    UIView *view = (UIView *)CFBridgingRelease(ptr);
    [view removeFromSuperview];
}

} // extern "C"
