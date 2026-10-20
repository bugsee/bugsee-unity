#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#include <stdlib.h>
#include <string.h>

#if __has_include(<Bugsee/Bugsee.h>)
#import <Bugsee/Bugsee.h>
#import <Bugsee/BugseeConstants.h>
#import <Bugsee/BugseeNetworkEvent.h>
#import <Bugsee/BugseeLogEvent.h>
#import <Bugsee/BGSContracts.h>
#define BUGSEE_IOS_CALLBACKS 1
#elif __has_include("Bugsee/Bugsee.h")
#import "Bugsee/Bugsee.h"
#import "Bugsee/BugseeConstants.h"
#import "Bugsee/BugseeNetworkEvent.h"
#import "Bugsee/BugseeLogEvent.h"
#import "Bugsee/BGSContracts.h"
#define BUGSEE_IOS_CALLBACKS 1
#else
#define BUGSEE_IOS_CALLBACKS 0
#endif

#if BUGSEE_IOS_CALLBACKS

// kind: 1=network, 2=log, 3=breadcrumb
typedef void (*BugseeUnityFilterCb)(int64_t requestId, int kind, const char *json);
// phase: 0=before, 1=after
typedef void (*BugseeUnityReportCb)(int64_t requestId, int phase, int isTerminating, const char *json);
typedef void (*BugseeUnityLifecycleCb)(const char *eventType, const char *dataJson);
// The SDK's requestData channel; the answer comes back through _bugsee_complete_data_request with the same id.
typedef void (*BugseeUnityDataRequestCb)(int64_t requestId, const char *dataType);

static BugseeUnityFilterCb gFilterCb = NULL;
static BugseeUnityReportCb gReportCb = NULL;
static BugseeUnityLifecycleCb gLifecycleCb = NULL;
static BugseeUnityDataRequestCb gDataRequestCb = NULL;
static BOOL gNetworkFilterCallbackInstalled = NO;
static BOOL gLogFilterCallbackInstalled = NO;
static BOOL gBreadcrumbFilterCallbackInstalled = NO;

static NSMutableDictionary<NSNumber *, NSMutableDictionary *> *gPending;
static int64_t gNextRequestId = 1;
static NSObject *gLock;
static NSString *gWrapperVersion = @"0.1.0";
static NSString *gWrapperBuild = nil;
static NSDictionary<NSString *, NSString *> *gWrapperContext = nil;

@class BugseeUnityWrapper;
static BugseeUnityWrapper *gWrapper;
static id<BGSWrapperChannel> gChannel;
static NSMutableDictionary<NSNumber *, NSData *> *gSecureRectBuffers;
static NSData *gDefaultSecureRectBuffer;

static void BugseeUnityEnsureSecureRectBuffers(void)
{
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        gSecureRectBuffers = [NSMutableDictionary dictionary];
        int32_t baseline[] = { 1, 0 };
        gDefaultSecureRectBuffer = [NSData dataWithBytes:baseline length:sizeof(baseline)];
    });
}

static void BugseeUnityEnsureState(void)
{
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        gPending = [NSMutableDictionary dictionary];
        gLock = [NSObject new];
    });
}

static NSData *BugseeUnitySecureBufferForDisplay(int display)
{
    BugseeUnityEnsureState();
    BugseeUnityEnsureSecureRectBuffers();
    @synchronized (gLock) {
        NSData *data = gSecureRectBuffers[@(display)] ?: gDefaultSecureRectBuffer;
        if (!data || data.length == 0) {
            return gDefaultSecureRectBuffer;
        }
        return [NSData dataWithBytes:data.bytes length:data.length];
    }
}

static char *BugseeUnityCopyUTF8(NSString *string)
{
    if (!string) return NULL;
    const char *utf8 = string.UTF8String;
    return utf8 ? strdup(utf8) : NULL;
}

static NSString *BugseeUnityJsonString(id obj)
{
    if (!obj || ![NSJSONSerialization isValidJSONObject:obj]) {
        return @"{}";
    }
    NSData *data = [NSJSONSerialization dataWithJSONObject:obj options:0 error:nil];
    if (!data) return @"{}";
    return [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] ?: @"{}";
}

static NSDictionary *BugseeUnityParseJson(const char *json)
{
    if (!json) return nil;
    NSData *data = [[NSString stringWithUTF8String:json] dataUsingEncoding:NSUTF8StringEncoding];
    if (!data) return nil;
    id parsed = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
    return [parsed isKindOfClass:[NSDictionary class]] ? parsed : nil;
}

static NSDictionary *BugseeUnityNetworkToDict(BugseeNetworkEvent *event)
{
    NSMutableDictionary *d = [NSMutableDictionary dictionary];
    d[@"id"] = event.ID ?: @"";
    d[@"url"] = event.url ?: [NSNull null];
    d[@"method"] = event.method ?: @"";
    d[@"mechanism"] = event.mechanism ?: [NSNull null];
    d[@"responseCode"] = @(event.responseCode);
    d[@"size"] = @(event.dataSize);
    d[@"statusText"] = [NSNull null];
    d[@"stage"] = event.bugseeNetworkEventType ?: @"";
    if (event.headers) d[@"headers"] = event.headers;
    if (event.body) {
        NSString *body = [[NSString alloc] initWithData:event.body encoding:NSUTF8StringEncoding];
        d[@"body"] = body ?: [NSNull null];
    } else {
        d[@"body"] = [NSNull null];
    }
    if ([event.error isKindOfClass:[NSDictionary class]]) {
        d[@"errorShortMessage"] = event.error[@"name"] ?: event.error[@"reason"] ?: [NSNull null];
        d[@"errorDescription"] = event.error[@"description"] ?: [NSNull null];
    } else {
        d[@"errorShortMessage"] = [NSNull null];
        d[@"errorDescription"] = [NSNull null];
    }
    return d;
}

static void BugseeUnityApplyNetworkDict(BugseeNetworkEvent *event, NSDictionary *d)
{
    if (![d isKindOfClass:[NSDictionary class]] || !event) return;
    id url = d[@"url"];
    if ([url isKindOfClass:[NSString class]]) event.url = url;
    else if (url == [NSNull null]) event.url = nil;

    id method = d[@"method"];
    if ([method isKindOfClass:[NSString class]]) event.method = method;

    id mechanism = d[@"mechanism"];
    if ([mechanism isKindOfClass:[NSString class]]) event.mechanism = mechanism;
    else if (mechanism == [NSNull null]) event.mechanism = nil;

    id stage = d[@"stage"];
    if ([stage isKindOfClass:[NSString class]]) event.bugseeNetworkEventType = stage;

    id timestampMs = d[@"timestampMs"];
    if ([timestampMs respondsToSelector:@selector(doubleValue)] && [timestampMs doubleValue] > 0) {
        // Unix milliseconds, the SDK's own unit (BugseeGetCurrentTimeStampMs).
        event.timestamp = [timestampMs doubleValue];
    }

    id body = d[@"body"];
    if ([body isKindOfClass:[NSString class]]) {
        event.body = [(NSString *)body dataUsingEncoding:NSUTF8StringEncoding];
    } else if (body == [NSNull null]) {
        event.body = nil;
    }

    id headers = d[@"headers"];
    if ([headers isKindOfClass:[NSDictionary class]]) event.headers = headers;

    id code = d[@"responseCode"];
    if ([code respondsToSelector:@selector(integerValue)]) event.responseCode = [code integerValue];

    id size = d[@"size"];
    if ([size respondsToSelector:@selector(longLongValue)]) event.dataSize = [size longLongValue];

    NSMutableDictionary *error = event.error ? [event.error mutableCopy] : [NSMutableDictionary dictionary];
    BOOL touchedError = NO;
    id shortMsg = d[@"errorShortMessage"];
    // An empty string removes the key like null: "no error name" must not leave a non-nil error behind.
    if ([shortMsg isKindOfClass:[NSString class]] && [(NSString *)shortMsg length] > 0) {
        error[@"name"] = shortMsg;
        touchedError = YES;
    } else if (shortMsg == [NSNull null] || [shortMsg isKindOfClass:[NSString class]]) {
        [error removeObjectForKey:@"name"];
        [error removeObjectForKey:@"reason"]; // read back as the name when there is no name: a cleared name leaves neither
        touchedError = YES;
    }
    id desc = d[@"errorDescription"];
    if ([desc isKindOfClass:[NSString class]] && [(NSString *)desc length] > 0) {
        error[@"description"] = desc;
        touchedError = YES;
    } else if (desc == [NSNull null] || [desc isKindOfClass:[NSString class]]) {
        [error removeObjectForKey:@"description"];
        touchedError = YES;
    }
    if (touchedError) {
        event.error = error.count > 0 ? error : nil;
    }
}

static NSDictionary *BugseeUnityLogToDict(BugseeLogEvent *event)
{
    return @{
        @"message": event.text ?: @"",
        @"level": @((int)event.level),
    };
}

// Only the keys present are applied; a JSON null applies as an empty string (the SDK treats nil and empty alike:
// an empty log text drops the line), and an absent key leaves the SDK's own value alone.
static void BugseeUnityApplyLogDict(BugseeLogEvent *event, NSDictionary *d)
{
    if (![d isKindOfClass:[NSDictionary class]] || !event) return;
    id msg = d[@"message"];
    if ([msg isKindOfClass:[NSString class]]) event.text = msg;
    else if (msg == [NSNull null]) event.text = @"";
    id level = d[@"level"];
    if ([level respondsToSelector:@selector(intValue)]) event.level = (BugseeLogLevel)[level intValue];
}

static NSDictionary *BugseeUnityBreadcrumbToDict(id<BGSBreadcrumb> crumb)
{
    NSMutableDictionary *d = [NSMutableDictionary dictionary];
    d[@"category"] = crumb.category ?: @"";
    d[@"message"] = crumb.message ?: @"";
    d[@"type"] = crumb.type ?: @"";
    d[@"level"] = @(crumb.level);
    d[@"timestamp"] = @((long long)(crumb.timestamp * 1000.0));
    if (crumb.data) d[@"data"] = crumb.data;
    return d;
}

// Only the keys present are applied; a JSON null applies as an empty string (the SDK's properties are non-null and
// its factory maps nil to empty).
static void BugseeUnityApplyBreadcrumbDict(id<BGSBreadcrumb> crumb, NSDictionary *d)
{
    if (![d isKindOfClass:[NSDictionary class]] || !crumb) return;
    id cat = d[@"category"];
    if ([cat isKindOfClass:[NSString class]]) crumb.category = cat;
    else if (cat == [NSNull null]) crumb.category = @"";
    id msg = d[@"message"];
    if ([msg isKindOfClass:[NSString class]]) crumb.message = msg;
    else if (msg == [NSNull null]) crumb.message = @"";
    id type = d[@"type"];
    if ([type isKindOfClass:[NSString class]]) crumb.type = type;
    else if (type == [NSNull null]) crumb.type = @"";
    id level = d[@"level"]; if ([level respondsToSelector:@selector(integerValue)]) crumb.level = [level integerValue];
    id data = d[@"data"]; if ([data isKindOfClass:[NSDictionary class]]) crumb.data = data;
}

// The attributes keep their kinds (number, bool, string, containers). A value JSON cannot hold (a date, data, NaN)
// is sent as its description: one such attribute must not turn the whole report JSON into "{}".
static NSDictionary *BugseeUnityJsonSafeAttributes(NSDictionary *attributes)
{
    NSMutableDictionary *safe = [NSMutableDictionary dictionaryWithCapacity:attributes.count];
    [attributes enumerateKeysAndObjectsUsingBlock:^(id key, id obj, BOOL *stop) {
        if (![key isKindOfClass:[NSString class]]) return;
        safe[key] = [NSJSONSerialization isValidJSONObject:@[obj]] ? obj : [obj description];
    }];
    return safe;
}

static NSDictionary *BugseeUnityReportToDict(id<BGSReportContract> report)
{
    NSMutableDictionary *d = [NSMutableDictionary dictionary];
    d[@"id"] = report.reportId ?: @"";
    d[@"summary"] = report.summary ?: @"";
    d[@"description"] = report.reportDescription ?: @"";
    d[@"email"] = report.email ?: @"";
    d[@"severity"] = @(report.severity);
    d[@"labels"] = report.labels ? [report.labels copy] : @[];
    d[@"attributes"] = report.attributes ? BugseeUnityJsonSafeAttributes(report.attributes) : @{};
    // Optional on concrete BugseeReport; BGSReportContract does not declare type.
    if ([(id)report respondsToSelector:@selector(type)]) {
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Warc-performSelector-leaks"
        id typeVal = [(id)report performSelector:@selector(type)];
#pragma clang diagnostic pop
        if ([typeVal isKindOfClass:[NSString class]]) {
            d[@"type"] = typeVal;
        }
    }
    return d;
}

static void BugseeUnityApplyReportDict(id<BGSReportContract> report, NSDictionary *d)
{
    if (![d isKindOfClass:[NSDictionary class]] || !report) return;

    id summary = d[@"summary"];
    if ([summary isKindOfClass:[NSString class]]) report.summary = summary;
    id desc = d[@"description"];
    if ([desc isKindOfClass:[NSString class]]) report.reportDescription = desc;
    id email = d[@"email"];
    if ([email isKindOfClass:[NSString class]]) report.email = email;
    id sev = d[@"severity"];
    if ([sev respondsToSelector:@selector(integerValue)]) {
        report.severity = (BugseeSeverityLevel)[sev integerValue];
    }

    id labels = d[@"labels"];
    if ([labels isKindOfClass:[NSArray class]]) {
        [report clearLabels];
        for (id label in (NSArray *)labels) {
            if ([label isKindOfClass:[NSString class]]) [report addLabel:label];
        }
    }

    id removals = d[@"attributeRemovals"];
    if ([removals isKindOfClass:[NSArray class]]) {
        for (id name in (NSArray *)removals) {
            if ([name isKindOfClass:[NSString class]]) {
                [report removeAttributeForName:(NSString *)name];
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
            if (![key isKindOfClass:[NSString class]]) return;
            // A JSON null is a name with no value: removed, as the SDK takes no nil attribute value.
            if (obj == [NSNull null]) {
                [report removeAttributeForName:key];
            } else {
                [report setAttribute:obj forName:key];
            }
        }];
    } else if (shouldReplaceAll) {
        [report clearAllAttributes];
    }

    id replaceAttachments = d[@"attachmentsReplaceAll"];
    BOOL shouldReplaceAttachments = [replaceAttachments respondsToSelector:@selector(boolValue)] && [replaceAttachments boolValue];

    id attachments = d[@"attachments"];
    if ([attachments isKindOfClass:[NSArray class]]) {
        if (shouldReplaceAttachments) {
            [report clearAttachments];
        }
        for (id item in (NSArray *)attachments) {
            if (![item isKindOfClass:[NSDictionary class]]) continue;
            NSDictionary *att = (NSDictionary *)item;
            NSString *name = [att[@"name"] isKindOfClass:[NSString class]] ? att[@"name"] : @"attachment";
            // A missing MIME type stays unset (nil), as the report contract says; none is invented.
            NSString *mime = [att[@"mimeType"] isKindOfClass:[NSString class]] && [(NSString *)att[@"mimeType"] length] > 0
                ? att[@"mimeType"] : nil;
            id pathVal = att[@"path"];
            if ([pathVal isKindOfClass:[NSString class]] && [(NSString *)pathVal length] > 0) {
                id reportObj = (id)report;
                id<BGSAttachmentContract> created = nil;
                if ([reportObj respondsToSelector:@selector(addAttachmentWithFilePath:name:mimeType:move:)]) {
                    created = [reportObj addAttachmentWithFilePath:(NSString *)pathVal name:name mimeType:mime move:YES];
                }
                if (created) {
                    id fileName = att[@"fileName"];
                    if ([fileName isKindOfClass:[NSString class]] && [(NSString *)fileName length] > 0) {
                        created.fileName = fileName;
                    }
                }
                continue;
            }
            id b64 = att[@"dataBase64"];
            // An empty string is valid data (an empty attachment); only a missing or undecodable payload is skipped.
            if ([b64 isKindOfClass:[NSString class]]) {
                NSData *decoded = [[NSData alloc] initWithBase64EncodedString:(NSString *)b64 options:0];
                if (decoded) {
                    id reportObj = (id)report;
                    id<BGSAttachmentContract> created = nil;
                    if ([reportObj respondsToSelector:@selector(addAttachmentWithData:name:mimeType:)]) {
                        created = [reportObj addAttachmentWithData:decoded name:name mimeType:mime];
                    }
                    if (created) {
                        id fileName = att[@"fileName"];
                        if ([fileName isKindOfClass:[NSString class]] && [(NSString *)fileName length] > 0) {
                            created.fileName = fileName;
                        }
                    }
                }
            }
        }
    }
}

// Lifecycle events reach C# through this wrapper only. It is registered in +load, so an event can arrive before
// C# has registered its callback (for example from a launch the host made natively before any C# call): those
// are held and replayed, in order, after _bugsee_register_unity_callbacks, like Android's UnityWrapperProvider.
// The replay is posted to the main queue, so it lands after the Launch or Relaunch call that registered, not
// inside it. It runs outside the lock; events that arrive meanwhile queue behind it and are flushed before the
// callback becomes the direct destination, so nothing overtakes it.
static const NSUInteger kMaxEarlyLifecycleEvents = 128;
static NSObject *gLifecycleLock;
static NSMutableArray<NSArray *> *gEarlyLifecycle;
static BOOL gLifecycleReplaying = NO;

// Created on first use, not in a static initializer: +load runs before this file's initializers.
static void BugseeUnityEnsureLifecycleState(void)
{
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        gLifecycleLock = [NSObject new];
        gEarlyLifecycle = [NSMutableArray array];
    });
}

// A string crosses as a JSON string literal and a dictionary as a JSON object (C# decodes both to the types
// Android delivers); anything else as a JSON string holding its description. nil: no payload.
static NSString *BugseeUnityLifecycleJson(id data)
{
    if (!data) return nil;
    if ([data isKindOfClass:[NSDictionary class]] && [NSJSONSerialization isValidJSONObject:data]) {
        return BugseeUnityJsonString(data);
    }
    NSString *text = [data isKindOfClass:[NSString class]] ? data : [data description];
    NSString *wrapped = BugseeUnityJsonString(@[ text ?: @"" ]);
    return wrapped.length > 2 ? [wrapped substringWithRange:NSMakeRange(1, wrapped.length - 2)] : @"\"\"";
}

static void BugseeUnityDeliverLifecycle(BugseeUnityLifecycleCb cb, NSString *eventType, NSString *json)
{
    char *eventCopy = BugseeUnityCopyUTF8(eventType);
    char *dataCopy = BugseeUnityCopyUTF8(json);
    cb(eventCopy, dataCopy);
    free(eventCopy);
    free(dataCopy);
}

@interface BugseeUnityWrapper : NSObject <BugseeWrapper>
@end

@implementation BugseeUnityWrapper

+ (void)load
{
    if (!gWrapper) {
        gWrapper = [BugseeUnityWrapper new];
    }
    [Bugsee setWrapper:gWrapper];
}

- (void)onWrapperChannelAvailable:(id<BGSWrapperChannel>)channel
{
    gChannel = channel;
}

- (NSString *)wrapperType { return @"unity"; }
- (NSString *)wrapperVersion { return gWrapperVersion ?: @"unknown"; }
- (NSString *)wrapperBuild { return gWrapperBuild; }
- (NSDictionary<NSString *, NSString *> *)context { return gWrapperContext ?: @{}; }

// The one flow the SDK asks for is "vh", the managed view hierarchy (specs: sdk/wrapper-data-requests). It is asked on
// the main thread, which is Unity's thread, so C# normally answers inside this call. Every other type, and a request
// made before C# has registered, is answered with nil: silence would cost the SDK its whole budget.
- (void)requestDataWithType:(NSString *)dataType callback:(id<BGSDataRequestResultCallback>)callback
{
    BugseeUnityDataRequestCb requestCb = gDataRequestCb;
    if (!callback) return;
    if (!requestCb || ![dataType isEqualToString:@"vh"]) {
        [callback onResult:nil];
        return;
    }
    BugseeUnityEnsureState();
    int64_t requestId;
    @synchronized (gLock) {
        requestId = gNextRequestId++;
        // An answer that never comes (Unity paused or torn down) must not hold its callback for good.
        NSTimeInterval now = [NSDate timeIntervalSinceReferenceDate];
        NSMutableArray<NSNumber *> *stale = [NSMutableArray array];
        [gPending enumerateKeysAndObjectsUsingBlock:^(NSNumber *key, NSMutableDictionary *entry, BOOL *stop) {
            NSNumber *at = entry[@"dataRequestAt"];
            if (at && now - at.doubleValue > 10.0) [stale addObject:key];
        }];
        [gPending removeObjectsForKeys:stale];
        gPending[@(requestId)] = [@{ @"kind": @"data", @"dataRequest": callback, @"dataRequestAt": @(now) } mutableCopy];
    }
    char *typeCopy = BugseeUnityCopyUTF8(dataType);
    requestCb(requestId, typeCopy);
    free(typeCopy);
}

- (void)onLifecycleEvent:(NSString *)eventType data:(id)data
{
    if (!eventType) return;
    NSString *json = BugseeUnityLifecycleJson(data);
    BugseeUnityEnsureLifecycleState();
    BugseeUnityLifecycleCb cb;
    @synchronized (gLifecycleLock) {
        if (gLifecycleReplaying || !gLifecycleCb) {
            // Keep the earliest events (Launching, RelaunchedAfterCrash...); once full, later ones are dropped.
            if (gEarlyLifecycle.count < kMaxEarlyLifecycleEvents) {
                [gEarlyLifecycle addObject:@[ eventType, json ?: [NSNull null] ]];
            }
            return;
        }
        cb = gLifecycleCb;
    }
    BugseeUnityDeliverLifecycle(cb, eventType, json);
}

- (void)onBeforeReportCreated:(id<BGSReportContract>)report
                isTerminating:(BOOL)isTerminating
                   completion:(BGSCallback)completion
{
    [self forwardReport:report phase:0 isTerminating:isTerminating completion:completion];
}

- (void)onAfterReportCreated:(id<BGSReportContract>)report
               isTerminating:(BOOL)isTerminating
                  completion:(BGSCallback)completion
{
    [self forwardReport:report phase:1 isTerminating:isTerminating completion:completion];
}

- (NSData *)secureRectanglesForDisplay:(NSInteger)display
{
    return BugseeUnitySecureBufferForDisplay((int)display);
}

- (void)forwardReport:(id<BGSReportContract>)report
                phase:(int)phase
        isTerminating:(BOOL)isTerminating
           completion:(BGSCallback)completion
{
    if (!gReportCb) {
        if (completion) completion();
        return;
    }
    if (isTerminating) {
        if (completion) completion();
        return;
    }
    BugseeUnityEnsureState();
    int64_t requestId;
    @synchronized (gLock) {
        requestId = gNextRequestId++;
        NSMutableDictionary *entry = [NSMutableDictionary dictionary];
        entry[@"kind"] = @"report";
        if (report) entry[@"report"] = report;
        if (completion) {
            entry[@"completion"] = [completion copy];
        } else {
            entry[@"completion"] = ^{};
        }
        gPending[@(requestId)] = entry;
    }
    NSString *json = BugseeUnityJsonString(BugseeUnityReportToDict(report));
    char *jsonCopy = BugseeUnityCopyUTF8(json);
    gReportCb(requestId, phase, isTerminating ? 1 : 0, jsonCopy);
    free(jsonCopy);
}

@end

static int64_t BugseeUnityEnqueueFilter(NSString *kind, id event, id decisionBlock)
{
    BugseeUnityEnsureState();
    int64_t requestId;
    @synchronized (gLock) {
        requestId = gNextRequestId++;
        NSMutableDictionary *entry = [NSMutableDictionary dictionary];
        entry[@"kind"] = kind;
        if (event) entry[@"event"] = event;
        entry[@"decision"] = [decisionBlock copy];
        gPending[@(requestId)] = entry;
    }
    return requestId;
}

#if defined(BUGSEE_UNITY_TESTS)
// Stands in for the SDK's report: records what the add-time attachment methods were asked for.
@class BugseeUnityAttachmentRecorder;
@interface BugseeUnityRecordedAttachment : NSObject
@property(nonatomic, weak) BugseeUnityAttachmentRecorder *recorder;
@property(nonatomic, copy) NSString *fileName;
@end

@interface BugseeUnityAttachmentRecorder : NSObject
@property(nonatomic, strong) NSMutableArray<NSString *> *calls;
@end

@implementation BugseeUnityRecordedAttachment
- (void)setFileName:(NSString *)fileName
{
    _fileName = [fileName copy];
    [self.recorder.calls addObject:[NSString stringWithFormat:@"fileName:%@", fileName]];
}
@end

@implementation BugseeUnityAttachmentRecorder
- (instancetype)init
{
    if ((self = [super init])) _calls = [NSMutableArray new];
    return self;
}
- (id)addAttachmentWithData:(NSData *)data name:(NSString *)name mimeType:(NSString *)mimeType
{
    [self.calls addObject:[NSString stringWithFormat:@"data:%@:%lu:%@", name, (unsigned long)data.length, mimeType ?: @"-"]];
    BugseeUnityRecordedAttachment *created = [BugseeUnityRecordedAttachment new];
    created.recorder = self;
    return created;
}
- (id)addAttachmentWithFilePath:(NSString *)path name:(NSString *)name mimeType:(NSString *)mimeType move:(BOOL)move
{
    [self.calls addObject:[NSString stringWithFormat:@"file:%@:%@", name, mimeType ?: @"-"]];
    BugseeUnityRecordedAttachment *created = [BugseeUnityRecordedAttachment new];
    created.recorder = self;
    return created;
}
@end
#endif

extern "C" {

void _bugsee_register_unity_callbacks(BugseeUnityFilterCb filterCb,
                                      BugseeUnityReportCb reportCb,
                                      BugseeUnityLifecycleCb lifecycleCb)
{
    gFilterCb = filterCb;
    gReportCb = reportCb;
    BugseeUnityEnsureLifecycleState();
    NSArray<NSArray *> *batch = nil;
    @synchronized (gLifecycleLock) {
        if (gLifecycleReplaying) return; // a replay is pending or running; it finishes with the callback it started with
        if (!lifecycleCb) {
            gLifecycleCb = NULL;
            return;
        }
        if (gEarlyLifecycle.count == 0) {
            gLifecycleCb = lifecycleCb;
            return;
        }
        batch = [gEarlyLifecycle copy];
        [gEarlyLifecycle removeAllObjects];
        gLifecycleReplaying = YES;
    }
    dispatch_async(dispatch_get_main_queue(), ^{
        NSArray<NSArray *> *current = batch;
        while (current) {
            for (NSArray *event in current) {
                id json = event[1];
                BugseeUnityDeliverLifecycle(lifecycleCb, event[0], [json isKindOfClass:[NSString class]] ? json : nil);
            }
            @synchronized (gLifecycleLock) {
                if (gEarlyLifecycle.count == 0) {
                    gLifecycleCb = lifecycleCb;
                    gLifecycleReplaying = NO;
                    current = nil;
                } else {
                    current = [gEarlyLifecycle copy];
                    [gEarlyLifecycle removeAllObjects];
                }
            }
        }
    });
}

void _bugsee_register_data_request_callback(BugseeUnityDataRequestCb dataRequestCb)
{
    gDataRequestCb = dataRequestCb;
}

// Answers a request handed to the data request callback: the JSON, or NULL for "nothing to contribute". The first answer
// wins; a second one (or one for an id already expired) finds nothing and does nothing.
void _bugsee_complete_data_request(int64_t requestId, const char *json)
{
    BugseeUnityEnsureState();
    NSMutableDictionary *pending;
    @synchronized (gLock) {
        pending = gPending[@(requestId)];
        if (![pending[@"kind"] isEqualToString:@"data"]) return;
        [gPending removeObjectForKey:@(requestId)];
    }
    id<BGSDataRequestResultCallback> callback = pending[@"dataRequest"];
    NSString *text = json ? [NSString stringWithUTF8String:json] : nil;
    [callback onResult:text];
}

void _bugsee_set_secure_buffer(int display, int *packed, int packedLength)
{
    if (!packed || packedLength < 2) return;
    BugseeUnityEnsureState();
    BugseeUnityEnsureSecureRectBuffers();
    NSData *data = [NSData dataWithBytes:packed length:(NSUInteger)packedLength * sizeof(int32_t)];
    if (!data) return;
    @synchronized (gLock) {
        gSecureRectBuffers[@(display)] = data;
    }
}

void _bugsee_channel_log(const char *message, int level, int source)
{
    id<BGSWrapperChannel> channel = gChannel;
    if (!channel || !message) return;
    if (![channel respondsToSelector:@selector(logWithTag:message:level:source:)]) return;
    NSString *text = [NSString stringWithUTF8String:message];
    if (!text) return;
    [channel logWithTag:nil
                message:text
                  level:(BugseeLogLevel)level
                 source:(BGSLogEventSource)source];
}

void _bugsee_channel_network(const char *eventJson, int requiresFiltering)
{
    id<BGSWrapperChannel> channel = gChannel;
    if (!channel || !eventJson) {
        return;
    }
    if (![channel respondsToSelector:@selector(addNetworkEvent:requiresFiltering:)]) {
        return;
    }
    NSDictionary *dict = BugseeUnityParseJson(eventJson);
    if (![dict isKindOfClass:[NSDictionary class]]) {
        return;
    }
    BugseeNetworkEvent *event = [BugseeNetworkEvent new];
    id eventId = dict[@"id"];
    if ([eventId isKindOfClass:[NSString class]]) {
        event.ID = eventId;
    }
    BugseeUnityApplyNetworkDict(event, dict);
    [channel addNetworkEvent:event requiresFiltering:requiresFiltering != 0];
}

void _bugsee_channel_breadcrumb(const char *category, const char *message, int iosLevel, double timestampUnixSeconds)
{
    id<BGSWrapperChannel> channel = gChannel;
    if (!channel) {
        return;
    }
    if (![channel respondsToSelector:@selector(addBreadcrumb:)]) {
        return;
    }
    NSString *categoryText = category ? [NSString stringWithUTF8String:category] : @"";
    NSString *messageText = message ? [NSString stringWithUTF8String:message] : @"";
    if (!categoryText) {
        categoryText = @"";
    }
    if (!messageText) {
        messageText = @"";
    }
    id factory = [Bugsee getExchangeFactory];
    if (!factory) {
        return;
    }
    NSTimeInterval timestamp = timestampUnixSeconds > 0 ? timestampUnixSeconds : [[NSDate date] timeIntervalSince1970];
    id<BGSBreadcrumb> breadcrumb = [factory createBreadcrumbWithTimestamp:timestamp
                                                                 category:categoryText
                                                                    level:(BugseeLogLevel)iosLevel
                                                                  message:messageText
                                                                     type:@"manual"
                                                                     data:nil];
    if (!breadcrumb) {
        return;
    }
    [channel addBreadcrumb:breadcrumb];
}

void _bugsee_ensure_wrapper(const char *version, const char *build)
{
    if (version) gWrapperVersion = [NSString stringWithUTF8String:version];
    // No build (NULL, empty or the "dev" placeholder) is nil: the contract's wrapperBuild is nullable, and
    // "unknown" or "dev" would be reported as a real build id.
    NSString *buildString = build ? [NSString stringWithUTF8String:build] : nil;
    gWrapperBuild = (buildString.length == 0 || [buildString isEqualToString:@"dev"]) ? nil : buildString;
    if (!gWrapper) {
        gWrapper = [BugseeUnityWrapper new];
    }
    [Bugsee setWrapper:gWrapper];
}

void _bugsee_set_wrapper_context(const char *json)
{
    if (!json) {
        gWrapperContext = nil;
        return;
    }
    NSDictionary *parsed = BugseeUnityParseJson(json);
    if (![parsed isKindOfClass:[NSDictionary class]]) {
        return;
    }
    NSMutableDictionary<NSString *, NSString *> *out = [NSMutableDictionary dictionary];
    [(NSDictionary *)parsed enumerateKeysAndObjectsUsingBlock:^(id key, id obj, BOOL *stop) {
        if ([key isKindOfClass:[NSString class]] && [obj isKindOfClass:[NSString class]]) {
            out[(NSString *)key] = (NSString *)obj;
        }
    }];
    gWrapperContext = [out copy];
}

void _bugsee_set_network_filter_enabled(int enabled)
{
    if (!enabled) {
        gNetworkFilterCallbackInstalled = NO;
        return;
    }
    gNetworkFilterCallbackInstalled = YES;
    [Bugsee setNetworkEventFilter:^(BugseeNetworkEvent *event, BugseeNetworkFilterDecisionBlock decisionBlock) {
        if (!gNetworkFilterCallbackInstalled) {
            decisionBlock(event);
            return;
        }
        if (!gFilterCb) {
            decisionBlock(nil);
            return;
        }
        int64_t requestId = BugseeUnityEnqueueFilter(@"network", event, decisionBlock);
        NSString *json = BugseeUnityJsonString(BugseeUnityNetworkToDict(event));
        char *copy = BugseeUnityCopyUTF8(json);
        gFilterCb(requestId, 1, copy);
        free(copy);
    }];
}

void _bugsee_set_log_filter_enabled(int enabled)
{
    if (!enabled) {
        gLogFilterCallbackInstalled = NO;
        return;
    }
    gLogFilterCallbackInstalled = YES;
    [Bugsee setLogEventFilter:^(BugseeLogEvent *event, BugseeLogFilterDecisionBlock decisionBlock) {
        if (!gLogFilterCallbackInstalled) {
            decisionBlock(event);
            return;
        }
        if (!gFilterCb) {
            decisionBlock(nil);
            return;
        }
        int64_t requestId = BugseeUnityEnqueueFilter(@"log", event, decisionBlock);
        NSString *json = BugseeUnityJsonString(BugseeUnityLogToDict(event));
        char *copy = BugseeUnityCopyUTF8(json);
        gFilterCb(requestId, 2, copy);
        free(copy);
    }];
}

void _bugsee_set_breadcrumb_filter_enabled(int enabled)
{
    if (!enabled) {
        gBreadcrumbFilterCallbackInstalled = NO;
        return;
    }
    gBreadcrumbFilterCallbackInstalled = YES;
    [Bugsee setBreadcrumbFilter:^(id<BGSBreadcrumb> crumb, BugseeBreadcrumbFilterDecisionBlock decisionBlock) {
        if (!gBreadcrumbFilterCallbackInstalled) {
            decisionBlock(crumb);
            return;
        }
        if (!gFilterCb) {
            decisionBlock(nil);
            return;
        }
        int64_t requestId = BugseeUnityEnqueueFilter(@"breadcrumb", crumb, decisionBlock);
        NSString *json = BugseeUnityJsonString(BugseeUnityBreadcrumbToDict(crumb));
        char *copy = BugseeUnityCopyUTF8(json);
        gFilterCb(requestId, 3, copy);
        free(copy);
    }];
}

void _bugsee_complete_filter(int64_t requestId, int keep, const char *resultJson)
{
    BugseeUnityEnsureState();
    NSMutableDictionary *pending;
    @synchronized (gLock) {
        pending = gPending[@(requestId)];
        [gPending removeObjectForKey:@(requestId)];
    }
    if (!pending) return;

    NSString *kind = pending[@"kind"];
    id event = pending[@"event"];
    id decision = pending[@"decision"];
    NSDictionary *result = BugseeUnityParseJson(resultJson);

    if (!keep) {
        if ([kind isEqualToString:@"network"]) ((BugseeNetworkFilterDecisionBlock)decision)(nil);
        else if ([kind isEqualToString:@"log"]) ((BugseeLogFilterDecisionBlock)decision)(nil);
        else if ([kind isEqualToString:@"breadcrumb"]) ((BugseeBreadcrumbFilterDecisionBlock)decision)(nil);
        return;
    }

    if ([kind isEqualToString:@"network"]) {
        BugseeUnityApplyNetworkDict(event, result);
        ((BugseeNetworkFilterDecisionBlock)decision)(event);
    } else if ([kind isEqualToString:@"log"]) {
        BugseeUnityApplyLogDict(event, result);
        ((BugseeLogFilterDecisionBlock)decision)(event);
    } else if ([kind isEqualToString:@"breadcrumb"]) {
        BugseeUnityApplyBreadcrumbDict(event, result);
        ((BugseeBreadcrumbFilterDecisionBlock)decision)(event);
    }
}

void _bugsee_complete_report(int64_t requestId, const char *resultJson)
{
    BugseeUnityEnsureState();
    NSMutableDictionary *pending;
    @synchronized (gLock) {
        pending = gPending[@(requestId)];
        [gPending removeObjectForKey:@(requestId)];
    }
    if (!pending) return;

    id<BGSReportContract> report = pending[@"report"];
    BGSCallback completion = pending[@"completion"];
    NSDictionary *result = BugseeUnityParseJson(resultJson);
    if (result) {
        BugseeUnityApplyReportDict(report, result);
    }
    if (completion) completion();
}

#if defined(BUGSEE_UNITY_TESTS)
const char *_bugsee_test_copy_wrapper_version(void)
{
    return gWrapperVersion ? strdup(gWrapperVersion.UTF8String) : NULL;
}

const char *_bugsee_test_copy_wrapper_build(void)
{
    return gWrapperBuild ? strdup(gWrapperBuild.UTF8String) : NULL;
}

// Hands the wrapper a lifecycle event the way the SDK does; payloadJson is a JSON value (string literal or
// object), NULL for no payload.
void _bugsee_test_emit_lifecycle(const char *eventType, const char *payloadJson)
{
    id payload = nil;
    if (payloadJson) {
        NSData *bytes = [[NSString stringWithUTF8String:payloadJson] dataUsingEncoding:NSUTF8StringEncoding];
        payload = [NSJSONSerialization JSONObjectWithData:bytes options:NSJSONReadingFragmentsAllowed error:nil];
    }
    [gWrapper onLifecycleEvent:[NSString stringWithUTF8String:eventType] data:payload];
}

int _bugsee_test_wrapper_installed(void)
{
    return gWrapper != nil;
}

const char *_bugsee_test_copy_context_value(const char *key)
{
    if (!gWrapperContext || !key) return NULL;
    NSString *value = gWrapperContext[[NSString stringWithUTF8String:key]];
    return value ? strdup(value.UTF8String) : NULL;
}

// Applies a report dictionary's attachments to a recording stand-in and returns its calls, joined by "|".
const char *_bugsee_test_apply_report_attachments(const char *json)
{
    NSDictionary *d = BugseeUnityParseJson(json);
    if (!d) return NULL;
    BugseeUnityAttachmentRecorder *recorder = [BugseeUnityAttachmentRecorder new];
    BugseeUnityApplyReportDict((id<BGSReportContract>)recorder, d);
    return strdup([recorder.calls componentsJoinedByString:@"|"].UTF8String);
}

// Builds a real SDK network event from startJson, applies a filter result the way _bugsee_complete_filter does and
// returns the event as JSON: url, body (text or null), error (object or null), headers. Caller frees.
const char *_bugsee_test_apply_network_result(const char *startJson, const char *resultJson)
{
    BugseeNetworkEvent *event = [BugseeNetworkEvent new];
    BugseeUnityApplyNetworkDict(event, BugseeUnityParseJson(startJson));
    BugseeUnityApplyNetworkDict(event, BugseeUnityParseJson(resultJson));
    NSMutableDictionary *d = [NSMutableDictionary dictionary];
    d[@"url"] = event.url ?: [NSNull null];
    NSString *body = event.body ? [[NSString alloc] initWithData:event.body encoding:NSUTF8StringEncoding] : nil;
    d[@"body"] = event.body ? (body ?: @"<undecodable>") : [NSNull null];
    d[@"error"] = [event.error isKindOfClass:[NSDictionary class]] ? event.error : [NSNull null];
    d[@"headers"] = event.headers ?: [NSNull null];
    d[@"responseCode"] = @(event.responseCode);
    NSData *json = [NSJSONSerialization dataWithJSONObject:d options:NSJSONWritingSortedKeys error:nil];
    NSString *text = json ? [[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding] : nil;
    return strdup((text ?: @"unserializable").UTF8String);
}

// Stands in for the SDK's callback of a requestData: counts the answers and keeps the last one.
@interface BugseeUnityTestDataCallback : NSObject <BGSDataRequestResultCallback>
@end

static int gTestDataReplyCount = 0;
static NSString *gTestDataReply = nil;

@implementation BugseeUnityTestDataCallback
- (void)onResult:(NSString *)result
{
    gTestDataReplyCount++;
    gTestDataReply = [result copy];
}
@end

// Asks the installed wrapper for `type` the way the SDK does. Returns the number of answers that arrived inside the call.
int _bugsee_test_request_data(const char *type)
{
    gTestDataReplyCount = 0;
    gTestDataReply = nil;
    [gWrapper requestDataWithType:[NSString stringWithUTF8String:type] callback:[BugseeUnityTestDataCallback new]];
    return gTestDataReplyCount;
}

int _bugsee_test_data_reply_count(void) { return gTestDataReplyCount; }

const char *_bugsee_test_copy_data_reply(void)
{
    return gTestDataReply ? strdup(gTestDataReply.UTF8String) : NULL;
}

// Builds a real SDK breadcrumb holding startDataJson as its data, applies a filter result the way
// _bugsee_complete_filter does, and returns the crumb's data as JSON ("null" when it has none). Caller frees.
const char *_bugsee_test_apply_breadcrumb_result(const char *startDataJson, const char *resultJson)
{
    id factory = [Bugsee getExchangeFactory];
    if (!factory) return NULL;
    NSDictionary *start = startDataJson ? BugseeUnityParseJson(startDataJson) : nil;
    id<BGSBreadcrumb> crumb = [factory createBreadcrumbWithTimestamp:1.0
                                                            category:@"cat"
                                                               level:(BugseeLogLevel)0
                                                             message:@"msg"
                                                                type:@"manual"
                                                                data:start];
    if (!crumb) return NULL;
    BugseeUnityApplyBreadcrumbDict(crumb, BugseeUnityParseJson(resultJson));
    id data = crumb.data;
    if (!data) return strdup("null");
    NSData *json = [NSJSONSerialization dataWithJSONObject:data options:NSJSONWritingSortedKeys error:nil];
    NSString *text = json ? [[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding] : nil;
    return strdup((text ?: @"unserializable").UTF8String);
}

// Builds a real SDK log event with the given text and level, applies a filter result the way _bugsee_complete_filter
// does, and returns {"text":..|null,"level":n} as JSON. Caller frees.
const char *_bugsee_test_apply_log_result(const char *startText, int startLevel, const char *resultJson)
{
    BugseeLogEvent *event = [BugseeLogEvent new];
    event.text = startText ? [NSString stringWithUTF8String:startText] : nil;
    event.level = (BugseeLogLevel)startLevel;
    BugseeUnityApplyLogDict(event, BugseeUnityParseJson(resultJson));
    NSDictionary *d = @{ @"text": event.text ?: [NSNull null], @"level": @((int)event.level) };
    NSData *json = [NSJSONSerialization dataWithJSONObject:d options:NSJSONWritingSortedKeys error:nil];
    NSString *text = json ? [[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding] : nil;
    return strdup((text ?: @"unserializable").UTF8String);
}

// Builds a real SDK breadcrumb (category, message, type, level; a NULL string is nil), applies a filter result the way
// _bugsee_complete_filter does, and returns its category, message, type and level as JSON (nil as null). Caller frees.
const char *_bugsee_test_apply_breadcrumb_fields(const char *category, const char *message, const char *type,
                                                 int level, const char *resultJson)
{
    id factory = [Bugsee getExchangeFactory];
    if (!factory) return NULL;
    id<BGSBreadcrumb> crumb = [factory createBreadcrumbWithTimestamp:1.0
                                                            category:category ? [NSString stringWithUTF8String:category] : nil
                                                               level:(BugseeLogLevel)level
                                                             message:message ? [NSString stringWithUTF8String:message] : nil
                                                                type:type ? [NSString stringWithUTF8String:type] : nil
                                                                data:nil];
    if (!crumb) return NULL;
    BugseeUnityApplyBreadcrumbDict(crumb, BugseeUnityParseJson(resultJson));
    NSDictionary *d = @{ @"category": crumb.category ?: [NSNull null], @"message": crumb.message ?: [NSNull null],
                         @"type": crumb.type ?: [NSNull null], @"level": @((int)crumb.level) };
    NSData *json = [NSJSONSerialization dataWithJSONObject:d options:NSJSONWritingSortedKeys error:nil];
    NSString *text = json ? [[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding] : nil;
    return strdup((text ?: @"unserializable").UTF8String);
}

} // extern "C" (an Objective-C class cannot be declared inside it)

// A report that keeps attributes the way the SDK's does (values stored as given, kinds intact), with only what
// BugseeUnityApplyReportDict touches for attributes implemented.
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wprotocol"
#pragma clang diagnostic ignored "-Wobjc-protocol-property-synthesis"
@interface BugseeUnityTestReport : NSObject <BGSReportContract>
@property(nonatomic, strong) NSMutableDictionary *store;
@end
@implementation BugseeUnityTestReport
@synthesize summary, reportDescription, email, severity;
- (NSString *)reportId { return @"test"; }
- (NSString *)type { return @"bug"; }
- (NSMutableArray *)labels { return [NSMutableArray array]; }
- (NSMutableDictionary *)attributes { return [self.store mutableCopy]; }
- (void)setAttribute:(id)value forName:(NSString *)name { self.store[name] = value; }
- (void)removeAttributeForName:(NSString *)name { [self.store removeObjectForKey:name]; }
- (void)clearAllAttributes { [self.store removeAllObjects]; }
@end
#pragma clang diagnostic pop

extern "C" {

// Applies a report handler result the way _bugsee_complete_report does to a report holding startAttributesJson
// as its attributes, and returns the report's attributes as JSON (sorted keys). Caller frees.
const char *_bugsee_test_apply_report_result(const char *startAttributesJson, const char *resultJson)
{
    BugseeUnityTestReport *report = [BugseeUnityTestReport new];
    NSDictionary *start = startAttributesJson ? BugseeUnityParseJson(startAttributesJson) : nil;
    report.store = start ? [start mutableCopy] : [NSMutableDictionary dictionary];
    BugseeUnityApplyReportDict(report, BugseeUnityParseJson(resultJson));
    NSData *json = [NSJSONSerialization dataWithJSONObject:report.store options:NSJSONWritingSortedKeys error:nil];
    NSString *text = json ? [[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding] : nil;
    return strdup((text ?: @"unserializable").UTF8String);
}

// The attributes JSON the report handler receives for a report whose attributes are startAttributesJson, plus
// a date and NaN value that JSON cannot hold. Caller frees.
const char *_bugsee_test_report_json_with_unserializable_attributes(const char *startAttributesJson)
{
    BugseeUnityTestReport *report = [BugseeUnityTestReport new];
    NSMutableDictionary *store = [BugseeUnityParseJson(startAttributesJson) mutableCopy] ?: [NSMutableDictionary dictionary];
    store[@"when"] = [NSDate dateWithTimeIntervalSince1970:0];
    store[@"nan"] = @(NAN);
    report.store = store;
    return strdup(BugseeUnityJsonString(BugseeUnityReportToDict(report)).UTF8String);
}
#endif

} // extern "C"

#else

extern "C" {
void _bugsee_register_unity_callbacks(void *a, void *b, void *c) { (void)a; (void)b; (void)c; }
void _bugsee_channel_log(const char *message, int level, int source) { (void)message; (void)level; (void)source; }
void _bugsee_channel_network(const char *eventJson, int requiresFiltering) { (void)eventJson; (void)requiresFiltering; }
void _bugsee_channel_breadcrumb(const char *category, const char *message, int iosLevel, double timestampUnixSeconds)
{
    (void)category;
    (void)message;
    (void)iosLevel;
    (void)timestampUnixSeconds;
}
void _bugsee_ensure_wrapper(const char *version, const char *build) { (void)version; (void)build; }
void _bugsee_set_wrapper_context(const char *json) { (void)json; }
void _bugsee_set_network_filter_enabled(int enabled) { (void)enabled; }
void _bugsee_set_log_filter_enabled(int enabled) { (void)enabled; }
void _bugsee_set_breadcrumb_filter_enabled(int enabled) { (void)enabled; }
void _bugsee_complete_filter(int64_t requestId, int keep, const char *resultJson) { (void)requestId; (void)keep; (void)resultJson; }
void _bugsee_complete_report(int64_t requestId, const char *resultJson) { (void)requestId; (void)resultJson; }
void _bugsee_register_data_request_callback(void *callback) { (void)callback; }
void _bugsee_complete_data_request(int64_t requestId, const char *json) { (void)requestId; (void)json; }
}

#endif
