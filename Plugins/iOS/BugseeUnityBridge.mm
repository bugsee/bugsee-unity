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

// bugsee-cocoa up to 7.0.0-beta6 has BugseeExceptionLoggingOptions and +logException:reason:options:completion:.
// 7.0.0-beta7 removed both: domain and skipFrames are arguments, the labels go on the report (see
// BugseeUnityQueueExceptionLabels), and the video, merging rules and the flag form of the delete changed with them.
// Remove this switch and its #if branches when the pin moves past beta6.
#if __has_include(<Bugsee/BugseeExceptionLoggingOptions.h>) || __has_include("Bugsee/BugseeExceptionLoggingOptions.h")
#define BUGSEE_IOS_LEGACY_EXCEPTION_API 1
#else
#define BUGSEE_IOS_LEGACY_EXCEPTION_API 0
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

// Written on the Unity thread, read on the SDK's completion queue: guard with one lock.
static NSObject *gManagedReportUploadLock = [NSObject new];
static uint64_t gManagedReportUploadFence;
static NSMutableSet<NSNumber *> *gCancelledManagedReportUploadIds;

static BOOL BugseeManagedReportUploadStillActive(uint64_t uploadId, uint64_t uploadFence)
{
    @synchronized (gManagedReportUploadLock) {
        if (uploadId == 0 || uploadFence != gManagedReportUploadFence) {
            return NO;
        }
        if (!gCancelledManagedReportUploadIds) {
            return YES;
        }
        return ![gCancelledManagedReportUploadIds containsObject:@(uploadId)];
    }
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

typedef void (*BugseeLaunchStartedFn)(int token, int success);

static BugseeStartedBlock BugseeStartedBlockFor(int token, BugseeLaunchStartedFn started)
{
    return ^(BOOL success) {
        started(token, success ? 1 : 0);
    };
}

// With a started callback (the Task-returning launch), the SDK's answer goes back with the caller's token; the
// dictionary goes through +[BugseeOptions optionsFrom:] because only the BugseeOptions variant takes a block.
void _bugsee_launch(const char *appToken, const char *optionsJson, int startedToken, BugseeLaunchStartedFn started)
{
    NSDictionary *dict = nil;
    if (optionsJson) {
        id parsed = BugseeDeserializeJson(optionsJson);
        if ([parsed isKindOfClass:[NSDictionary class]]) {
            dict = parsed;
        }
    }
    if (!started) {
        [Bugsee launchWithToken:BugseeNSString(appToken) andOptions:dict];
        return;
    }
    [Bugsee launchWithToken:BugseeNSString(appToken)
                    options:[BugseeOptions optionsFrom:dict]
                    started:BugseeStartedBlockFor(startedToken, started)];
}

void _bugsee_relaunch(const char *optionsJson, int startedToken, BugseeLaunchStartedFn started)
{
    NSDictionary *dict = nil;
    if (optionsJson) {
        id parsed = BugseeDeserializeJson(optionsJson);
        if ([parsed isKindOfClass:[NSDictionary class]]) {
            dict = parsed;
        }
    }
    if (!started) {
        [Bugsee relaunchWithDictionaryOptions:dict];
        return;
    }
    [Bugsee relaunchWithOptions:[BugseeOptions optionsFrom:dict] started:BugseeStartedBlockFor(startedToken, started)];
}

typedef void (*BugseeStopCompletionFn)(int token);

// Stop is asynchronous; the completion runs once teardown has finished, with the caller's token so the
// managed side can match it to the right Stop call.
void _bugsee_stop(int token, BugseeStopCompletionFn completion)
{
    if (!completion) {
        [Bugsee stop:nil];
        return;
    }
    [Bugsee stop:^{
        completion(token);
    }];
}

// BugseeStatus raw values: Stopped 0, Launching 1, Launched 2, Stopping 3.
int _bugsee_get_status(void)
{
    return (int)[Bugsee sharedInstance].status;
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

static NSArray<NSString *> *BugseeStringsIn(id value)
{
    if (![value isKindOfClass:[NSArray class]]) {
        return nil;
    }
    NSMutableArray<NSString *> *clean = [NSMutableArray array];
    for (id item in (NSArray *)value) {
        if ([item isKindOfClass:[NSString class]]) {
            [clean addObject:item];
        }
    }
    return clean;
}

#if BUGSEE_IOS_LEGACY_EXCEPTION_API

// Honoured keys: labels (strings), includeVideo (bool, default true), mergingRules (map, the SDK's own keys) and
// skipFrames (number, as on Android). The domain is owned by the wrapper (the exception name), so a caller-supplied one is ignored, as on Android.
static BugseeExceptionLoggingOptions *BugseeExceptionOptionsFrom(const char *optionsJson)
{
    id parsed = BugseeDeserializeJson(optionsJson);
    if (![parsed isKindOfClass:[NSDictionary class]]) {
        return nil;
    }
    NSDictionary *d = parsed;
    BugseeExceptionLoggingOptions *options = [BugseeExceptionLoggingOptions new];
    // A fresh options object has includeVideo off; nil options keep the video, so only an explicit false drops it.
    options.includeVideo = YES;
    NSArray<NSString *> *labels = BugseeStringsIn(d[@"labels"]);
    if (labels) {
        options.labels = labels;
    }
    id includeVideo = d[@"includeVideo"];
    if ([includeVideo isKindOfClass:[NSNumber class]]) {
        options.includeVideo = [includeVideo boolValue];
    }
    id rules = d[@"mergingRules"];
    NSMutableDictionary *mergingRules = [rules isKindOfClass:[NSDictionary class]]
        ? [rules mutableCopy] : [NSMutableDictionary dictionary];
    // Android's top-level skipFrames is the SDK's skip-frames merging rule here.
    id skipFrames = d[@"skipFrames"];
    if ([skipFrames isKindOfClass:[NSNumber class]]) {
        mergingRules[BugseeMergingRulesSkipFramesKey] = skipFrames;
    }
    if (mergingRules.count > 0) {
        options.mergingRules = mergingRules;
    }
    return options;
}

#else

// Queues a handled exception's labels for the error report the SDK files for it (BugseeUnityCallbacks.mm).
void BugseeUnityQueueExceptionLabels(NSArray<NSString *> *labels);

// Honoured keys: labels (strings) and skipFrames (number, as on Android). includeVideo and mergingRules have no
// counterpart in the SDK any more: a logged exception always carries the live capture and has no merging rules
// besides skipFrames. The domain is owned by the wrapper (the exception name), so a caller-supplied one is ignored, as on Android.
static NSDictionary *BugseeExceptionOptionsDict(const char *optionsJson)
{
    id parsed = BugseeDeserializeJson(optionsJson);
    return [parsed isKindOfClass:[NSDictionary class]] ? parsed : nil;
}

static NSInteger BugseeExceptionSkipFrames(NSDictionary *options)
{
    id skipFrames = options[@"skipFrames"];
    return [skipFrames isKindOfClass:[NSNumber class]] ? [skipFrames integerValue] : 0;
}

#endif

void _bugsee_logException(const char *name, const char *reason, bool handled, const char *optionsJson)
{
    NSString *finalName = BugseeNSString(name) ?: @"UnityManagedException";
    NSString *finalReason = BugseeNSString(reason) ?: @"";
    if (handled) {
#if BUGSEE_IOS_LEGACY_EXCEPTION_API
        [Bugsee logException:finalName reason:finalReason options:BugseeExceptionOptionsFrom(optionsJson) completion:nil];
#else
        NSDictionary *options = BugseeExceptionOptionsDict(optionsJson);
#if TARGET_OS_SIMULATOR || TARGET_OS_MACCATALYST
        // The SDK files nothing here and only logs it on every call; say it once, where a Unity developer looks.
        static dispatch_once_t simulatorOnce;
        dispatch_once(&simulatorOnce, ^{
            NSLog(@"[Bugsee] Logged managed exceptions are not filed on the iOS simulator or Mac Catalyst; run on a device to see them.");
        });
#else
        // Only a call the SDK accepts files a report; a labels entry queued for a refused one would mislabel a later report.
        BugseeStatus status = [Bugsee sharedInstance].status;
        if (status == BugseeStatusLaunching || status == BugseeStatusLaunched) {
            BugseeUnityQueueExceptionLabels(BugseeStringsIn(options[@"labels"]));
        }
#endif
        [Bugsee logException:finalName
                      reason:finalReason
                      domain:finalName
                  skipFrames:BugseeExceptionSkipFrames(options)
                  completion:nil];
#endif
    } else {
        // The SDK has no options on the unhandled path.
        [Bugsee logUnhandledException:finalName reason:finalReason completion:nil];
    }
}

#if BUGSEE_UNITY_TESTS
const char *_bugsee_test_exception_options(const char *optionsJson)
{
#if BUGSEE_IOS_LEGACY_EXCEPTION_API
    BugseeExceptionLoggingOptions *o = BugseeExceptionOptionsFrom(optionsJson);
    if (!o) return NULL;
    NSString *summary = [NSString stringWithFormat:@"labels=%@;video=%d;rules=%@",
                         [o.labels componentsJoinedByString:@","], o.includeVideo ? 1 : 0, o.mergingRules];
#else
    NSDictionary *d = BugseeExceptionOptionsDict(optionsJson);
    if (!d) return NULL;
    NSString *summary = [NSString stringWithFormat:@"labels=%@;skipFrames=%ld",
                         [BugseeStringsIn(d[@"labels"]) componentsJoinedByString:@","], (long)BugseeExceptionSkipFrames(d)];
#endif
    return strdup(summary.UTF8String);
}
#endif

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

// NSJSONSerialization throws on anything outside the JSON types (NSDate, NSData, blocks, non-string keys,
// NaN), and launch options can hold such values, so a value it cannot take crosses as its description.
static id BugseeJsonSafe(id value, int depth)
{
    if (!value || value == [NSNull null] || depth > 16) {
        return [NSNull null];
    }
    if ([value isKindOfClass:[NSString class]]) {
        return value;
    }
    if ([value isKindOfClass:[NSNumber class]]) {
        double d = [value doubleValue];
        return (isnan(d) || isinf(d)) ? [NSNull null] : value;
    }
    if ([value isKindOfClass:[NSDictionary class]]) {
        NSMutableDictionary *out = [NSMutableDictionary dictionaryWithCapacity:[value count]];
        [value enumerateKeysAndObjectsUsingBlock:^(id key, id obj, BOOL *stop) {
            NSString *name = [key isKindOfClass:[NSString class]] ? key : [key description];
            out[name] = BugseeJsonSafe(obj, depth + 1);
        }];
        return out;
    }
    if ([value isKindOfClass:[NSArray class]] || [value isKindOfClass:[NSSet class]]) {
        NSMutableArray *out = [NSMutableArray arrayWithCapacity:[value count]];
        for (id item in value) {
            [out addObject:BugseeJsonSafe(item, depth + 1)];
        }
        return out;
    }
    return [value description];
}

char *_bugsee_get_launch_options(void)
{
    NSDictionary *options = [Bugsee getLaunchOptions];
    id safe = BugseeJsonSafe(options, 0);
    if (![safe isKindOfClass:[NSDictionary class]] || ![NSJSONSerialization isValidJSONObject:safe]) {
        return NULL;
    }
    NSData *data = [NSJSONSerialization dataWithJSONObject:safe options:0 error:nil];
    NSString *json = data ? [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] : nil;
    return BugseeCopyUTF8(json);
}

void _bugsee_notify(const char *title, const char *body, int severity, const char *fieldsJson, int urgent)
{
    NSString *titleStr = BugseeNSString(title);
    if (titleStr.length == 0) {
        return;
    }
    NSMutableDictionary<NSString *, NSString *> *fields = nil;
    id parsed = BugseeDeserializeJson(fieldsJson);
    if ([parsed isKindOfClass:[NSDictionary class]]) {
        fields = [NSMutableDictionary dictionary];
        [parsed enumerateKeysAndObjectsUsingBlock:^(id key, id obj, BOOL *stop) {
            if ([key isKindOfClass:[NSString class]] && [obj isKindOfClass:[NSString class]]) {
                fields[key] = obj;
            }
        }];
    }
    [Bugsee notifyWithTitle:titleStr
                       body:BugseeNSString(body)
                   severity:(BugseeSeverityLevel)severity
                     fields:fields
                     urgent:urgent != 0];
}

// APM spans are live SDK objects, so managed code holds them by id. An entry lives from the span's creation until
// _bugsee_span_release, which the managed wrapper calls exactly once after finishing the span (and its finalizer
// calls for a span that was dropped unfinished). Ids start at 1 and are never reused; 0 means "no span".
static NSMutableDictionary<NSNumber *, id<BGSSpan>> *BugseeSpanRegistry(void)
{
    static NSMutableDictionary<NSNumber *, id<BGSSpan>> *registry;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        registry = [NSMutableDictionary dictionary];
    });
    return registry;
}

static int64_t BugseeRegisterSpan(id<BGSSpan> span)
{
    if (!span) {
        return 0;
    }
    static int64_t nextId = 0;
    NSMutableDictionary<NSNumber *, id<BGSSpan>> *registry = BugseeSpanRegistry();
    @synchronized (registry) {
        int64_t spanId = ++nextId;
        registry[@(spanId)] = span;
        return spanId;
    }
}

static id<BGSSpan> BugseeLookupSpan(int64_t spanId)
{
    NSMutableDictionary<NSNumber *, id<BGSSpan>> *registry = BugseeSpanRegistry();
    @synchronized (registry) {
        return registry[@(spanId)];
    }
}

int64_t _bugsee_start_transaction(const char *name, const char *operation, const char *attributesJson)
{
    NSString *nameStr = BugseeNSString(name);
    NSString *operationStr = BugseeNSString(operation);
    if (nameStr.length == 0 || operationStr.length == 0) {
        return 0;
    }
    id parsed = BugseeDeserializeJson(attributesJson);
    NSDictionary *attributes = [parsed isKindOfClass:[NSDictionary class]] ? parsed : nil;
    return BugseeRegisterSpan([Bugsee startTransactionWithName:nameStr operation:operationStr attributes:attributes]);
}

int64_t _bugsee_span_start_child(int64_t spanId, const char *operation, const char *description)
{
    id<BGSSpan> span = BugseeLookupSpan(spanId);
    NSString *operationStr = BugseeNSString(operation);
    if (!span || operationStr.length == 0) {
        return 0;
    }
    return BugseeRegisterSpan([span startChildSpanWithOperation:operationStr description:BugseeNSString(description)]);
}

char *_bugsee_span_get_id(int64_t spanId)
{
    return BugseeCopyUTF8([BugseeLookupSpan(spanId) spanId]);
}

char *_bugsee_span_get_trace_id(int64_t spanId)
{
    return BugseeCopyUTF8([BugseeLookupSpan(spanId) traceId]);
}

void _bugsee_span_set_name(int64_t spanId, const char *name)
{
    NSString *nameStr = BugseeNSString(name);
    if (nameStr.length > 0) {
        [BugseeLookupSpan(spanId) setName:nameStr];
    }
}

void _bugsee_span_set_description(int64_t spanId, const char *description)
{
    [BugseeLookupSpan(spanId) setSpanDescription:BugseeNSString(description)];
}

void _bugsee_span_set_attribute(int64_t spanId, const char *key, const char *valueJson)
{
    NSString *keyStr = BugseeNSString(key);
    id value = valueJson ? BugseeDeserializeJson(valueJson) : nil;
    if (keyStr.length > 0 && value && value != [NSNull null]) {
        [BugseeLookupSpan(spanId) setAttribute:keyStr value:value];
    }
}

void _bugsee_span_set_status(int64_t spanId, int status)
{
    [BugseeLookupSpan(spanId) setStatus:(BGSSpanStatus)status];
}

void _bugsee_span_finish(int64_t spanId, int status)
{
    [BugseeLookupSpan(spanId) finishWithStatus:(BGSSpanStatus)status];
}

void _bugsee_span_release(int64_t spanId)
{
    NSMutableDictionary<NSNumber *, id<BGSSpan>> *registry = BugseeSpanRegistry();
    @synchronized (registry) {
        [registry removeObjectForKey:@(spanId)];
    }
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
    // The SDK keeps the value's kind. Tag it for C# ("s:" string, "b:" bool 1/0, "i:" integer, "d:" floating
    // point, "j:" JSON container) so a string "1", the number 1 and true stay distinguishable.
    // locale:nil keeps the digits POSIX; C# parses them with the invariant culture.
    NSString *wire = nil;
    if ([attr isKindOfClass:[NSString class]]) {
        wire = [@"s:" stringByAppendingString:attr];
    } else if ([attr isKindOfClass:[NSNumber class]]) {
        NSNumber *number = attr;
        char type = [number objCType][0];
        if (CFGetTypeID((__bridge CFTypeRef)number) == CFBooleanGetTypeID()) {
            wire = [[NSString alloc] initWithFormat:@"b:%d" locale:nil, [number boolValue] ? 1 : 0];
        } else if (type == 'f' || type == 'd') {
            wire = [[NSString alloc] initWithFormat:@"d:%.17g" locale:nil, [number doubleValue]];
        } else if (type == 'Q' || type == 'L') {
            wire = [[NSString alloc] initWithFormat:@"i:%llu" locale:nil, [number unsignedLongLongValue]];
        } else {
            wire = [[NSString alloc] initWithFormat:@"i:%lld" locale:nil, [number longLongValue]];
        }
    } else if (([attr isKindOfClass:[NSDictionary class]] || [attr isKindOfClass:[NSArray class]]) &&
               [NSJSONSerialization isValidJSONObject:attr]) {
        NSData *data = [NSJSONSerialization dataWithJSONObject:attr options:0 error:nil];
        NSString *json = data ? [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] : nil;
        if (json) {
            wire = [@"j:" stringByAppendingString:json];
        }
    }
    return BugseeCopyUTF8(wire ?: [@"s:" stringByAppendingString:[attr description]]);
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
    (void)x;
    (void)y;
    (void)w;
    (void)h;
}

void _bugsee_remove_secure_rect(float x, float y, float w, float h)
{
    (void)x;
    (void)y;
    (void)w;
    (void)h;
}

void _bugsee_remove_all_secure_rects(void) {}

float _bugsee_screen_scale(void)
{
    return (float)[UIScreen mainScreen].scale;
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

// Unity property name -> BugseeTheme property (the 7.x iOS theme API; there is no BGSAppearance object).
// Names with no row have no iOS equivalent and are skipped.
typedef struct {
    const char *name;
    const char *themeKey;
} BugseeThemeProperty;

static const BugseeThemeProperty kBugseeThemeProperties[] = {
    { "Report::ActionBarColor", "reportNavigationBarColor" },
    { "Report::EditTextBackgroundColor", "reportCellBackgroundColor" },
    { "Report::VersionColor", "reportVersionColor" },
    { "Report::TextColor", "reportTextColor" },
    { "Report::HintColor", "reportPlaceholderColor" },
    { "Report::BackgroundColor", "reportBackgroundColor" },
    { "Report::SummaryPlaceholder", "reportSummaryPlaceholder" },
    { "Report::DescriptionPlaceholder", "reportDescriptionPlaceholder" },
    { "Report::LabelsPlaceholder", "reportLabelsPlaceholder" },
    { "Report::EmailPlaceholder", "reportEmailPlaceholder" },
    { "Feedback::ActionBarColor", "feedbackBarsColor" },
    { "Feedback::BackgroundColor", "feedbackBackgroundColor" },
    { "Feedback::IncomingBubbleColor", "feedbackIncomingBubbleColor" },
    { "Feedback::OutgoingBubbleColor", "feedbackOutgoingBubbleColor" },
    { "Feedback::IncomingTextColor", "feedbackIncomingTextColor" },
    { "Feedback::OutgoingTextColor", "feedbackOutgoingTextColor" },
    { "Feedback::TitleTextColor", "feedbackTitleTextColor" },
    { "Feedback::EmailSkipTextColor", "feedbackEmailSkipColor" },
    { "Feedback::EmailBackgroundColor", "feedbackEmailBackgroundColor" },
    { "Feedback::EmailContinueNotActiveColor", "feedbackEmailContinueNotActiveColor" },
    { "Feedback::EmailContinueActiveColor", "feedbackEmailContinueActiveColor" },
    { "Feedback::InputTextColor", "feedbackInputTextColor" },
};

static NSString *BugseeThemeKey(NSString *name)
{
    for (size_t i = 0; i < sizeof(kBugseeThemeProperties) / sizeof(kBugseeThemeProperties[0]); i++) {
        if ([name isEqualToString:@(kBugseeThemeProperties[i].name)]) {
            return @(kBugseeThemeProperties[i].themeKey);
        }
    }
    static NSMutableSet<NSString *> *logged;
    @synchronized([Bugsee class]) {
        if (!logged) {
            logged = [NSMutableSet set];
        }
        if (name && ![logged containsObject:name]) {
            [logged addObject:name];
            NSLog(@"[Bugsee] Appearance property %@ has no iOS equivalent; skipped.", name);
        }
    }
    return nil;
}

// Theme properties are nonatomic and read on the main thread: run the access there.
static void BugseeThemeSync(dispatch_block_t block)
{
    if ([NSThread isMainThread]) {
        block();
    } else {
        dispatch_sync(dispatch_get_main_queue(), block);
    }
}

static int BugseeThemeSet(const char *propertyName, NSString *suffix, id value)
{
    NSString *key = BugseeThemeKey(BugseeNSString(propertyName));
    if (!key || ![key hasSuffix:suffix]) {
        return 0;
    }
    BugseeThemeSync(^{
        [[Bugsee getAppearance] setValue:value forKey:key];
    });
    return 1;
}

static id BugseeThemeGet(const char *propertyName, NSString *suffix)
{
    NSString *key = BugseeThemeKey(BugseeNSString(propertyName));
    if (!key || ![key hasSuffix:suffix]) {
        return nil;
    }
    __block id value = nil;
    BugseeThemeSync(^{
        value = [[Bugsee getAppearance] valueForKey:key];
    });
    return value;
}

// Return 1 when the property exists on iOS and the theme was updated.
int _bugsee_appearance_set_color(const char *propertyName, int r, int g, int b, int a)
{
    return BugseeThemeSet(propertyName, @"Color",
                          [UIColor colorWithRed:r / 255.0 green:g / 255.0 blue:b / 255.0 alpha:a / 255.0]);
}

char *_bugsee_appearance_get_color(const char *propertyName)
{
    UIColor *value = BugseeThemeGet(propertyName, @"Color");
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

// NULL restores the SDK's default text.
int _bugsee_appearance_set_string(const char *propertyName, const char *propertyValue)
{
    return BugseeThemeSet(propertyName, @"Placeholder", BugseeNSString(propertyValue));
}

char *_bugsee_appearance_get_string(const char *propertyName)
{
    return BugseeCopyUTF8(BugseeThemeGet(propertyName, @"Placeholder"));
}

void _bugsee_free(char *ptr)
{
    if (ptr) {
        free(ptr);
    }
}

// Deletes after the SDK is stopped, and reports the token back once the SDK says the wipe finished (its files
// are cleared on a background thread), so the managed side can hold a following Launch until then. wiped is 1
// when the SDK reported the wipe done, 0 when it failed or never ran (the SDK did not finish stopping).
typedef void (*BugseeDeleteCompletionFn)(int token, int wiped);

void _bugsee_delete_collected_data(int token, BugseeDeleteCompletionFn completion)
{
    BugseeRunOnMain(^{
        void (^runDelete)(void) = ^{
            BugseeDeleteCollectedDataCompletionBlock done = ^(BOOL success) {
                if (completion) {
                    completion(token, success ? 1 : 0);
                }
            };
#if BUGSEE_IOS_LEGACY_EXCEPTION_API
            [Bugsee deleteCollectedDataOnDevice:YES completion:done];
#else
            // The flag form keeps the report bundles waiting for upload; this one deletes everything Bugsee collected.
            [Bugsee deleteCollectedDataOnDevice:done];
#endif
        };
        // Only a running SDK is stopped: on a stopped one the stop's block may land after a launch that follows
        // this call on the main thread, and the wipe would then be skipped.
        Bugsee *instance = [Bugsee sharedInstance];
        BugseeStatus status = instance != nil ? instance.status : BugseeStatusStopped;
        if (status == BugseeStatusStopping) {
            // A stop is already running (the managed side gave up waiting for it): a second [Bugsee stop:] could
            // land on a launch made after that, so wait for the stop that is running instead.
            __block void (^waitForStopped)(int) = nil;
            waitForStopped = ^(int remainingTries) {
                if (instance.status == BugseeStatusStopping && remainingTries > 0) {
                    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(100 * NSEC_PER_MSEC)), dispatch_get_main_queue(), ^{
                        waitForStopped(remainingTries - 1);
                    });
                    return;
                }
                if (instance.status == BugseeStatusStopped) {
                    runDelete();
                } else if (completion) {
                    completion(token, 0);
                }
                waitForStopped = nil;
            };
            waitForStopped(100);
        } else if (status != BugseeStatusStopped) {
            [Bugsee stop:^{
                runDelete();
            }];
        } else {
            runDelete();
        }
    });
}

void _bugsee_cancel_managed_report_upload(uint64_t uploadId)
{
    @synchronized (gManagedReportUploadLock) {
        if (!gCancelledManagedReportUploadIds) {
            gCancelledManagedReportUploadIds = [NSMutableSet new];
        }
        [gCancelledManagedReportUploadIds addObject:@(uploadId)];
    }
}

void _bugsee_invalidate_managed_report_uploads(void)
{
    @synchronized (gManagedReportUploadLock) {
        gManagedReportUploadFence++;
        [gCancelledManagedReportUploadIds removeAllObjects];
    }
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

typedef void (*BugseeManagedReportCreateCallback)(int succeeded, uint64_t uploadToken);

extern "C" {

typedef void (*BugseeLaunchStartedFn)(int token, int success);
void _bugsee_launch(const char *appToken, const char *optionsJson, int startedToken, BugseeLaunchStartedFn started)
{
    (void)appToken;
    (void)optionsJson;
    if (started) started(startedToken, 0);
}
void _bugsee_relaunch(const char *optionsJson, int startedToken, BugseeLaunchStartedFn started)
{
    (void)optionsJson;
    if (started) started(startedToken, 0);
}
typedef void (*BugseeStopCompletionFn)(int token);
void _bugsee_stop(int token, BugseeStopCompletionFn completion) { if (completion) completion(token); }
int _bugsee_get_status(void) { return 0; }
void _bugsee_start_blackout(void) {}
void _bugsee_end_blackout(void) {}
void _bugsee_log(const char *message, int level) { (void)message; (void)level; }
void _bugsee_trace(const char *name, const char *valueJson) { (void)name; (void)valueJson; }
void _bugsee_event(const char *name, const char *paramsJson) { (void)name; (void)paramsJson; }
void _bugsee_logException(const char *name, const char *reason, bool handled, const char *optionsJson) { (void)name; (void)reason; (void)handled; (void)optionsJson; }
void _bugsee_test_crash(void) {}
void _bugsee_show_report(const char *summary, const char *description, int severity, const char *labelsJson) { (void)summary; (void)description; (void)severity; (void)labelsJson; }
void _bugsee_upload(const char *summary, const char *description, int severity, const char *labelsJson) { (void)summary; (void)description; (void)severity; (void)labelsJson; }
char *_bugsee_get_launch_options(void) { return NULL; }
void _bugsee_notify(const char *title, const char *body, int severity, const char *fieldsJson, int urgent) { (void)title; (void)body; (void)severity; (void)fieldsJson; (void)urgent; }
int64_t _bugsee_start_transaction(const char *name, const char *operation, const char *attributesJson) { (void)name; (void)operation; (void)attributesJson; return 0; }
int64_t _bugsee_span_start_child(int64_t spanId, const char *operation, const char *description) { (void)spanId; (void)operation; (void)description; return 0; }
char *_bugsee_span_get_id(int64_t spanId) { (void)spanId; return NULL; }
char *_bugsee_span_get_trace_id(int64_t spanId) { (void)spanId; return NULL; }
void _bugsee_span_set_name(int64_t spanId, const char *name) { (void)spanId; (void)name; }
void _bugsee_span_set_description(int64_t spanId, const char *description) { (void)spanId; (void)description; }
void _bugsee_span_set_attribute(int64_t spanId, const char *key, const char *valueJson) { (void)spanId; (void)key; (void)valueJson; }
void _bugsee_span_set_status(int64_t spanId, int status) { (void)spanId; (void)status; }
void _bugsee_span_finish(int64_t spanId, int status) { (void)spanId; (void)status; }
void _bugsee_span_release(int64_t spanId) { (void)spanId; }
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
float _bugsee_screen_scale(void) { return 1.f; }
void _bugsee_set_secure_buffer(int display, int *packed, int packedLength)
{
    (void)display;
    (void)packed;
    (void)packedLength;
}
void _bugsee_capture_view_hierarchy(void) {}
void _bugsee_feedback_show(void) {}
void _bugsee_feedback_set_greeting(const char *message) { (void)message; }
int _bugsee_appearance_set_color(const char *propertyName, int r, int g, int b, int a) { (void)propertyName; (void)r; (void)g; (void)b; (void)a; return 0; }
char *_bugsee_appearance_get_color(const char *propertyName) { (void)propertyName; return NULL; }
int _bugsee_appearance_set_string(const char *propertyName, const char *propertyValue) { (void)propertyName; (void)propertyValue; return 0; }
char *_bugsee_appearance_get_string(const char *propertyName) { (void)propertyName; return NULL; }
void _bugsee_free(char *ptr) { (void)ptr; }
typedef void (*BugseeDeleteCompletionFn)(int token, int wiped);
void _bugsee_delete_collected_data(int token, BugseeDeleteCompletionFn completion) { if (completion) completion(token, 0); }
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
