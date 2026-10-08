#include "owning_stack_runtime.h"

#include <limits.h>

#define AL_OWNING_POISON 0xA5u

static uint32_t al_owning_bit_bytes(uint32_t byte_count) {
  return byte_count / 8u + ((byte_count & 7u) != 0u ? 1u : 0u);
}

static int32_t al_owning_range_valid(const al_owning_stack_context *ctx,
                                     uint32_t offset, uint32_t byte_count) {
  return offset <= ctx->stack_capacity_bytes &&
         byte_count <= ctx->stack_capacity_bytes - offset;
}

static int32_t al_owning_bit_get(const uint8_t *bits, uint32_t index) {
  return (bits[index >> 3] & (uint8_t)(1u << (index & 7u))) != 0u;
}

static void al_owning_bit_set(uint8_t *bits, uint32_t index, int32_t value) {
  const uint8_t mask = (uint8_t)(1u << (index & 7u));
  if (value) {
    bits[index >> 3] = (uint8_t)(bits[index >> 3] | mask);
  } else {
    bits[index >> 3] = (uint8_t)(bits[index >> 3] & (uint8_t)~mask);
  }
}

static void al_owning_mark(al_owning_stack_context *ctx, uint32_t offset,
                           uint32_t byte_count, int32_t initialized,
                           int32_t poisoned) {
  uint32_t index;
  for (index = 0u; index < byte_count; ++index) {
    al_owning_bit_set(ctx->init_bitmap, offset + index, initialized);
    if (poisoned)
      al_owning_bit_set(ctx->poison_bitmap, offset + index, 1);
    else if (!initialized)
      al_owning_bit_set(ctx->poison_bitmap, offset + index, 0);
  }
}

static void al_owning_fill(al_owning_stack_context *ctx, uint32_t offset,
                           uint32_t byte_count, uint8_t value) {
  uint32_t index;
  if (!al_owning_range_valid(ctx, offset, byte_count)) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u,
                          offset + byte_count, ctx->stack_capacity_bytes);
    return;
  }
  for (index = 0u; index < byte_count; ++index) {
    ctx->stack_data[offset + index] = value;
  }
}

static void al_owning_bytes_copy(uint8_t *destination, const uint8_t *source,
                                 uint32_t byte_count) {
  uint32_t index;
  for (index = 0u; index < byte_count; ++index) {
    destination[index] = source[index];
  }
}

static void al_owning_stack_bytes_move(al_owning_stack_context *ctx,
                                       uint32_t destination_offset,
                                       uint32_t source_offset,
                                       uint32_t byte_count) {
  if (destination_offset < source_offset) {
    uint32_t index;
    for (index = 0u; index < byte_count; ++index) {
      ctx->stack_data[destination_offset + index] =
          ctx->stack_data[source_offset + index];
    }
  } else if (destination_offset > source_offset) {
    uint32_t index = byte_count;
    while (index > 0u) {
      --index;
      ctx->stack_data[destination_offset + index] =
          ctx->stack_data[source_offset + index];
    }
  }
}

static uint64_t al_owning_checksum(const uint8_t *bytes, uint32_t byte_count) {
  uint64_t value = 1469598103934665603ull;
  uint32_t index;
  for (index = 0u; index < byte_count; ++index) {
    value ^= bytes[index];
    value *= 1099511628211ull;
  }
  return value;
}

static void al_owning_event(al_owning_stack_context *ctx, uint32_t kind,
                            uint32_t type_id, uint32_t offset,
                            uint32_t extent_bytes, uint32_t payload_bytes,
                            uint32_t source_offset,
                            uint32_t source_extent_bytes, uint64_t checksum) {
  al_owning_stack_event *event;
  if (ctx->trace_events == 0 || ctx->trace_event_capacity == 0u)
    return;
  if (ctx->trace_event_count >= ctx->trace_event_capacity) {
    ctx->trace_truncated = 1u;
    return;
  }
  event = &ctx->trace_events[ctx->trace_event_count++];
  event->kind = kind;
  event->type_id = type_id;
  event->offset = offset;
  event->extent_bytes = extent_bytes;
  event->payload_bytes = payload_bytes;
  event->source_offset = source_offset;
  event->source_extent_bytes = source_extent_bytes;
  event->flags = 0u;
  event->checksum = checksum;
}

void al_owning_set_failure(al_owning_stack_context *ctx, uint32_t status,
                           uint32_t error_id, uint32_t required_bytes,
                           uint32_t available_bytes) {
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return;
  ctx->status = status;
  ctx->error_id = error_id;
  ctx->required_bytes = required_bytes;
  ctx->available_bytes = available_bytes;
}

void al_owning_begin(al_owning_stack_context *ctx) {
  if (ctx == 0)
    return;
  ctx->cursor_bytes = 0u;
  ctx->peak_cursor_bytes = 0u;
  ctx->live_payload_bytes = 0u;
  ctx->peak_live_payload_bytes = 0u;
  ctx->active_local_reserved_bytes = 0u;
  ctx->peak_local_reserved_bytes = 0u;
  ctx->live_local_payload_bytes = 0u;
  ctx->peak_live_local_payload_bytes = 0u;
  ctx->steps_consumed = 0u;
  ctx->call_depth = 0u;
  ctx->trace_event_count = 0u;
  ctx->trace_truncated = 0u;
  ctx->duplicate_disjoint_checks = 0u;
  ctx->drop_survivor_checks = 0u;
  ctx->poison_reuse_checks = 0u;
  ctx->cursor_invariant_checks = 0u;
  ctx->frame_return_count = 0u;
  ctx->status = AL_OWNING_STATUS_OK;
  ctx->error_id = 0u;
  ctx->required_bytes = 0u;
  ctx->available_bytes = ctx->stack_capacity_bytes;
  ctx->deep_copy_bytes = 0u;
  ctx->move_bytes = 0u;
  ctx->input_copy_bytes = 0u;
  ctx->retained_copy_bytes = 0u;
  if (ctx->abi_version != AL_OWNING_STACK_ABI_VERSION ||
      ctx->stack_capacity_bytes > (uint32_t)INT32_MAX ||
      ctx->init_bitmap_bytes < al_owning_bit_bytes(ctx->stack_capacity_bytes) ||
      ctx->stack_data == 0 || ctx->init_bitmap == 0 ||
      ctx->poison_bitmap == 0) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INVALID_REQUEST, 0u,
                          al_owning_bit_bytes(ctx->stack_capacity_bytes),
                          ctx->init_bitmap_bytes);
    return;
  }
  {
    uint32_t index;
    for (index = 0u; index < ctx->init_bitmap_bytes; ++index) {
      ctx->init_bitmap[index] = 0u;
      ctx->poison_bitmap[index] = 0u;
    }
  }
}

int32_t al_owning_enter_frame(al_owning_stack_context *ctx, uint32_t error_id) {
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  /* The compiled entry body contributes one native frame before user calls.
   * Interpreter/ABI3 guard caller depth > 64, so the equivalent native limit
   * permits that entry frame plus 65 active user frames (depth 0..64 calls).
   */
  if (ctx->call_depth >= 66u) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_DIAGNOSTIC, error_id, 0u, 0u);
    return 1;
  }
  ++ctx->call_depth;
  al_owning_event(ctx, AL_OWNING_EVENT_FRAME_ENTER, ctx->call_depth,
                  ctx->cursor_bytes, 0u, 0u, 0u, 0u, 0u);
  return 0;
}

void al_owning_leave_frame(al_owning_stack_context *ctx) {
  if (ctx == 0)
    return;
  if (ctx->call_depth > 0u)
    --ctx->call_depth;
  ++ctx->frame_return_count;
  al_owning_event(ctx, AL_OWNING_EVENT_FRAME_RETURN, ctx->call_depth,
                  ctx->cursor_bytes, 0u, 0u, 0u, 0u, 0u);
}

int32_t al_owning_reserve_to(al_owning_stack_context *ctx, uint32_t new_cursor,
                             uint32_t error_id) {
  uint32_t index;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (new_cursor < ctx->cursor_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          ctx->cursor_bytes, new_cursor);
    return 1;
  }
  if (new_cursor > ctx->stack_capacity_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_STACK_CAPACITY, error_id,
                          new_cursor, ctx->stack_capacity_bytes);
    return 1;
  }
  for (index = ctx->cursor_bytes; index < new_cursor; ++index) {
    ctx->stack_data[index] = (uint8_t)AL_OWNING_POISON;
    al_owning_bit_set(ctx->init_bitmap, index, 0);
    al_owning_bit_set(ctx->poison_bitmap, index, 1);
  }
  ctx->cursor_bytes = new_cursor;
  if (new_cursor > ctx->peak_cursor_bytes)
    ctx->peak_cursor_bytes = new_cursor;
  return 0;
}

void al_owning_release_to(al_owning_stack_context *ctx, uint32_t new_cursor,
                          uint32_t event_kind, uint32_t type_id,
                          uint32_t payload_bytes) {
  uint32_t index;
  uint32_t old_cursor;
  if (ctx == 0)
    return;
  old_cursor = ctx->cursor_bytes;
  if (new_cursor > old_cursor) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u, new_cursor,
                          old_cursor);
    return;
  }
  if (event_kind != 0u)
    al_owning_event(ctx, event_kind, type_id, new_cursor,
                    old_cursor - new_cursor, payload_bytes, 0u, 0u, 0u);
  for (index = new_cursor; index < old_cursor; ++index) {
    ctx->stack_data[index] = (uint8_t)AL_OWNING_POISON;
    al_owning_bit_set(ctx->init_bitmap, index, 0);
    al_owning_bit_set(ctx->poison_bitmap, index, 1);
  }
  ctx->cursor_bytes = new_cursor;
}

int32_t al_owning_check_initialized(al_owning_stack_context *ctx,
                                    uint32_t offset, uint32_t byte_count) {
  uint32_t index;
  int32_t saw_poison = 0;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (!al_owning_range_valid(ctx, offset, byte_count) ||
      offset + byte_count > ctx->cursor_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u,
                          offset + byte_count, ctx->stack_capacity_bytes);
    return 1;
  }
  for (index = 0u; index < byte_count; ++index) {
    if (!al_owning_bit_get(ctx->init_bitmap, offset + index)) {
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u,
                            offset + index + 1u, ctx->stack_capacity_bytes);
      return 1;
    }
    if (al_owning_bit_get(ctx->poison_bitmap, offset + index))
      saw_poison = 1;
  }
  if (saw_poison) {
    ++ctx->poison_reuse_checks;
    for (index = 0u; index < byte_count; ++index)
      al_owning_bit_set(ctx->poison_bitmap, offset + index, 0);
  }
  return 0;
}

int64_t al_owning_load_i64(al_owning_stack_context *ctx, uint32_t offset) {
  uint64_t result = 0u;
  uint32_t index;
  if (al_owning_check_initialized(ctx, offset, 8u) != 0)
    return 0;
  for (index = 0u; index < 8u; ++index)
    result |= (uint64_t)ctx->stack_data[offset + index] << (index * 8u);
  return (int64_t)result;
}

void al_owning_store_i64(al_owning_stack_context *ctx, uint32_t offset,
                         int64_t value, uint32_t type_id) {
  uint64_t bits = (uint64_t)value;
  uint32_t index;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return;
  if (!al_owning_range_valid(ctx, offset, 8u) ||
      offset + 8u > ctx->cursor_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u, offset + 8u,
                          ctx->cursor_bytes);
    return;
  }
  for (index = 0u; index < 8u; ++index) {
    ctx->stack_data[offset + index] = (uint8_t)(bits >> (index * 8u));
    al_owning_bit_set(ctx->init_bitmap, offset + index, 1);
  }
  al_owning_event(ctx, AL_OWNING_EVENT_ALLOCATE, type_id, offset, 8u, 8u, 0u,
                  0u, 0u);
}

void al_owning_store_token(al_owning_stack_context *ctx, uint32_t offset,
                           uint32_t type_id) {
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return;
  if (!al_owning_range_valid(ctx, offset, 8u) ||
      offset + 8u > ctx->cursor_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u, offset + 8u,
                          ctx->cursor_bytes);
    return;
  }
  al_owning_fill(ctx, offset, 8u, 0u);
  al_owning_mark(ctx, offset, 8u, 1, 0);
  al_owning_event(ctx, AL_OWNING_EVENT_ALLOCATE, type_id, offset, 8u, 0u, 0u,
                  0u, 0u);
}

void al_owning_copy_external(al_owning_stack_context *ctx,
                             uint32_t destination_offset, const uint8_t *source,
                             uint32_t payload_bytes, uint32_t extent_bytes,
                             uint32_t type_id) {
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return;
  if (extent_bytes < payload_bytes ||
      !al_owning_range_valid(ctx, destination_offset, extent_bytes) ||
      destination_offset + extent_bytes > ctx->cursor_bytes ||
      (payload_bytes > 0u && source == 0)) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u,
                          destination_offset + extent_bytes, ctx->cursor_bytes);
    return;
  }
  al_owning_bytes_copy(ctx->stack_data + destination_offset, source,
                       payload_bytes);
  if (extent_bytes > payload_bytes)
    al_owning_fill(ctx, destination_offset + payload_bytes,
                   extent_bytes - payload_bytes, 0u);
  al_owning_mark(ctx, destination_offset, extent_bytes, 1, 0);
  ctx->input_copy_bytes += extent_bytes;
  al_owning_update_live(ctx, (int32_t)payload_bytes, 0);
  al_owning_event(ctx, AL_OWNING_EVENT_ALLOCATE, type_id, destination_offset,
                  extent_bytes, payload_bytes, 0u, 0u, 0u);
}

void al_owning_move_range(al_owning_stack_context *ctx,
                          uint32_t destination_offset, uint32_t source_offset,
                          uint32_t byte_count, uint32_t payload_bytes,
                          uint32_t type_id, uint32_t event_kind) {
  uint32_t source_end;
  uint32_t destination_end;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK || byte_count == 0u)
    return;
  if (!al_owning_range_valid(ctx, destination_offset, byte_count) ||
      !al_owning_range_valid(ctx, source_offset, byte_count) ||
      destination_offset + byte_count > ctx->cursor_bytes ||
      source_offset + byte_count > ctx->cursor_bytes ||
      al_owning_check_initialized(ctx, source_offset, byte_count) != 0) {
    if (ctx->status == AL_OWNING_STATUS_OK)
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u,
                            source_offset + byte_count,
                            ctx->stack_capacity_bytes);
    return;
  }
  al_owning_stack_bytes_move(ctx, destination_offset, source_offset,
                             byte_count);
  al_owning_mark(ctx, destination_offset, byte_count, 1, 0);
  source_end = source_offset + byte_count;
  destination_end = destination_offset + byte_count;
  if (destination_offset < source_offset) {
    const uint32_t clear_start =
        destination_end < source_end ? destination_end : source_end;
    if (clear_start < source_end) {
      al_owning_fill(ctx, clear_start, source_end - clear_start,
                     (uint8_t)AL_OWNING_POISON);
      al_owning_mark(ctx, clear_start, source_end - clear_start, 0, 1);
    }
  } else if (destination_offset > source_offset) {
    const uint32_t clear_end =
        destination_offset < source_end ? destination_offset : source_end;
    if (source_offset < clear_end) {
      al_owning_fill(ctx, source_offset, clear_end - source_offset,
                     (uint8_t)AL_OWNING_POISON);
      al_owning_mark(ctx, source_offset, clear_end - source_offset, 0, 1);
    }
  }
  ctx->move_bytes += byte_count;
  al_owning_event(ctx, event_kind, type_id, destination_offset, byte_count,
                  payload_bytes, source_offset, byte_count, 0u);
}

void al_owning_duplicate(al_owning_stack_context *ctx,
                         uint32_t destination_offset, uint32_t source_offset,
                         uint32_t extent_bytes, uint32_t payload_bytes,
                         uint32_t type_id) {
  uint64_t checksum;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return;
  if (!al_owning_range_valid(ctx, source_offset, extent_bytes) ||
      !al_owning_range_valid(ctx, destination_offset, extent_bytes) ||
      source_offset + extent_bytes > ctx->cursor_bytes ||
      destination_offset + extent_bytes > ctx->cursor_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u,
                          destination_offset + extent_bytes, ctx->cursor_bytes);
    return;
  }
  if (destination_offset < source_offset + extent_bytes &&
      source_offset < destination_offset + extent_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u,
                          destination_offset + extent_bytes, source_offset);
    return;
  }
  if (al_owning_check_initialized(ctx, source_offset, extent_bytes) != 0)
    return;
  al_owning_bytes_copy(ctx->stack_data + destination_offset,
                       ctx->stack_data + source_offset, extent_bytes);
  al_owning_mark(ctx, destination_offset, extent_bytes, 1, 0);
  checksum = al_owning_checksum(ctx->stack_data + source_offset, extent_bytes);
  ++ctx->duplicate_disjoint_checks;
  ctx->deep_copy_bytes += extent_bytes;
  al_owning_update_live(ctx, (int32_t)payload_bytes, 0);
  al_owning_event(ctx, AL_OWNING_EVENT_DUPLICATE, type_id, destination_offset,
                  extent_bytes, payload_bytes, source_offset, extent_bytes,
                  checksum);
}

void al_owning_drop(al_owning_stack_context *ctx, uint32_t start_offset,
                    uint32_t extent_bytes, uint32_t payload_bytes,
                    uint32_t type_id) {
  uint32_t index;
  uint32_t old_cursor;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return;
  old_cursor = ctx->cursor_bytes;
  if (start_offset > old_cursor || extent_bytes != old_cursor - start_offset) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u,
                          start_offset + extent_bytes, old_cursor);
    return;
  }
  if (al_owning_check_initialized(ctx, start_offset, extent_bytes) != 0)
    return;
  for (index = ctx->trace_event_count; index > 0u; --index) {
    al_owning_stack_event *event = &ctx->trace_events[index - 1u];
    if (event->kind == AL_OWNING_EVENT_DUPLICATE &&
        event->offset == start_offset && event->extent_bytes == extent_bytes &&
        event->flags == 0u) {
      uint32_t check_index;
      int32_t source_is_live =
          al_owning_range_valid(ctx, event->source_offset,
                                event->source_extent_bytes) &&
          event->source_offset + event->source_extent_bytes <= old_cursor;
      for (check_index = 0u;
           source_is_live && check_index < event->source_extent_bytes;
           ++check_index) {
        if (!al_owning_bit_get(ctx->init_bitmap,
                               event->source_offset + check_index))
          source_is_live = 0;
      }
      if (source_is_live &&
          al_owning_checksum(ctx->stack_data + event->source_offset,
                             event->source_extent_bytes) == event->checksum) {
        ++ctx->drop_survivor_checks;
      }
      event->flags = 1u;
      break;
    }
  }
  if (ctx->status != AL_OWNING_STATUS_OK)
    return;
  al_owning_event(ctx, AL_OWNING_EVENT_DROP, type_id, start_offset,
                  extent_bytes, payload_bytes, 0u, 0u, 0u);
  for (index = start_offset; index < old_cursor; ++index) {
    ctx->stack_data[index] = (uint8_t)AL_OWNING_POISON;
    al_owning_bit_set(ctx->init_bitmap, index, 0);
    al_owning_bit_set(ctx->poison_bitmap, index, 1);
  }
  ctx->cursor_bytes = start_offset;
  al_owning_update_live(ctx, -(int32_t)payload_bytes, 0);
}

void al_owning_store_local(al_owning_stack_context *ctx,
                           uint32_t destination_offset, uint32_t source_offset,
                           uint32_t reserved_bytes,
                           uint32_t source_extent_bytes,
                           uint32_t source_payload_bytes,
                           uint32_t old_payload_bytes, uint32_t type_id) {
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return;
  if (reserved_bytes < source_extent_bytes ||
      !al_owning_range_valid(ctx, destination_offset, reserved_bytes) ||
      destination_offset + reserved_bytes > ctx->cursor_bytes ||
      !al_owning_range_valid(ctx, source_offset, source_extent_bytes) ||
      source_offset + source_extent_bytes > ctx->cursor_bytes ||
      (destination_offset < source_offset + source_extent_bytes &&
       source_offset < destination_offset + reserved_bytes) ||
      al_owning_check_initialized(ctx, source_offset, source_extent_bytes) !=
          0) {
    if (ctx->status == AL_OWNING_STATUS_OK)
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u,
                            destination_offset + reserved_bytes,
                            ctx->stack_capacity_bytes);
    return;
  }
  al_owning_fill(ctx, destination_offset, reserved_bytes, 0u);
  al_owning_bytes_copy(ctx->stack_data + destination_offset,
                       ctx->stack_data + source_offset, source_extent_bytes);
  al_owning_mark(ctx, destination_offset, reserved_bytes, 1, 0);
  ctx->move_bytes += source_extent_bytes;
  al_owning_update_live(
      ctx, (int32_t)source_payload_bytes - (int32_t)old_payload_bytes,
      (int32_t)source_payload_bytes - (int32_t)old_payload_bytes);
  al_owning_event(ctx, AL_OWNING_EVENT_LOCAL_STORE, type_id, destination_offset,
                  source_extent_bytes, source_payload_bytes, source_offset,
                  source_extent_bytes, 0u);
}

void al_owning_load_local(al_owning_stack_context *ctx,
                          uint32_t destination_offset, uint32_t source_offset,
                          uint32_t extent_bytes, uint32_t payload_bytes,
                          uint32_t type_id) {
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return;
  if (!al_owning_range_valid(ctx, source_offset, extent_bytes) ||
      !al_owning_range_valid(ctx, destination_offset, extent_bytes) ||
      source_offset + extent_bytes > ctx->cursor_bytes ||
      destination_offset + extent_bytes > ctx->cursor_bytes ||
      al_owning_check_initialized(ctx, source_offset, extent_bytes) != 0) {
    if (ctx->status == AL_OWNING_STATUS_OK)
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u,
                            destination_offset + extent_bytes,
                            ctx->cursor_bytes);
    return;
  }
  al_owning_bytes_copy(ctx->stack_data + destination_offset,
                       ctx->stack_data + source_offset, extent_bytes);
  al_owning_mark(ctx, destination_offset, extent_bytes, 1, 0);
  ctx->deep_copy_bytes += extent_bytes;
  al_owning_update_live(ctx, (int32_t)payload_bytes, 0);
  al_owning_event(ctx, AL_OWNING_EVENT_LOCAL_LOAD, type_id, destination_offset,
                  extent_bytes, payload_bytes, source_offset, extent_bytes, 0u);
}

void al_owning_clear_local(al_owning_stack_context *ctx, uint32_t offset,
                           uint32_t reserved_bytes, uint32_t payload_bytes,
                           uint32_t type_id) {
  if (ctx == 0)
    return;
  if (!al_owning_range_valid(ctx, offset, reserved_bytes) ||
      offset + reserved_bytes > ctx->cursor_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u,
                          offset + reserved_bytes, ctx->stack_capacity_bytes);
    return;
  }
  al_owning_fill(ctx, offset, reserved_bytes, (uint8_t)AL_OWNING_POISON);
  al_owning_mark(ctx, offset, reserved_bytes, 0, 1);
  al_owning_update_live(ctx, -(int32_t)payload_bytes, -(int32_t)payload_bytes);
  al_owning_event(ctx, AL_OWNING_EVENT_SCOPE_CLEAR, type_id, offset,
                  reserved_bytes, payload_bytes, 0u, 0u, 0u);
}

void al_owning_local_reserve(al_owning_stack_context *ctx,
                             uint32_t byte_count) {
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return;
  if (ctx->active_local_reserved_bytes > ctx->stack_capacity_bytes ||
      byte_count >
          ctx->stack_capacity_bytes - ctx->active_local_reserved_bytes) {
    al_owning_set_failure(
        ctx, AL_OWNING_STATUS_INTERNAL, 0u, byte_count,
        ctx->active_local_reserved_bytes > ctx->stack_capacity_bytes
            ? 0u
            : ctx->stack_capacity_bytes - ctx->active_local_reserved_bytes);
    return;
  }
  ctx->active_local_reserved_bytes += byte_count;
  if (ctx->active_local_reserved_bytes > ctx->peak_local_reserved_bytes)
    ctx->peak_local_reserved_bytes = ctx->active_local_reserved_bytes;
}

void al_owning_local_release(al_owning_stack_context *ctx,
                             uint32_t byte_count) {
  if (ctx == 0)
    return;
  if (byte_count > ctx->active_local_reserved_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u, byte_count,
                          ctx->active_local_reserved_bytes);
    return;
  }
  ctx->active_local_reserved_bytes -= byte_count;
}

void al_owning_update_live(al_owning_stack_context *ctx, int32_t payload_delta,
                           int32_t local_delta) {
  int64_t live;
  int64_t local_live;
  const int64_t operand_delta = (int64_t)payload_delta - local_delta;
  if (ctx == 0)
    return;
  live = (int64_t)ctx->live_payload_bytes + operand_delta;
  local_live = (int64_t)ctx->live_local_payload_bytes + local_delta;
  if (live < 0 || local_live < 0 || live > UINT32_MAX ||
      local_live > UINT32_MAX) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u, 0u,
                          ctx->stack_capacity_bytes);
    return;
  }
  ctx->live_payload_bytes = (uint32_t)live;
  ctx->live_local_payload_bytes = (uint32_t)local_live;
  if (ctx->live_payload_bytes > ctx->peak_live_payload_bytes)
    ctx->peak_live_payload_bytes = ctx->live_payload_bytes;
  if (ctx->live_local_payload_bytes > ctx->peak_live_local_payload_bytes)
    ctx->peak_live_local_payload_bytes = ctx->live_local_payload_bytes;
}

void al_owning_record_layout(al_owning_stack_context *ctx, uint32_t event_kind,
                             uint32_t type_id, uint32_t offset,
                             uint32_t extent_bytes, uint32_t payload_bytes,
                             uint32_t source_offset,
                             uint32_t source_extent_bytes) {
  if (ctx == 0)
    return;
  al_owning_event(ctx, event_kind, type_id, offset, extent_bytes, payload_bytes,
                  source_offset, source_extent_bytes, 0u);
}

static void al_owning_reverse(uint8_t *bytes, uint32_t size) {
  uint32_t left = 0u;
  uint32_t right = size;
  while (left < right && left + 1u < right) {
    uint8_t temporary;
    --right;
    temporary = bytes[left];
    bytes[left] = bytes[right];
    bytes[right] = temporary;
    ++left;
  }
}

void al_owning_swap(al_owning_stack_context *ctx, uint32_t left_offset,
                    uint32_t left_extent, uint32_t right_extent,
                    uint32_t left_type_id, uint32_t right_type_id) {
  uint32_t total;
  uint8_t *bytes;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return;
  if (left_offset > ctx->cursor_bytes ||
      right_extent > ctx->cursor_bytes - left_offset ||
      left_extent != ctx->cursor_bytes - left_offset - right_extent) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u,
                          left_extent + right_extent,
                          ctx->cursor_bytes - (left_offset <= ctx->cursor_bytes
                                                   ? left_offset
                                                   : ctx->cursor_bytes));
    return;
  }
  total = left_extent + right_extent;
  if (al_owning_check_initialized(ctx, left_offset, total) != 0)
    return;
  bytes = ctx->stack_data + left_offset;
  al_owning_reverse(bytes, total);
  al_owning_reverse(bytes, right_extent);
  al_owning_reverse(bytes + right_extent, left_extent);
  al_owning_event(ctx, AL_OWNING_EVENT_CALL_RETURN_MOVE, left_type_id,
                  left_offset, right_extent, right_extent,
                  left_offset + left_extent, left_extent, 0u);
  al_owning_event(ctx, AL_OWNING_EVENT_CALL_RETURN_MOVE, right_type_id,
                  left_offset + right_extent, left_extent, left_extent,
                  left_offset, right_extent, 0u);
  ctx->move_bytes += total * 3ull;
}

int32_t al_owning_equal(al_owning_stack_context *ctx, uint32_t left_offset,
                        uint32_t right_offset, uint32_t byte_count) {
  uint32_t index;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK ||
      al_owning_check_initialized(ctx, left_offset, byte_count) != 0 ||
      al_owning_check_initialized(ctx, right_offset, byte_count) != 0)
    return 0;
  for (index = 0u; index < byte_count; ++index) {
    if (ctx->stack_data[left_offset + index] !=
        ctx->stack_data[right_offset + index])
      return 0;
  }
  return 1;
}

void al_owning_publish(al_owning_stack_context *ctx, uint8_t *retained,
                       uint32_t retained_capacity, uint32_t source_offset,
                       uint32_t byte_count, uint32_t type_id) {
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return;
  if (byte_count > retained_capacity || (byte_count > 0u && retained == 0)) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_RETAINED_CAPACITY, 0u,
                          byte_count, retained_capacity);
    return;
  }
  if (al_owning_check_initialized(ctx, source_offset, byte_count) != 0)
    return;
  if (byte_count > 0u)
    al_owning_bytes_copy(retained, ctx->stack_data + source_offset, byte_count);
  ctx->retained_copy_bytes += byte_count;
  al_owning_event(ctx, AL_OWNING_EVENT_RETAINED_COPY, type_id, 0u, byte_count,
                  byte_count, source_offset, byte_count, 0u);
}

int32_t al_owning_check_cursor(al_owning_stack_context *ctx,
                               uint32_t expected_cursor) {
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  ++ctx->cursor_invariant_checks;
  if (ctx->cursor_bytes != expected_cursor) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u, expected_cursor,
                          ctx->cursor_bytes);
    return 1;
  }
  return 0;
}

int32_t al_owning_charge_step(al_owning_stack_context *ctx, uint32_t error_id) {
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  ++ctx->steps_consumed;
  if (ctx->steps_consumed > 10000u) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_DIAGNOSTIC, error_id, 0u, 0u);
    return 1;
  }
  return 0;
}

uint8_t *al_owning_stack_base(al_owning_stack_context *ctx) {
  return ctx == 0 ? 0 : ctx->stack_data;
}
