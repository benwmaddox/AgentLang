#ifndef AL_CAMPFIRE_HTTP_RENDERER_H
#define AL_CAMPFIRE_HTTP_RENDERER_H

#include <stdint.h>

#define AL_CAMPFIRE_HTTP_RENDERER_ABI_VERSION 1u
#define AL_CAMPFIRE_HTTP_RENDERER_MANIFEST_FORMAT_VERSION 2u
#define AL_CAMPFIRE_HTTP_RENDERER_LAYOUT_SCHEMA_VERSION 5u
#define AL_CAMPFIRE_HTTP_RENDERER_LAYOUT_ABI_VERSION 5u
#define AL_CAMPFIRE_HTTP_RENDERER_MAX_RESPONSE_BYTES 16777216u

enum al_campfire_http_render_status_v1 {
  AL_CAMPFIRE_HTTP_RENDER_OK = 0,
  AL_CAMPFIRE_HTTP_RENDER_OUTPUT_TOO_SMALL = 1,
  AL_CAMPFIRE_HTTP_RENDER_INVALID_ARGUMENT = 2,
  AL_CAMPFIRE_HTTP_RENDER_MANIFEST_MISMATCH = 3,
  AL_CAMPFIRE_HTTP_RENDER_MALFORMED_RESPONSE = 4,
  AL_CAMPFIRE_HTTP_RENDER_UNSUPPORTED_VIEW = 5,
  AL_CAMPFIRE_HTTP_RENDER_LENGTH_OVERFLOW = 6
};

typedef struct al_campfire_http_utf16_span_v1 {
  const uint8_t *data_utf16le;
  uint32_t code_units;
  uint32_t present;
} al_campfire_http_utf16_span_v1;

typedef struct al_campfire_http_render_metadata_v1 {
  int32_t status;
  uint32_t reserved0;
  const uint8_t *content_type_utf8;
  uint32_t content_type_bytes;
  uint32_t has_location;
  const uint8_t *location_utf8;
  uint32_t location_bytes;
  al_campfire_http_utf16_span_v1 refresh_session_token;
  uint32_t remember_return_to;
  uint32_t has_last_room;
  int64_t last_room;
  al_campfire_http_utf16_span_v1 alert;
  uint32_t complete_login;
  uint32_t complete_logout;
  /* A CSRF secret Flow generated for a session without one (report 271);
   * the host stores it as the session's _csrf_token. Room view only. */
  al_campfire_http_utf16_span_v1 new_csrf_token;
} al_campfire_http_render_metadata_v1;

#if defined(_WIN32) || defined(__clang__)
#define AL_CAMPFIRE_HTTP_RENDER_API __declspec(dllexport)
#else
#define AL_CAMPFIRE_HTTP_RENDER_API
#endif

#if defined(__cplusplus)
extern "C" {
#endif

/* Render the closed CampfireRoomReadV1 CampfireHttpResponse owning-layout
 * value. Before calling, the host must verify profileId ==
 * "campfire-room-read-v1" and the exact CampfireHttpResponse boundary schema
 * in the compiler-minted manifest. This function independently requires the
 * pinned manifest format/layout-schema/layout-ABI version tuple (2, 5, 5).
 *
 * response_abi5 is borrowed, immutable, little-endian owning layout ABI5,
 * must be 2-byte aligned, and remains live for the call. The function rejects
 * a misaligned base before parsing any typed UTF-16 payload. Metadata string
 * spans borrow UTF-16LE bytes from that response and remain valid only while
 * the response arena remains live;
 * content-type and location pointers refer to static UTF-8 literals. Body
 * bytes are caller-owned UTF-8 without a NUL terminator. No pointers are
 * retained, records are not relocated, and no heap memory is allocated.
 *
 * The function validates the entire response before measuring or writing.
 * The input, body, required-size, and metadata ranges must not overlap. The
 * required-size pointer must be naturally uint32-aligned, and metadata must
 * satisfy this ABI's 8-byte struct alignment. A NULL body is allowed only
 * with zero capacity. On OUTPUT_TOO_SMALL, the exact
 * required body byte count and metadata are returned, while body is untouched.
 * Other failures clear required size and metadata and leave body untouched.
 */
AL_CAMPFIRE_HTTP_RENDER_API int32_t al_campfire_http_render_v1(
    uint32_t manifest_format_version, uint32_t layout_schema_version,
    uint32_t layout_abi_version, const uint8_t *response_abi5,
    uint32_t response_extent_bytes, uint8_t *body_utf8,
    uint32_t body_capacity_bytes, uint32_t *out_body_required_bytes,
    al_campfire_http_render_metadata_v1 *out_metadata);

#if defined(__cplusplus)
}
#endif

#endif
