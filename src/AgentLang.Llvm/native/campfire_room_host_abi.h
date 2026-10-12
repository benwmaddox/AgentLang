#ifndef AGENTLANG_CAMPFIRE_ROOM_HOST_ABI_H
#define AGENTLANG_CAMPFIRE_ROOM_HOST_ABI_H

#include <stdint.h>

#define AL_CAMPFIRE_ROOM_HOST_ABI_VERSION 1u
#define AL_CAMPFIRE_ROOM_HOST_OUTPUT_LIMIT 65536u

/* Trusted build-time adapter for the exact compiler-approved contract:
 * UserId RoomId -> Result<Option<RoomRow>, DbError>, effect db.read.
 * provider_context is execution-scoped worker state, never a language value.
 * output is a borrowed writable arena suffix. Its representation uses owning
 * layout ABI4; the caller validates it before making it language-visible.
 * Neither output nor provider_context may be retained beyond the call.
 * Return zero with an exact serialized extent, or nonzero for a boundary
 * failure. Database errors are serialized Result.Error values, not nonzero
 * status. The adapter must respect output_capacity_bytes on every path. */
int32_t al_host_campfire_rooms_find_for_user_v1(
    void *provider_context, int64_t user_id, int64_t room_id, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes);

#endif
