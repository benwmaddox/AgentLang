#ifndef AL_CAMPFIRE_BROWSER_NORMALIZER_H
#define AL_CAMPFIRE_BROWSER_NORMALIZER_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define AL_CAMPFIRE_BROWSER_MAX_INPUT_UNITS_V1 4096u
#define AL_CAMPFIRE_BROWSER_MAX_PRODUCTS_V1 128u
#define AL_CAMPFIRE_BROWSER_SCRATCH_BYTES_V1 \
    (sizeof(uint32_t) * (1u + AL_CAMPFIRE_BROWSER_MAX_PRODUCTS_V1 * 8u) \
     + sizeof(uint16_t) * AL_CAMPFIRE_BROWSER_MAX_INPUT_UNITS_V1)
#define AL_CAMPFIRE_BROWSER_SCRATCH_ALIGNMENT_BYTES_V1 (sizeof(uint32_t))

typedef enum al_campfire_browser_family_v1 {
    AL_CAMPFIRE_BROWSER_SAFARI_V1 = 0,
    AL_CAMPFIRE_BROWSER_CHROME_V1 = 1,
    AL_CAMPFIRE_BROWSER_FIREFOX_V1 = 2,
    AL_CAMPFIRE_BROWSER_OPERA_V1 = 3,
    AL_CAMPFIRE_BROWSER_INTERNET_EXPLORER_V1 = 4,
    AL_CAMPFIRE_BROWSER_OTHER_V1 = 5
} al_campfire_browser_family_v1;

typedef enum al_campfire_browser_status_v1 {
    AL_CAMPFIRE_BROWSER_OK_V1 = 0,
    AL_CAMPFIRE_BROWSER_INVALID_ARGUMENT_V1 = 1,
    AL_CAMPFIRE_BROWSER_INPUT_LIMIT_V1 = 2,
    AL_CAMPFIRE_BROWSER_SCRATCH_EXTENT_V1 = 3,
    AL_CAMPFIRE_BROWSER_SCRATCH_ALIGNMENT_V1 = 4,
    AL_CAMPFIRE_BROWSER_PRODUCT_LIMIT_V1 = 5,
    AL_CAMPFIRE_BROWSER_VERSION_EXTENT_V1 = 6
} al_campfire_browser_status_v1;

typedef struct al_campfire_browser_result_v1 {
    uint32_t family;
    uint32_t has_version;
    uint32_t version_units;
    uint32_t is_bot;
} al_campfire_browser_result_v1;

size_t al_campfire_browser_scratch_size_v1(void);
size_t al_campfire_browser_scratch_alignment_v1(void);

/*
 * Input is a borrowed UTF-16 code-unit span. Missing uses present=0, NULL, 0;
 * empty-but-present uses present=1, any pointer, 0. Scratch, result, and
 * version output must remain valid for the duration of the call. The input is
 * never retained. Scratch includes the bounded product table and UTF-16 join
 * workspace; use the published size/alignment queries. On failure, result and
 * version output are left untouched.
 */
al_campfire_browser_status_v1 al_campfire_browser_normalize_utf16_v1(
    uint32_t present,
    const uint16_t *input,
    size_t input_units,
    void *scratch,
    size_t scratch_bytes,
    uint16_t *version_output,
    size_t version_capacity_units,
    al_campfire_browser_result_v1 *result);

#ifdef __cplusplus
}
#endif

#endif
