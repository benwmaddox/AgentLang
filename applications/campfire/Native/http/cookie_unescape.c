#include "cookie_unescape.h"

#include <stddef.h>
#include <stdint.h>

typedef struct cf_cookie_unescape_scratch_v1 {
    uint8_t wire_bytes[CF_COOKIE_UNESCAPE_MAX_WIRE_UTF8_BYTES_V1];
    uint16_t decoded_units[CF_COOKIE_UNESCAPE_MAX_OUTPUT_UTF16_UNITS_V1];
} cf_cookie_unescape_scratch_v1;

_Static_assert(
    sizeof(cf_cookie_unescape_scratch_v1) == CF_COOKIE_UNESCAPE_SCRATCH_BYTES_V1,
    "public cookie unescape scratch size must match implementation");
_Static_assert(
    _Alignof(cf_cookie_unescape_scratch_v1) == CF_COOKIE_UNESCAPE_SCRATCH_ALIGNMENT_BYTES_V1,
    "public cookie unescape scratch alignment must match implementation");

size_t cf_cookie_unescape_scratch_size_v1(void)
{
    return sizeof(cf_cookie_unescape_scratch_v1);
}

size_t cf_cookie_unescape_scratch_alignment_v1(void)
{
    return _Alignof(cf_cookie_unescape_scratch_v1);
}

static int cf_cookie_hex_value_v1(uint8_t value)
{
    if (value >= (uint8_t)'0' && value <= (uint8_t)'9') {
        return (int)(value - (uint8_t)'0');
    }
    if (value >= (uint8_t)'a' && value <= (uint8_t)'f') {
        return (int)(value - (uint8_t)'a') + 10;
    }
    if (value >= (uint8_t)'A' && value <= (uint8_t)'F') {
        return (int)(value - (uint8_t)'A') + 10;
    }
    return -1;
}

static size_t cf_utf8_scalar_bytes_v1(uint32_t scalar)
{
    if (scalar <= 0x7fu) {
        return 1u;
    }
    if (scalar <= 0x7ffu) {
        return 2u;
    }
    if (scalar <= 0xffffu) {
        return 3u;
    }
    return 4u;
}

static size_t cf_utf8_write_scalar_v1(uint32_t scalar, uint8_t *output)
{
    if (scalar <= 0x7fu) {
        output[0] = (uint8_t)scalar;
        return 1u;
    }
    if (scalar <= 0x7ffu) {
        output[0] = (uint8_t)(0xc0u | (scalar >> 6u));
        output[1] = (uint8_t)(0x80u | (scalar & 0x3fu));
        return 2u;
    }
    if (scalar <= 0xffffu) {
        output[0] = (uint8_t)(0xe0u | (scalar >> 12u));
        output[1] = (uint8_t)(0x80u | ((scalar >> 6u) & 0x3fu));
        output[2] = (uint8_t)(0x80u | (scalar & 0x3fu));
        return 3u;
    }
    output[0] = (uint8_t)(0xf0u | (scalar >> 18u));
    output[1] = (uint8_t)(0x80u | ((scalar >> 12u) & 0x3fu));
    output[2] = (uint8_t)(0x80u | ((scalar >> 6u) & 0x3fu));
    output[3] = (uint8_t)(0x80u | (scalar & 0x3fu));
    return 4u;
}

static size_t cf_utf8_encode_utf16_v1(
    const uint16_t *input,
    size_t input_units,
    uint8_t *output)
{
    size_t input_index = 0u;
    size_t output_index = 0u;

    while (input_index < input_units) {
        const uint16_t first = input[input_index];
        uint32_t scalar;
        if (first >= 0xd800u && first <= 0xdbffu) {
            if (input_index + 1u < input_units
                && input[input_index + 1u] >= 0xdc00u
                && input[input_index + 1u] <= 0xdfffu) {
                scalar = 0x10000u
                    + (((uint32_t)first - 0xd800u) << 10u)
                    + ((uint32_t)input[input_index + 1u] - 0xdc00u);
                input_index += 2u;
            } else {
                scalar = 0xfffdu;
                ++input_index;
            }
        } else if (first >= 0xdc00u && first <= 0xdfffu) {
            scalar = 0xfffdu;
            ++input_index;
        } else {
            scalar = (uint32_t)first;
            ++input_index;
        }
        {
            const size_t scalar_bytes = cf_utf8_scalar_bytes_v1(scalar);
            if (scalar_bytes
                > CF_COOKIE_UNESCAPE_MAX_WIRE_UTF8_BYTES_V1 - output_index) {
                return CF_COOKIE_UNESCAPE_MAX_WIRE_UTF8_BYTES_V1 + 1u;
            }
            output_index += cf_utf8_write_scalar_v1(scalar, output + output_index);
        }
    }
    return output_index;
}

static int cf_utf8_is_continuation_v1(uint8_t value)
{
    return value >= 0x80u && value <= 0xbfu;
}

static int cf_utf16_write_scalar_v1(
    uint32_t scalar,
    uint16_t *output,
    size_t output_capacity,
    size_t *output_index)
{
    if (scalar <= 0xffffu) {
        if (*output_index >= output_capacity) {
            return 0;
        }
        output[(*output_index)++] = (uint16_t)scalar;
        return 1;
    }
    if (*output_index > output_capacity || output_capacity - *output_index < 2u) {
        return 0;
    }
    scalar -= 0x10000u;
    output[(*output_index)++] = (uint16_t)(0xd800u + (scalar >> 10u));
    output[(*output_index)++] = (uint16_t)(0xdc00u + (scalar & 0x3ffu));
    return 1;
}

/*
 * Match UTF8Encoding(false, false) replacement boundaries. Invalid leading
 * bytes consume one byte. A truncated but otherwise valid prefix consumes the
 * whole prefix. If a continuation violates a lead-specific range, only the
 * lead is replaced; for a later bad continuation, the valid prefix is replaced
 * and the offending byte is reprocessed.
 */
static int cf_utf8_decode_replacing_v1(
    const uint8_t *input,
    size_t input_bytes,
    uint16_t *output,
    size_t output_capacity,
    size_t *output_units)
{
    size_t input_index = 0u;
    size_t output_index = 0u;

    while (input_index < input_bytes) {
        const uint8_t first = input[input_index];
        uint32_t scalar = 0xfffdu;
        size_t consumed = 1u;

        if (first <= 0x7fu) {
            scalar = (uint32_t)first;
        } else if (first >= 0xc2u && first <= 0xdfu) {
            if (input_index + 1u < input_bytes
                && cf_utf8_is_continuation_v1(input[input_index + 1u])) {
                scalar = (((uint32_t)first & 0x1fu) << 6u)
                    | ((uint32_t)input[input_index + 1u] & 0x3fu);
                consumed = 2u;
            }
        } else if (first >= 0xe0u && first <= 0xefu) {
            if (input_index + 1u < input_bytes) {
                const uint8_t second = input[input_index + 1u];
                const int second_in_range =
                    cf_utf8_is_continuation_v1(second)
                    && !(first == 0xe0u && second < 0xa0u)
                    && !(first == 0xedu && second > 0x9fu);
                if (second_in_range) {
                    if (input_index + 2u < input_bytes) {
                        const uint8_t third = input[input_index + 2u];
                        if (cf_utf8_is_continuation_v1(third)) {
                            scalar = (((uint32_t)first & 0x0fu) << 12u)
                                | (((uint32_t)second & 0x3fu) << 6u)
                                | ((uint32_t)third & 0x3fu);
                            consumed = 3u;
                        } else {
                            consumed = 2u;
                        }
                    } else {
                        consumed = 2u;
                    }
                }
            }
        } else if (first >= 0xf0u && first <= 0xf4u) {
            if (input_index + 1u < input_bytes) {
                const uint8_t second = input[input_index + 1u];
                if (cf_utf8_is_continuation_v1(second)
                    && !(first == 0xf0u && second < 0x90u)
                    && !(first == 0xf4u && second > 0x8fu)) {
                    if (input_index + 2u < input_bytes
                        && cf_utf8_is_continuation_v1(input[input_index + 2u])) {
                        if (input_index + 3u < input_bytes
                            && cf_utf8_is_continuation_v1(input[input_index + 3u])) {
                            scalar = (((uint32_t)first & 0x07u) << 18u)
                                | (((uint32_t)second & 0x3fu) << 12u)
                                | (((uint32_t)input[input_index + 2u] & 0x3fu) << 6u)
                                | ((uint32_t)input[input_index + 3u] & 0x3fu);
                            consumed = 4u;
                        } else {
                            consumed = 3u;
                        }
                    } else {
                        consumed = 2u;
                    }
                }
            }
        }

        if (!cf_utf16_write_scalar_v1(
                scalar, output, output_capacity, &output_index)) {
            return 0;
        }
        input_index += consumed;
    }
    *output_units = output_index;
    return 1;
}

cf_cookie_unescape_status_v1 cf_cookie_unescape_utf16_v1(
    const uint16_t *wire,
    size_t wire_units,
    uint16_t *output,
    size_t output_capacity_units,
    size_t *required_units,
    void *scratch,
    size_t scratch_bytes)
{
    cf_cookie_unescape_scratch_v1 *workspace;
    size_t wire_bytes;
    size_t read_index;
    size_t write_index = 0u;
    size_t decoded_units_count = 0u;
    uint32_t malformed = 0u;

    if (wire == NULL || required_units == NULL || scratch == NULL
        || (output == NULL && output_capacity_units != 0u)) {
        return CF_COOKIE_UNESCAPE_INVALID_ARGUMENT_V1;
    }
    if (scratch_bytes < sizeof(cf_cookie_unescape_scratch_v1)) {
        return CF_COOKIE_UNESCAPE_SCRATCH_EXTENT_V1;
    }
    if (((uintptr_t)scratch
         % (uintptr_t)_Alignof(cf_cookie_unescape_scratch_v1)) != 0u) {
        return CF_COOKIE_UNESCAPE_SCRATCH_ALIGNMENT_V1;
    }
    if (wire_units > CF_COOKIE_UNESCAPE_MAX_WIRE_UTF16_UNITS_V1) {
        return CF_COOKIE_UNESCAPE_WIRE_TOO_LARGE_V1;
    }

    workspace = (cf_cookie_unescape_scratch_v1 *)scratch;
    wire_bytes = cf_utf8_encode_utf16_v1(
        wire, wire_units, workspace->wire_bytes);
    if (wire_bytes > CF_COOKIE_UNESCAPE_MAX_WIRE_UTF8_BYTES_V1) {
        return CF_COOKIE_UNESCAPE_WIRE_TOO_LARGE_V1;
    }

    read_index = 0u;
    while (read_index < wire_bytes) {
        const uint8_t value = workspace->wire_bytes[read_index];
        if (value == (uint8_t)'+') {
            workspace->wire_bytes[write_index++] = (uint8_t)' ';
            ++read_index;
        } else if (value == (uint8_t)'%') {
            int high;
            int low;
            if (read_index + 2u >= wire_bytes) {
                malformed = 1u;
                break;
            }
            high = cf_cookie_hex_value_v1(workspace->wire_bytes[read_index + 1u]);
            low = cf_cookie_hex_value_v1(workspace->wire_bytes[read_index + 2u]);
            if (high < 0 || low < 0) {
                malformed = 1u;
                break;
            }
            workspace->wire_bytes[write_index++] =
                (uint8_t)((unsigned int)high * 16u + (unsigned int)low);
            read_index += 3u;
        } else {
            workspace->wire_bytes[write_index++] = value;
            ++read_index;
        }
    }

    if (malformed != 0u) {
        *required_units = wire_units;
        if (output_capacity_units < wire_units) {
            return CF_COOKIE_UNESCAPE_CAPACITY_V1;
        }
        for (read_index = 0u; read_index < wire_units; ++read_index) {
            output[read_index] = wire[read_index];
        }
        return CF_COOKIE_UNESCAPE_OK_V1;
    }

    if (!cf_utf8_decode_replacing_v1(
            workspace->wire_bytes,
            write_index,
            workspace->decoded_units,
            CF_COOKIE_UNESCAPE_MAX_OUTPUT_UTF16_UNITS_V1,
            &decoded_units_count)) {
        return CF_COOKIE_UNESCAPE_INTERNAL_FAILURE_V1;
    }

    *required_units = decoded_units_count;
    if (output_capacity_units < decoded_units_count) {
        return CF_COOKIE_UNESCAPE_CAPACITY_V1;
    }
    for (read_index = 0u; read_index < decoded_units_count; ++read_index) {
        output[read_index] = workspace->decoded_units[read_index];
    }
    return CF_COOKIE_UNESCAPE_OK_V1;
}




