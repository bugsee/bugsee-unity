#include <stdio.h>
#include <stdlib.h>
#include <string.h>
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
}

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

    // A scalar attribute must not throw from NSJSONSerialization (top-level non-container), and
    // reads back as its text form: the string, the number, 1/0 for a bool.
    struct { const char *name; const char *json; const char *expected; } scalars[] = {
        { "text", "\"text\"", "text" },
        { "number", "1", "1" },
        { "decimal", "1.5", "1.5" },
        { "yes", "true", "1" },
        { "no", "false", "0" },
        { "empty", "\"\"", "" },
        { "unicode", "\"h\\u00e9llo\"", "h\xc3\xa9llo" },
    };
    for (auto &c : scalars) {
        _bugsee_set_attribute(c.name, c.json);
        char *attr = _bugsee_get_attribute(c.name);
        char message[96];
        snprintf(message, sizeof message, "scalar attribute %s reads back as its text", c.name);
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
    // LogException options reach BugseeExceptionLoggingOptions; unknown keys and wrong types are ignored.
    expect(_bugsee_test_exception_options(NULL) == NULL, "no exception options means nil options");
    expect(_bugsee_test_exception_options("[1]") == NULL, "non-object exception options are ignored");
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
    // Lifecycle against the real SDK: answers come back with the caller's token.
    _bugsee_launch("00000000-0000-0000-0000-000000000000", "{\"max_recording_time\":60}", 41, OnStarted);
    expect(waitFor(^{ return gStartedCount == 1 ? 1 : 0; }, 5) && gStartedToken == 41,
           "launch reports the SDK's started answer with its token");
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
    expect(gStartedCount == 2, "a launch without a started callback calls nothing back");

    printf("ios bridge tests passed\n");
    return 0;
}
