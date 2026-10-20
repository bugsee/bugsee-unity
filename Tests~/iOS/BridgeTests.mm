#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <locale.h>
#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <Bugsee/Bugsee.h>

extern "C" {
void _bugsee_set_wrapper_context(const char *json);
void _bugsee_ensure_wrapper(const char *version, const char *build);
const char *_bugsee_test_copy_wrapper_version(void);
const char *_bugsee_test_copy_wrapper_build(void);
int _bugsee_test_wrapper_installed(void);
const char *_bugsee_test_apply_breadcrumb_result(const char *startDataJson, const char *resultJson);
const char *_bugsee_test_copy_context_value(const char *key);
const char *_bugsee_test_apply_network_result(const char *startJson, const char *resultJson);
typedef void (*BugseeDataRequestFn)(int64_t requestId, const char *dataType);
void _bugsee_register_data_request_callback(BugseeDataRequestFn callback);
void _bugsee_complete_data_request(int64_t requestId, const char *json);
int _bugsee_test_request_data(const char *type);
int _bugsee_test_data_reply_count(void);
const char *_bugsee_test_copy_data_reply(void);
float _bugsee_pixels_per_point(int unityPixelWidth);
float _bugsee_screen_scale(void);
const char *_bugsee_test_apply_report_result(const char *startAttributesJson, const char *resultJson);
const char *_bugsee_test_report_json_with_unserializable_attributes(const char *startAttributesJson);
void _bugsee_set_attribute(const char *key, const char *valueJson);
char *_bugsee_get_attribute(const char *key);
void _bugsee_clear_attribute(const char *key);
char *_bugsee_get_launch_options(void);
int _bugsee_test_default_bug_severity(void);
const char *_bugsee_test_last_report_overload(void);
int _bugsee_test_last_report_severity(void);
void _bugsee_show_report(const char *summary, const char *description, int severity, const char *labelsJson);
void _bugsee_upload(const char *summary, const char *description, int severity, const char *labelsJson);
void _bugsee_notify(const char *title, const char *body, int severity, const char *fieldsJson, int urgent);
int64_t _bugsee_start_transaction(const char *name, const char *operation, const char *attributesJson);
int64_t _bugsee_span_start_child(int64_t spanId, const char *operation, const char *description);
char *_bugsee_span_get_id(int64_t spanId);
char *_bugsee_span_get_trace_id(int64_t spanId);
void _bugsee_span_set_name(int64_t spanId, const char *name);
void _bugsee_span_set_description(int64_t spanId, const char *description);
void _bugsee_span_set_attribute(int64_t spanId, const char *key, const char *valueJson);
void _bugsee_span_set_status(int64_t spanId, int status);
void _bugsee_span_finish(int64_t spanId, int status);
void _bugsee_span_release(int64_t spanId);
int _bugsee_appearance_set_color(const char *propertyName, int r, int g, int b, int a);
char *_bugsee_appearance_get_color(const char *propertyName);
int _bugsee_appearance_set_string(const char *propertyName, const char *propertyValue);
char *_bugsee_appearance_get_string(const char *propertyName);
typedef void (*BugseeLaunchStartedFn)(int token, int success);
void _bugsee_launch(const char *appToken, const char *optionsJson, int startedToken, BugseeLaunchStartedFn started);
void _bugsee_relaunch(const char *optionsJson, int startedToken, BugseeLaunchStartedFn started);
typedef void (*BugseeLifecycleFn)(const char *eventType, const char *dataJson);
void _bugsee_test_emit_lifecycle(const char *eventType, const char *payloadJson);
void _bugsee_register_unity_callbacks(void *filterCb, void *reportCb, BugseeLifecycleFn lifecycleCb);
typedef void (*BugseeStopCompletionFn)(int token);
void _bugsee_stop(int token, BugseeStopCompletionFn completion);
void _bugsee_start_blackout(void);
void _bugsee_end_blackout(void);
int _bugsee_is_blackout(void);
typedef void (*BugseeDeleteCompletionFn)(int token, int wiped);
void _bugsee_delete_collected_data(int token, BugseeDeleteCompletionFn completion);
const char *_bugsee_test_exception_options(const char *optionsJson);
int _bugsee_test_simulator_warning_count(void);
int _bugsee_exceptions_filed(void);
void _bugsee_logException(const char *name, const char *reason, bool handled, const char *optionsJson);
int _bugsee_exception_options_dropped(void);
}

extern "C" int _bugsee_test_dropped_options_warning_count(void);

static int gStartedToken;
static int gStartedCount;
static int gStoppedToken;

static void OnStarted(int token, int success)
{
    (void)success;
    gStartedToken = token;
    gStartedCount++;
}

static NSMutableArray<NSString *> *gLifecycleEvents;

static void OnLifecycle(const char *eventType, const char *dataJson)
{
    // Real SDK events (from the launch above) also arrive here, on a worker thread: keep only this test's.
    if (strncmp(eventType, "e.", 2) != 0) return;
    @synchronized (gLifecycleEvents) {
        [gLifecycleEvents addObject:[NSString stringWithFormat:@"%s|%s", eventType, dataJson ? dataJson : "(null)"]];
    }
}

static NSArray<NSString *> *lifecycleEvents(void)
{
    @synchronized (gLifecycleEvents) {
        return [gLifecycleEvents copy];
    }
}

static int gDeletedToken;
static int gDeletedWiped = -1;

static void OnDeleted(int token, int wiped)
{
    gDeletedToken = token;
    gDeletedWiped = wiped;
}

static void OnStopped(int token)
{
    gStoppedToken = token;
}

// Stands in for C#'s data request callback: records the ids and types it is asked, answers inline when told to.
static int64_t gDataRequestIds[8];
static char gDataRequestTypes[8][16];
static int gDataRequestCalls;
static const char *gDataRequestInlineAnswer;

static void OnDataRequest(int64_t requestId, const char *dataType)
{
    if (gDataRequestCalls < 8) {
        gDataRequestIds[gDataRequestCalls] = requestId;
        strncpy(gDataRequestTypes[gDataRequestCalls], dataType ? dataType : "", 15);
    }
    gDataRequestCalls++;
    if (gDataRequestInlineAnswer) _bugsee_complete_data_request(requestId, gDataRequestInlineAnswer);
}

// Spins the main run loop until the condition holds or the time is up.
static int waitFor(int (^condition)(void), double seconds)
{
    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:seconds];
    while (!condition() && [deadline timeIntervalSinceNow] > 0) {
        [[NSRunLoop mainRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.05]];
    }
    return condition();
}

static void expect(int condition, const char *message)
{
    if (!condition) {
        fprintf(stderr, "FAIL %s\n", message);
        exit(1);
    }
    printf("ok %s\n", message);
}

// True when the last data reply was nil (the copy is freed, so a check does not leak it).
static int dataReplyIsNil(void)
{
    const char *reply = _bugsee_test_copy_data_reply();
    int isNil = reply == NULL;
    free((void *)reply);
    return isNil;
}

static int same(const char *value, const char *expected)
{
    if (value == NULL || expected == NULL) {
        return value == expected;
    }
    return strcmp(value, expected) == 0;
}

int main(void)
{
    const char *value;

    expect(_bugsee_test_wrapper_installed() == 1, "load registers one wrapper");

    // Nothing has started a blackout yet, and one requested before any launch is kept, not lost.
    expect(_bugsee_is_blackout() == 0, "no blackout is reported before one is started");
    _bugsee_start_blackout();
    expect(_bugsee_is_blackout() == 1, "a blackout started before the first launch is reported");
    _bugsee_end_blackout();
    expect(_bugsee_is_blackout() == 0, "a blackout ended before the first launch is gone");

    _bugsee_set_wrapper_context("{\"unity_version\":\"2021.3\",\"count\":1}");
    value = _bugsee_test_copy_context_value("unity_version");
    expect(same(value, "2021.3"), "context keeps string values");
    free((void *)value);
    expect(_bugsee_test_copy_context_value("count") == NULL, "context drops non-strings");

    _bugsee_set_wrapper_context("not-json");
    value = _bugsee_test_copy_context_value("unity_version");
    expect(same(value, "2021.3"), "invalid json keeps the previous context");
    free((void *)value);

    _bugsee_set_wrapper_context(NULL);
    expect(_bugsee_test_copy_context_value("unity_version") == NULL, "null json clears context");

    _bugsee_ensure_wrapper("9.9.9", "ci");
    value = _bugsee_test_copy_wrapper_version();
    expect(same(value, "9.9.9"), "ensure wrapper stores the version");
    free((void *)value);
    value = _bugsee_test_copy_wrapper_build();
    expect(same(value, "ci"), "ensure wrapper stores the build");
    free((void *)value);
    // "dev" (the placeholder of every checkout and release), empty and NULL are no build, not a build id.
    const char *noBuild[] = { "dev", "", NULL };
    for (int i = 0; i < 3; i++) {
        _bugsee_ensure_wrapper("9.9.9", "ci");
        _bugsee_ensure_wrapper("9.9.9", noBuild[i]);
        value = _bugsee_test_copy_wrapper_build();
        expect(value == NULL, "a placeholder build is no build");
        free((void *)value);
    }

    // Network filter results: native applies only the keys present, a JSON null clears, and an untouched event keeps
    // a nil error / body / url (the wrapper omits what the filter did not change).
    const char *netStart = "{\"id\":\"n1\",\"url\":\"host-a\",\"method\":\"GET\",\"stage\":\"complete\",\"responseCode\":200}";
    value = _bugsee_test_apply_network_result(netStart, "{\"stage\":\"complete\"}");
    expect(strstr(value, "\"error\":null") && strstr(value, "\"body\":null") && strstr(value, "\"url\":\"host-a\""),
           "a no-change network result leaves error and body nil and the url as it was");
    free((void *)value);
    value = _bugsee_test_apply_network_result("{\"url\":null,\"stage\":\"complete\"}", "{\"stage\":\"complete\"}");
    expect(strstr(value, "\"url\":null") && strstr(value, "\"error\":null"), "a no-change result keeps a nil url nil");
    free((void *)value);
    value = _bugsee_test_apply_network_result(netStart, "{\"stage\":\"complete\",\"url\":\"host-redacted\",\"headers\":{\"Authorization\":\"x\"}}");
    expect(strstr(value, "\"url\":\"host-redacted\"") && strstr(value, "\"Authorization\":\"x\"") && strstr(value, "\"error\":null"),
           "a deliberate url and headers change applies and leaves the error nil");
    free((void *)value);
    value = _bugsee_test_apply_network_result(netStart, "{\"url\":null,\"body\":\"\",\"errorShortMessage\":\"boom\"}");
    expect(strstr(value, "\"url\":null") && strstr(value, "\"body\":\"\"") && strstr(value, "\"name\":\"boom\""),
           "an explicit null url, empty body and error name apply");
    free((void *)value);
    value = _bugsee_test_apply_network_result(
        "{\"url\":\"host-a\",\"body\":\"secret\",\"errorShortMessage\":\"e\",\"errorDescription\":\"d\"}",
        "{\"body\":null,\"errorShortMessage\":null,\"errorDescription\":null}");
    expect(strstr(value, "\"body\":null") && strstr(value, "\"error\":null"), "an explicit null clears the body and the whole error");
    free((void *)value);
    // Empty error strings remove the key like null: a filter clearing an error with "" must not leave {name:""} behind.
    value = _bugsee_test_apply_network_result(netStart, "{\"url\":\"\",\"errorShortMessage\":\"\",\"errorDescription\":\"\"}");
    expect(strstr(value, "\"error\":null") && strstr(value, "\"url\":\"\""), "empty error strings leave no error and an empty url applies");
    free((void *)value);
    value = _bugsee_test_apply_network_result("{\"errorShortMessage\":\"e\",\"errorDescription\":\"d\"}", "{\"errorShortMessage\":\"\",\"errorDescription\":\"\"}");
    expect(strstr(value, "\"error\":null") != NULL, "clearing both error fields with empty strings clears the error");
    free((void *)value);
    value = _bugsee_test_apply_network_result("{\"errorShortMessage\":\"e\",\"errorDescription\":\"d\"}", "{\"errorShortMessage\":\"\"}");
    expect(strstr(value, "\"description\":\"d\"") && !strstr(value, "\"name\""), "an empty name removes only the name");
    free((void *)value);
    value = _bugsee_test_apply_network_result("{\"errorShortMessage\":\"e\"}", "{\"errorShortMessage\":null}");
    expect(strstr(value, "\"error\":null") != NULL, "a cleared name leaves no error");
    free((void *)value);
    // A replacement event is applied onto the original: the original's body, Authorization header and error must go.
    value = _bugsee_test_apply_network_result(
        "{\"url\":\"host-a\",\"body\":\"secret\",\"headers\":{\"Authorization\":\"Bearer abc\"},\"errorShortMessage\":\"e\",\"errorDescription\":\"d\"}",
        "{\"stage\":\"complete\",\"url\":\"host-clean\",\"body\":null,\"errorShortMessage\":null,\"errorDescription\":null,\"size\":0,\"responseCode\":200,\"headers\":{}}");
    expect(strstr(value, "\"url\":\"host-clean\"") && strstr(value, "\"body\":null") && strstr(value, "\"error\":null")
               && strstr(value, "\"headers\":{}") && !strstr(value, "Authorization"),
           "a replacement result removes the original's body, header and error");
    free((void *)value);
    // The same replacement with keys left out (what omission-style JSON would do) would keep them: why it is written in full.
    value = _bugsee_test_apply_network_result(
        "{\"url\":\"host-a\",\"body\":\"secret\",\"headers\":{\"Authorization\":\"Bearer abc\"}}",
        "{\"stage\":\"complete\",\"url\":\"host-clean\"}");
    expect(strstr(value, "Authorization") && strstr(value, "\"body\":\"secret\""), "keys left out keep the original's body and header");
    free((void *)value);

    // A scalar attribute must not throw from NSJSONSerialization (top-level non-container), and
    // reads back tagged with its kind: s string, b bool, i integer, d floating point, j container.
    struct { const char *name; const char *json; const char *expected; } scalars[] = {
        { "text", "\"text\"", "s:text" },
        { "numtext", "\"1\"", "s:1" },
        { "number", "1", "i:1" },
        { "negative", "-7", "i:-7" },
        { "bigint", "9007199254740993", "i:9007199254740993" },
        { "decimal", "1.5", "d:1.5" },
        { "wholedouble", "1.0", "d:1" },
        { "yes", "true", "b:1" },
        { "no", "false", "b:0" },
        { "empty", "\"\"", "s:" },
        { "unicode", "\"h\\u00e9llo\"", "s:h\xc3\xa9llo" },
        { "container", "{\"a\":1}", "j:{\"a\":1}" },
    };
    for (auto &c : scalars) {
        _bugsee_set_attribute(c.name, c.json);
        char *attr = _bugsee_get_attribute(c.name);
        char message[96];
        snprintf(message, sizeof message, "attribute %s reads back with its kind", c.name);
        expect(attr != NULL && same(attr, c.expected), message);
        free(attr);
        _bugsee_clear_attribute(c.name);
    }
    // Appearance maps onto the SDK's BugseeTheme.
    struct { const char *name; const char *key; } colors[] = {
        { "Report::ActionBarColor", "reportNavigationBarColor" },
        { "Report::EditTextBackgroundColor", "reportCellBackgroundColor" },
        { "Report::VersionColor", "reportVersionColor" },
        { "Report::TextColor", "reportTextColor" },
        { "Report::HintColor", "reportPlaceholderColor" },
        { "Report::BackgroundColor", "reportBackgroundColor" },
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
    for (auto &c : colors) {
        _bugsee_appearance_set_color(c.name, 0x12, 0x34, 0x56, 0x78);
        char *hex = _bugsee_appearance_get_color(c.name);
        char message[128];
        snprintf(message, sizeof message, "appearance color %s round-trips", c.name);
        expect(hex != NULL && same(hex, "#78123456"), message);
        free(hex);
        UIColor *themed = [[Bugsee getAppearance] valueForKey:@(c.key)];
        CGFloat r = 0, g = 0, b = 0, a = 0;
        [themed getRed:&r green:&g blue:&b alpha:&a];
        snprintf(message, sizeof message, "appearance color %s lands on BugseeTheme.%s", c.name, c.key);
        expect(themed != nil && lroundf(r * 255) == 0x12 && lroundf(b * 255) == 0x56 && lroundf(a * 255) == 0x78, message);
    }
    struct { const char *name; const char *key; } strings[] = {
        { "Report::SummaryPlaceholder", "reportSummaryPlaceholder" },
        { "Report::DescriptionPlaceholder", "reportDescriptionPlaceholder" },
        { "Report::LabelsPlaceholder", "reportLabelsPlaceholder" },
        { "Report::EmailPlaceholder", "reportEmailPlaceholder" },
    };
    for (auto &c : strings) {
        _bugsee_appearance_set_string(c.name, "h\xc3\xa9llo");
        char *text = _bugsee_appearance_get_string(c.name);
        char message[128];
        snprintf(message, sizeof message, "appearance string %s round-trips", c.name);
        expect(text != NULL && same(text, "h\xc3\xa9llo"), message);
        free(text);
        snprintf(message, sizeof message, "appearance string %s lands on BugseeTheme.%s", c.name, c.key);
        expect([[[Bugsee getAppearance] valueForKey:@(c.key)] isEqualToString:@"h\u00e9llo"], message);
    }
    // Null restores the SDK defaults.
    expect(_bugsee_appearance_set_string("Report::SummaryPlaceholder", NULL) == 1, "a null string is applied");
    expect([[Bugsee getAppearance] valueForKey:@"reportSummaryPlaceholder"] == nil, "a null string clears the theme placeholder");
    // No iOS equivalent: skipped, not faked.
    const char *unavailable[] = { "Report::ActionBarTextColor", "Report::ActionBarButtonBackgroundClickedColor",
        "Report::SeverityLabelActiveColor", "Feedback::DateTextColor", "Feedback::ErrorTextColor",
        "Feedback::InputTextHintColor", "Notification::Title", "Nonsense" };
    for (auto name : unavailable) {
        char message[128];
        snprintf(message, sizeof message, "unavailable appearance property %s is not applied", name);
        expect(_bugsee_appearance_set_color(name, 1, 2, 3, 4) == 0 && _bugsee_appearance_set_string(name, "x") == 0, message);
        char *color = _bugsee_appearance_get_color(name);
        char *text = _bugsee_appearance_get_string(name);
        snprintf(message, sizeof message, "unavailable appearance property %s reads nothing", name);
        expect(color == NULL && text == NULL, message);
        free(color);
        free(text);
    }
    // A color name is not a string property and vice versa.
    _bugsee_appearance_set_color("Report::TextColor", 1, 2, 3, 4);
    _bugsee_appearance_set_string("Report::SummaryPlaceholder", "x");
    char *kindText = _bugsee_appearance_get_string("Report::TextColor");
    char *kindColor = _bugsee_appearance_get_color("Report::SummaryPlaceholder");
    expect(kindText == NULL, "a color property has no string value");
    expect(kindColor == NULL, "a string property has no color value");
    free(kindText);
    free(kindColor);
    // LogException options reach the SDK; unknown keys and wrong types are ignored.
    expect(_bugsee_test_exception_options(NULL) == NULL, "no exception options means nil options");
    expect(_bugsee_test_exception_options("[1]") == NULL, "non-object exception options are ignored");
    expect(_bugsee_test_exception_options("{\"labels\":[\"\xff\"]}") == NULL, "options that are not valid UTF-8 are ignored, not raised on");
    expect(_bugsee_exception_options_dropped() == 1,
           "the wrapper knows whether the SDK drops the labels, includeVideo and mergingRules options");
    value = _bugsee_test_exception_options(
        "{\"labels\":[\"a\"],\"includeVideo\":false,\"mergingRules\":{\"k\":1},\"skipFrames\":2,\"domain\":\"x\"}");
    expect(value != NULL && strcmp(value, "skipFrames=2") == 0,
           "exception options carry skipFrames and nothing the SDK no longer has");
    free((void *)value);
    // The dropped-options warning is its own one-time line, apart from the simulator warning.
    int simulatorWarnings = _bugsee_test_simulator_warning_count();
    _bugsee_logException("UnityManagedException", "{}", true, "{\"skipFrames\":1}");
    expect(_bugsee_test_dropped_options_warning_count() == 0, "no warning when only skipFrames is passed");
    _bugsee_logException("UnityManagedException", "{}", true, "{\"labels\":[\"a\"]}");
    _bugsee_logException("UnityManagedException", "{}", true, "{\"includeVideo\":false}");
    expect(_bugsee_test_dropped_options_warning_count() == 1, "dropped LogException options are warned about once");
    expect(_bugsee_test_simulator_warning_count() == simulatorWarnings, "the simulator warning is untouched by it");
    value = _bugsee_test_exception_options("{\"skipFrames\":\"two\"}");
    expect(value != NULL && strcmp(value, "skipFrames=0") == 0, "a wrong-typed skipFrames is dropped");
    free((void *)value);
    value = _bugsee_test_exception_options("{\"skipFrames\":true}");
    expect(value != NULL && strcmp(value, "skipFrames=0") == 0, "a boolean skipFrames is not a frame count");
    free((void *)value);
    // The wire's digits must not follow the user's locale: C# parses them as invariant.
    NSUserDefaults *defaults = [NSUserDefaults standardUserDefaults];
    [defaults setObject:@"de_DE" forKey:@"AppleLocale"];
    [defaults setObject:@[@"de"] forKey:@"AppleLanguages"];
    setlocale(LC_ALL, "de_DE");
    // A runner without de_DE installed (or one that ignores the override) cannot show a comma: skip the precondition
    // rather than fail; the attribute checks below hold in any locale and only prove the point under a comma.
    if ([[NSString localizedStringWithFormat:@"%.1f", 1.5] containsString:@","]) {
        printf("ok the test locale really uses a decimal comma\n");
    } else {
        printf("skip the test locale does not use a decimal comma on this host; the checks below do not prove locale independence here\n");
    }
    _bugsee_set_attribute("localized", "1.5");
    char *localized = _bugsee_get_attribute("localized");
    expect(localized != NULL && same(localized, "d:1.5"), "floating attribute stays POSIX under a comma locale");
    free(localized);
    _bugsee_clear_attribute("localized");
    _bugsee_set_attribute("localized", "1234567");
    localized = _bugsee_get_attribute("localized");
    expect(localized != NULL && same(localized, "i:1234567"), "integer attribute has no grouping under a comma locale");
    free(localized);
    _bugsee_clear_attribute("localized");
    setlocale(LC_ALL, "C");
    [defaults removeObjectForKey:@"AppleLocale"];
    [defaults removeObjectForKey:@"AppleLanguages"];

    // requestData: only "vh" reaches managed code, every request is answered once, and nothing hangs the SDK's budget.
    expect(_bugsee_test_request_data("vh") == 1 && dataReplyIsNil(),
           "vh is answered nil before managed code has registered");
    _bugsee_register_data_request_callback(OnDataRequest);
    expect(_bugsee_test_request_data("other") == 1 && gDataRequestCalls == 0 && dataReplyIsNil(),
           "an unknown type is answered nil without asking managed code");
    gDataRequestInlineAnswer = "{\"class_name\":\"UnityPlayer\"}";
    expect(_bugsee_test_request_data("vh") == 1 && gDataRequestCalls == 1 && same(gDataRequestTypes[0], "vh"),
           "vh is handed to managed code and answered inside the request");
    value = _bugsee_test_copy_data_reply();
    expect(same(value, "{\"class_name\":\"UnityPlayer\"}"), "the managed answer reaches the SDK byte for byte");
    free((void *)value);
    _bugsee_complete_data_request(gDataRequestIds[0], "{\"late\":true}");
    expect(_bugsee_test_data_reply_count() == 1, "a second answer to the same request is ignored");
    gDataRequestInlineAnswer = NULL;
    expect(_bugsee_test_request_data("vh") == 0 && gDataRequestCalls == 2, "an answer made later is waited for");
    _bugsee_complete_data_request(gDataRequestIds[1], "{\"later\":true}");
    value = _bugsee_test_copy_data_reply();
    expect(_bugsee_test_data_reply_count() == 1 && same(value, "{\"later\":true}"), "the later answer arrives once with its text");
    free((void *)value);
    _bugsee_complete_data_request(gDataRequestIds[1], "{\"again\":true}");
    expect(_bugsee_test_data_reply_count() == 1, "the id is spent after its answer");
    expect(_bugsee_test_request_data("vh") == 0, "a request waits for its answer");
    _bugsee_complete_data_request(gDataRequestIds[2], NULL);
    expect(_bugsee_test_data_reply_count() == 1 && dataReplyIsNil(), "null from managed code is answered as nil");
    _bugsee_complete_data_request(987654, "{}");
    // The hierarchy's unit: Unity pixels per point is Unity's width over the screen's width in points, not UIScreen.scale.
    CGFloat pointsWide = [UIScreen mainScreen].bounds.size.width;
    float perPoint = _bugsee_pixels_per_point((int)(pointsWide * 2.0));
    expect(perPoint > 1.99f && perPoint < 2.01f, "pixels per point follows Unity's width over the screen width in points");
    expect(_bugsee_pixels_per_point((int)(pointsWide * 2.608)) > 2.6f, "a render width below the screen scale is not rounded to it");
    expect(_bugsee_pixels_per_point(0) == _bugsee_screen_scale() && _bugsee_pixels_per_point(-5) == _bugsee_screen_scale(),
           "an unusable width falls back to the screen scale");
    _bugsee_register_data_request_callback(NULL);

    // Lifecycle against the real SDK: answers come back with the caller's token.
    _bugsee_launch("00000000-0000-0000-0000-000000000000", "{\"max_recording_time\":60}", 41, OnStarted);
    expect(waitFor(^{ return gStartedCount == 1 ? 1 : 0; }, 5) && gStartedToken == 41,
           "launch reports the SDK's started answer with its token");
    // The wrapper is installed in +load, so lifecycle events can arrive before the managed callback is
    // registered: they are held and replayed, in order and with their payloads, on registration. (The launch
    // above cannot announce Launching here: the SDK defers a launch until the app is active.)
    gLifecycleEvents = [NSMutableArray array];
    _bugsee_test_emit_lifecycle("e.early1", NULL);
    _bugsee_test_emit_lifecycle("e.early2", "\"snap-1\"");
    _bugsee_test_emit_lifecycle("e.early3", "{\"value\":\"x\"}");
    expect(lifecycleEvents().count == 0, "events before the callback registered are held, not delivered");
    _bugsee_register_unity_callbacks(NULL, NULL, OnLifecycle);
    expect(lifecycleEvents().count == 0, "registering does not replay inside the registering call");
    // Arrives while the replay is pending: it queues behind the held events instead of overtaking them.
    _bugsee_test_emit_lifecycle("e.live", "\"snap-2\"");
    expect(waitFor(^{ return lifecycleEvents().count == 4 ? 1 : 0; }, 5)
               && [lifecycleEvents()[0] isEqualToString:@"e.early1|(null)"]
               && [lifecycleEvents()[1] isEqualToString:@"e.early2|\"snap-1\""]
               && [lifecycleEvents()[2] isEqualToString:@"e.early3|{\"value\":\"x\"}"]
               && [lifecycleEvents()[3] isEqualToString:@"e.live|\"snap-2\""],
           "held events replay after the registering call, in order (no payload, a string literal, a dictionary as an object), before a later event");
    _bugsee_register_unity_callbacks(NULL, NULL, OnLifecycle);
    _bugsee_test_emit_lifecycle("e.live2", NULL);
    expect(lifecycleEvents().count == 5 && [lifecycleEvents()[4] isEqualToString:@"e.live2|(null)"],
           "once replayed, an event goes straight through and registering again replays nothing twice");
    // The effective options are valid JSON holding the host's value merged over the SDK defaults.
    char *launchOptions = _bugsee_get_launch_options();
    id parsedOptions = launchOptions
        ? [NSJSONSerialization JSONObjectWithData:[NSData dataWithBytes:launchOptions length:strlen(launchOptions)] options:0 error:nil]
        : nil;
    expect([parsedOptions isKindOfClass:[NSDictionary class]] && [parsedOptions count] > 1,
           "launch options come back as a JSON object with the defaults");
    expect([parsedOptions[BugseeMaxRecordingTimeKey] intValue] == 60, "launch options hold the host's value");
    free(launchOptions);
    // An omitted report severity is the SDK default, High until the host configures ReportingDefaultBugPriority.
    expect(_bugsee_test_default_bug_severity() == (int)BugseeSeverityHigh, "the default bug severity is High");
    // Which SDK overload each report entry point reaches (omitted severity is 0): the ones without a severity read
    // the option inside the SDK; with labels (no such overload) the bridge passes the default itself.
    _bugsee_show_report("", "", 0, NULL);
    expect(same(_bugsee_test_last_report_overload(), "showReportDialog"), "an empty dialog uses the plain showReportDialog");
    _bugsee_show_report("s", "d", 0, NULL);
    expect(same(_bugsee_test_last_report_overload(), "showReportDialogWithSummary:description:"),
           "a dialog without severity or labels uses the overload without a severity");
    _bugsee_show_report("s", "d", 0, "[\"a\"]");
    expect(same(_bugsee_test_last_report_overload(), "showReportDialogWithSummary:description:severity:labels:")
           && _bugsee_test_last_report_severity() == (int)BugseeSeverityHigh,
           "a dialog with labels and no severity passes the default severity");
    _bugsee_show_report("s", "d", 2, "[\"a\"]");
    expect(_bugsee_test_last_report_severity() == 2, "an explicit dialog severity is passed as given");
    _bugsee_upload("s", "d", 0, NULL);
    expect(same(_bugsee_test_last_report_overload(), "uploadWithSummary:description:"),
           "an upload without severity or labels uses the overload without a severity");
    _bugsee_upload("s", "d", 0, "[\"a\"]");
    expect(same(_bugsee_test_last_report_overload(), "uploadWithSummary:description:severity:labels:")
           && _bugsee_test_last_report_severity() == (int)BugseeSeverityHigh,
           "an upload with labels and no severity passes the default severity");
    _bugsee_upload("s", "d", 4, NULL);
    expect(_bugsee_test_last_report_severity() == 4, "an explicit upload severity is passed as given");
    // A breadcrumb filter result reaches the SDK's breadcrumb as the data object C# sends: nested values and
    // scalar kinds intact; a result without a data field leaves the crumb's own data (and a nil data) alone.
    {
        const char *start = "{\"orig\":1,\"nul\":null}";
        const char *kept = _bugsee_test_apply_breadcrumb_result(start, "{\"category\":\"c\",\"level\":2}");
        expect(kept != NULL && same(kept, "{\"nul\":null,\"orig\":1}"), "a filter result without data keeps the crumb data exactly");
        free((void *)kept);
        const char *nilKept = _bugsee_test_apply_breadcrumb_result(NULL, "{\"category\":\"c\"}");
        expect(nilKept != NULL && same(nilKept, "null"), "a filter result without data keeps a nil data nil");
        free((void *)nilKept);
        const char *nested = _bugsee_test_apply_breadcrumb_result(start,
            "{\"data\":{\"n\":3,\"f\":1.5,\"b\":true,\"s\":\"x\\\\ny\\t\",\"o\":{\"deep\":[1,\"two\",{\"k\":false}]}}}");
        expect(nested != NULL && same(nested,
            "{\"b\":true,\"f\":1.5,\"n\":3,\"o\":{\"deep\":[1,\"two\",{\"k\":false}]},\"s\":\"x\\\\ny\\t\"}"),
            "a filter result with nested data replaces the crumb data with its values and kinds");
        free((void *)nested);
        const char *emptied = _bugsee_test_apply_breadcrumb_result(start, "{\"data\":{}}");
        expect(emptied != NULL && same(emptied, "{}"), "an empty data object clears the crumb data");
        free((void *)emptied);
    }
    // A report handler result reaches the SDK report as a change set: typed values stay typed, attributes the
    // handler did not touch are left alone, and only an explicit clear replaces the set.
    {
        const char *start = "{\"i\":42,\"b\":true,\"d\":1.5,\"s\":\"x\",\"o\":{\"k\":[1,2]}}";
        const char *untouched = _bugsee_test_apply_report_result(start, "{\"summary\":\"s\"}");
        expect(untouched != NULL && same(untouched, "{\"b\":true,\"d\":1.5,\"i\":42,\"o\":{\"k\":[1,2]},\"s\":\"x\"}"),
               "a report result without attributes keeps every attribute and its kind");
        free((void *)untouched);
        const char *changed = _bugsee_test_apply_report_result(start,
            "{\"attributes\":{\"n\":7,\"i\":43,\"t\":false},\"attributeRemovals\":[\"s\"]}");
        expect(changed != NULL && same(changed,
            "{\"b\":true,\"d\":1.5,\"i\":43,\"n\":7,\"o\":{\"k\":[1,2]},\"t\":false}"),
            "a change set sets typed values, removes names and leaves the rest as they were");
        free((void *)changed);
        const char *replaced = _bugsee_test_apply_report_result(start, "{\"attributes\":{\"a\":1},\"attributesReplaceAll\":true}");
        expect(replaced != NULL && same(replaced, "{\"a\":1}"), "attributesReplaceAll replaces the set");
        free((void *)replaced);
        const char *nulled = _bugsee_test_apply_report_result(start, "{\"attributes\":{\"i\":null}}");
        expect(nulled != NULL && same(nulled, "{\"b\":true,\"d\":1.5,\"o\":{\"k\":[1,2]},\"s\":\"x\"}"),
               "a null value removes the attribute instead of storing NSNull");
        free((void *)nulled);
        const char *reportJson = _bugsee_test_report_json_with_unserializable_attributes(start);
        NSData *reportData = [[NSString stringWithUTF8String:reportJson] dataUsingEncoding:NSUTF8StringEncoding];
        NSDictionary *seen = [NSJSONSerialization JSONObjectWithData:reportData options:0 error:nil];
        NSDictionary *seenAttrs = seen[@"attributes"];
        expect([seenAttrs[@"i"] isEqual:@42] && [seenAttrs[@"d"] isEqual:@1.5] && [seenAttrs[@"b"] isEqual:@YES]
                   && [seenAttrs[@"s"] isEqual:@"x"] && [seenAttrs[@"o"][@"k"] count] == 2 && [seenAttrs[@"when"] isKindOfClass:[NSString class]],
               "the report handed to C# keeps attribute kinds and degrades a non-JSON value to text");
        free((void *)reportJson);
    }
    // notify takes every argument shape without throwing; the title is required and fields are strings only.
    _bugsee_notify("title", NULL, 2, NULL, 0);
    _bugsee_notify("title", "body", 3, "{\"env\":\"prod\",\"n\":1}", 1);
    _bugsee_notify("", "body", 3, NULL, 0);
    _bugsee_notify(NULL, NULL, 3, "not json", 0);
    // APM: spans cross as registry ids; an id is valid until released and every call on a stale id is a no-op.
    expect(_bugsee_start_transaction("", "op", NULL) == 0 && _bugsee_start_transaction("n", NULL, NULL) == 0,
           "a transaction needs a name and an operation");
    int64_t txn = _bugsee_start_transaction("checkout", "user.flow", "{\"k\":\"v\",\"n\":2}");
    expect(txn > 0, "a transaction gets a registry id");
    char *txnSpanId = _bugsee_span_get_id(txn);
    char *txnTraceId = _bugsee_span_get_trace_id(txn);
    expect(txnSpanId && strlen(txnSpanId) > 0 && txnTraceId && strlen(txnTraceId) > 0, "a transaction has span and trace ids");
    int64_t child = _bugsee_span_start_child(txn, "db", NULL);
    int64_t child2 = _bugsee_span_start_child(txn, "db2", "select");
    expect(child > 0 && child2 > 0 && child != txn && child2 != child, "children get distinct ids");
    expect(_bugsee_span_start_child(txn, "", NULL) == 0, "a child needs an operation");
    char *childTraceId = _bugsee_span_get_trace_id(child);
    expect(childTraceId && txnTraceId && strcmp(childTraceId, txnTraceId) == 0, "a child shares the trace");
    _bugsee_span_set_name(txn, "renamed");
    _bugsee_span_set_name(txn, "");
    _bugsee_span_set_description(txn, NULL);
    _bugsee_span_set_attribute(txn, "k2", "\"v\"");
    _bugsee_span_set_attribute(txn, "k3", "not json");
    _bugsee_span_set_attribute(txn, "k4", "null");
    _bugsee_span_set_status(child, 2);
    _bugsee_span_finish(child, 1);
    _bugsee_span_release(child);
    _bugsee_span_finish(child, 0);
    _bugsee_span_set_name(child, "stale");
    expect(_bugsee_span_get_id(child) == NULL, "a released span has no id");
    expect(_bugsee_span_start_child(child, "x", NULL) == 0, "a released span starts no children");
    _bugsee_span_finish(txn, 0);
    _bugsee_span_release(child2);
    _bugsee_span_release(txn);
    _bugsee_span_release(txn);
    expect(_bugsee_span_get_id(txn) == NULL, "a released transaction has no id");
    _bugsee_span_finish(0, 0);
    _bugsee_span_release(987654);
    free(txnSpanId);
    free(txnTraceId);
    free(childTraceId);
    _bugsee_stop(7, OnStopped);
    expect(waitFor(^{ return gStoppedToken == 7 ? 1 : 0; }, 5), "stop reports completion with its token");
    _bugsee_relaunch(NULL, 42, OnStarted);
    expect(waitFor(^{ return gStartedCount == 2 ? 1 : 0; }, 5) && gStartedToken == 42,
           "relaunch reports the SDK's started answer with its token");
    // The wipe reports back with its token once the SDK has finished it (it stops a running SDK first).
    _bugsee_delete_collected_data(9, OnDeleted);
    expect(waitFor(^{ return gDeletedToken == 9 ? 1 : 0; }, 15), "delete reports its completion with its token");
    expect(gDeletedWiped == 1, "delete reports wiped on the normal path");
    _bugsee_launch("00000000-0000-0000-0000-000000000000", NULL, 0, NULL);
    // The tests run on a simulator: the warning that exceptions are not filed there shows once, however many launches.
    expect(_bugsee_test_simulator_warning_count() == 1, "the simulator warning is logged once across launches");
    expect(_bugsee_exceptions_filed() == 0, "the simulator reports that exceptions are not filed");
    expect(gStartedCount == 2, "a launch without a started callback calls nothing back");

    // IsBlackout reads the SDK, not a managed flag. A blackout the wrapper did not start is seen, and one the
    // wrapper ended is gone, and the answer survives the stop and launches above (the SDK keeps the flag).
    [Bugsee startBlackout];
    expect(_bugsee_is_blackout() == 1, "the blackout export sees a blackout the wrapper did not start");
    _bugsee_end_blackout();
    expect(_bugsee_is_blackout() == 0, "the blackout export sees a blackout the wrapper ended");
    _bugsee_start_blackout();
    expect(_bugsee_is_blackout() == 1, "the blackout export reports a blackout the wrapper started");
    _bugsee_stop(8, OnStopped);
    expect(waitFor(^{ return gStoppedToken == 8 ? 1 : 0; }, 5) && _bugsee_is_blackout() == 1,
           "the blackout export still reports the blackout after a stop");
    [Bugsee endBlackout];
    expect(_bugsee_is_blackout() == 0, "the blackout export sees a blackout the SDK ended");

    printf("ios bridge tests passed\n");
    return 0;
}
