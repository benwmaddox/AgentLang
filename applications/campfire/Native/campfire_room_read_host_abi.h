#ifndef AGENTLANG_CAMPFIRE_ROOM_READ_HOST_ABI_H
#define AGENTLANG_CAMPFIRE_ROOM_READ_HOST_ABI_H

#include "owning_stack_runtime.h"
#include <stddef.h>

#define AL_CAMPFIRE_ROOM_READ_HOST_ABI_VERSION 1u
#define AL_CAMPFIRE_ROOM_READ_OUTPUT_LIMIT 16777216u

enum al_campfire_room_read_operation {
  AL_CAMPFIRE_COOKIE_VERIFY = 1,
  AL_CAMPFIRE_SESSION_FIND = 2,
  AL_CAMPFIRE_SESSION_REFRESH = 3,
  AL_CAMPFIRE_USER_FIND = 4,
  AL_CAMPFIRE_USER_BOT_KEY = 5,
  /* 6 (browser.version-at-least?) is retired: version comparison is Flow. */
  AL_CAMPFIRE_ROOM_FIND = 7,
  AL_CAMPFIRE_MEMBERSHIP_EXISTS = 8,
  /* 9 (messages.last-page) is retired; tags are never reused. */
  AL_CAMPFIRE_MESSAGES_LAST_PAGE_WITH_CREATORS = 10
  /* 11-13 (csrf.session-secret, form-token, boost-tokens) are retired: Rails
   * CSRF tokens are Flow (csrf.flow) on the language stdlib. */
};

/* Borrowed owning-layout ABI5 root encoding, valid for this callback only.
 * The compiler supplies operation and type indexes as constants. Input storage
 * must remain live while output is reserved; adapters cannot retain pointers. */
typedef struct al_campfire_room_read_input {
  const uint8_t *data;
  uint32_t extent_bytes;
  uint32_t type_index;
} al_campfire_room_read_input;

/* The generated LLVM declaration is { ptr, i32, i32 } on the x64 target. */
typedef char al_campfire_input_size_check[
    sizeof(al_campfire_room_read_input) == 16u ? 1 : -1];
typedef char al_campfire_input_extent_offset_check[
    offsetof(al_campfire_room_read_input, extent_bytes) == 8u ? 1 : -1];
typedef char al_campfire_input_type_offset_check[
    offsetof(al_campfire_room_read_input, type_index) == 12u ? 1 : -1];

/* Exact closed profile only. Return zero with an exact bounded output extent,
 * or nonzero for a boundary failure. SQL errors are typed Result.Error values.
 * The caller validates output against output_type_index before committing it.
 * No operation tag or native symbol is supplied by language/request data. */
int32_t al_host_campfire_room_read_v1(
    void *provider_context, uint32_t operation,
    const al_owning_layout *layout,
    const al_campfire_room_read_input *inputs, uint32_t input_count,
    uint32_t output_type_index, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes);

#endif
