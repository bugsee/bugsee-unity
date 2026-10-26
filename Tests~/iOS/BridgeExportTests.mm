// Second bridge test binary (run by run-tests.sh after BridgeTests.mm): drives the exports BridgeTests.mm does not,
// through the same C ABI the C# DllImports use, against the real SDK slice with no launch. Where the SDK has an
// observable state the test checks it (a fake wrapper channel, the filter blocks the bridge hands the SDK, the secure
// rect buffer the wrapper answers the SDK with, the managed upload's end, the hand-offs to the SDK's filter setters and report creation); the rest are smoke calls, named in place:
// they must link, take the argument shapes C# passes (NULL strings, short buffers, unknown request ids) and return
// without crashing, and assert nothing else. The prototypes below are checked against the real exports by NativeBindingTests; IosExportCoverageTests fails when a
// DllImported export is not called from one of the Tests~/iOS/*.mm files.
#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <Bugsee/Bugsee.h>
#import <Security/Security.h>
#include <dlfcn.h>
#import <objc/runtime.h>

extern "C" {
typedef void (*BugseeManagedReportCreateCallback)(int succeeded, uint64_t uploadToken);
typedef void (*BugseeUnityFilterCb)(int64_t requestId, int kind, const char *json);

int _bugsee_get_status(void);
void _bugsee_log(const char *message, int level, int requiresFiltering);
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
char *_bugsee_build_token_fingerprint(void);
float _bugsee_pixels_per_point(int unityPixelWidth);
void _bugsee_capture_view_hierarchy(void);
int _bugsee_add_secure_web_view(void *view);
int _bugsee_remove_secure_web_view(void *view);
int _bugsee_feedback_available(void);
void _bugsee_feedback_show(void);
typedef void (*BugseeFeedbackReceivedFn)(const char *messagesJson);
typedef void (*BugseeFeedbackSentFn)(const char *message);
void _bugsee_feedback_set_listener(BugseeFeedbackReceivedFn received, BugseeFeedbackSentFn sent);
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
char *_bugsee_report_screenshot_ids(int64_t requestId);
int _bugsee_report_screenshot_png(int64_t requestId, int displayId, unsigned char **outBytes);
int _bugsee_report_set_screenshot(int64_t requestId, int displayId, const unsigned char *bytes, int length);
int _bugsee_report_screenshot_changed(int64_t requestId);
int64_t _bugsee_test_register_screenshot_report(void);
int64_t _bugsee_test_register_odd_screenshot_report(void);
void _bugsee_complete_data_request(int64_t requestId, const char *json);
void _bugsee_register_unity_callbacks(BugseeUnityFilterCb filterCb, void *reportCb, void *lifecycleCb);

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

// SDK hand-offs. The bridge's calls into the SDK that need a launched SDK to do anything observable are checked by
// swapping the SDK's class method for a recorder for the length of a check (the real method is put back afterwards).
// The swap fails the test, rather than silently skipping it, when the SDK no longer has the method.
static void withClassMethodReplaced(Class cls, SEL selector, id block, void (^body)(void))
{
    Method method = class_getClassMethod(cls, selector);
    expect(method != NULL, "the SDK class method the bridge calls exists");
    IMP original = method_getImplementation(method);
    method_setImplementation(method, imp_implementationWithBlock(block));
    body();
    method_setImplementation(method, original);
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

    // User identifier ("email" on the C# side). The SDK keeps it in the keychain. A bare simulator process cannot use it
    // (SecItemAdd fails with errSecMissingEntitlement, -34018), so on CI the round trip below is skipped and the email
    // exports are effectively unasserted there (the device e2e scenarios assert them). Where the keychain works (probed
    // with a throwaway item of our own) the round trip is required.
    NSDictionary *probe = @{ (__bridge id)kSecClass: (__bridge id)kSecClassGenericPassword,
                             (__bridge id)kSecAttrService: @"com.bugsee.unity.tests.probe",
                             (__bridge id)kSecAttrAccount: @"probe" };
    NSMutableDictionary *probeAdd = [probe mutableCopy];
    probeAdd[(__bridge id)kSecValueData] = [@"x" dataUsingEncoding:NSUTF8StringEncoding];
    SecItemDelete((__bridge CFDictionaryRef)probe); // a stale item from an earlier run must not read as a failure
    OSStatus probeStatus = SecItemAdd((__bridge CFDictionaryRef)probeAdd, NULL);
    if (probeStatus == errSecSuccess) SecItemDelete((__bridge CFDictionaryRef)probe);
    _bugsee_clear_email();
    _bugsee_set_email("user@example.com");
    if (probeStatus == errSecMissingEntitlement) {
        printf("note: no keychain entitlement in this process (%d), email read-back skipped\n", (int)probeStatus);
    } else {
        expect(probeStatus == errSecSuccess, "the keychain probe either works or lacks the entitlement");
        expect([takeString(_bugsee_get_email()) isEqualToString:@"user@example.com"], "a set email reads back");
        _bugsee_set_email("other@example.com");
        expect([takeString(_bugsee_get_email()) isEqualToString:@"other@example.com"], "a second email replaces the first");
        _bugsee_clear_email();
        expect([takeString(_bugsee_get_email()) isEqualToString:@"(null)"], "clear_email removes the email");
    }
    _bugsee_clear_email();
    _bugsee_set_email(NULL);
    _bugsee_free(_bugsee_get_email());
    _bugsee_clear_email();
    expect(1, "the email exports link and run");

    // The build token fingerprint is the Info.plist key the Xcode Run Script writes; the test host's plist has none.
    expect(_bugsee_build_token_fingerprint() == NULL, "no recorded build token fingerprint reads back as NULL");

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
    expect([finishUpload("{\"summary\":\"S\",\"labels\":[\"a\",\"b\"]}", 1, 5, fence) isEqualToString:@"cb1:5,up|S|a,b"],
           "an active upload reports success with its token, then uploads the report with the JSON's fields");
    expect([finishUpload("{}", 0, 5, fence) isEqualToString:@"cb0:5|-"], "no report made: a failed create, nothing uploaded");
    expect([finishUpload("not json", 1, 5, fence) isEqualToString:@"cb0:5|-"], "an unreadable report JSON fails and uploads nothing");
    expect([finishUpload("{}", 1, 0, fence) isEqualToString:@"cb0:0|-"], "upload id 0 is never active");
    expect([finishUpload("{}", 1, 5, fence + 1) isEqualToString:@"cb0:5|-"], "a fence from another generation fails and uploads nothing");
    _bugsee_invalidate_managed_report_uploads();
    expect(_bugsee_test_managed_upload_fence() == fence + 1, "invalidating moves the fence");
    expect([finishUpload("{}", 1, 5, fence) isEqualToString:@"cb0:5|-"], "an upload started before the invalidation fails and uploads nothing");
    expect([finishUpload("{}", 1, 5, fence + 1) isEqualToString:@"cb1:5,up|-|-"], "an upload started after the invalidation goes through");
    // Not asserted: the two later fence re-checks (after the report fields are applied, and just before the upload) only
    // matter when an invalidation lands in between, a race this single-threaded test cannot stage. The hand-off to the SDK
    // (createReportWithCompletion:, uploadReport:) is checked near the end with the SDK's methods swapped for recorders.

    // Logging and telemetry with no running SDK, including the NULL and non-object payloads C# can send (smoke only: the
    // calls have no observable effect before a launch). The Feedback add-on is not linked into this test, so the feedback
    // exports find nothing to call: available is 0 and show / greeting / listener are safe no-ops.
    _bugsee_log("hello", 3, 0);
    _bugsee_log("hello", 3, 1);
    _bugsee_log(NULL, 3, 1);
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
    expect(_bugsee_feedback_available() == 0, "Feedback is not available without the BugseeFeedback package");
    _bugsee_feedback_set_listener(NULL, NULL);
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
    int second[] = { 1, 1, 5, 5, 20, 20 };
    _bugsee_set_secure_buffer(0, second, 6);
    expect([[wrapper secureRectanglesForDisplay:0] isEqualToData:[NSData dataWithBytes:second length:sizeof(second)]]
               && [[wrapper secureRectanglesForDisplay:1] isEqualToData:baseline],
           "a later buffer replaces the earlier one for its display only");
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
    // (Producers called with no channel attached are smoke only: there is nothing to observe.)

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
    // The bridge keeps whatever channel it was last handed and has no getter to restore the earlier one, so a second fake
    // stays installed for the rest of the process (nothing after this sends through the channel).
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

    // Report screenshots: unknown request ids are refused, a live report's screenshot reads, replaces and removes.
    unsigned char *none = (unsigned char *)1;
    expect(_bugsee_report_screenshot_ids(9999) == NULL, "screenshot ids: an unknown request has none");
    expect(_bugsee_report_screenshot_png(9999, 0, &none) == -1 && none == NULL, "screenshot png: an unknown request is refused");
    expect(_bugsee_report_set_screenshot(9999, 0, NULL, 0) == -1, "set screenshot: an unknown request is refused");
    expect(_bugsee_report_screenshot_changed(9999) == -1, "screenshot changed: an unknown request is refused");
    int64_t live = _bugsee_test_register_screenshot_report();
    char *ids = _bugsee_report_screenshot_ids(live);
    expect(ids != NULL && strcmp(ids, "[0]") == 0, "screenshot ids: a live report lists its displays");
    _bugsee_free(ids);
    expect(_bugsee_report_screenshot_changed(live) == 0, "screenshot changed: false before any edit");
    unsigned char *png = NULL;
    int pngLength = _bugsee_report_screenshot_png(live, 0, &png);
    expect(pngLength > 8 && png != NULL && png[0] == 0x89 && png[1] == 'P' && png[2] == 'N' && png[3] == 'G', "screenshot png: a live report's screenshot reads as PNG");
    expect(_bugsee_report_screenshot_png(live, 7, &none) == 0 && none == NULL, "screenshot png: a display without one reads as empty");
    expect(_bugsee_report_set_screenshot(live, 1, png, pngLength) == 1, "set screenshot: a PNG is applied to another display");
    expect(_bugsee_report_screenshot_changed(live) == 0, "screenshot changed: still false after adding a screenshot to an empty display");
    const unsigned char junk[] = { 1, 2, 3 };
    expect(_bugsee_report_set_screenshot(live, 2, junk, 3) == 0, "set screenshot: bytes that are not an image change nothing");
    ids = _bugsee_report_screenshot_ids(live);
    expect(ids != NULL && strcmp(ids, "[0,1]") == 0, "screenshot ids: the replacement is listed, the refused one is not");
    _bugsee_free(ids);
    expect(_bugsee_report_set_screenshot(live, 1, png, pngLength) == 1, "set screenshot: a PNG replaces the one on display 1");
    expect(_bugsee_report_screenshot_changed(live) == 1, "screenshot changed: true after replacing a held screenshot");
    expect(_bugsee_report_set_screenshot(live, 0, NULL, 0) == 1, "set screenshot: NULL removes the display's screenshot");
    ids = _bugsee_report_screenshot_ids(live);
    expect(ids != NULL && strcmp(ids, "[1]") == 0, "screenshot ids: the removed display is gone");
    _bugsee_free(ids);
    _bugsee_free((char *)png);
    _bugsee_complete_report(live, "{}");
    expect(_bugsee_report_screenshot_ids(live) == NULL && _bugsee_report_set_screenshot(live, 0, NULL, 0) == -1, "screenshots: nothing is reachable once the handler has answered");

    // An image with no CGImage is redrawn before it is encoded; one with nothing to encode is refused as -2, not read as absent.
    int64_t odd = _bugsee_test_register_odd_screenshot_report();
    unsigned char *oddPng = NULL;
    int oddLength = _bugsee_report_screenshot_png(odd, 0, &oddPng);
    expect(oddLength > 8 && oddPng != NULL && oddPng[0] == 0x89 && oddPng[1] == 'P', "screenshot png: a CIImage-backed screenshot is redrawn and encodes as PNG");
    _bugsee_free((char *)oddPng);
    unsigned char *emptyPng = (unsigned char *)1;
    expect(_bugsee_report_screenshot_png(odd, 1, &emptyPng) == -2 && emptyPng == NULL, "screenshot png: a screenshot that cannot be encoded is -2, not absent");
    _bugsee_complete_report(odd, "{}");

    // SDK hand-offs. Enabling a filter gives the SDK a non-nil filter block through its setter; disabling it does not
    // touch the SDK (the installed block then passes events through, asserted above).
    {
        __block int setterCalls = 0;
        __block id handed = nil;
        id recorder = ^(id self_, id block) { setterCalls++; handed = block; };
        SEL selectors[3] = { @selector(setNetworkEventFilter:), @selector(setLogEventFilter:), @selector(setBreadcrumbFilter:) };
        void (*enable[3])(int) = { _bugsee_set_network_filter_enabled, _bugsee_set_log_filter_enabled, _bugsee_set_breadcrumb_filter_enabled };
        const char *names[3] = { "network", "log", "breadcrumb" };
        for (int i = 0; i < 3; i++) {
            void (*toggle)(int) = enable[i];
            setterCalls = 0;
            handed = nil;
            withClassMethodReplaced([Bugsee class], selectors[i], recorder, ^{
                toggle(1);
                expect(setterCalls == 1 && handed != nil, "enabling a filter hands the SDK a filter block");
                toggle(0);
                expect(setterCalls == 1, "disabling a filter does not call the SDK again");
            });
            printf("note: %s filter hand-off checked\n", names[i]);
        }
    }

    // _bugsee_upload_managed_report: the bridge asks the SDK for a report, applies the JSON's fields to it, reports success
    // and uploads that same report.
    {
        __block int createCalls = 0;
        __block int uploadCalls = 0;
        __block id uploaded = nil;
        __block BugseeExtendedReport *made = nil;
        __block BOOL makeReport = YES;
        id create = ^(id self_, void (^completion)(BugseeExtendedReport *)) {
            createCalls++;
            made = makeReport ? [BugseeExtendedReport new] : nil;
            completion(made);
        };
        id upload = ^(id self_, BugseeExtendedReport *report, id completion) { uploadCalls++; uploaded = report; };
        withClassMethodReplaced([Bugsee class], @selector(createReportWithCompletion:), create, ^{
            withClassMethodReplaced([Bugsee class], @selector(uploadReport:completion:), upload, ^{
                uint64_t uploadFence = _bugsee_test_managed_upload_fence();
                gUploadCalls = 0;
                _bugsee_upload_managed_report("{\"summary\":\"S\"}", 77, uploadFence, OnUploadCreated);
                expect(createCalls == 1 && uploadCalls == 1 && uploaded == made && [made.summary isEqualToString:@"S"],
                       "the upload asks the SDK for one report, fills it and uploads that report");
                expect(gUploadCalls == 1 && gUploadSucceeded == 1 && gUploadToken == 77, "the upload reports success with its token");

                gUploadCalls = 0;
                makeReport = NO;
                _bugsee_upload_managed_report("{}", 78, uploadFence, OnUploadCreated);
                expect(createCalls == 2 && uploadCalls == 1 && gUploadCalls == 1 && gUploadSucceeded == 0 && gUploadToken == 78,
                       "the SDK making no report fails the upload and uploads nothing");
                makeReport = YES;

                gUploadCalls = 0;
                _bugsee_upload_managed_report("{}", 79, uploadFence + 1, OnUploadCreated);
                expect(uploadCalls == 1 && gUploadCalls == 1 && gUploadSucceeded == 0 && gUploadToken == 79,
                       "an upload from another generation fails and uploads nothing");
            });
        });
    }

    printf("ios bridge export tests passed\n");
    return 0;
}
