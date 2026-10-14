#ifndef AGENTLANG_CAMPFIRE_ROOM_READ_PURE_H
#define AGENTLANG_CAMPFIRE_ROOM_READ_PURE_H

#include "campfire_room_read_host_abi.h"

#if defined(_WIN32)
#define AL_CAMPFIRE_PURE_API __declspec(dllexport)
#else
#define AL_CAMPFIRE_PURE_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* The host supplies execution-scoped aligned workspace. Its signing key and
 * cookie scratch are cleared by destroy. The secret is a UTF-8 byte sequence,
 * matching Encoding.UTF8.GetBytes in CookieService.fs. */
AL_CAMPFIRE_PURE_API uint32_t al_campfire_pure_context_size_v1(void);
AL_CAMPFIRE_PURE_API uint32_t al_campfire_pure_context_alignment_v1(void);
AL_CAMPFIRE_PURE_API int32_t al_campfire_pure_context_init_v1(
    void *storage, uint32_t storage_bytes, const uint8_t *secret_utf8,
    uint32_t secret_length);
AL_CAMPFIRE_PURE_API void
al_campfire_pure_context_destroy_v1(void *pure_context);

/* Pure callbacks for the exact ABI5 host operation contracts. Inputs and
 * output are borrowed only for the duration of each call. Invalid cookie
 * values serialize Option.None; malformed ABI/layout/capacity returns nonzero. */
AL_CAMPFIRE_PURE_API int32_t al_campfire_verify_cookie_v1(
    void *pure_context, const al_owning_layout *layout,
    const al_campfire_room_read_input *inputs, uint32_t input_count,
    uint32_t output_type_index, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes);

/* Rails `user.signed_id(purpose: :avatar)`: base64url(JSON) + "--" + hex
 * HMAC-SHA256, keyed by PBKDF2(secret_key_base, "active_record/signed_id",
 * 1000, 64). The key is derived once at context init. */
#define AL_CAMPFIRE_AVATAR_TOKEN_MAX_BYTES 160u
AL_CAMPFIRE_PURE_API uint32_t al_campfire_avatar_token_length_v1(int64_t user_id);
AL_CAMPFIRE_PURE_API int32_t al_campfire_avatar_token_v1(
    void *pure_context, int64_t user_id, uint8_t *output,
    uint32_t output_capacity, uint32_t *output_length);

#ifdef __cplusplus
}
#endif

#endif
