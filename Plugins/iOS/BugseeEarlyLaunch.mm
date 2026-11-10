// Native early launch (opt-in, off unless the Editor build step wrote the configuration into Info.plist).
//
// Unity starts the Bugsee iOS SDK from C# (Bugsee.Launch), after the engine's native startup and the iOS launch watchdog window.
// With the early launch the SDK is started from here, very early, with the token and the JSON launch options fixed at build time
// (Info.plist dictionary "BugseeEarlyLaunch", written by Editor/BugseeIosEarlyLaunchPostProcess.cs). The first C# Bugsee.Launch then
// adopts that session (_bugsee_early_launch_adopt) instead of starting a second one. Without the dictionary nothing here runs.
// See DESIGN.md, "iOS native early launch".

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#include <TargetConditionals.h>
#include <stdlib.h>
#include <string.h>

#if __has_include(<Bugsee/Bugsee.h>)
#import <Bugsee/Bugsee.h>
#import <Bugsee/BugseeSPI.h>
#define BUGSEE_EARLY_SDK 1
#elif __has_include("Bugsee/Bugsee.h")
#import "Bugsee/Bugsee.h"
#import "Bugsee/BugseeSPI.h"
#define BUGSEE_EARLY_SDK 1
#else
#define BUGSEE_EARLY_SDK 0
#endif

// The simulator slice of the SDK has no crash reporter and the Crash Report Extension never runs there: nothing to gain, so no early launch.
#if BUGSEE_EARLY_SDK && !TARGET_OS_SIMULATOR && !TARGET_OS_MACCATALYST
#define BUGSEE_EARLY_ACTIVE 1
#else
#define BUGSEE_EARLY_ACTIVE 0
#endif

#if BUGSEE_EARLY_ACTIVE

// BugseeUnityCallbacks.mm
extern "C" {
    void _bugsee_ensure_wrapper(const char *version, const char *build);
    void _bugsee_set_wrapper_context(const char *json);
}

static NSString *const kBugseeEarlyKey = @"BugseeEarlyLaunch";

// Unity builds with Objective-C exceptions disabled, so no @try and no @synchronized: a plain lock.
static NSLock *EarlyLock(void)
{
    static NSLock *lock;
    static dispatch_once_t once;
    dispatch_once(&once, ^{ lock = [NSLock new]; });
    return lock;
}

// Guarded by EarlyLock.
static BOOL gEarlyAttempted = NO;  // the early run started (once); says nothing about whether it got as far as the launch call
static BOOL gEarlyLaunched = NO;   // launchWithToken is being called (set only right before it): the only state that can be adopted
static BOOL gEarlyAdopted = NO;    // a C# Launch has asked about it (once)
static NSString *gEarlyToken = nil;
static NSString *gEarlyOptionsJson = nil;

static NSString *EarlyString(NSDictionary *config, NSString *key)
{
    id value = config[key];
    return [value isKindOfClass:[NSString class]] ? (NSString *)value : nil;
}

static void BugseeEarlyLaunchRun(NSString *point)
{
    [EarlyLock() lock];
    // One owner for the slot: a C# Launch or a Stop that came first has claimed it (gEarlyAdopted), so the early launch must not start
    // the SDK as well.
    BOOL already = gEarlyAttempted || gEarlyAdopted;
    gEarlyAttempted = YES;
    [EarlyLock() unlock];
    if (already) {
        return;
    }
    {
        NSDictionary *config = [[NSBundle mainBundle] objectForInfoDictionaryKey:kBugseeEarlyKey];
        if (![config isKindOfClass:[NSDictionary class]]) {
            return;
        }
        NSString *token = EarlyString(config, @"Token");
        if (token.length == 0) {
            return;
        }
        NSString *optionsJson = EarlyString(config, @"Options");
        NSDictionary *options = nil;
        if (optionsJson.length > 0) {
            id parsed = [NSJSONSerialization JSONObjectWithData:[optionsJson dataUsingEncoding:NSUTF8StringEncoding]
                                                        options:0
                                                          error:nil];
            if ([parsed isKindOfClass:[NSDictionary class]]) {
                options = parsed;
            }
        }

        // The wrapper identity the C# Launch reports (Unity wrapper version, build and host context), set before the SDK reads it.
        NSString *version = EarlyString(config, @"WrapperVersion");
        NSString *build = EarlyString(config, @"WrapperBuild");
        _bugsee_ensure_wrapper(version.length ? version.UTF8String : NULL, build.length ? build.UTF8String : NULL);
        NSDictionary *context = @{
            @"unity_version": EarlyString(config, @"UnityVersion") ?: @"",
            @"unity_platform": @"IPhonePlayer",
            @"product_name": EarlyString(config, @"ProductName") ?: @"",
        };
        NSData *contextData = [NSJSONSerialization dataWithJSONObject:context options:0 error:nil];
        NSString *contextJson = contextData ? [[NSString alloc] initWithData:contextData encoding:NSUTF8StringEncoding] : nil;
        if (contextJson) {
            _bugsee_set_wrapper_context(contextJson.UTF8String);
        }

        [EarlyLock() lock];
        // Every early return above (no or invalid configuration) leaves these unset: nothing to adopt, the C# Launch starts normally.
        // The claim is checked again here, in the same step that records the launch: a C# Launch or Stop that came in meanwhile owns the slot.
        BOOL claimed = gEarlyAdopted;
        if (!claimed) {
            gEarlyToken = [token copy];
            gEarlyOptionsJson = [optionsJson copy];
            gEarlyLaunched = YES;
        }
        [EarlyLock() unlock];
        if (claimed) {
            return;
        }
        NSLog(@"[Bugsee] Native early launch (%@).", point);
        // Launch with the parsed options, like the C# launch does.
        [Bugsee launchWithToken:token andOptions:options];
    }
}

@interface BugseeEarlyLaunchHook : NSObject
@end

@implementation BugseeEarlyLaunchHook

+ (void)load
{
    if (![[NSBundle mainBundle] objectForInfoDictionaryKey:kBugseeEarlyKey]) {
        return;
    }
    [[NSNotificationCenter defaultCenter] addObserverForName:UIApplicationDidFinishLaunchingNotification
                                                      object:nil
                                                       queue:nil
                                                  usingBlock:^(NSNotification *note) {
        BugseeEarlyLaunchRun(@"didFinishLaunching");
    }];
}

@end

extern "C" {

// 0: nothing to adopt (the early launch did not run, in which case it is now claimed and will not run; or a C# Launch already asked). 1: the early launch ran (launchWithToken was
// called): adopt it, whatever the SDK status is now, because the SDK defers the real start until the app is active (status stays
// Stopped meanwhile) and a second launch call must never be made. 2: the same, but the token differs from the early one.
// Answers once: the first C# Launch adopts, later ones (after a Stop, say) launch normally. Mirrors EarlyLaunchSession in
// Runtime/WrapperPolicy/IosEarlyLaunchPolicy.cs (unit-tested there); C# also looks at the status (a stop in flight: no adoption).
int _bugsee_early_launch_adopt(const char *appToken)
{
    [EarlyLock() lock];
    int answer = 0;
    if (!gEarlyAdopted) {
        // The first question claims the slot even when the early launch has not run yet: it then never runs (BugseeEarlyLaunchRun
        // checks the claim), so the SDK is started once, by this C# Launch.
        gEarlyAdopted = YES;
        if (gEarlyLaunched && gEarlyToken != nil) {
            NSString *given = appToken ? [NSString stringWithUTF8String:appToken] : nil;
            answer = (given != nil && [given isEqualToString:gEarlyToken]) ? 1 : 2;
        }
    }
    [EarlyLock() unlock];
    return answer;
}

// A Stop or a delete claims the early session: after the app stopped the SDK (consent withheld, say), a later Launch must start it
// normally. Without this the Launch would adopt a stopped SDK, report success and start nothing. Also true when the early launch has
// not run yet: it then cannot be adopted afterwards.
void _bugsee_early_launch_cancel(void)
{
    [EarlyLock() lock];
    gEarlyAdopted = YES;
    [EarlyLock() unlock];
}

char *_bugsee_early_launch_options(void)
{
    [EarlyLock() lock];
    char *copy = gEarlyOptionsJson ? strdup(gEarlyOptionsJson.UTF8String) : NULL;
    [EarlyLock() unlock];
    return copy;
}

} // extern "C"

#else // early launch not available (no SDK, simulator, Mac Catalyst)

extern "C" {
int _bugsee_early_launch_adopt(const char *appToken) { (void)appToken; return 0; }
void _bugsee_early_launch_cancel(void) {}
char *_bugsee_early_launch_options(void) { return NULL; }
}

#endif
