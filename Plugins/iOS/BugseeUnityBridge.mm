#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#include <stdlib.h>
#include <string.h>

#if __has_include(<Bugsee/Bugsee.h>)
#import <Bugsee/Bugsee.h>
#import <Bugsee/BGSContracts.h>
#define BUGSEE_IOS_SDK 1
#elif __has_include("Bugsee/Bugsee.h")
#import "Bugsee/Bugsee.h"
#import "Bugsee/BGSContracts.h"
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

static void BugseeRunOnMain(dispatch_block_t block)
{
    if ([NSThread isMainThread]) {
        block();
    } else {
        dispatch_async(dispatch_get_main_queue(), block);
    }
}

typedef void (*BugseeManagedReportCreateCallback)(int succeeded, uint64_t uploadToken);

static uint64_t gManagedReportUploadFence;
static NSMutableSet<NSNumber *> *gCancelledManagedReportUploadIds;

static BOOL BugseeManagedReportUploadStillActive(uint64_t uploadId, uint64_t uploadFence)
{
    if (uploadId == 0 || uploadFence != gManagedReportUploadFence) {
        return NO;
    }
    if (!gCancelledManagedReportUploadIds) {
        return YES;
    }
    return ![gCancelledManagedReportUploadIds containsObject:@(uploadId)];
}

static void BugseeBridgeSetAttachmentFileNameIfNeeded(id attachment, NSString *displayName, NSString *fileName)
{
    if (!attachment || fileName.length == 0 || [fileName isEqualToString:displayName]) {
        return;
    }
    if ([attachment conformsToProtocol:@protocol(BGSAttachmentContract)]) {
        ((id<BGSAttachmentContract>)attachment).fileName = fileName;
    }
}

static void BugseeBridgeSetAttachmentMimeTypeIfNeeded(id attachment, NSString *mimeType)
{
    if (!attachment || mimeType.length == 0) {
        return;
    }
    if ([attachment conformsToProtocol:@protocol(BGSAttachmentContract)]) {
        ((id<BGSAttachmentContract>)attachment).mimeType = mimeType;
        return;
    }
    if ([attachment respondsToSelector:@selector(setMimeType:)]) {
        [attachment performSelector:@selector(setMimeType:) withObject:mimeType];
    }
}

static void BugseeBridgeApplyExtendedReportDict(BugseeExtendedReport *report, NSDictionary *d)
{
    if (![d isKindOfClass:[NSDictionary class]] || !report) {
        return;
    }

    id summary = d[@"summary"];
    if ([summary isKindOfClass:[NSString class]]) {
        [report setSummary:summary];
    }
    id desc = d[@"description"];
    if ([desc isKindOfClass:[NSString class]]) {
        [report setDescription:desc];
    }
    id sev = d[@"severity"];
    if ([sev respondsToSelector:@selector(integerValue)]) {
        NSInteger sevVal = [sev integerValue];
        if (sevVal != 0) {
            [report setSeverity:(BugseeSeverityLevel)sevVal];
        }
    }

    id labels = d[@"labels"];
    if ([labels isKindOfClass:[NSArray class]]) {
        NSMutableArray<NSString *> *clean = [NSMutableArray array];
        for (id label in (NSArray *)labels) {
            if ([label isKindOfClass:[NSString class]]) {
                [clean addObject:label];
            }
        }
        report.labels = clean;
    }

    id removals = d[@"attributeRemovals"];
    if ([removals isKindOfClass:[NSArray class]]) {
        for (id name in (NSArray *)removals) {
            if ([name isKindOfClass:[NSString class]]) {
                [report clearAttribute:(NSString *)name];
            }
        }
    }

    id replaceAll = d[@"attributesReplaceAll"];
    BOOL shouldReplaceAll = [replaceAll respondsToSelector:@selector(boolValue)] && [replaceAll boolValue];

    id attrs = d[@"attributes"];
    if ([attrs isKindOfClass:[NSDictionary class]]) {
        if (shouldReplaceAll) {
            [report clearAllAttributes];
        }
        [(NSDictionary *)attrs enumerateKeysAndObjectsUsingBlock:^(id key, id obj, BOOL *stop) {
            if ([key isKindOfClass:[NSString class]]) {
                [report setAttribute:(NSString *)key withValue:obj];
            }
        }];
    } else if (shouldReplaceAll) {
        [report clearAllAttributes];
    }

    id attachments = d[@"attachments"];
    if ([attachments isKindOfClass:[NSArray class]]) {
        [report clearAllAttachments];
        for (id item in (NSArray *)attachments) {
            if (![item isKindOfClass:[NSDictionary class]]) {
                continue;
            }
            NSDictionary *att = (NSDictionary *)item;
            NSString *name = [att[@"name"] isKindOfClass:[NSString class]] ? att[@"name"] : @"attachment";
            id fileNameVal = att[@"fileName"];
            NSString *fileName = [fileNameVal isKindOfClass:[NSString class]] ? fileNameVal : nil;
            if (fileName.length == 0) {
                fileName = name;
            }
            id mime = att[@"mimeType"];
            NSString *mimeType = [mime isKindOfClass:[NSString class]] ? (NSString *)mime : nil;

            id pathVal = att[@"path"];
            if ([pathVal isKindOfClass:[NSString class]] && [(NSString *)pathVal length] > 0) {
                NSData *fileData = [NSData dataWithContentsOfFile:(NSString *)pathVal];
                if (!fileData) {
                    continue;
                }
                BugseeAttachment *attachment = [BugseeAttachment attachmentWithName:name filename:fileName data:fileData];
                if (attachment) {
                    BugseeBridgeSetAttachmentMimeTypeIfNeeded(attachment, mimeType);
                    [report setAttachment:attachment];
                }
                continue;
            }

            NSData *data = nil;
            id b64 = att[@"dataBase64"];
            if ([b64 isKindOfClass:[NSString class]] && [(NSString *)b64 length] > 0) {
                data = [[NSData alloc] initWithBase64EncodedString:(NSString *)b64 options:0];
            }
            id text = att[@"text"];
            if (!data && [text isKindOfClass:[NSString class]]) {
                data = [(NSString *)text dataUsingEncoding:NSUTF8StringEncoding];
            }
            if (!data) {
                continue;
            }
            BugseeAttachment *attachment = [BugseeAttachment attachmentWithName:name filename:fileName data:data];
            if (attachment) {
                BugseeBridgeSetAttachmentMimeTypeIfNeeded(attachment, mimeType);
                [report setAttachment:attachment];
            }
        }
    }
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
    [Bugsee startBlackout];
}

void _bugsee_end_blackout(void)
{
    [Bugsee endBlackout];
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
    [Bugsee trace:BugseeNSString(name) value:value];
}

void _bugsee_event(const char *name, const char *paramsJson)
{
    if (paramsJson) {
        id params = BugseeDeserializeJson(paramsJson);
        if ([params isKindOfClass:[NSDictionary class]]) {
            [Bugsee event:BugseeNSString(name) params:params];
            return;
        }
    }
    [Bugsee event:BugseeNSString(name)];
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
    [Bugsee testCrash];
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
    NSString *summaryStr = summary ? [NSString stringWithUTF8String:summary] : nil;
    NSString *descriptionStr = description ? [NSString stringWithUTF8String:description] : nil;
    NSString *labelsStr = labelsJson ? [NSString stringWithUTF8String:labelsJson] : nil;
    BugseeRunOnMain(^{
        NSArray<NSString *> *labels = BugseeLabelsFromJson(labelsStr.UTF8String);
        if (summaryStr) {
            [Bugsee showReportDialogWithSummary:summaryStr
                                    description:descriptionStr ?: @""
                                       severity:(BugseeSeverityLevel)severity
                                         labels:labels];
        } else {
            [Bugsee showReportDialog];
        }
    });
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
    [Bugsee setUserIdentifier:BugseeNSString(email) ?: @""];
}

char *_bugsee_get_email(void)
{
    return BugseeCopyUTF8([Bugsee getUserIdentifier]);
}

void _bugsee_clear_email(void)
{
    [Bugsee clearUserIdentifier];
}

char *_bugsee_get_device_id(void)
{
    // 7.x dropped getDeviceId from the public facade.
    return NULL;
}

void _bugsee_add_secure_rect(float x, float y, float w, float h)
{
    CGFloat scale = [UIScreen mainScreen].scale;
    CGRect rect = CGRectMake(x / scale, y / scale, w / scale, h / scale);
    [Bugsee addSecureRectangle:rect];
}

void _bugsee_remove_secure_rect(float x, float y, float w, float h)
{
    CGFloat scale = [UIScreen mainScreen].scale;
    CGRect rect = CGRectMake(x / scale, y / scale, w / scale, h / scale);
    [Bugsee removeSecureRectangle:rect];
}

void _bugsee_remove_all_secure_rects(void)
{
    [Bugsee removeAllSecureRectangles];
}

void _bugsee_capture_view_hierarchy(void)
{
    [Bugsee captureViewHierarchy];
}

void _bugsee_feedback_show(void)
{
    NSLog(@"[Bugsee] Feedback UI is not in the core 7.x SPM package; install the Feedback module to enable it.");
}

void _bugsee_feedback_set_greeting(const char *message)
{
    (void)message;
    NSLog(@"[Bugsee] Feedback greeting is not in the core 7.x SPM package; install the Feedback module to enable it.");
}

static id<BGSAppearance> BugseeAppearanceOrNil(void)
{
    id appearance = [Bugsee getAppearance];
    if ([appearance conformsToProtocol:@protocol(BGSAppearance)]) {
        return (id<BGSAppearance>)appearance;
    }
    static BOOL logged;
    if (!logged) {
        NSLog(@"[Bugsee] getAppearance does not implement BGSAppearance; color/string property APIs are skipped.");
        logged = YES;
    }
    return nil;
}

void _bugsee_appearance_set_color(const char *propertyName, int r, int g, int b, int a)
{
    NSString *name = BugseeNSString(propertyName);
    id<BGSAppearance> appearance = BugseeAppearanceOrNil();
    if (!name || !appearance) {
        return;
    }
    UIColor *color = [UIColor colorWithRed:r / 255.0 green:g / 255.0 blue:b / 255.0 alpha:a / 255.0];
    [appearance setColor:color forProperty:name];
}

char *_bugsee_appearance_get_color(const char *propertyName)
{
    NSString *name = BugseeNSString(propertyName);
    id<BGSAppearance> appearance = BugseeAppearanceOrNil();
    if (!name || !appearance) {
        return NULL;
    }
    UIColor *value = [appearance colorForProperty:name];
    if (!value) {
        return NULL;
    }
    CGFloat r = 0, g = 0, b = 0, a = 0;
    if (![value getRed:&r green:&g blue:&b alpha:&a]) {
        return NULL;
    }
    NSString *hex = [NSString stringWithFormat:@"#%02lX%02lX%02lX%02lX",
                     lroundf(a * 255), lroundf(r * 255), lroundf(g * 255), lroundf(b * 255)];
    return BugseeCopyUTF8(hex);
}

void _bugsee_appearance_set_string(const char *propertyName, const char *propertyValue)
{
    NSString *name = BugseeNSString(propertyName);
    id<BGSAppearance> appearance = BugseeAppearanceOrNil();
    if (!name || !appearance) {
        return;
    }
    [appearance setString:BugseeNSString(propertyValue) ?: @"" forProperty:name];
}

char *_bugsee_appearance_get_string(const char *propertyName)
{
    NSString *name = BugseeNSString(propertyName);
    id<BGSAppearance> appearance = BugseeAppearanceOrNil();
    if (!name || !appearance) {
        return NULL;
    }
    return BugseeCopyUTF8([appearance stringForProperty:name]);
}

void _bugsee_free(char *ptr)
{
    if (ptr) {
        free(ptr);
    }
}

typedef int (*BugseeDeleteCollectedDataShouldRunFn)(int capturedGeneration);

void _bugsee_delete_collected_data(int capturedGeneration, BugseeDeleteCollectedDataShouldRunFn shouldRun)
{
    BugseeRunOnMain(^{
        void (^runDeleteIfAllowed)(void) = ^{
            if (shouldRun && !shouldRun(capturedGeneration)) {
                return;
            }
            [Bugsee deleteCollectedDataOnDevice:YES completion:nil];
        };
        if ([Bugsee sharedInstance] != nil) {
            [Bugsee stop:^{
                runDeleteIfAllowed();
            }];
        } else {
            runDeleteIfAllowed();
        }
    });
}

void _bugsee_cancel_managed_report_upload(uint64_t uploadId)
{
    if (!gCancelledManagedReportUploadIds) {
        gCancelledManagedReportUploadIds = [NSMutableSet set];
    }
    [gCancelledManagedReportUploadIds addObject:@(uploadId)];
}

void _bugsee_invalidate_managed_report_uploads(void)
{
    gManagedReportUploadFence++;
    [gCancelledManagedReportUploadIds removeAllObjects];
}

void _bugsee_upload_managed_report(const char *reportJson,
                                   uint64_t uploadId,
                                   uint64_t uploadFence,
                                   BugseeManagedReportCreateCallback callback)
{
    if (!reportJson) {
        if (callback) {
            callback(0, uploadId);
        }
        return;
    }
    NSString *jsonCopy = [NSString stringWithUTF8String:reportJson];
    [Bugsee createReportWithCompletion:^(BugseeExtendedReport *_Nullable report) {
        if (!BugseeManagedReportUploadStillActive(uploadId, uploadFence)) {
            if (callback) {
                callback(0, uploadId);
            }
            return;
        }
        if (!report) {
            if (callback) {
                callback(0, uploadId);
            }
            return;
        }
        NSDictionary *dict = BugseeDeserializeJson(jsonCopy.UTF8String);
        if (![dict isKindOfClass:[NSDictionary class]]) {
            if (callback) {
                callback(0, uploadId);
            }
            return;
        }
        BugseeBridgeApplyExtendedReportDict(report, dict);
        if (!BugseeManagedReportUploadStillActive(uploadId, uploadFence)) {
            if (callback) {
                callback(0, uploadId);
            }
            return;
        }
        if (callback) {
            callback(1, uploadId);
        }
        if (BugseeManagedReportUploadStillActive(uploadId, uploadFence)) {
            [Bugsee uploadReport:report completion:nil];
        }
    }];
}

} // extern "C"

#else // !BUGSEE_IOS_SDK

typedef void (*BugseeManagedReportCreateCallback)(int succeeded);
typedef int (*BugseeDeleteCollectedDataShouldRunFn)(int capturedGeneration);

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
void _bugsee_delete_collected_data(int capturedGeneration, BugseeDeleteCollectedDataShouldRunFn shouldRun)
{
    (void)capturedGeneration;
    (void)shouldRun;
}
void _bugsee_cancel_managed_report_upload(uint64_t uploadId) { (void)uploadId; }
void _bugsee_invalidate_managed_report_uploads(void) {}
void _bugsee_upload_managed_report(const char *reportJson,
                                   uint64_t uploadId,
                                   uint64_t uploadFence,
                                   BugseeManagedReportCreateCallback callback)
{
    (void)reportJson;
    (void)uploadId;
    (void)uploadFence;
    if (callback) {
        callback(0, uploadId);
    }
}

}

#endif
