// Sample only: a native WKWebView (or a plain UIView) on screen, handed to C# as a retained pointer, so the native-extras e2e scenario
// has a real WebView to pass to Bugsee.AddSecureWebView, and a probe of whether the SDK ended up hiding it. Unity calls this on the
// main thread. WebKit is loaded at run time, so the sample needs no framework added to the Xcode project.
#import <UIKit/UIKit.h>
#include <dlfcn.h>

static UIWindow *SampleKeyWindow(void)
{
    for (UIScene *scene in [UIApplication sharedApplication].connectedScenes)
    {
        if (scene.activationState != UISceneActivationStateForegroundActive || ![scene isKindOfClass:[UIWindowScene class]]) continue;
        for (UIWindow *window in ((UIWindowScene *)scene).windows)
            if (window.isKeyWindow) return window;
    }
    for (UIScene *scene in [UIApplication sharedApplication].connectedScenes)
        if ([scene isKindOfClass:[UIWindowScene class]] && ((UIWindowScene *)scene).windows.count > 0)
            return ((UIWindowScene *)scene).windows.firstObject;
    return nil;
}

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
    [SampleKeyWindow().rootViewController.view addSubview:view];
    return (void *)CFBridgingRetain(view);
}

// UIView (Bugsee).bugseeProtectedView: whether the SDK's hidden-view set holds the view (read through KVC, so no Bugsee header is needed).
int _sample_native_view_is_protected(void *ptr)
{
    if (ptr == NULL) return 0;
    UIView *view = (__bridge UIView *)ptr;
    return [[view valueForKey:@"bugseeProtectedView"] boolValue] ? 1 : 0;
}

void _sample_destroy_native_view(void *ptr)
{
    if (ptr == NULL) return;
    UIView *view = (UIView *)CFBridgingRelease(ptr);
    [view removeFromSuperview];
}

} // extern "C"
