#ifndef AGENTLANG_OWNING_STACK_PROFILE_H
#define AGENTLANG_OWNING_STACK_PROFILE_H

#include <stdint.h>

#ifndef AL_OWNING_DETAILED_PROFILE
#define AL_OWNING_DETAILED_PROFILE 0
#endif

#if AL_OWNING_DETAILED_PROFILE != 0 && AL_OWNING_DETAILED_PROFILE != 1
#error "AL_OWNING_DETAILED_PROFILE must be 0 or 1"
#endif

#define AL_OWNING_PROFILE_ABI_VERSION 1u
#define AL_OWNING_PROFILE_CATEGORY_COUNT 22u

typedef enum al_owning_profile_category {
  AL_OWNING_PROFILE_RESERVE_RELEASE = 0u,
  AL_OWNING_PROFILE_INITIALIZATION_CHECK = 1u,
  AL_OWNING_PROFILE_ARENA_COPY = 2u,
  AL_OWNING_PROFILE_VALUE_MEASURE = 3u,
  AL_OWNING_PROFILE_EXTERNAL_MEASURE = 4u,
  AL_OWNING_PROFILE_STRUCTURAL_LOCATE = 5u,
  AL_OWNING_PROFILE_HOST_OUTPUT_COMMIT = 6u,
  AL_OWNING_PROFILE_FRAME_VALIDATION = 7u,
  AL_OWNING_PROFILE_OTHER_RUNTIME_VALIDATION = 8u,
  AL_OWNING_PROFILE_COOKIE_VERIFY_SESSION_TOKEN = 9u,
  AL_OWNING_PROFILE_SESSION_FIND_BY_TOKEN = 10u,
  AL_OWNING_PROFILE_SESSION_REFRESH_BEFORE = 11u,
  AL_OWNING_PROFILE_USER_FIND_BY_ID = 12u,
  AL_OWNING_PROFILE_USER_AUTHENTICATE_BOT_KEY = 13u,
  AL_OWNING_PROFILE_BROWSER_VERSION_AT_LEAST = 14u,
  AL_OWNING_PROFILE_ROOM_FIND_FOR_USER = 15u,
  AL_OWNING_PROFILE_MEMBERSHIP_EXISTS = 16u,
  AL_OWNING_PROFILE_MESSAGE_LAST_PAGE = 17u,
  AL_OWNING_PROFILE_SQL_PREPARE = 18u,
  AL_OWNING_PROFILE_SQL_BIND = 19u,
  AL_OWNING_PROFILE_SQL_STEP = 20u,
  AL_OWNING_PROFILE_SQL_RESET = 21u
} al_owning_profile_category;

enum {
  AL_OWNING_PROFILE_STATUS_OK = 0,
  AL_OWNING_PROFILE_STATUS_INVALID_ARGUMENT = 1,
  AL_OWNING_PROFILE_STATUS_VERSION_UNSUPPORTED = 2,
  AL_OWNING_PROFILE_STATUS_BUFFER_TOO_SMALL = 3,
  AL_OWNING_PROFILE_STATUS_TIMER_FAILURE = 4,
  AL_OWNING_PROFILE_STATUS_CATEGORY_UNSUPPORTED = 5
};

typedef struct al_owning_profile_counter {
  uint64_t calls;
  uint64_t ticks;
  uint64_t failures;
} al_owning_profile_counter;

typedef struct al_owning_profile_snapshot {
  uint32_t abi_version;
  uint32_t size_bytes;
  uint32_t category_count;
  uint32_t reserved;
  uint64_t qpc_frequency;
  al_owning_profile_counter counters[AL_OWNING_PROFILE_CATEGORY_COUNT];
} al_owning_profile_snapshot;

#if AL_OWNING_DETAILED_PROFILE
#if defined(_WIN32)
#define AL_OWNING_PROFILE_EXPORT __declspec(dllexport)
#else
#define AL_OWNING_PROFILE_EXPORT
#endif

#ifdef __cplusplus
extern "C" {
#endif

AL_OWNING_PROFILE_EXPORT uint64_t al_owning_profile_begin_v1(void);
AL_OWNING_PROFILE_EXPORT int32_t al_owning_profile_end_v1(
    uint32_t category_id, uint64_t start_ticks, int32_t failed);
AL_OWNING_PROFILE_EXPORT int32_t
al_owning_profile_reset_v1(uint32_t abi_version);
AL_OWNING_PROFILE_EXPORT int32_t al_owning_profile_snapshot_v1(
    uint32_t abi_version, void *out, uint32_t capacity_bytes);

#ifdef __cplusplus
}
#endif
#endif

#endif
