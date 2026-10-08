#ifndef AGENTLANG_OWNING_STACK_RUNTIME_H
#define AGENTLANG_OWNING_STACK_RUNTIME_H

#include <stdint.h>

#define AL_OWNING_STACK_ABI_VERSION 1u

typedef struct al_owning_stack_event {
  uint32_t kind;
  uint32_t type_id;
  uint32_t offset;
  uint32_t extent_bytes;
  uint32_t payload_bytes;
  uint32_t source_offset;
  uint32_t source_extent_bytes;
  uint32_t flags;
  uint64_t checksum;
} al_owning_stack_event;

typedef struct al_owning_stack_context {
  uint32_t abi_version;
  uint32_t stack_capacity_bytes;
  uint32_t cursor_bytes;
  uint32_t peak_cursor_bytes;
  uint32_t live_payload_bytes;
  uint32_t peak_live_payload_bytes;
  uint32_t active_local_reserved_bytes;
  uint32_t peak_local_reserved_bytes;
  uint32_t live_local_payload_bytes;
  uint32_t peak_live_local_payload_bytes;
  uint32_t steps_consumed;
  uint32_t trace_event_count;
  uint32_t trace_event_capacity;
  uint32_t trace_truncated;
  uint32_t duplicate_disjoint_checks;
  uint32_t drop_survivor_checks;
  uint32_t poison_reuse_checks;
  uint32_t cursor_invariant_checks;
  uint32_t frame_return_count;
  uint32_t status;
  uint32_t error_id;
  uint32_t required_bytes;
  uint32_t available_bytes;
  uint32_t init_bitmap_bytes;
  uint32_t call_depth;
  uint8_t *stack_data;
  uint8_t *init_bitmap;
  uint8_t *poison_bitmap;
  al_owning_stack_event *trace_events;
  uint64_t deep_copy_bytes;
  uint64_t move_bytes;
  uint64_t input_copy_bytes;
  uint64_t retained_copy_bytes;
} al_owning_stack_context;

enum {
  AL_OWNING_STATUS_OK = 0,
  AL_OWNING_STATUS_DIAGNOSTIC = 1,
  AL_OWNING_STATUS_STACK_CAPACITY = 2,
  AL_OWNING_STATUS_RETAINED_CAPACITY = 3,
  AL_OWNING_STATUS_INVALID_REQUEST = 4,
  AL_OWNING_STATUS_INTERNAL = 5
};

enum {
  AL_OWNING_EVENT_ALLOCATE = 1,
  AL_OWNING_EVENT_DUPLICATE = 2,
  AL_OWNING_EVENT_DROP = 3,
  AL_OWNING_EVENT_LOCAL_STORE = 4,
  AL_OWNING_EVENT_LOCAL_LOAD = 5,
  AL_OWNING_EVENT_RECORD_BUILD = 6,
  AL_OWNING_EVENT_FIELD_EXTRACT = 7,
  AL_OWNING_EVENT_CALL_INPUT_MOVE = 8,
  AL_OWNING_EVENT_CALL_RETURN_MOVE = 9,
  AL_OWNING_EVENT_RETAINED_COPY = 10,
  AL_OWNING_EVENT_FRAME_ENTER = 11,
  AL_OWNING_EVENT_FRAME_RETURN = 12,
  AL_OWNING_EVENT_SCOPE_CLEAR = 13
};

void al_owning_begin(al_owning_stack_context *ctx);
int32_t al_owning_enter_frame(al_owning_stack_context *ctx, uint32_t error_id);
void al_owning_leave_frame(al_owning_stack_context *ctx);
int32_t al_owning_reserve_to(al_owning_stack_context *ctx, uint32_t new_cursor,
                             uint32_t error_id);
void al_owning_release_to(al_owning_stack_context *ctx, uint32_t new_cursor,
                          uint32_t event_kind, uint32_t type_id,
                          uint32_t payload_bytes);
int32_t al_owning_check_initialized(al_owning_stack_context *ctx,
                                    uint32_t offset, uint32_t byte_count);
int64_t al_owning_load_i64(al_owning_stack_context *ctx, uint32_t offset);
void al_owning_store_i64(al_owning_stack_context *ctx, uint32_t offset,
                         int64_t value, uint32_t type_id);
void al_owning_store_token(al_owning_stack_context *ctx, uint32_t offset,
                           uint32_t type_id);
void al_owning_copy_external(al_owning_stack_context *ctx,
                             uint32_t destination_offset, const uint8_t *source,
                             uint32_t payload_bytes, uint32_t extent_bytes,
                             uint32_t type_id);
void al_owning_move_range(al_owning_stack_context *ctx,
                          uint32_t destination_offset, uint32_t source_offset,
                          uint32_t byte_count, uint32_t payload_bytes,
                          uint32_t type_id, uint32_t event_kind);
void al_owning_duplicate(al_owning_stack_context *ctx,
                         uint32_t destination_offset, uint32_t source_offset,
                         uint32_t extent_bytes, uint32_t payload_bytes,
                         uint32_t type_id);
void al_owning_drop(al_owning_stack_context *ctx, uint32_t start_offset,
                    uint32_t extent_bytes, uint32_t payload_bytes,
                    uint32_t type_id);
void al_owning_store_local(al_owning_stack_context *ctx,
                           uint32_t destination_offset, uint32_t source_offset,
                           uint32_t reserved_bytes,
                           uint32_t source_extent_bytes,
                           uint32_t source_payload_bytes,
                           uint32_t old_payload_bytes, uint32_t type_id);
void al_owning_load_local(al_owning_stack_context *ctx,
                          uint32_t destination_offset, uint32_t source_offset,
                          uint32_t extent_bytes, uint32_t payload_bytes,
                          uint32_t type_id);
void al_owning_clear_local(al_owning_stack_context *ctx, uint32_t offset,
                           uint32_t reserved_bytes, uint32_t payload_bytes,
                           uint32_t type_id);
void al_owning_local_reserve(al_owning_stack_context *ctx, uint32_t byte_count);
void al_owning_local_release(al_owning_stack_context *ctx, uint32_t byte_count);
void al_owning_update_live(al_owning_stack_context *ctx, int32_t payload_delta,
                           int32_t local_delta);
void al_owning_record_layout(al_owning_stack_context *ctx, uint32_t event_kind,
                             uint32_t type_id, uint32_t offset,
                             uint32_t extent_bytes, uint32_t payload_bytes,
                             uint32_t source_offset,
                             uint32_t source_extent_bytes);
void al_owning_swap(al_owning_stack_context *ctx, uint32_t left_offset,
                    uint32_t left_extent, uint32_t right_extent,
                    uint32_t left_type_id, uint32_t right_type_id);
int32_t al_owning_equal(al_owning_stack_context *ctx, uint32_t left_offset,
                        uint32_t right_offset, uint32_t byte_count);
void al_owning_publish(al_owning_stack_context *ctx, uint8_t *retained,
                       uint32_t retained_capacity, uint32_t source_offset,
                       uint32_t byte_count, uint32_t type_id);
int32_t al_owning_check_cursor(al_owning_stack_context *ctx,
                               uint32_t expected_cursor);
int32_t al_owning_charge_step(al_owning_stack_context *ctx, uint32_t error_id);
void al_owning_set_failure(al_owning_stack_context *ctx, uint32_t status,
                           uint32_t error_id, uint32_t required_bytes,
                           uint32_t available_bytes);
uint8_t *al_owning_stack_base(al_owning_stack_context *ctx);

#endif
