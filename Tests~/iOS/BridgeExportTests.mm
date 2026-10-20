// Second bridge test binary (run by run-tests.sh after BridgeTests.mm): drives the exports BridgeTests.mm does not,
// through the same C ABI the C# DllImports use, against the real SDK slice with no launch. Where the SDK has an
// observable state the test checks it; the rest are smoke calls: they must link, take the argument shapes C# passes
// (NULL strings, short buffers, unknown request ids) and return without crashing, and assert nothing else. The
// prototypes below are checked against the real exports by NativeBindingTests; IosExportCoverageTests fails when a
// DllImported export is not called from one of the Tests~/iOS/*.mm files.
#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <Bugsee/Bugsee.h>

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
void _bugsee_feedback_show(void);
void _bugsee_feedback_set_greeting(const char *message);
void _bugsee_free(char *ptr);
void _bugsee_invalidate_managed_report_uploads(void);
void _bugsee_upload_managed_report(const char *reportJson, uint64_t uploadId, uint64_t uploadFence,
                                   BugseeManagedReportCreateCallback callback);
void _bugsee_set_secure_buffer(int display, int *packed, int packedLength);
void _bugsee_channel_log(const char *message, int level, int source);
void _bugsee_channel_network(const char *eventJson, int requiresFiltering);
void _bugsee_channel_breadcrumb(const char *category, const char *message, int iosLevel, double timestampUnixSeconds);
void _bugsee_set_network_filter_enabled(int enabled);
void _bugsee_set_log_filter_enabled(int enabled);
void _bugsee_set_breadcrumb_filter_enabled(int enabled);
void _bugsee_complete_filter(int64_t requestId, int keep, const char *resultJson);
void _bugsee_complete_report(int64_t requestId, const char *resultJson);
void _bugsee_complete_data_request(int64_t requestId, const char *json);
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

    // User identifier ("email" on the C# side). The SDK keeps it in the keychain, which a bare simulator process
    // cannot write, so a read-back is not checked here (the device e2e scenarios do): the calls must only be safe,
    // and the getter must return a string that _bugsee_free accepts, or NULL.
    _bugsee_clear_email();
    _bugsee_set_email("user@example.com");
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
    _bugsee_invalidate_managed_report_uploads();

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

    // Secure rect buffer (smoke only, nothing reads the stored buffer back): short or NULL buffers must be ignored and a
    // packed one accepted for any display.
    int packed[] = { 1, 0, 0, 10, 10 };
    _bugsee_set_secure_buffer(0, NULL, 5);
    _bugsee_set_secure_buffer(0, packed, 1);
    _bugsee_set_secure_buffer(0, packed, 5);
    _bugsee_set_secure_buffer(1, packed, 5);

    // Wrapper channel with no channel attached (no launch): every producer must return quietly (smoke only).
    _bugsee_channel_log("msg", 3, 1);
    _bugsee_channel_log(NULL, 3, 1);
    _bugsee_channel_network("{\"id\":\"n\",\"url\":\"u\"}", 1);
    _bugsee_channel_network("not json", 0);
    _bugsee_channel_network(NULL, 0);
    _bugsee_channel_breadcrumb("cat", "msg", 3, 0);
    _bugsee_channel_breadcrumb(NULL, NULL, 3, 1700000000.0);

    // Filters (smoke only): enabling and disabling in any order, and answers for request ids nobody is waiting on, must be
    // safe. Whether the SDK hook is installed or an answer is applied needs an SDK-triggered event (follow-up #206).
    _bugsee_set_network_filter_enabled(1);
    _bugsee_set_log_filter_enabled(1);
    _bugsee_set_breadcrumb_filter_enabled(1);
    _bugsee_complete_filter(9999, 1, "{}");
    _bugsee_complete_filter(9999, 0, NULL);
    _bugsee_complete_report(9999, "{}");
    _bugsee_complete_report(9999, NULL);
    _bugsee_complete_data_request(9999, NULL);
    _bugsee_set_network_filter_enabled(0);
    _bugsee_set_log_filter_enabled(0);
    _bugsee_set_breadcrumb_filter_enabled(0);
    _bugsee_set_network_filter_enabled(0);

    printf("ios bridge export tests passed\n");
    return 0;
}
