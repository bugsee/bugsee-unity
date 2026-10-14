#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#import <Foundation/Foundation.h>

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
typedef void (*BugseeLaunchStartedFn)(int token, int success);
void _bugsee_launch(const char *appToken, const char *optionsJson, int startedToken, BugseeLaunchStartedFn started);
void _bugsee_relaunch(const char *optionsJson, int startedToken, BugseeLaunchStartedFn started);
typedef void (*BugseeStopCompletionFn)(int token);
void _bugsee_stop(int token, BugseeStopCompletionFn completion);
typedef void (*BugseeDeleteCompletionFn)(int token, int wiped);
void _bugsee_delete_collected_data(int token, BugseeDeleteCompletionFn completion);
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
