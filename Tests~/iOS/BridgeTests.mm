#include <stdio.h>
#include <stdlib.h>
#include <string.h>

extern "C" {
void _bugsee_set_wrapper_context(const char *json);
void _bugsee_ensure_wrapper(const char *version, const char *build);
const char *_bugsee_test_copy_wrapper_version(void);
const char *_bugsee_test_copy_wrapper_build(void);
int _bugsee_test_wrapper_installed(void);
const char *_bugsee_test_copy_context_value(const char *key);
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

    printf("ios bridge tests passed\n");
    return 0;
}
