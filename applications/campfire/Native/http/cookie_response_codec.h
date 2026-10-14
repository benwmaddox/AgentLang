#ifndef AGENTLANG_CAMPFIRE_COOKIE_RESPONSE_CODEC_H
#define AGENTLANG_CAMPFIRE_COOKIE_RESPONSE_CODEC_H

#include "campfire_room_read_host_abi.h"

#if defined(_WIN32)
#define AL_CAMPFIRE_PURE_API __declspec(dllexport)
#else
#define AL_CAMPFIRE_PURE_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* The copied 252 pure callback API retains its own context identity. A 253
 * cookie-response caller must use the response-specific lifecycle functions
 * below; its context magic is intentionally distinct even though this private
 * prototype currently reuses the same physical layout. */
AL_CAMPFIRE_PURE_API uint32_t al_campfire_pure_context_size_v1(void);
AL_CAMPFIRE_PURE_API uint32_t al_campfire_pure_context_alignment_v1(void);
AL_CAMPFIRE_PURE_API int32_t al_campfire_pure_context_init_v1(
    void *storage, uint32_t storage_bytes, const uint8_t *secret_utf8,
    uint32_t secret_length);
AL_CAMPFIRE_PURE_API void
al_campfire_pure_context_destroy_v1(void *pure_context);

/* Each pinned worker supplies its own execution-scoped aligned workspace.
 * Destroy clears both fixed-purpose cookie keys and all codec scratch. The
 * secret is UTF-8, matching Encoding.UTF8.GetBytes in CookieService.fs. */
AL_CAMPFIRE_PURE_API uint32_t
al_campfire_cookie_response_context_size_v1(void);
AL_CAMPFIRE_PURE_API uint32_t
al_campfire_cookie_response_context_alignment_v1(void);
AL_CAMPFIRE_PURE_API int32_t al_campfire_cookie_response_context_init_v1(
    void *storage, uint32_t storage_bytes, const uint8_t *secret_utf8,
    uint32_t secret_length);
AL_CAMPFIRE_PURE_API void
al_campfire_cookie_response_context_destroy_v1(void *response_context);

/* Pure callbacks for the exact ABI5 host operation contracts. Inputs and
 * output are borrowed only for the duration of each call. Invalid cookie
 * values serialize Option.None; malformed ABI/layout/capacity returns nonzero. */
AL_CAMPFIRE_PURE_API int32_t al_campfire_verify_cookie_v1(
    void *pure_context, const al_owning_layout *layout,
    const al_campfire_room_read_input *inputs, uint32_t input_count,
    uint32_t output_type_index, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes);

AL_CAMPFIRE_PURE_API int32_t al_campfire_browser_version_v1(
    void *pure_context, const al_owning_layout *layout,
    const al_campfire_room_read_input *inputs, uint32_t input_count,
    uint32_t output_type_index, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes);

/* Private HTTP response-cookie prototype. These operations accept only a
 * context initialized by al_campfire_cookie_response_context_init_v1.
 * Inputs and output are borrowed only for the call; the context owns
 * fixed-purpose CNG keys and bounded scratch. */
AL_CAMPFIRE_PURE_API int32_t
al_campfire_cookie_response_sign_session_token_v1(
    void *response_context, const uint8_t *token_utf8, uint32_t token_bytes,
    int64_t now_unix_microseconds, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_bytes);

AL_CAMPFIRE_PURE_API int32_t
al_campfire_cookie_response_encrypt_session_json_v1(
    void *response_context, const uint8_t *session_json_utf8,
    uint32_t session_json_bytes, int64_t now_unix_microseconds,
    uint8_t *output, uint32_t output_capacity_bytes, uint32_t *output_bytes);

AL_CAMPFIRE_PURE_API int32_t
al_campfire_cookie_response_decrypt_session_json_v1(
    void *response_context, const uint8_t *raw_cookie_utf8, uint32_t cookie_bytes,
    int64_t now_unix_microseconds, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_bytes);

/* Fixed Transport.finalizeCookies session mutations for the room-read cases.
 * A zero cookie length means missing; any invalid/expired cookie starts from
 * an empty object. The trusted host supplies the already-resolved return URL.
 * Flags distinguish absent fields from explicit empty strings. Return 4 when
 * a duplicate top-level key makes JsonObject mutation fail in the managed
 * reference implementation. */
AL_CAMPFIRE_PURE_API int32_t
al_campfire_cookie_response_finalize_session_v1(
    void *response_context, const uint8_t *raw_cookie_utf8, uint32_t cookie_bytes,
    uint32_t has_return_url, const uint8_t *return_url_utf8,
    uint32_t return_url_bytes, uint32_t has_alert,
    const uint8_t *alert_utf8, uint32_t alert_bytes,
    int64_t now_unix_microseconds, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_bytes);

/* As v1, and stores csrf_utf8 as the session's "_csrf_token" when has_csrf
 * is set (a secret Flow generated for a session without one; report 271). */
AL_CAMPFIRE_PURE_API int32_t
al_campfire_cookie_response_finalize_session_v2(
    void *response_context, const uint8_t *raw_cookie_utf8, uint32_t cookie_bytes,
    uint32_t has_return_url, const uint8_t *return_url_utf8,
    uint32_t return_url_bytes, uint32_t has_alert,
    const uint8_t *alert_utf8, uint32_t alert_bytes,
    uint32_t has_csrf, const uint8_t *csrf_utf8, uint32_t csrf_bytes,
    int64_t now_unix_microseconds, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_bytes);

/* The Rails session's "_csrf_token" ASCII value: 0 when present, else 1. */
AL_CAMPFIRE_PURE_API int32_t
al_campfire_cookie_response_session_csrf_v1(
    void *response_context, const uint8_t *raw_cookie_utf8, uint32_t cookie_bytes,
    int64_t now_unix_microseconds, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_bytes);

#ifdef __cplusplus
}
#endif

#endif
