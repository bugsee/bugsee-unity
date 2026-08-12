#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#include <stdlib.h>
#include <string.h>

#if __has_include(<Bugsee/Bugsee.h>)
#import <Bugsee/Bugsee.h>
#define BUGSEE_IOS_SDK 1
#elif __has_include("Bugsee/Bugsee.h")
#import "Bugsee/Bugsee.h"
#define BUGSEE_IOS_SDK 1
#else
#define BUGSEE_IOS_SDK 0
#endif

#if BUGSEE_IOS_SDK

static id BugseeDeserializeJson(const char *string)
{
    if (!string) {
        return nil;
    }
    NSError *err = nil;
    NSData *jsonData = [[NSString stringWithUTF8String:string] dataUsingEncoding:NSUTF8StringEncoding];
    id obj = [NSJSONSerialization JSONObjectWithData:jsonData
                                             options:NSJSONReadingAllowFragments
                                               error:&err];
    return err ? nil : obj;
}

static NSString *BugseeNSString(const char *c)
{
    return c ? [NSString stringWithUTF8String:c] : nil;
}

extern "C" {

void _bugsee_launch(const char *appToken, const char *optionsJson)
{
    NSDictionary *dict = nil;
    if (optionsJson) {
        id parsed = BugseeDeserializeJson(optionsJson);
        if ([parsed isKindOfClass:[NSDictionary class]]) {
            dict = parsed;
        }
    }
    [Bugsee launchWithToken:BugseeNSString(appToken) andOptions:dict];
}

void _bugsee_relaunch(const char *optionsJson)
{
    NSDictionary *dict = nil;
    if (optionsJson) {
        id parsed = BugseeDeserializeJson(optionsJson);
        if ([parsed isKindOfClass:[NSDictionary class]]) {
            dict = parsed;
        }
    }
    [Bugsee relaunchWithDictionaryOptions:dict];
}

void _bugsee_stop(void)
{
    [Bugsee stop:nil];
}

bool _bugsee_get_launched(void)
{
    return [Bugsee sharedInstance] != nil;
}

void _bugsee_start_blackout(void)
{
    [Bugsee pause];
}

void _bugsee_end_blackout(void)
{
    [Bugsee resume];
}

void _bugsee_log(const char *message, int level)
{
    [Bugsee log:BugseeNSString(message) level:(BugseeLogLevel)level];
}

void _bugsee_trace(const char *name, const char *valueJson)
{
    id value = valueJson ? BugseeDeserializeJson(valueJson) : nil;
    if (!value) {
        value = BugseeNSString(valueJson);
    }
    [Bugsee traceKey:BugseeNSString(name) withValue:value];
}

void _bugsee_event(const char *name, const char *paramsJson)
{
    if (paramsJson) {
        id params = BugseeDeserializeJson(paramsJson);
        if ([params isKindOfClass:[NSDictionary class]]) {
            [Bugsee registerEvent:BugseeNSString(name) withParams:params];
            return;
        }
    }
    [Bugsee registerEvent:BugseeNSString(name)];
}

void _bugsee_logException(const char *name, const char *reason, bool handled)
{
    NSString *finalName = BugseeNSString(name) ?: @"UnityManagedException";
    NSString *finalReason = BugseeNSString(reason) ?: @"";
    if (handled) {
        [Bugsee logException:finalName reason:finalReason options:nil completion:nil];
    } else {
        [Bugsee logUnhandledException:finalName reason:finalReason completion:nil];
    }
}

void _bugsee_test_crash(void)
{
    [Bugsee testSignalCrash];
}

static NSArray<NSString *> *BugseeLabelsFromJson(const char *labelsJson)
{
    if (!labelsJson) {
        return nil;
    }
    id parsed = BugseeDeserializeJson(labelsJson);
    if (![parsed isKindOfClass:[NSArray class]]) {
        return nil;
    }
    NSMutableArray<NSString *> *out = [NSMutableArray array];
    for (id item in (NSArray *)parsed) {
        if ([item isKindOfClass:[NSString class]]) {
            [out addObject:item];
        } else if (item) {
            [out addObject:[item description]];
        }
    }
    return out.count > 0 ? out : nil;
}

static char *BugseeCopyUTF8(NSString *string)
{
    if (!string) {
        return NULL;
    }
    const char *utf8 = string.UTF8String;
    if (!utf8) {
        return NULL;
    }
    return strdup(utf8);
}

void _bugsee_show_report(const char *summary, const char *description, int severity, const char *labelsJson)
{
    NSArray<NSString *> *labels = BugseeLabelsFromJson(labelsJson);
    if (summary) {
        [Bugsee showReportControllerWithSummary:BugseeNSString(summary)
                                    description:BugseeNSString(description) ?: @""
                                       severity:(BugseeSeverityLevel)severity
                                         labels:labels];
    } else {
        [Bugsee showReportController];
    }
}

void _bugsee_upload(const char *summary, const char *description, int severity, const char *labelsJson)
{
    NSArray<NSString *> *labels = BugseeLabelsFromJson(labelsJson);
    [Bugsee uploadWithSummary:BugseeNSString(summary) ?: @""
                  description:BugseeNSString(description) ?: @""
                     severity:(BugseeSeverityLevel)severity
                       labels:labels];
}

void _bugsee_set_attribute(const char *key, const char *valueJson)
{
    id value = valueJson ? BugseeDeserializeJson(valueJson) : nil;
    if (!value) {
        value = BugseeNSString(valueJson);
    }
    if (!value) {
        return;
    }
    [Bugsee setAttribute:BugseeNSString(key) withValue:value];
}

char *_bugsee_get_attribute(const char *key)
{
    id attr = [Bugsee getAttribute:BugseeNSString(key)];
    if (!attr) {
        return NULL;
    }
    if ([NSJSONSerialization isValidJSONObject:@[attr]]) {
        NSData *data = [NSJSONSerialization dataWithJSONObject:attr options:0 error:nil];
        if (data) {
            NSString *json = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
            return BugseeCopyUTF8(json);
        }
    }
    return BugseeCopyUTF8([attr description]);
}

void _bugsee_clear_attribute(const char *key)
{
    [Bugsee clearAttribute:BugseeNSString(key)];
}

void _bugsee_clear_all_attributes(void)
{
    [Bugsee clearAllAttributes];
}

void _bugsee_set_email(const char *email)
{
    [Bugsee setEmail:BugseeNSString(email) ?: @""];
}

char *_bugsee_get_email(void)
{
    return BugseeCopyUTF8([Bugsee getEmail]);
}

void _bugsee_clear_email(void)
{
    [Bugsee clearEmail];
}

char *_bugsee_get_device_id(void)
{
    return BugseeCopyUTF8([Bugsee getDeviceId]);
}

void _bugsee_add_secure_rect(float x, float y, float w, float h)
{
    CGFloat scale = [UIScreen mainScreen].scale;
    CGRect rect = CGRectMake(x / scale, y / scale, w / scale, h / scale);
    [Bugsee addSecureRect:rect];
}

void _bugsee_remove_secure_rect(float x, float y, float w, float h)
{
    CGFloat scale = [UIScreen mainScreen].scale;
    CGRect rect = CGRectMake(x / scale, y / scale, w / scale, h / scale);
    [Bugsee removeSecureRect:rect];
}

void _bugsee_remove_all_secure_rects(void)
{
    [Bugsee removeAllSecureRects];
}

void _bugsee_capture_view_hierarchy(void)
{
    [Bugsee captureViewHierarchy];
}

void _bugsee_feedback_show(void)
{
    [Bugsee showFeedbackController];
}

void _bugsee_feedback_set_greeting(const char *message)
{
    [Bugsee setDefaultFeedbackGreeting:BugseeNSString(message)];
}

void _bugsee_appearance_set_color(const char *propertyName, int r, int g, int b, int a)
{
    NSString *name = BugseeNSString(propertyName);
    if (!name) {
        return;
    }
    UIColor *color = [UIColor colorWithRed:r / 255.0 green:g / 255.0 blue:b / 255.0 alpha:a / 255.0];
    [[Bugsee appearance] setValue:color forKey:name];
}

char *_bugsee_appearance_get_color(const char *propertyName)
{
    NSString *name = BugseeNSString(propertyName);
    if (!name) {
        return NULL;
    }
    id value = [[Bugsee appearance] valueForKey:name];
    if (![value isKindOfClass:[UIColor class]]) {
        return NULL;
    }
    CGFloat r = 0, g = 0, b = 0, a = 0;
    if (![(UIColor *)value getRed:&r green:&g blue:&b alpha:&a]) {
        return NULL;
    }
    NSString *hex = [NSString stringWithFormat:@"#%02lX%02lX%02lX%02lX",
                     lroundf(a * 255), lroundf(r * 255), lroundf(g * 255), lroundf(b * 255)];
    return BugseeCopyUTF8(hex);
}

void _bugsee_appearance_set_string(const char *propertyName, const char *propertyValue)
{
    NSString *name = BugseeNSString(propertyName);
    if (!name) {
        return;
    }
    [[Bugsee appearance] setValue:BugseeNSString(propertyValue) ?: @"" forKey:name];
}

char *_bugsee_appearance_get_string(const char *propertyName)
{
    NSString *name = BugseeNSString(propertyName);
    if (!name) {
        return NULL;
    }
    id value = [[Bugsee appearance] valueForKey:name];
    if (![value isKindOfClass:[NSString class]]) {
        return NULL;
    }
    return BugseeCopyUTF8((NSString *)value);
}

void _bugsee_free(char *ptr)
{
    if (ptr) {
        free(ptr);
    }
}

} // extern "C"

#else // !BUGSEE_IOS_SDK

extern "C" {

void _bugsee_launch(const char *appToken, const char *optionsJson) { (void)appToken; (void)optionsJson; }
void _bugsee_relaunch(const char *optionsJson) { (void)optionsJson; }
void _bugsee_stop(void) {}
bool _bugsee_get_launched(void) { return false; }
void _bugsee_start_blackout(void) {}
void _bugsee_end_blackout(void) {}
void _bugsee_log(const char *message, int level) { (void)message; (void)level; }
void _bugsee_trace(const char *name, const char *valueJson) { (void)name; (void)valueJson; }
void _bugsee_event(const char *name, const char *paramsJson) { (void)name; (void)paramsJson; }
void _bugsee_logException(const char *name, const char *reason, bool handled) { (void)name; (void)reason; (void)handled; }
void _bugsee_test_crash(void) {}
void _bugsee_show_report(const char *summary, const char *description, int severity, const char *labelsJson) { (void)summary; (void)description; (void)severity; (void)labelsJson; }
void _bugsee_upload(const char *summary, const char *description, int severity, const char *labelsJson) { (void)summary; (void)description; (void)severity; (void)labelsJson; }
void _bugsee_set_attribute(const char *key, const char *valueJson) { (void)key; (void)valueJson; }
char *_bugsee_get_attribute(const char *key) { (void)key; return NULL; }
void _bugsee_clear_attribute(const char *key) { (void)key; }
void _bugsee_clear_all_attributes(void) {}
void _bugsee_set_email(const char *email) { (void)email; }
char *_bugsee_get_email(void) { return NULL; }
void _bugsee_clear_email(void) {}
char *_bugsee_get_device_id(void) { return NULL; }
void _bugsee_add_secure_rect(float x, float y, float w, float h) { (void)x; (void)y; (void)w; (void)h; }
void _bugsee_remove_secure_rect(float x, float y, float w, float h) { (void)x; (void)y; (void)w; (void)h; }
void _bugsee_remove_all_secure_rects(void) {}
void _bugsee_capture_view_hierarchy(void) {}
void _bugsee_feedback_show(void) {}
void _bugsee_feedback_set_greeting(const char *message) { (void)message; }
void _bugsee_appearance_set_color(const char *propertyName, int r, int g, int b, int a) { (void)propertyName; (void)r; (void)g; (void)b; (void)a; }
char *_bugsee_appearance_get_color(const char *propertyName) { (void)propertyName; return NULL; }
void _bugsee_appearance_set_string(const char *propertyName, const char *propertyValue) { (void)propertyName; (void)propertyValue; }
char *_bugsee_appearance_get_string(const char *propertyName) { (void)propertyName; return NULL; }
void _bugsee_free(char *ptr) { (void)ptr; }

}

#endif
