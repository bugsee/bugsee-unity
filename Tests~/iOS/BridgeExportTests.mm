// Second bridge test binary (run by run-tests.sh after BridgeTests.mm): drives the exports BridgeTests.mm does not,
// through the same C ABI the C# DllImports use, against the real SDK slice with no launch. Where the SDK has an
// observable state the test checks it (a fake wrapper channel, the filter blocks the bridge hands the SDK, the secure
// rect buffer the wrapper answers the SDK with, the managed upload's end); the rest are smoke calls, named in place:
// they must link, take the argument shapes C# passes (NULL strings, short buffers, unknown request ids) and return
// without crashing, and assert nothing else. The
// prototypes below are checked against the real exports by NativeBindingTests; IosExportCoverageTests fails when a
// DllImported export is not called from one of the Tests~/iOS/*.mm files.
#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <Bugsee/Bugsee.h>
#include <dlfcn.h>

extern "C" {
typedef void (*BugseeManagedReportCreateCallback)(int succeeded, uint64_t uploadToken);

int _bugsee_get_status(void);
void _bugsee_log(const char *message, int level);
void _bugsee_trace(const char *name, const char *valueJson);
void _bugsee_event(const char *name, const char *paramsJson);
void _bugsee_set_attribute(const char *key, const char *valueJson);
char *_bugsee_get_attribute(const char *key);
void _bugsee_clear_attribute(const char *key);
void _bugsee_clear_all_attributes(void);
void _bugsee_set_email(const char *email);
char *_bugsee_get_email(void);
void _bugsee_clear_email(void);
float _bugsee_screen_scale(void);
float _bugsee_pixels_per_point(int unityPixelWidth);
void _bugsee_capture_view_hierarchy(void);
int _bugsee_add_secure_web_view(void *view);
int _bugsee_remove_secure_web_view(void *view);
void _bugsee_feedback_show(void);
void _bugsee_feedback_set_greeting(const char *message);
void _bugsee_free(char *ptr);
void _bugsee_invalidate_managed_report_uploads(void);
void _bugsee_upload_managed_report(const char *reportJson, uint64_t uploadId, uint64_t uploadFence,
                                   BugseeManagedReportCreateCallback callback);
void _bugsee_set_secure_buffer(int display, int *packed, int packedLength);
int _bugsee_unity_view_region(float *outRect);
int _bugsee_screen_points(float *outWH);
void _bugsee_channel_log(const char *message, int level, int source);
void _bugsee_channel_network(const char *eventJson, int requiresFiltering);
void _bugsee_channel_breadcrumb(const char *category, const char *message, int iosLevel, double timestampUnixSeconds);
void _bugsee_set_network_filter_enabled(int enabled);
void _bugsee_set_log_filter_enabled(int enabled);
void _bugsee_set_breadcrumb_filter_enabled(int enabled);
void _bugsee_complete_filter(int64_t requestId, int keep, const char *resultJson);
void _bugsee_complete_report(int64_t requestId, const char *resultJson);
void _bugsee_complete_data_request(int64_t requestId, const char *json);
void _bugsee_register_unity_callbacks(void (*filterCb)(int64_t, int, const char *), void *reportCb, void *lifecycleCb);

// Test hooks (compiled into the bridge only under BUGSEE_UNITY_TESTS).
void *_bugsee_test_wrapper(void);
int _bugsee_test_run_filter(int kind, const char *json);
int _bugsee_test_filter_decision_count(void);
const char *_bugsee_test_copy_filter_decision(void);
int _bugsee_test_pending_request_count(void);
const char *_bugsee_test_finish_managed_upload(const char *json, int withReport, uint64_t uploadId, uint64_t uploadFence);
uint64_t _bugsee_test_managed_upload_fence(void);
}

static void expect(int condition, const char *message)
{
    if (!condition) {
        fprintf(stderr, "FAIL %s\n", message);
        exit(1);
    }
    printf("ok %s\n", message);
}

static int gUploadSucceeded = -1;
static uint64_t gUploadToken = 0;
static int gUploadCalls = 0;

static void OnUploadCreated(int succeeded, uint64_t token)
{
    gUploadSucceeded = succeeded;
    gUploadToken = token;
    gUploadCalls++;
}

// Copies and frees a returned string so a check does not leak; "(null)" stands for NULL.
static NSString *takeString(char *value)
{
    if (!value) return @"(null)";
    NSString *s = [NSString stringWithUTF8String:value];
    _bugsee_free(value);
    return s;
}

// Stands in for the SDK's wrapper channel and records what the bridge sends through it.
@interface FakeChannel : NSObject <BGSWrapperChannel>
@property(nonatomic, strong) NSMutableArray<NSString *> *logs;
@property(nonatomic, strong) NSMutableArray<BugseeNetworkEvent *> *events;
@property(nonatomic, strong) NSMutableArray<NSNumber *> *filtering;
@property(nonatomic, strong) NSMutableArray<id<BGSBreadcrumb>> *crumbs;
@end
@implementation FakeChannel
- (instancetype)init
{
    if ((self = [super init])) {
        _logs = [NSMutableArray array];
        _events = [NSMutableArray array];
        _filtering = [NSMutableArray array];
        _crumbs = [NSMutableArray array];
    }
    return self;
}
- (void)logWithTag:(NSString *)tag message:(NSString *)message level:(BugseeLogLevel)level source:(BGSLogEventSource)source
{
    [self.logs addObject:[NSString stringWithFormat:@"%@|%d|%d|%@", message, (int)level, (int)source, tag ?: @"-"]];
}
- (void)addNetworkEvent:(BugseeNetworkEvent *)event requiresFiltering:(BOOL)requiresFiltering
{
    [self.events addObject:event];
    [self.filtering addObject:@(requiresFiltering)];
}
- (void)addBreadcrumb:(id<BGSBreadcrumb>)breadcrumb
{
    [self.crumbs addObject:breadcrumb];
}
@end

// What the filter callback was handed (it is C#'s side of the filter flow).
static int gFilterCalls = 0;
static int64_t gFilterId = 0;
static int gFilterKind = 0;
static NSString *gFilterJson = nil;
static void OnFilter(int64_t requestId, int kind, const char *json)
{
    gFilterCalls++;
    gFilterId = requestId;
    gFilterKind = kind;
    gFilterJson = json ? [NSString stringWithUTF8String:json] : nil;
}

static NSDictionary *parse(NSString *json)
{
    if (!json) return nil;
    return [NSJSONSerialization JSONObjectWithData:[json dataUsingEncoding:NSUTF8StringEncoding] options:0 error:nil];
}

static NSString *decision(void)
{
    const char *d = _bugsee_test_copy_filter_decision();
    NSString *s = d ? [NSString stringWithUTF8String:d] : @"(none)";
    free((void *)d);
    return s;
}

static NSString *finishUpload(const char *json, int withReport, uint64_t id, uint64_t fence)
{
    const char *r = _bugsee_test_finish_managed_upload(json, withReport, id, fence);
    NSString *s = [NSString stringWithUTF8String:r];
    free((void *)r);
    return s;
}

int main(void)
{
    // Status: nothing launched, so the SDK is stopped (BugseeStatus raw 0).
    expect(_bugsee_get_status() == 0, "status is stopped before any launch");

    // Attributes: a value set is read back in the wire form, clearing one removes only it, clear-all removes the rest.
    _bugsee_clear_all_attributes();
    _bugsee_set_attribute("a", "\"one\"");
    _bugsee_set_attribute("b", "2");
    expect([takeString(_bugsee_get_attribute("a")) isEqualToString:@"s:one"], "a string attribute reads back as s:one");
    expect([takeString(_bugsee_get_attribute("b")) isEqualToString:@"i:2"], "an integer attribute reads back as i:2");
    _bugsee_clear_attribute("a");
    expect([takeString(_bugsee_get_attribute("a")) isEqualToString:@"(null)"], "clear_attribute removes the attribute");
    expect([takeString(_bugsee_get_attribute("b")) isEqualToString:@"i:2"], "clear_attribute leaves the others");
    _bugsee_clear_all_attributes();
    expect([takeString(_bugsee_get_attribute("b")) isEqualToString:@"(null)"], "clear_all_attributes removes every attribute");

    // User identifier ("email" on the C# side). Probed at run time: the SDK keeps it in the keychain, which a bare simulator
    // process may not be able to write. When it can, the set value is read back, replaced, and cleared; when it cannot,
    // only the calls are checked (the device e2e scenarios assert it there).
    _bugsee_clear_email();
    _bugsee_set_email("user@example.com");
    NSString *stored = takeString(_bugsee_get_email());
    if ([stored isEqualToString:@"user@example.com"]) {
        expect(1, "a set email reads back");
        _bugsee_set_email("other@example.com");
        expect([takeString(_bugsee_get_email()) isEqualToString:@"other@example.com"], "a second email replaces the first");
        _bugsee_clear_email();
        expect([takeString(_bugsee_get_email()) isEqualToString:@"(null)"], "clear_email removes the email");
    } else {
        printf("note: no keychain in this process, email read-back skipped (got %s)\n", stored.UTF8String);
        _bugsee_clear_email();
    }
    _bugsee_set_email(NULL);
    _bugsee_free(_bugsee_get_email());
    _bugsee_clear_email();
    expect(1, "the email exports link and run");

    // Screen metrics follow UIKit; the unusable width falls back to the scale.
    expect(_bugsee_screen_scale() == (float)[UIScreen mainScreen].scale, "screen scale is UIScreen's scale");
    expect(_bugsee_pixels_per_point(0) == _bugsee_screen_scale(), "an unusable pixel width falls back to the scale");
    float points = (float)[UIScreen mainScreen].bounds.size.width;
    expect(_bugsee_pixels_per_point((int)(points * 2)) == 2.0f || points != (float)(int)points,
           "pixels per point is the pixel width over the width in points");

    // Free takes NULL (and, above, a returned string).
    _bugsee_free(NULL);

    // Managed report upload: no JSON is a failed create, reported on the same call with the caller's token.
    _bugsee_upload_managed_report(NULL, 41, 7, OnUploadCreated);
    expect(gUploadCalls == 1 && gUploadSucceeded == 0 && gUploadToken == 41, "a report upload without JSON fails with its token");
    _bugsee_upload_managed_report(NULL, 42, 7, NULL);
    expect(gUploadCalls == 1, "a report upload without JSON and without a callback calls nothing back");

    // The end of an upload (what runs once the SDK has made the report), driven with a fresh report and a recording
    // upload step: the callback comes first with success, then the report holding the JSON's fields is uploaded.
    uint64_t fence = _bugsee_test_managed_upload_fence();
    expect([finishUpload("{\"summary\":\"S\",\"labels\":[\"a\",\"b\"]}", 1, 5, fence) isEqualToString:@"1:1:5|1|S|a,b"],
           "an active upload reports success with its token, then uploads the report with the JSON's fields");
    expect([finishUpload("{}", 0, 5, fence) isEqualToString:@"1:0:5|0|-"], "no report made: a failed create, nothing uploaded");
    expect([finishUpload("not json", 1, 5, fence) isEqualToString:@"1:0:5|0|-"], "an unreadable report JSON fails and uploads nothing");
    expect([finishUpload("{}", 1, 0, fence) isEqualToString:@"1:0:0|0|-"], "upload id 0 is never active");
    expect([finishUpload("{}", 1, 5, fence + 1) isEqualToString:@"1:0:5|0|-"], "a fence from another generation fails and uploads nothing");
    _bugsee_invalidate_managed_report_uploads();
    expect(_bugsee_test_managed_upload_fence() == fence + 1, "invalidating moves the fence");
    expect([finishUpload("{}", 1, 5, fence) isEqualToString:@"1:0:5|0|-"], "an upload started before the invalidation fails and uploads nothing");
    expect([finishUpload("{}", 1, 5, fence + 1) isEqualToString:@"1:1:5|1|-|-"], "an upload started after the invalidation goes through");
    // Not asserted: _bugsee_upload_managed_report's own hand-off to [Bugsee createReportWithCompletion:], which needs a
    // launched SDK to make a report (device scenario: upload).

    // Logging and telemetry with no running SDK, including the NULL and non-object payloads C# can send (smoke only: the
    // calls have no observable effect before a launch). _bugsee_feedback_show / _set_greeting only log today (the Feedback
    // module is not in the core 7.x package); revisit them if a future SDK ships Feedback.
    _bugsee_log("hello", 3);
    _bugsee_log(NULL, 3);
    _bugsee_trace("t", "1");
    _bugsee_trace("t", "\"text\"");
    _bugsee_trace("t", "not json");
    _bugsee_trace("t", NULL);
    _bugsee_event("e", "{\"k\":1}");
    _bugsee_event("e", "[1]");
    _bugsee_event("e", NULL);
    _bugsee_capture_view_hierarchy();
    _bugsee_feedback_show();
    _bugsee_feedback_set_greeting("hi");
    _bugsee_feedback_set_greeting(NULL);
    expect(_bugsee_get_status() == 0, "logging and telemetry do not start the SDK");

    // WebView secure views: a NULL pointer and a live object that is not a WKWebView are refused; a real WKWebView is accepted
    // (before a launch the SDK registers nothing, so only the result is checked) and can be taken off again.
    expect(_bugsee_add_secure_web_view(NULL) == 0 && _bugsee_remove_secure_web_view(NULL) == 0, "a NULL secure WebView is refused");
    UIView *plainView = [[UIView alloc] initWithFrame:CGRectMake(0, 0, 10, 10)];
    expect(_bugsee_add_secure_web_view((__bridge void *)plainView) == 0
               && _bugsee_remove_secure_web_view((__bridge void *)plainView) == 0,
           "a view that is not a WKWebView is refused");
    dlopen("/System/Library/Frameworks/WebKit.framework/WebKit", RTLD_LAZY);
    Class webKitClass = NSClassFromString(@"WKWebView");
    expect(webKitClass != Nil, "WebKit loads");
    UIView *webView = [(UIView *)[webKitClass alloc] initWithFrame:CGRectMake(0, 0, 10, 10)];
    expect(_bugsee_add_secure_web_view((__bridge void *)webView) == 1, "a WKWebView is accepted as a secure WebView");
    expect(_bugsee_remove_secure_web_view((__bridge void *)webView) == 1, "a WKWebView is accepted for removal from the secure list");

    // Secure rect buffer: what the SDK asks the wrapper for (secureRectanglesForDisplay:) is the stored buffer, per display,
    // and the baseline {1, 0} (version 1, no rectangles) where none was stored or the stored one was refused.
    id<BugseeWrapper> wrapper = (__bridge id<BugseeWrapper>)_bugsee_test_wrapper();
    expect(wrapper != nil, "the bridge registered a wrapper");
    NSData *baseline = [NSData dataWithBytes:(int32_t[]){ 1, 0 } length:8];
    expect([[wrapper secureRectanglesForDisplay:0] isEqualToData:baseline], "no stored buffer: the baseline");
    int packed[] = { 1, 1, 0, 0, 10, 10 };
    _bugsee_set_secure_buffer(0, NULL, 6);
    _bugsee_set_secure_buffer(0, packed, 1);
    expect([[wrapper secureRectanglesForDisplay:0] isEqualToData:baseline], "a NULL or too short buffer is ignored");
    _bugsee_set_secure_buffer(0, packed, 6);
    expect([[wrapper secureRectanglesForDisplay:0] isEqualToData:[NSData dataWithBytes:packed length:sizeof(packed)]],
           "the packed buffer is stored whole for its display");
    expect([[wrapper secureRectanglesForDisplay:1] isEqualToData:baseline], "another display keeps the baseline");
    int second[] = { 1, 0 };
    _bugsee_set_secure_buffer(1, second, 2);
    _bugsee_set_secure_buffer(0, second, 2);
    expect([[wrapper secureRectanglesForDisplay:0] isEqualToData:baseline] && [[wrapper secureRectanglesForDisplay:1] isEqualToData:baseline],
           "a later buffer replaces the earlier one");
    // Neither is guaranteed in a test process (no screen size, no UnityView): when they answer, the numbers are usable.
    float wh[2] = { 0, 0 };
    if (_bugsee_screen_points(wh) == 1) expect(wh[0] > 0 && wh[1] > 0, "a reported screen size has a size");
    expect(_bugsee_screen_points(NULL) == 0 && _bugsee_unity_view_region(NULL) == 0, "a NULL output is refused");
    float rect[4] = { 0, 0, 0, 0 };
    if (_bugsee_unity_view_region(rect) == 1) expect(rect[2] > 0 && rect[3] > 0, "a view region has a size");

    // Wrapper channel. With none attached every producer returns quietly; with a fake one attached (the wrapper hands the
    // bridge its channel the way the SDK does) each producer lands there with the fields C# sent.
    _bugsee_channel_log("msg", 3, 1);
    _bugsee_channel_network("{\"id\":\"n\",\"url\":\"u\"}", 1);
    _bugsee_channel_breadcrumb("cat", "msg", 3, 0);
    FakeChannel *channel = [FakeChannel new];
    [wrapper onWrapperChannelAvailable:channel];
    expect(channel.logs.count == 0 && channel.events.count == 0 && channel.crumbs.count == 0, "nothing sent before the channel arrived reaches it");

    _bugsee_channel_log("hello", 4, 2);
    _bugsee_channel_log(NULL, 3, 1);
    expect(channel.logs.count == 1 && [channel.logs[0] isEqualToString:@"hello|4|2|-"],
           "a channel log carries its text, level and source and no tag; NULL text sends nothing");

    _bugsee_channel_network("{\"id\":\"n1\",\"url\":\"https://e.com/a\",\"method\":\"POST\",\"responseCode\":201,\"body\":\"b\"}", 1);
    _bugsee_channel_network("{\"url\":\"https://e.com/b\"}", 0);
    _bugsee_channel_network("not json", 0);
    _bugsee_channel_network(NULL, 0);
    expect(channel.events.count == 2, "a channel network event is sent per valid JSON; bad JSON and NULL send nothing");
    BugseeNetworkEvent *sent = channel.events[0];
    expect([sent.ID isEqualToString:@"n1"] && [sent.url isEqualToString:@"https://e.com/a"] && [sent.method isEqualToString:@"POST"]
               && sent.responseCode == 201 && [[[NSString alloc] initWithData:sent.body encoding:NSUTF8StringEncoding] isEqualToString:@"b"],
           "the network event holds the id, url, method, response code and body");
    expect([channel.filtering[0] boolValue] == YES && [channel.filtering[1] boolValue] == NO, "requiresFiltering is passed through");

    _bugsee_channel_breadcrumb("cat", "msg", 3, 1700000000.0);
    _bugsee_channel_breadcrumb(NULL, NULL, 2, 0);
    expect(channel.crumbs.count == 2, "each breadcrumb is sent");
    id<BGSBreadcrumb> crumb = channel.crumbs[0];
    expect([crumb.category isEqualToString:@"cat"] && [crumb.message isEqualToString:@"msg"] && (int)crumb.level == 3
               && [crumb.type isEqualToString:@"manual"] && crumb.timestamp == 1700000000.0,
           "a breadcrumb holds the category, message, level, manual type and the given timestamp");
    crumb = channel.crumbs[1];
    expect([crumb.category isEqualToString:@""] && [crumb.message isEqualToString:@""] && (int)crumb.level == 2
               && fabs(crumb.timestamp - [[NSDate date] timeIntervalSince1970]) < 60,
           "NULL texts are empty and no timestamp is now");
    // The channel the SDK drops events on while it is not running is the real one; this fake only proves what the bridge sends.
    [wrapper onWrapperChannelAvailable:(id<BGSWrapperChannel>)[FakeChannel new]];

    // Filters. The bridge hands the SDK a block per kind; the test runs that same block with a recording decision block
    // (the SDK cannot raise the event without a launch). C# gets the event as JSON and answers with
    // _bugsee_complete_filter; the answer is what the decision block receives.
    // Not asserted: that [Bugsee setNetworkEventFilter:] and its two siblings were called with the block (one line each).
    _bugsee_register_unity_callbacks(OnFilter, NULL, NULL);
    _bugsee_set_network_filter_enabled(1);
    int pendingBefore = _bugsee_test_pending_request_count();
    expect(_bugsee_test_run_filter(1, "{\"id\":\"e1\",\"url\":\"https://e.com/secret\",\"method\":\"GET\"}") == 0
               && gFilterCalls == 1 && gFilterKind == 1 && _bugsee_test_pending_request_count() == pendingBefore + 1,
           "an enabled network filter hands C# the event and waits for the answer");
    expect([parse(gFilterJson)[@"url"] isEqualToString:@"https://e.com/secret"] && [parse(gFilterJson)[@"id"] isEqualToString:@"e1"],
           "the network event reaches C# as JSON");
    int64_t id1 = gFilterId;
    _bugsee_complete_filter(id1, 1, "{\"url\":\"https://e.com/redacted\"}");
    expect(_bugsee_test_filter_decision_count() == 1 && [[parse(decision()) objectForKey:@"url"] isEqualToString:@"https://e.com/redacted"],
           "a kept network event carries the changes C# made");
    expect(_bugsee_test_pending_request_count() == pendingBefore, "the answered request is no longer pending");
    _bugsee_complete_filter(id1, 0, NULL);
    expect(_bugsee_test_filter_decision_count() == 1, "a second answer for the same request does nothing");

    _bugsee_test_run_filter(1, "{\"url\":\"https://e.com/x\"}");
    _bugsee_complete_filter(gFilterId, 0, NULL);
    expect(_bugsee_test_filter_decision_count() == 1 && [decision() isEqualToString:@"null"], "a network event C# drops is dropped");

    _bugsee_test_run_filter(1, "{\"url\":\"https://e.com/y\"}");
    _bugsee_complete_filter(gFilterId, 1, NULL);
    expect([[parse(decision()) objectForKey:@"url"] isEqualToString:@"https://e.com/y"], "a kept network event without changes passes unchanged");

    _bugsee_set_log_filter_enabled(1);
    gFilterCalls = 0;
    expect(_bugsee_test_run_filter(2, "{\"message\":\"card 4111\",\"level\":4}") == 0 && gFilterCalls == 1 && gFilterKind == 2
               && [parse(gFilterJson)[@"message"] isEqualToString:@"card 4111"] && [parse(gFilterJson)[@"level"] intValue] == 4,
           "an enabled log filter hands C# the text and level");
    _bugsee_complete_filter(gFilterId, 1, "{\"message\":\"card ****\"}");
    expect([[parse(decision()) objectForKey:@"message"] isEqualToString:@"card ****"] && [[parse(decision()) objectForKey:@"level"] intValue] == 4,
           "a kept log line carries the new text and keeps its level");
    _bugsee_test_run_filter(2, "{\"message\":\"drop me\",\"level\":3}");
    _bugsee_complete_filter(gFilterId, 0, NULL);
    expect([decision() isEqualToString:@"null"], "a log line C# drops is dropped");

    _bugsee_set_breadcrumb_filter_enabled(1);
    gFilterCalls = 0;
    expect(_bugsee_test_run_filter(3, "{\"category\":\"nav\",\"message\":\"open\",\"level\":3}") == 0 && gFilterCalls == 1 && gFilterKind == 3
               && [parse(gFilterJson)[@"category"] isEqualToString:@"nav"] && [parse(gFilterJson)[@"type"] isEqualToString:@"manual"],
           "an enabled breadcrumb filter hands C# the breadcrumb");
    _bugsee_complete_filter(gFilterId, 1, "{\"message\":\"opened\"}");
    expect([[parse(decision()) objectForKey:@"message"] isEqualToString:@"opened"] && [[parse(decision()) objectForKey:@"category"] isEqualToString:@"nav"],
           "a kept breadcrumb carries the new message and keeps its category");
    _bugsee_test_run_filter(3, "{\"category\":\"nav\",\"message\":\"x\",\"level\":3}");
    _bugsee_complete_filter(gFilterId, 0, NULL);
    expect([decision() isEqualToString:@"null"], "a breadcrumb C# drops is dropped");

    // Disabled: the event passes straight through and C# is not asked.
    gFilterCalls = 0;
    _bugsee_set_network_filter_enabled(0);
    _bugsee_set_log_filter_enabled(0);
    _bugsee_set_breadcrumb_filter_enabled(0);
    expect(_bugsee_test_run_filter(1, "{\"url\":\"https://e.com/z\"}") == 1 && [[parse(decision()) objectForKey:@"url"] isEqualToString:@"https://e.com/z"],
           "a disabled network filter passes the event through");
    expect(_bugsee_test_run_filter(2, "{\"message\":\"keep\",\"level\":3}") == 1 && [[parse(decision()) objectForKey:@"message"] isEqualToString:@"keep"],
           "a disabled log filter passes the line through");
    expect(_bugsee_test_run_filter(3, "{\"category\":\"c\",\"message\":\"m\",\"level\":3}") == 1 && [[parse(decision()) objectForKey:@"message"] isEqualToString:@"m"],
           "a disabled breadcrumb filter passes the breadcrumb through");
    expect(gFilterCalls == 0, "a disabled filter never asks C#");

    // Enabled but C# has registered nothing: the event is dropped (a filter must not let an unredacted event through).
    _bugsee_register_unity_callbacks(NULL, NULL, NULL);
    _bugsee_set_network_filter_enabled(1);
    _bugsee_set_log_filter_enabled(1);
    _bugsee_set_breadcrumb_filter_enabled(1);
    expect(_bugsee_test_run_filter(1, "{\"url\":\"https://e.com/q\"}") == 1 && [decision() isEqualToString:@"null"], "no callback: a network event is dropped");
    expect(_bugsee_test_run_filter(2, "{\"message\":\"q\",\"level\":3}") == 1 && [decision() isEqualToString:@"null"], "no callback: a log line is dropped");
    expect(_bugsee_test_run_filter(3, "{\"category\":\"c\",\"message\":\"q\",\"level\":3}") == 1 && [decision() isEqualToString:@"null"], "no callback: a breadcrumb is dropped");
    _bugsee_set_network_filter_enabled(0);
    _bugsee_set_log_filter_enabled(0);
    _bugsee_set_breadcrumb_filter_enabled(0);

    // Answers for request ids nobody is waiting on are safe (smoke): nothing is pending, so nothing is applied.
    int pendingNow = _bugsee_test_pending_request_count();
    _bugsee_complete_filter(9999, 1, "{}");
    _bugsee_complete_filter(9999, 0, NULL);
    _bugsee_complete_report(9999, "{}");
    _bugsee_complete_report(9999, NULL);
    _bugsee_complete_data_request(9999, NULL);
    expect(_bugsee_test_pending_request_count() == pendingNow, "answers for unknown request ids change nothing");

    printf("ios bridge export tests passed\n");
    return 0;
}
