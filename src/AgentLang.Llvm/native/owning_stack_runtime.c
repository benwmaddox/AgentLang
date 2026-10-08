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

int32_t al_owning_copy_external_bounded(
    al_owning_stack_context *ctx, uint32_t destination_offset,
    const uint8_t *source, uint32_t source_length, uint32_t source_offset,
    uint32_t payload_bytes, uint32_t extent_bytes, uint32_t type_id,
    uint32_t error_id) {
  uintptr_t source_address;
  uintptr_t source_end;
  uintptr_t stack_address;
  uintptr_t stack_end;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (source == 0 || source_offset > source_length ||
      extent_bytes > source_length - source_offset ||
      extent_bytes < payload_bytes) {
    al_owning_set_failure(
        ctx, AL_OWNING_STATUS_INVALID_REQUEST, error_id, extent_bytes,
        source_offset <= source_length ? source_length - source_offset : 0u);
    return 1;
  }
  if (ctx->stack_data == 0 ||
      !al_owning_range_valid(ctx, destination_offset, extent_bytes) ||
      destination_offset > ctx->cursor_bytes ||
      extent_bytes > ctx->cursor_bytes - destination_offset ||
      payload_bytes > (uint32_t)INT32_MAX ||
      ctx->live_payload_bytes > UINT32_MAX - payload_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          extent_bytes > UINT32_MAX - destination_offset
                              ? UINT32_MAX
                              : destination_offset + extent_bytes,
                          ctx->cursor_bytes);
    return 1;
  }
  if ((uintptr_t)source > UINTPTR_MAX - source_offset) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INVALID_REQUEST, error_id,
                          extent_bytes, source_length - source_offset);
    return 1;
  }
  source_address = (uintptr_t)source + source_offset;
  stack_address = (uintptr_t)ctx->stack_data;
  if (source_address > UINTPTR_MAX - extent_bytes ||
      stack_address > UINTPTR_MAX - ctx->stack_capacity_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INVALID_REQUEST, error_id,
                          extent_bytes, source_length - source_offset);
    return 1;
  }
  source_end = source_address + extent_bytes;
  stack_end = stack_address + ctx->stack_capacity_bytes;
  if (source_address < stack_end && stack_address < source_end) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INVALID_REQUEST, error_id,
                          extent_bytes, source_length - source_offset);
    return 1;
  }
  al_owning_bytes_copy(ctx->stack_data + destination_offset,
                       source + source_offset, extent_bytes);
  al_owning_mark(ctx, destination_offset, extent_bytes, 1, 0);
  ctx->input_copy_bytes += extent_bytes;
  al_owning_update_live(ctx, (int32_t)payload_bytes, 0);
  al_owning_event(ctx, AL_OWNING_EVENT_ALLOCATE, type_id, destination_offset,
                  extent_bytes, payload_bytes, 0u, 0u, 0u);
  return ctx->status == AL_OWNING_STATUS_OK ? 0 : 1;
}

int32_t al_owning_copy_constant(al_owning_stack_context *ctx,
                                uint32_t destination_offset,
                                const uint8_t *source, uint32_t source_length,
                                uint32_t payload_bytes, uint32_t extent_bytes,
                                uint32_t type_id, uint32_t error_id) {
  uintptr_t source_address;
  uintptr_t source_end;
  uintptr_t stack_address;
  uintptr_t stack_end;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (source_length != extent_bytes || extent_bytes < payload_bytes ||
      (extent_bytes != 0u && source == 0)) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          extent_bytes, source_length);
    return 1;
  }
  if (ctx->stack_data == 0 ||
      !al_owning_range_valid(ctx, destination_offset, extent_bytes) ||
      destination_offset > ctx->cursor_bytes ||
      extent_bytes > ctx->cursor_bytes - destination_offset ||
      payload_bytes > (uint32_t)INT32_MAX ||
      ctx->live_payload_bytes > UINT32_MAX - payload_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          extent_bytes > UINT32_MAX - destination_offset
                              ? UINT32_MAX
                              : destination_offset + extent_bytes,
                          ctx->cursor_bytes);
    return 1;
  }
  if (extent_bytes != 0u) {
    if ((uintptr_t)source > UINTPTR_MAX - extent_bytes) {
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                            extent_bytes, source_length);
      return 1;
    }
    source_address = (uintptr_t)source;
    source_end = source_address + extent_bytes;
    stack_address = (uintptr_t)ctx->stack_data;
    if (stack_address > UINTPTR_MAX - ctx->stack_capacity_bytes) {
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                            extent_bytes, ctx->stack_capacity_bytes);
      return 1;
    }
    stack_end = stack_address + ctx->stack_capacity_bytes;
    if (source_address < stack_end && stack_address < source_end) {
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                            extent_bytes, source_length);
      return 1;
    }
    al_owning_bytes_copy(ctx->stack_data + destination_offset, source,
                         extent_bytes);
  }
  al_owning_mark(ctx, destination_offset, extent_bytes, 1, 0);
  ctx->deep_copy_bytes += extent_bytes;
  al_owning_update_live(ctx, (int32_t)payload_bytes, 0);
  al_owning_event(ctx, AL_OWNING_EVENT_ALLOCATE, type_id, destination_offset,
                  extent_bytes, payload_bytes, 0u, 0u, 0u);
  return ctx->status == AL_OWNING_STATUS_OK ? 0 : 1;
}

typedef struct al_owning_scan_view {
  al_owning_stack_context *ctx;
  const uint8_t *bytes;
  uint32_t length;
  uint32_t error_id;
  int32_t external;
} al_owning_scan_view;

static void al_owning_scan_failure(al_owning_scan_view *view,
                                   uint32_t required_bytes,
                                   uint32_t available_bytes) {
  al_owning_set_failure(view->ctx,
                        view->external ? AL_OWNING_STATUS_INVALID_REQUEST
                                       : AL_OWNING_STATUS_INTERNAL,
                        view->error_id, required_bytes, available_bytes);
}

static uint32_t al_owning_saturating_add_u32(uint32_t left, uint32_t right) {
  return right > UINT32_MAX - left ? UINT32_MAX : left + right;
}

static int32_t al_owning_scan_readable(al_owning_scan_view *view,
                                       uint32_t offset, uint32_t byte_count) {
  if (offset > view->length || byte_count > view->length - offset) {
    al_owning_scan_failure(
        view, al_owning_saturating_add_u32(offset, byte_count), view->length);
    return 0;
  }
  if (!view->external &&
      (offset > view->ctx->stack_capacity_bytes ||
       byte_count > view->ctx->stack_capacity_bytes - offset ||
       offset > view->ctx->cursor_bytes ||
       byte_count > view->ctx->cursor_bytes - offset ||
       al_owning_check_initialized(view->ctx, offset, byte_count) != 0)) {
    if (view->ctx->status == AL_OWNING_STATUS_OK) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id,
                            al_owning_saturating_add_u32(offset, byte_count),
                            view->ctx->cursor_bytes);
    }
    return 0;
  }
  return 1;
}

static uint32_t al_owning_read_u32_le(const uint8_t *bytes, uint32_t offset) {
  return (uint32_t)bytes[offset] | ((uint32_t)bytes[offset + 1u] << 8u) |
         ((uint32_t)bytes[offset + 2u] << 16u) |
         ((uint32_t)bytes[offset + 3u] << 24u);
}

static void al_owning_write_u32_le(uint8_t *bytes, uint32_t offset,
                                   uint32_t value) {
  bytes[offset] = (uint8_t)value;
  bytes[offset + 1u] = (uint8_t)(value >> 8u);
  bytes[offset + 2u] = (uint8_t)(value >> 16u);
  bytes[offset + 3u] = (uint8_t)(value >> 24u);
}

typedef struct al_owning_scan_target {
  uint32_t parent_type_index;
  uint32_t parent_offset;
  uint32_t field_index;
  al_owning_field_location *location;
  int32_t found;
} al_owning_scan_target;

static int32_t al_owning_scan_value(al_owning_scan_view *view,
                                    const al_owning_layout *layout,
                                    uint32_t type_index, uint32_t offset,
                                    uint32_t containing_owner_end,
                                    uint32_t depth, uint32_t *payload_bytes,
                                    uint32_t *extent_bytes,
                                    al_owning_scan_target *target);

static int32_t al_owning_validate_layout_header(al_owning_stack_context *ctx,
                                                const al_owning_layout *layout,
                                                uint32_t error_id) {
  if (ctx == 0 || layout == 0 ||
      layout->abi_version != AL_OWNING_LAYOUT_ABI_VERSION ||
      layout->types == 0 || layout->type_count == 0u ||
      layout->type_count > AL_OWNING_LAYOUT_MAX_TYPES ||
      layout->field_count > AL_OWNING_LAYOUT_MAX_FIELDS ||
      (layout->field_count != 0u && layout->fields == 0)) {
    al_owning_set_failure(
        ctx, AL_OWNING_STATUS_INTERNAL, error_id,
        layout != 0 && layout->type_count > AL_OWNING_LAYOUT_MAX_TYPES
            ? layout->type_count
            : (layout != 0 && layout->field_count > AL_OWNING_LAYOUT_MAX_FIELDS
                   ? layout->field_count
                   : 0u),
        layout != 0 && layout->type_count > AL_OWNING_LAYOUT_MAX_TYPES
            ? AL_OWNING_LAYOUT_MAX_TYPES
            : (layout != 0 && layout->field_count > AL_OWNING_LAYOUT_MAX_FIELDS
                   ? AL_OWNING_LAYOUT_MAX_FIELDS
                   : 0u));
    return 0;
  }
  return 1;
}

static int32_t al_owning_validate_type_descriptor(
    al_owning_scan_view *view, const al_owning_layout *layout,
    uint32_t type_index, const al_owning_type_descriptor **out_type) {
  const al_owning_type_descriptor *type;
  int32_t payload_dynamic;
  int32_t extent_dynamic;
  if (type_index >= layout->type_count) {
    al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
                          type_index, layout->type_count);
    return 0;
  }
  type = &layout->types[type_index];
  if (type->first_field > layout->field_count ||
      type->field_count > layout->field_count - type->first_field) {
    al_owning_set_failure(
        view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
        al_owning_saturating_add_u32(type->first_field, type->field_count),
        layout->field_count);
    return 0;
  }
  payload_dynamic = type->fixed_payload_bytes == AL_OWNING_LAYOUT_DYNAMIC_U32;
  extent_dynamic = type->fixed_extent_bytes == AL_OWNING_LAYOUT_DYNAMIC_U32;
  if (payload_dynamic != extent_dynamic ||
      (type->kind != AL_OWNING_TYPE_I64 && type->kind != AL_OWNING_TYPE_BOOL &&
       type->kind != AL_OWNING_TYPE_UNIT &&
       type->kind != AL_OWNING_TYPE_RECORD &&
       type->kind != AL_OWNING_TYPE_STRING) ||
      type->minimum_extent_bytes < type->minimum_payload_bytes ||
      (!payload_dynamic &&
       (type->minimum_payload_bytes != type->fixed_payload_bytes ||
        type->minimum_extent_bytes != type->fixed_extent_bytes))) {
    al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
                          type_index, layout->type_count);
    return 0;
  }
  if ((type->kind == AL_OWNING_TYPE_I64 || type->kind == AL_OWNING_TYPE_BOOL ||
       type->kind == AL_OWNING_TYPE_UNIT) &&
      (type->field_count != 0u || payload_dynamic ||
       type->fixed_payload_bytes != 8u || type->fixed_extent_bytes != 8u)) {
    al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
                          type_index, layout->type_count);
    return 0;
  }
  if (type->kind == AL_OWNING_TYPE_STRING &&
      (type->field_count != 0u || !payload_dynamic ||
       type->minimum_payload_bytes != 8u || type->minimum_extent_bytes != 8u)) {
    al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
                          type_index, layout->type_count);
    return 0;
  }
  *out_type = type;
  return 1;
}

static int32_t al_owning_validate_type_graph_inner(
    al_owning_scan_view *view, const al_owning_layout *layout,
    uint32_t type_index, uint32_t depth,
    uint8_t state[AL_OWNING_LAYOUT_MAX_TYPES],
    uint8_t height[AL_OWNING_LAYOUT_MAX_TYPES], uint32_t *visited_fields) {
  const al_owning_type_descriptor *type;
  uint32_t index;
  uint64_t fixed_payload = 0u;
  uint64_t fixed_extent = 0u;
  uint64_t minimum_payload = 0u;
  uint64_t minimum_extent = 0u;
  int32_t dynamic = 0;
  int32_t preceding_dynamic = 0;
  uint32_t child_height_max = 0u;
  uint32_t computed_height;
  if (depth >= AL_OWNING_LAYOUT_MAX_DEPTH) {
    al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
                          depth + 1u, AL_OWNING_LAYOUT_MAX_DEPTH);
    return 0;
  }
  if (type_index >= layout->type_count) {
    al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
                          type_index, layout->type_count);
    return 0;
  }
  if (state[type_index] == 1u) {
    al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
                          type_index, layout->type_count);
    return 0;
  }
  if (state[type_index] == 2u) {
    if (depth + (uint32_t)height[type_index] > AL_OWNING_LAYOUT_MAX_DEPTH) {
      al_owning_set_failure(
          view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
          depth + (uint32_t)height[type_index], AL_OWNING_LAYOUT_MAX_DEPTH);
      return 0;
    }
    return 1;
  }
  if (!al_owning_validate_type_descriptor(view, layout, type_index, &type))
    return 0;
  state[type_index] = 1u;
  if (type->kind != AL_OWNING_TYPE_RECORD) {
    height[type_index] = 1u;
    state[type_index] = 2u;
    return 1;
  }

  for (index = 0u; index < type->field_count; ++index) {
    const al_owning_field_descriptor *field =
        &layout->fields[type->first_field + index];
    const al_owning_type_descriptor *child;
    uint32_t expected_offset;
    if (field->child_type_index >= layout->type_count ||
        (field->flags & ~AL_OWNING_FIELD_ZERO_WIDTH) != 0u ||
        field->reserved != 0u) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id, field->child_type_index,
                            layout->type_count);
      return 0;
    }
    if (*visited_fields >= AL_OWNING_LAYOUT_MAX_FIELDS) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id, *visited_fields + 1u,
                            AL_OWNING_LAYOUT_MAX_FIELDS);
      return 0;
    }
    ++*visited_fields;
    if (!al_owning_validate_type_graph_inner(
            view, layout, field->child_type_index, depth + 1u, state, height,
            visited_fields))
      return 0;
    if ((uint32_t)height[field->child_type_index] > child_height_max)
      child_height_max = (uint32_t)height[field->child_type_index];
    child = &layout->types[field->child_type_index];
    if (field->flags == AL_OWNING_FIELD_ZERO_WIDTH) {
      if (child->kind != AL_OWNING_TYPE_RECORD ||
          child->fixed_payload_bytes != 0u ||
          child->fixed_extent_bytes == AL_OWNING_LAYOUT_DYNAMIC_U32) {
        al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                              view->error_id, field->child_type_index,
                              layout->type_count);
        return 0;
      }
      if (preceding_dynamic) {
        if (field->fixed_offset_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32) {
          al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                                view->error_id, field->fixed_offset_bytes,
                                AL_OWNING_LAYOUT_DYNAMIC_U32);
          return 0;
        }
      } else if (fixed_extent > UINT32_MAX ||
                 field->fixed_offset_bytes != (uint32_t)fixed_extent) {
        al_owning_set_failure(
            view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
            field->fixed_offset_bytes,
            fixed_extent > UINT32_MAX ? UINT32_MAX : (uint32_t)fixed_extent);
        return 0;
      }
      continue;
    }
    if (child->kind == AL_OWNING_TYPE_RECORD &&
        child->fixed_payload_bytes == 0u &&
        child->fixed_extent_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id, field->child_type_index,
                            layout->type_count);
      return 0;
    }
    if (preceding_dynamic) {
      if (field->fixed_offset_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32) {
        al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                              view->error_id, field->fixed_offset_bytes,
                              AL_OWNING_LAYOUT_DYNAMIC_U32);
        return 0;
      }
    } else {
      if (fixed_extent > UINT32_MAX) {
        al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                              view->error_id, UINT32_MAX, 0u);
        return 0;
      }
      expected_offset = (uint32_t)fixed_extent;
      if (field->fixed_offset_bytes != expected_offset) {
        al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                              view->error_id, field->fixed_offset_bytes,
                              expected_offset);
        return 0;
      }
    }
    minimum_payload += child->minimum_payload_bytes;
    minimum_extent += child->minimum_extent_bytes;
    if (child->fixed_extent_bytes == AL_OWNING_LAYOUT_DYNAMIC_U32) {
      dynamic = 1;
      preceding_dynamic = 1;
    } else {
      fixed_payload += child->fixed_payload_bytes;
      fixed_extent += child->fixed_extent_bytes;
    }
    if (minimum_payload > UINT32_MAX || minimum_extent > UINT32_MAX ||
        fixed_payload > UINT32_MAX || fixed_extent > UINT32_MAX) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id, UINT32_MAX, 0u);
      return 0;
    }
  }
  if (minimum_payload == 0u)
    minimum_extent = 8u;
  if (minimum_payload != type->minimum_payload_bytes ||
      minimum_extent != type->minimum_extent_bytes ||
      dynamic != (type->fixed_extent_bytes == AL_OWNING_LAYOUT_DYNAMIC_U32) ||
      (!dynamic && (fixed_payload != type->fixed_payload_bytes ||
                    (fixed_payload == 0u ? 8u : fixed_extent) !=
                        type->fixed_extent_bytes))) {
    al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
                          type_index, layout->type_count);
    return 0;
  }
  computed_height = child_height_max + 1u;
  if (computed_height > AL_OWNING_LAYOUT_MAX_DEPTH - depth) {
    al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
                          depth + computed_height, AL_OWNING_LAYOUT_MAX_DEPTH);
    return 0;
  }
  height[type_index] = (uint8_t)computed_height;
  state[type_index] = 2u;
  return 1;
}

static void
al_owning_initialize_graph_memo(uint8_t state[AL_OWNING_LAYOUT_MAX_TYPES],
                                uint8_t height[AL_OWNING_LAYOUT_MAX_TYPES]) {
  volatile uint8_t *state_bytes = state;
  volatile uint8_t *height_bytes = height;
  uint32_t index;
  /* Keep this explicit volatile fill in the freestanding runtime: aggregate
   * zero initialization can introduce a CRT memset dependency in COFF. */
  for (index = 0u; index < AL_OWNING_LAYOUT_MAX_TYPES; ++index) {
    state_bytes[index] = 0u;
    height_bytes[index] = 0u;
  }
}

static int32_t al_owning_validate_type_graph(al_owning_scan_view *view,
                                             const al_owning_layout *layout,
                                             uint32_t type_index) {
  uint8_t state[AL_OWNING_LAYOUT_MAX_TYPES];
  uint8_t height[AL_OWNING_LAYOUT_MAX_TYPES];
  uint32_t visited_fields = 0u;
  al_owning_initialize_graph_memo(state, height);
  return al_owning_validate_type_graph_inner(view, layout, type_index, 0u,
                                             state, height, &visited_fields);
}

static int32_t al_owning_scan_string(al_owning_scan_view *view, uint32_t offset,
                                     uint32_t containing_owner_end,
                                     uint32_t *out_code_units,
                                     uint32_t *out_payload_bytes,
                                     uint32_t *out_extent_bytes) {
  uint32_t code_units;
  uint32_t payload;
  uint32_t extent;
  uint32_t index;
  uint64_t payload_wide;
  uint64_t extent_wide;
  uint32_t available;
  if ((offset & 7u) != 0u || offset > containing_owner_end ||
      containing_owner_end > view->length ||
      containing_owner_end - offset < 8u ||
      !al_owning_scan_readable(view, offset, 8u)) {
    if (view->ctx->status == AL_OWNING_STATUS_OK) {
      available =
          offset <= containing_owner_end ? containing_owner_end - offset : 0u;
      al_owning_scan_failure(view, 8u, available);
    }
    return 0;
  }
  code_units = al_owning_read_u32_le(view->bytes, offset);
  if (view->bytes[offset + 4u] != 0u || view->bytes[offset + 5u] != 0u ||
      view->bytes[offset + 6u] != 0u || view->bytes[offset + 7u] != 0u) {
    al_owning_scan_failure(
        view, 8u,
        containing_owner_end > offset ? containing_owner_end - offset : 0u);
    return 0;
  }
  payload_wide = 8ull + (uint64_t)code_units * 2ull;
  extent_wide = (payload_wide + 7ull) & ~7ull;
  available = containing_owner_end - offset;
  if (payload_wide > UINT32_MAX || extent_wide > UINT32_MAX ||
      extent_wide > available) {
    const uint32_t required =
        extent_wide > UINT32_MAX ? UINT32_MAX : (uint32_t)extent_wide;
    al_owning_scan_failure(view, required, available);
    return 0;
  }
  payload = (uint32_t)payload_wide;
  extent = (uint32_t)extent_wide;
  if (!al_owning_scan_readable(view, offset, extent))
    return 0;
  for (index = payload; index < extent; ++index) {
    if (view->bytes[offset + index] != 0u) {
      al_owning_scan_failure(view, extent, available);
      return 0;
    }
  }
  *out_code_units = code_units;
  *out_payload_bytes = payload;
  *out_extent_bytes = extent;
  return 1;
}

static int32_t al_owning_scan_value(al_owning_scan_view *view,
                                    const al_owning_layout *layout,
                                    uint32_t type_index, uint32_t offset,
                                    uint32_t containing_owner_end,
                                    uint32_t depth, uint32_t *payload_bytes,
                                    uint32_t *extent_bytes,
                                    al_owning_scan_target *target) {
  const al_owning_type_descriptor *type;
  uint32_t payload = 0u;
  uint32_t extent = 0u;
  uint32_t index;
  if (depth >= AL_OWNING_LAYOUT_MAX_DEPTH) {
    al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
                          depth + 1u, AL_OWNING_LAYOUT_MAX_DEPTH);
    return 0;
  }
  if (!al_owning_validate_type_descriptor(view, layout, type_index, &type))
    return 0;
  if (offset > containing_owner_end || containing_owner_end > view->length ||
      (offset & 7u) != 0u) {
    al_owning_scan_failure(view, offset, containing_owner_end);
    return 0;
  }
  if (type->kind == AL_OWNING_TYPE_STRING) {
    uint32_t code_units;
    if (!al_owning_scan_string(view, offset, containing_owner_end, &code_units,
                               &payload, &extent))
      return 0;
    (void)code_units;
  } else if (type->kind == AL_OWNING_TYPE_I64 ||
             type->kind == AL_OWNING_TYPE_BOOL ||
             type->kind == AL_OWNING_TYPE_UNIT) {
    if (containing_owner_end - offset < 8u ||
        !al_owning_scan_readable(view, offset, 8u))
      return 0;
    payload = 8u;
    extent = 8u;
  } else {
    uint64_t payload_wide = 0u;
    uint64_t extent_wide = 0u;
    uint64_t minimum_payload_wide = 0u;
    uint64_t minimum_extent_wide = 0u;
    int32_t child_is_dynamic = 0;
    if (target != 0 && target->location != 0 &&
        type_index == target->parent_type_index &&
        offset == target->parent_offset &&
        target->field_index >= type->field_count) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id, target->field_index,
                            type->field_count);
      return 0;
    }
    for (index = 0u; index < type->field_count; ++index) {
      const al_owning_field_descriptor *field =
          &layout->fields[type->first_field + index];
      const al_owning_type_descriptor *child;
      uint32_t field_payload = 0u;
      uint32_t field_extent = 0u;
      uint32_t field_offset;
      if (extent_wide > UINT32_MAX - offset ||
          extent_wide > containing_owner_end - offset) {
        al_owning_scan_failure(
            view, al_owning_saturating_add_u32(offset, (uint32_t)extent_wide),
            containing_owner_end);
        return 0;
      }
      field_offset = offset + (uint32_t)extent_wide;
      if (field->child_type_index >= layout->type_count ||
          (field->flags & ~AL_OWNING_FIELD_ZERO_WIDTH) != 0u ||
          field->reserved != 0u) {
        al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                              view->error_id, field->child_type_index,
                              layout->type_count);
        return 0;
      }
      child = &layout->types[field->child_type_index];
      if (child->first_field > layout->field_count ||
          child->field_count > layout->field_count - child->first_field) {
        al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                              view->error_id,
                              al_owning_saturating_add_u32(child->first_field,
                                                           child->field_count),
                              layout->field_count);
        return 0;
      }
      if (field->flags == AL_OWNING_FIELD_ZERO_WIDTH) {
        if (field->fixed_offset_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
            (child_is_dynamic ||
             field->fixed_offset_bytes != (uint32_t)extent_wide)) {
          al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                                view->error_id, field->fixed_offset_bytes,
                                (uint32_t)extent_wide);
          return 0;
        }
        if (child->kind != AL_OWNING_TYPE_RECORD ||
            child->fixed_payload_bytes != 0u ||
            child->fixed_extent_bytes == AL_OWNING_LAYOUT_DYNAMIC_U32) {
          al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                                view->error_id, field->child_type_index,
                                layout->type_count);
          return 0;
        }
      } else if (child->kind == AL_OWNING_TYPE_RECORD &&
                 child->fixed_payload_bytes == 0u &&
                 child->fixed_extent_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32) {
        al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                              view->error_id, field->child_type_index,
                              layout->type_count);
        return 0;
      }
      if (field->fixed_offset_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
          (child_is_dynamic ||
           field->fixed_offset_bytes != (uint32_t)extent_wide)) {
        al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                              view->error_id, field->fixed_offset_bytes,
                              (uint32_t)extent_wide);
        return 0;
      }
      if (target != 0 && target->location != 0 &&
          type_index == target->parent_type_index &&
          offset == target->parent_offset && target->field_index == index) {
        target->location->offset_bytes = field_offset;
        target->location->payload_bytes = 0u;
        target->location->extent_bytes = 0u;
        target->found = 1;
      }
      if (field->flags == AL_OWNING_FIELD_ZERO_WIDTH) {
        minimum_payload_wide += 0u;
        minimum_extent_wide += 0u;
      } else {
        if (!al_owning_scan_value(view, layout, field->child_type_index,
                                  field_offset, containing_owner_end,
                                  depth + 1u, &field_payload, &field_extent,
                                  target))
          return 0;
        minimum_payload_wide += child->minimum_payload_bytes;
        minimum_extent_wide += child->minimum_extent_bytes;
        child_is_dynamic = child_is_dynamic || child->fixed_extent_bytes ==
                                                   AL_OWNING_LAYOUT_DYNAMIC_U32;
      }
      if (payload_wide + field_payload > UINT32_MAX ||
          extent_wide + field_extent > UINT32_MAX ||
          minimum_payload_wide > UINT32_MAX ||
          minimum_extent_wide > UINT32_MAX) {
        al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                              view->error_id, UINT32_MAX,
                              containing_owner_end - offset);
        return 0;
      }
      payload_wide += field_payload;
      extent_wide += field_extent;
      if (field->flags == AL_OWNING_FIELD_ZERO_WIDTH && target != 0 &&
          target->location != 0 && type_index == target->parent_type_index &&
          offset == target->parent_offset && target->field_index == index) {
        target->location->payload_bytes = 0u;
        target->location->extent_bytes = 0u;
        target->found = 1;
      } else if (target != 0 && target->location != 0 &&
                 type_index == target->parent_type_index &&
                 offset == target->parent_offset &&
                 target->field_index == index) {
        target->location->payload_bytes = field_payload;
        target->location->extent_bytes = field_extent;
      }
    }
    if (payload_wide == 0u) {
      if (containing_owner_end - offset < 8u ||
          !al_owning_scan_readable(view, offset, 8u))
        return 0;
      for (index = 0u; index < 8u; ++index) {
        if (view->bytes[offset + index] != 0u) {
          al_owning_scan_failure(view, 8u, containing_owner_end - offset);
          return 0;
        }
      }
      extent_wide = 8u;
    }
    if (child_is_dynamic !=
        (type->fixed_extent_bytes == AL_OWNING_LAYOUT_DYNAMIC_U32)) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id, type_index, layout->type_count);
      return 0;
    }
    if (minimum_payload_wide == 0u && type->field_count == 0u)
      minimum_extent_wide = 8u;
    else if (minimum_payload_wide == 0u)
      minimum_extent_wide = 8u;
    if (minimum_payload_wide != type->minimum_payload_bytes ||
        minimum_extent_wide != type->minimum_extent_bytes) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id, (uint32_t)minimum_payload_wide,
                            type->minimum_payload_bytes);
      return 0;
    }
    if (type->fixed_payload_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
        (payload_wide != type->fixed_payload_bytes ||
         extent_wide != type->fixed_extent_bytes)) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id, (uint32_t)extent_wide,
                            type->fixed_extent_bytes);
      return 0;
    }
    if (extent_wide > containing_owner_end - offset) {
      al_owning_scan_failure(view, (uint32_t)extent_wide,
                             containing_owner_end - offset);
      return 0;
    }
    payload = (uint32_t)payload_wide;
    extent = (uint32_t)extent_wide;
  }
  *payload_bytes = payload;
  *extent_bytes = extent;
  return 1;
}

int32_t al_owning_measure_value(al_owning_stack_context *ctx,
                                const al_owning_layout *layout,
                                uint32_t type_index, uint32_t offset,
                                uint32_t containing_owner_end,
                                uint32_t error_id,
                                al_owning_value_size *out_value_size) {
  al_owning_scan_view view;
  uint32_t payload;
  uint32_t extent;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (out_value_size == 0 ||
      !al_owning_validate_layout_header(ctx, layout, error_id)) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 1;
  }
  if (containing_owner_end > ctx->cursor_bytes ||
      containing_owner_end > ctx->stack_capacity_bytes ||
      offset > containing_owner_end) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          containing_owner_end, ctx->cursor_bytes);
    return 1;
  }
  view.ctx = ctx;
  view.bytes = ctx->stack_data;
  view.length = containing_owner_end;
  view.error_id = error_id;
  view.external = 0;
  if (!al_owning_validate_type_graph(&view, layout, type_index))
    return 1;
  if (!al_owning_scan_value(&view, layout, type_index, offset,
                            containing_owner_end, 0u, &payload, &extent, 0))
    return 1;
  out_value_size->payload_bytes = payload;
  out_value_size->extent_bytes = extent;
  return 0;
}

int32_t al_owning_measure_external_value(
    al_owning_stack_context *ctx, const al_owning_layout *layout,
    uint32_t type_index, const uint8_t *source, uint32_t source_length,
    uint32_t source_offset, uint32_t error_id, uint32_t *out_payload_bytes,
    uint32_t *out_extent_bytes) {
  al_owning_scan_view view;
  uint32_t payload;
  uint32_t extent;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (out_payload_bytes == 0 || out_extent_bytes == 0 ||
      !al_owning_validate_layout_header(ctx, layout, error_id)) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 1;
  }
  if (source == 0 || source_offset > source_length) {
    al_owning_set_failure(
        ctx, AL_OWNING_STATUS_INVALID_REQUEST, error_id, 8u,
        source_offset <= source_length ? source_length - source_offset : 0u);
    return 1;
  }
  view.ctx = ctx;
  view.bytes = source;
  view.length = source_length;
  view.error_id = error_id;
  view.external = 1;
  if (!al_owning_validate_type_graph(&view, layout, type_index))
    return 1;
  if (!al_owning_scan_value(&view, layout, type_index, source_offset,
                            source_length, 0u, &payload, &extent, 0))
    return 1;
  *out_payload_bytes = payload;
  *out_extent_bytes = extent;
  return 0;
}

int32_t al_owning_locate_field(al_owning_stack_context *ctx,
                               const al_owning_layout *layout,
                               uint32_t parent_type_index,
                               uint32_t parent_offset, uint32_t parent_extent,
                               uint32_t field_index, uint32_t error_id,
                               al_owning_field_location *out_field_location) {
  al_owning_scan_view view;
  al_owning_scan_target target;
  al_owning_field_location measured_location;
  const al_owning_type_descriptor *parent;
  uint32_t parent_end;
  uint32_t parent_payload;
  uint32_t measured_extent;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (out_field_location == 0 ||
      !al_owning_validate_layout_header(ctx, layout, error_id)) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 1;
  }
  view.ctx = ctx;
  view.bytes = ctx->stack_data;
  view.length = ctx->stack_capacity_bytes;
  view.error_id = error_id;
  view.external = 0;
  if (!al_owning_validate_type_descriptor(&view, layout, parent_type_index,
                                          &parent) ||
      parent->kind != AL_OWNING_TYPE_RECORD ||
      field_index >= parent->field_count || parent_offset > ctx->cursor_bytes ||
      parent_extent > ctx->cursor_bytes - parent_offset ||
      parent_extent > UINT32_MAX - parent_offset) {
    if (ctx->status == AL_OWNING_STATUS_OK)
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                            parent_extent, ctx->cursor_bytes);
    return 1;
  }
  if (!al_owning_validate_type_graph(&view, layout, parent_type_index))
    return 1;
  parent_end = parent_offset + parent_extent;
  view.length = parent_end;
  target.parent_type_index = parent_type_index;
  target.parent_offset = parent_offset;
  target.field_index = field_index;
  target.location = &measured_location;
  target.found = 0;
  if (!al_owning_scan_value(&view, layout, parent_type_index, parent_offset,
                            parent_end, 0u, &parent_payload, &measured_extent,
                            &target))
    return 1;
  (void)parent_payload;
  if (measured_extent != parent_extent || !target.found) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          measured_extent, parent_extent);
    return 1;
  }
  *out_field_location = measured_location;
  return 0;
}

int32_t al_owning_string_length(al_owning_stack_context *ctx, uint32_t offset,
                                uint32_t extent_bytes, uint32_t error_id,
                                uint32_t *out_code_units) {
  al_owning_scan_view view;
  uint32_t code_units;
  uint32_t payload;
  uint32_t extent;
  uint32_t owner_end;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (out_code_units == 0 || offset > ctx->cursor_bytes ||
      extent_bytes > ctx->cursor_bytes - offset ||
      extent_bytes > UINT32_MAX - offset) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          extent_bytes, ctx->cursor_bytes);
    return 1;
  }
  owner_end = offset + extent_bytes;
  view.ctx = ctx;
  view.bytes = ctx->stack_data;
  view.length = owner_end;
  view.error_id = error_id;
  view.external = 0;
  if (!al_owning_scan_string(&view, offset, owner_end, &code_units, &payload,
                             &extent))
    return 1;
  if (extent != extent_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, extent,
                          extent_bytes);
    return 1;
  }
  *out_code_units = code_units;
  (void)payload;
  return 0;
}

static int32_t al_owning_string_scan_stack_exact(
    al_owning_stack_context *ctx, uint32_t offset, uint32_t extent_bytes,
    uint32_t error_id, uint32_t *out_code_units, uint32_t *out_payload_bytes) {
  al_owning_scan_view view;
  uint32_t owner_end;
  uint32_t measured_extent;
  if (offset > ctx->cursor_bytes || extent_bytes > ctx->cursor_bytes - offset ||
      extent_bytes > UINT32_MAX - offset) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          extent_bytes, ctx->cursor_bytes);
    return 0;
  }
  owner_end = offset + extent_bytes;
  view.ctx = ctx;
  view.bytes = ctx->stack_data;
  view.length = owner_end;
  view.error_id = error_id;
  view.external = 0;
  if (!al_owning_scan_string(&view, offset, owner_end, out_code_units,
                             out_payload_bytes, &measured_extent))
    return 0;
  if (measured_extent != extent_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          measured_extent, extent_bytes);
    return 0;
  }
  return 1;
}

int32_t al_owning_string_concat_plan(
    al_owning_stack_context *ctx, uint32_t left_offset,
    uint32_t left_extent_bytes, uint32_t right_offset,
    uint32_t right_extent_bytes, uint32_t error_id, uint32_t *out_code_units,
    uint32_t *out_payload_bytes, uint32_t *out_extent_bytes) {
  uint32_t left_units;
  uint32_t right_units;
  uint32_t left_payload;
  uint32_t right_payload;
  uint64_t units_wide;
  uint64_t payload_wide;
  uint64_t extent_wide;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (out_code_units == 0 || out_payload_bytes == 0 || out_extent_bytes == 0) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 1;
  }
  if (!al_owning_string_scan_stack_exact(ctx, left_offset, left_extent_bytes,
                                         error_id, &left_units,
                                         &left_payload) ||
      !al_owning_string_scan_stack_exact(ctx, right_offset, right_extent_bytes,
                                         error_id, &right_units,
                                         &right_payload))
    return 1;
  units_wide = (uint64_t)left_units + (uint64_t)right_units;
  payload_wide = 8ull + units_wide * 2ull;
  extent_wide = (payload_wide + 7ull) & ~7ull;
  if (units_wide > INT32_MAX || payload_wide > UINT32_MAX ||
      extent_wide > UINT32_MAX) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_DIAGNOSTIC, error_id,
                          UINT32_MAX, ctx->stack_capacity_bytes);
    return 1;
  }
  *out_code_units = (uint32_t)units_wide;
  *out_payload_bytes = (uint32_t)payload_wide;
  *out_extent_bytes = (uint32_t)extent_wide;
  (void)left_payload;
  (void)right_payload;
  return 0;
}

int32_t al_owning_string_concat_write(
    al_owning_stack_context *ctx, uint32_t destination_offset,
    uint32_t destination_extent_bytes, uint32_t left_offset,
    uint32_t left_extent_bytes, uint32_t right_offset,
    uint32_t right_extent_bytes, uint32_t type_id, uint32_t error_id) {
  uint32_t code_units;
  uint32_t payload;
  uint32_t extent;
  uint32_t left_units;
  uint32_t right_units;
  uint32_t ignored_payload;
  uint32_t left_data_bytes;
  uint32_t right_data_bytes;
  uint32_t cursor;
  uint32_t index;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (al_owning_string_concat_plan(ctx, left_offset, left_extent_bytes,
                                   right_offset, right_extent_bytes, error_id,
                                   &code_units, &payload, &extent) != 0)
    return 1;
  if (extent != destination_extent_bytes || (destination_offset & 7u) != 0u ||
      destination_offset > ctx->cursor_bytes ||
      destination_extent_bytes > ctx->cursor_bytes - destination_offset ||
      destination_offset > UINT32_MAX - destination_extent_bytes ||
      !al_owning_string_scan_stack_exact(ctx, left_offset, left_extent_bytes,
                                         error_id, &left_units,
                                         &ignored_payload) ||
      !al_owning_string_scan_stack_exact(ctx, right_offset, right_extent_bytes,
                                         error_id, &right_units,
                                         &ignored_payload)) {
    if (ctx->status == AL_OWNING_STATUS_OK)
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, extent,
                            destination_extent_bytes);
    return 1;
  }
  if ((destination_offset < left_offset + left_extent_bytes &&
       left_offset < destination_offset + destination_extent_bytes) ||
      (destination_offset < right_offset + right_extent_bytes &&
       right_offset < destination_offset + destination_extent_bytes)) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          destination_extent_bytes, destination_offset);
    return 1;
  }
  left_data_bytes = left_units * 2u;
  right_data_bytes = right_units * 2u;
  al_owning_write_u32_le(ctx->stack_data + destination_offset, 0u, code_units);
  ctx->stack_data[destination_offset + 4u] = 0u;
  ctx->stack_data[destination_offset + 5u] = 0u;
  ctx->stack_data[destination_offset + 6u] = 0u;
  ctx->stack_data[destination_offset + 7u] = 0u;
  cursor = destination_offset + 8u;
  for (index = 0u; index < left_data_bytes; ++index)
    ctx->stack_data[cursor + index] = ctx->stack_data[left_offset + 8u + index];
  cursor += left_data_bytes;
  for (index = 0u; index < right_data_bytes; ++index)
    ctx->stack_data[cursor + index] =
        ctx->stack_data[right_offset + 8u + index];
  cursor += right_data_bytes;
  while (cursor < destination_offset + extent)
    ctx->stack_data[cursor++] = 0u;
  al_owning_mark(ctx, destination_offset, extent, 1, 0);
  ctx->deep_copy_bytes += extent;
  al_owning_event(ctx, AL_OWNING_EVENT_STRING_CONCAT_LEFT, type_id,
                  destination_offset + 8u, left_data_bytes, left_data_bytes,
                  left_offset + 8u, left_data_bytes, 0u);
  al_owning_event(ctx, AL_OWNING_EVENT_STRING_CONCAT_RIGHT, type_id,
                  destination_offset + 8u + left_data_bytes, right_data_bytes,
                  right_data_bytes, right_offset + 8u, right_data_bytes, 0u);
  return 0;
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
        destination_end > source_offset ? destination_end : source_offset;
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

void al_owning_copy_range(al_owning_stack_context *ctx,
                          uint32_t destination_offset, uint32_t source_offset,
                          uint32_t extent_bytes, uint32_t payload_bytes,
                          uint32_t type_id, uint32_t event_kind) {
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return;
  /* Check the complete request before writing or updating counters. */
  if (payload_bytes > extent_bytes || payload_bytes > INT32_MAX ||
      payload_bytes > UINT32_MAX - ctx->live_payload_bytes ||
      !al_owning_range_valid(ctx, source_offset, extent_bytes) ||
      !al_owning_range_valid(ctx, destination_offset, extent_bytes) ||
      source_offset > ctx->cursor_bytes ||
      extent_bytes > ctx->cursor_bytes - source_offset ||
      destination_offset > ctx->cursor_bytes ||
      extent_bytes > ctx->cursor_bytes - destination_offset) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u, extent_bytes,
                          ctx->cursor_bytes);
    return;
  }
  if (extent_bytes != 0u && destination_offset < source_offset + extent_bytes &&
      source_offset < destination_offset + extent_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u, extent_bytes,
                          ctx->cursor_bytes);
    return;
  }
  if (al_owning_check_initialized(ctx, source_offset, extent_bytes) != 0)
    return;
  al_owning_bytes_copy(ctx->stack_data + destination_offset,
                       ctx->stack_data + source_offset, extent_bytes);
  al_owning_mark(ctx, destination_offset, extent_bytes, 1, 0);
  ctx->deep_copy_bytes += extent_bytes;
  al_owning_update_live(ctx, (int32_t)payload_bytes, 0);
  al_owning_event(ctx, event_kind, type_id, destination_offset, extent_bytes,
                  payload_bytes, source_offset, extent_bytes, 0u);
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
