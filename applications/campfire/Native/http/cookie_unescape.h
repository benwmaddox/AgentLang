#ifndef CF_COOKIE_UNESCAPE_H
#define CF_COOKIE_UNESCAPE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define CF_COOKIE_UNESCAPE_MAX_WIRE_UTF8_BYTES_V1 12288u
#define CF_COOKIE_UNESCAPE_MAX_WIRE_UTF16_UNITS_V1 12288u
#define CF_COOKIE_UNESCAPE_MAX_OUTPUT_UTF16_UNITS_V1 12288u
#define CF_COOKIE_UNESCAPE_SCRATCH_BYTES_V1 \
    (CF_COOKIE_UNESCAPE_MAX_WIRE_UTF8_BYTES_V1 \
     + sizeof(uint16_t) * CF_COOKIE_UNESCAPE_MAX_OUTPUT_UTF16_UNITS_V1)
#define CF_COOKIE_UNESCAPE_SCRATCH_ALIGNMENT_BYTES_V1 (sizeof(uint16_t))

typedef enum cf_cookie_unescape_status_v1 {
    CF_COOKIE_UNESCAPE_OK_V1 = 0,
    CF_COOKIE_UNESCAPE_INVALID_ARGUMENT_V1 = 1,
    CF_COOKIE_UNESCAPE_CAPACITY_V1 = 2,
    CF_COOKIE_UNESCAPE_WIRE_TOO_LARGE_V1 = 3,
    CF_COOKIE_UNESCAPE_SCRATCH_EXTENT_V1 = 4,
    CF_COOKIE_UNESCAPE_SCRATCH_ALIGNMENT_V1 = 5,
    CF_COOKIE_UNESCAPE_INTERNAL_FAILURE_V1 = 6
} cf_cookie_unescape_status_v1;

size_t cf_cookie_unescape_scratch_size_v1(void);
size_t cf_cookie_unescape_scratch_alignment_v1(void);

/*
 * Decodes Rack cookie wire text as RackCookieEncoding.unescape does.
 *
 * wire is a borrowed UTF-16 code-unit span. It must be nonnull even for a
 * present-empty value; an absent cookie is handled by the host before calling.
 * output has no NUL terminator and may be null only when its capacity is zero.
 * The caller must provide aligned scratch and keep input, output, required_units,
 * and scratch non-overlapping for the duration of the call.
 *
 * On success, required_units receives the exact output extent. On capacity
 * failure it receives the exact required extent and output is left untouched.
 * Other failures leave required_units and output untouched. Scratch and output
 * are never retained.
 */
cf_cookie_unescape_status_v1 cf_cookie_unescape_utf16_v1(
    const uint16_t *wire,
    size_t wire_units,
    uint16_t *output,
    size_t output_capacity_units,
    size_t *required_units,
    void *scratch,
    size_t scratch_bytes);

#ifdef __cplusplus
}
#endif

#endif

