#ifndef AL_CAMPFIRE_COOKIE_EXPIRY_H
#define AL_CAMPFIRE_COOKIE_EXPIRY_H

#include <stdint.h>

/*
 * Formats the UTC instant twenty calendar years later as the 29-byte
 * RFC 1123 value produced by DateTimeOffset.AddYears(20).ToString("R",
 * InvariantCulture). Leap days clamp to the last valid day of February.
 *
 * now_unix_microseconds must be within the .NET DateTimeOffset UTC range.
 * output and written must be non-null and non-overlapping. `output` must
 * provide at least 29 bytes. The output is not NUL-terminated. Returns 1 on
 * success and 0 on invalid input, expiry overflow, or insufficient capacity;
 * failures leave both output and written unchanged.
 */
int32_t al_campfire_cookie_expiry_20_years_v1(
    int64_t now_unix_microseconds, uint8_t *output, uint32_t capacity,
    uint32_t *written);

#endif
