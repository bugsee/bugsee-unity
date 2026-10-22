// Sample-only probe for the "feedback" e2e scenario: names the view controllers presented over the Unity view, so the
// run can tell that the native Feedback UI opened without a screenshot.
#import <UIKit/UIKit.h>

static UIViewController *SampleRoot(void)
{
    for (UIScene *scene in UIApplication.sharedApplication.connectedScenes)
    {
        if (![scene isKindOfClass:[UIWindowScene class]]) continue;
        for (UIWindow *window in ((UIWindowScene *)scene).windows)
            if (window.isKeyWindow && window.rootViewController != nil) return window.rootViewController;
    }
    return UIApplication.sharedApplication.keyWindow.rootViewController;
}

// "Root > Presented > ..." (a navigation controller adds its top controller); the string is owned by the caller.
extern "C" const char *_sample_presented_chain(void)
{
    NSMutableArray<NSString *> *names = [NSMutableArray array];
    UIViewController *controller = SampleRoot();
    while (controller != nil)
    {
        [names addObject:NSStringFromClass([controller class])];
        if ([controller isKindOfClass:[UINavigationController class]])
        {
            UIViewController *top = ((UINavigationController *)controller).topViewController;
            if (top != nil) [names addObject:NSStringFromClass([top class])];
        }
        controller = controller.presentedViewController;
    }
    return strdup([names componentsJoinedByString:@" > "].UTF8String);
}
