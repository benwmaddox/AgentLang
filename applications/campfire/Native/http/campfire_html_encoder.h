#ifndef AL_CAMPFIRE_HTML_ENCODER_H
#define AL_CAMPFIRE_HTML_ENCODER_H

#include <stdint.h>

#define AL_CAMPFIRE_HTML_ENCODER_ABI_VERSION 1u

enum al_campfire_html_encode_status_v1 {
  AL_CAMPFIRE_HTML_ENCODE_OK = 0,
  AL_CAMPFIRE_HTML_ENCODE_INVALID_ARGUMENT = 1,
  AL_CAMPFIRE_HTML_ENCODE_OUTPUT_TOO_SMALL = 2,
  AL_CAMPFIRE_HTML_ENCODE_LENGTH_OVERFLOW = 3
};

#if defined(_MSC_VER) || defined(__clang__)
#define AL_CAMPFIRE_HTML_EXPORT __declspec(dllexport)
#else
#define AL_CAMPFIRE_HTML_EXPORT
#endif

#if defined(__cplusplus)
extern "C" {
#endif

/* Encode the exact default HtmlEncoder mapping to UTF-8 bytes.
 *
 * `input_utf16` (now UTF-8 bytes, length in bytes) is borrowed for this call
 * and may be NULL only when its length is zero. Malformed UTF-8 maps to U+FFFD.
 * `output_utf8` is caller-owned, receives no NUL terminator, and may be NULL
 * only when capacity is zero. Input and output ranges must not overlap.
 * `out_required_bytes` must be a valid non-overlapping pointer.
 *
 * Success writes exactly the reported byte count. If capacity is too small,
 * the exact required size is reported and the output is left untouched. All
 * other failures report zero required bytes and leave the output untouched.
 */
AL_CAMPFIRE_HTML_EXPORT int32_t al_campfire_html_encode_utf8_v1(
    const uint8_t *input_utf16, uint32_t input_code_units,
    uint8_t *output_utf8, uint32_t output_capacity_bytes,
    uint32_t *out_required_bytes);

#if defined(__cplusplus)
}
#endif

#endif
