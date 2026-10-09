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

static BugseeUnityFilterCb gFilterCb = NULL;
static BugseeUnityReportCb gReportCb = NULL;
static BugseeUnityLifecycleCb gLifecycleCb = NULL;

static NSMutableDictionary<NSNumber *, NSMutableDictionary *> *gPending;
static int64_t gNextRequestId = 1;
static NSObject *gLock;
static NSString *gWrapperVersion = @"0.1.0";
static NSString *gWrapperBuild = @"dev";
static NSDictionary<NSString *, NSString *> *gWrapperContext = nil;

@class BugseeUnityWrapper;
static BugseeUnityWrapper *gWrapper;
static id<BGSWrapperChannel> gChannel;

static void BugseeUnityEnsureState(void)
{
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        gPending = [NSMutableDictionary dictionary];
        gLock = [NSObject new];
    });
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
    if ([shortMsg isKindOfClass:[NSString class]]) {
        error[@"name"] = shortMsg;
        touchedError = YES;
    } else if (shortMsg == [NSNull null]) {
        [error removeObjectForKey:@"name"];
        touchedError = YES;
    }
    id desc = d[@"errorDescription"];
    if ([desc isKindOfClass:[NSString class]]) {
        error[@"description"] = desc;
        touchedError = YES;
    } else if (desc == [NSNull null]) {
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

static void BugseeUnityApplyLogDict(BugseeLogEvent *event, NSDictionary *d)
{
    if (![d isKindOfClass:[NSDictionary class]] || !event) return;
    id msg = d[@"message"];
    if ([msg isKindOfClass:[NSString class]]) event.text = msg;
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

static void BugseeUnityApplyBreadcrumbDict(id<BGSBreadcrumb> crumb, NSDictionary *d)
{
    if (![d isKindOfClass:[NSDictionary class]] || !crumb) return;
    id cat = d[@"category"]; if ([cat isKindOfClass:[NSString class]]) crumb.category = cat;
    id msg = d[@"message"]; if ([msg isKindOfClass:[NSString class]]) crumb.message = msg;
    id type = d[@"type"]; if ([type isKindOfClass:[NSString class]]) crumb.type = type;
    id level = d[@"level"]; if ([level respondsToSelector:@selector(integerValue)]) crumb.level = [level integerValue];
    id data = d[@"data"]; if ([data isKindOfClass:[NSDictionary class]]) crumb.data = data;
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
    d[@"attributes"] = report.attributes ? [report.attributes copy] : @{};
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
    if ([sev respondsToSelector:@selector(integerValue)]) report.severity = [sev integerValue];

    id labels = d[@"labels"];
    if ([labels isKindOfClass:[NSArray class]]) {
        [report clearLabels];
        for (id label in (NSArray *)labels) {
            if ([label isKindOfClass:[NSString class]]) [report addLabel:label];
        }
    }

    id attrs = d[@"attributes"];
    if ([attrs isKindOfClass:[NSDictionary class]]) {
        [report clearAllAttributes];
        [(NSDictionary *)attrs enumerateKeysAndObjectsUsingBlock:^(id key, id obj, BOOL *stop) {
            if ([key isKindOfClass:[NSString class]]) {
                [report setAttribute:obj forName:key];
            }
        }];
    }

    id attachments = d[@"attachments"];
    if ([attachments isKindOfClass:[NSArray class]]) {
        for (id item in (NSArray *)attachments) {
            if (![item isKindOfClass:[NSDictionary class]]) continue;
            NSDictionary *att = (NSDictionary *)item;
            NSString *name = [att[@"name"] isKindOfClass:[NSString class]] ? att[@"name"] : @"attachment";
            id<BGSAttachmentContract> created = [report createAndAddAttachmentWithName:name];
            if (!created) continue;
            id fileName = att[@"fileName"];
            if ([fileName isKindOfClass:[NSString class]]) created.fileName = fileName;
            id mime = att[@"mimeType"];
            if ([mime isKindOfClass:[NSString class]]) created.mimeType = mime;
            id text = att[@"text"];
            if ([text isKindOfClass:[NSString class]]) {
                created.data = [(NSString *)text dataUsingEncoding:NSUTF8StringEncoding];
            }
            id b64 = att[@"dataBase64"];
            if ([b64 isKindOfClass:[NSString class]] && [(NSString *)b64 length] > 0) {
                NSData *decoded = [[NSData alloc] initWithBase64EncodedString:(NSString *)b64 options:0];
                if (decoded) created.data = decoded;
            }
        }
    }
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
- (NSString *)wrapperBuild { return gWrapperBuild ?: @"unknown"; }
- (NSDictionary<NSString *, NSString *> *)context { return gWrapperContext ?: @{}; }

- (void)requestDataWithType:(NSString *)dataType callback:(id<BGSDataRequestResultCallback>)callback
{
    [callback onResult:nil];
}

- (void)onLifecycleEvent:(NSString *)eventType data:(id)data
{
    if (!gLifecycleCb || !eventType) return;
    NSString *json = nil;
    if (data && [NSJSONSerialization isValidJSONObject:data]) {
        json = BugseeUnityJsonString(data);
    } else if ([data isKindOfClass:[NSString class]]) {
        json = BugseeUnityJsonString(@{ @"value": data });
    } else if (data != nil) {
        json = BugseeUnityJsonString(@{ @"value": [data description] });
    }
    char *eventCopy = BugseeUnityCopyUTF8(eventType);
    char *dataCopy = BugseeUnityCopyUTF8(json);
    gLifecycleCb(eventCopy, dataCopy);
    free(eventCopy);
    free(dataCopy);
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

- (void)forwardReport:(id<BGSReportContract>)report
                phase:(int)phase
        isTerminating:(BOOL)isTerminating
           completion:(BGSCallback)completion
{
    if (!gReportCb) {
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
        entry[@"completion"] = completion ? [completion copy] : ^{};
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

extern "C" {

void _bugsee_register_unity_callbacks(BugseeUnityFilterCb filterCb,
                                      BugseeUnityReportCb reportCb,
                                      BugseeUnityLifecycleCb lifecycleCb)
{
    gFilterCb = filterCb;
    gReportCb = reportCb;
    gLifecycleCb = lifecycleCb;
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

void _bugsee_ensure_wrapper(const char *version, const char *build)
{
    if (version) gWrapperVersion = [NSString stringWithUTF8String:version];
    if (build) gWrapperBuild = [NSString stringWithUTF8String:build];
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
        [Bugsee setNetworkEventFilter:nil];
        return;
    }
    [Bugsee setNetworkEventFilter:^(BugseeNetworkEvent *event, BugseeNetworkFilterDecisionBlock decisionBlock) {
        if (!gFilterCb) {
            decisionBlock(event);
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
        [Bugsee setLogEventFilter:nil];
        return;
    }
    [Bugsee setLogEventFilter:^(BugseeLogEvent *event, BugseeLogFilterDecisionBlock decisionBlock) {
        if (!gFilterCb) {
            decisionBlock(event);
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
        [Bugsee setBreadcrumbFilter:nil];
        return;
    }
    [Bugsee setBreadcrumbFilter:^(id<BGSBreadcrumb> crumb, BugseeBreadcrumbFilterDecisionBlock decisionBlock) {
        if (!gFilterCb) {
            decisionBlock(crumb);
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

} // extern "C"

#else

extern "C" {
void _bugsee_register_unity_callbacks(void *a, void *b, void *c) { (void)a; (void)b; (void)c; }
void _bugsee_channel_log(const char *message, int level, int source) { (void)message; (void)level; (void)source; }
void _bugsee_ensure_wrapper(const char *version, const char *build) { (void)version; (void)build; }
void _bugsee_set_wrapper_context(const char *json) { (void)json; }
void _bugsee_set_network_filter_enabled(int enabled) { (void)enabled; }
void _bugsee_set_log_filter_enabled(int enabled) { (void)enabled; }
void _bugsee_set_breadcrumb_filter_enabled(int enabled) { (void)enabled; }
void _bugsee_complete_filter(int64_t requestId, int keep, const char *resultJson) { (void)requestId; (void)keep; (void)resultJson; }
void _bugsee_complete_report(int64_t requestId, const char *resultJson) { (void)requestId; (void)resultJson; }
}

#endif
