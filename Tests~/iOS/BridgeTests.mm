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
const char *_bugsee_test_copy_context_value(const char *key);
void _bugsee_set_attribute(const char *key, const char *valueJson);
char *_bugsee_get_attribute(const char *key);
void _bugsee_clear_attribute(const char *key);
char *_bugsee_get_launch_options(void);
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
typedef void (*BugseeStopCompletionFn)(int token);
void _bugsee_stop(int token, BugseeStopCompletionFn completion);
typedef void (*BugseeDeleteCompletionFn)(int token, int wiped);
void _bugsee_delete_collected_data(int token, BugseeDeleteCompletionFn completion);
const char *_bugsee_test_exception_options(const char *optionsJson);
int _bugsee_test_simulator_warning_count(void);
int _bugsee_exceptions_filed(void);
void _bugsee_logException(const char *name, const char *reason, bool handled, const char *optionsJson);
int _bugsee_exception_options_dropped(void);
}

// Same switch as BugseeUnityBridge.mm: bugsee-cocoa up to 7.0.0-beta6 still has the exception logging options.
#if __has_include(<Bugsee/BugseeExceptionLoggingOptions.h>)
#define BUGSEE_IOS_LEGACY_EXCEPTION_API 1
#else
#define BUGSEE_IOS_LEGACY_EXCEPTION_API 0
#endif
#if !BUGSEE_IOS_LEGACY_EXCEPTION_API
extern "C" int _bugsee_test_dropped_options_warning_count(void);
#endif

static int gStartedToken;
static int gStartedCount;
static int gStoppedToken;

static void OnStarted(int token, int success)
{
    (void)success;
    gStartedToken = token;
    gStartedCount++;
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
    expect(_bugsee_exception_options_dropped() == (BUGSEE_IOS_LEGACY_EXCEPTION_API ? 0 : 1),
           "the wrapper knows whether the SDK drops the labels, includeVideo and mergingRules options");
#if BUGSEE_IOS_LEGACY_EXCEPTION_API
    value = _bugsee_test_exception_options(
        "{\"labels\":[\"a\",3,\"b\"],\"includeVideo\":false,\"mergingRules\":{\"k\":1},\"domain\":\"x\"}");
    expect(value != NULL && strstr(value, "labels=a,b;video=0;") != NULL && strstr(value, "k = 1") != NULL,
           "exception options carry labels, includeVideo and mergingRules");
    free((void *)value);
    value = _bugsee_test_exception_options("{\"labels\":\"nope\",\"includeVideo\":\"yes\"}");
    expect(value != NULL && strstr(value, "labels=(null);video=1;") != NULL, "wrong-typed exception options are dropped");
    free((void *)value);
    value = _bugsee_test_exception_options("{\"labels\":[\"only\"]}");
    expect(value != NULL && strstr(value, "labels=only;video=1;") != NULL, "options without includeVideo keep the video");
    free((void *)value);
    value = _bugsee_test_exception_options("{\"skipFrames\":2,\"domain\":\"x\"}");
    expect(value != NULL && strstr(value, "video=1;") != NULL && strstr(value, "= 2") != NULL,
           "top-level skipFrames becomes the skip-frames merging rule");
    free((void *)value);
#else
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
#endif
    // The wire's digits must not follow the user's locale: C# parses them as invariant.
    NSUserDefaults *defaults = [NSUserDefaults standardUserDefaults];
    [defaults setObject:@"de_DE" forKey:@"AppleLocale"];
    [defaults setObject:@[@"de"] forKey:@"AppleLanguages"];
    setlocale(LC_ALL, "de_DE");
    expect([[NSString localizedStringWithFormat:@"%.1f", 1.5] containsString:@","],
           "the test locale really uses a decimal comma");
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

    // Lifecycle against the real SDK: answers come back with the caller's token.
    _bugsee_launch("00000000-0000-0000-0000-000000000000", "{\"max_recording_time\":60}", 41, OnStarted);
    expect(waitFor(^{ return gStartedCount == 1 ? 1 : 0; }, 5) && gStartedToken == 41,
           "launch reports the SDK's started answer with its token");
    // The effective options are valid JSON holding the host's value merged over the SDK defaults.
    char *launchOptions = _bugsee_get_launch_options();
    id parsedOptions = launchOptions
        ? [NSJSONSerialization JSONObjectWithData:[NSData dataWithBytes:launchOptions length:strlen(launchOptions)] options:0 error:nil]
        : nil;
    expect([parsedOptions isKindOfClass:[NSDictionary class]] && [parsedOptions count] > 1,
           "launch options come back as a JSON object with the defaults");
    expect([parsedOptions[BugseeMaxRecordingTimeKey] intValue] == 60, "launch options hold the host's value");
    free(launchOptions);
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

    printf("ios bridge tests passed\n");
    return 0;
}
