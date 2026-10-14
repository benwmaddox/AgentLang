#include "owning_stack_runtime.h"
#include "owning_stack_profile.h"
#include "float_format.h"

#include <limits.h>

#define AL_OWNING_POISON 0xA5u

#if AL_OWNING_DETAILED_PROFILE
/* Windows x64 uses the same 64-bit integer ABI for LARGE_INTEGER. Keep the
 * freestanding runtime independent of the Windows SDK headers. */
__declspec(dllimport) int32_t __stdcall QueryPerformanceCounter(int64_t *value);
__declspec(dllimport) int32_t __stdcall QueryPerformanceFrequency(int64_t *value);

static _Thread_local al_owning_profile_counter
    al_owning_profile_counters[AL_OWNING_PROFILE_CATEGORY_COUNT];
static _Thread_local uint32_t al_owning_profile_timer_failed;

static uint64_t al_owning_profile_saturating_add(uint64_t left,
                                                 uint64_t right) {
  return right > UINT64_MAX - left ? UINT64_MAX : left + right;
}

uint64_t al_owning_profile_begin_v1(void) {
  int64_t start_ticks = 0;
  if (!QueryPerformanceCounter(&start_ticks) || start_ticks <= 0) {
    al_owning_profile_timer_failed = 1u;
    return UINT64_MAX;
  }
  return (uint64_t)start_ticks;
}

int32_t al_owning_profile_end_v1(uint32_t category_id, uint64_t start_ticks,
                                 int32_t failed) {
  int64_t stop_ticks = 0;
  al_owning_profile_counter *counter;
  uint64_t elapsed_ticks;
  if (category_id >= AL_OWNING_PROFILE_CATEGORY_COUNT)
    return AL_OWNING_PROFILE_STATUS_CATEGORY_UNSUPPORTED;
  if (start_ticks == UINT64_MAX) {
    al_owning_profile_timer_failed = 1u;
    return AL_OWNING_PROFILE_STATUS_TIMER_FAILURE;
  }
  if (start_ticks == 0u || failed < 0 || failed > 1)
    return AL_OWNING_PROFILE_STATUS_INVALID_ARGUMENT;
  if (!QueryPerformanceCounter(&stop_ticks) || stop_ticks <= 0 ||
      (uint64_t)stop_ticks < start_ticks) {
    al_owning_profile_timer_failed = 1u;
    return AL_OWNING_PROFILE_STATUS_TIMER_FAILURE;
  }
  elapsed_ticks = (uint64_t)stop_ticks - start_ticks;
  counter = &al_owning_profile_counters[category_id];
  counter->calls = al_owning_profile_saturating_add(counter->calls, 1u);
  counter->ticks = al_owning_profile_saturating_add(counter->ticks,
                                                    elapsed_ticks);
  if (failed != 0)
    counter->failures =
        al_owning_profile_saturating_add(counter->failures, 1u);
  return AL_OWNING_PROFILE_STATUS_OK;
}

int32_t al_owning_profile_reset_v1(uint32_t abi_version) {
  uint32_t index;
  if (abi_version != AL_OWNING_PROFILE_ABI_VERSION)
    return AL_OWNING_PROFILE_STATUS_VERSION_UNSUPPORTED;
  for (index = 0u; index < AL_OWNING_PROFILE_CATEGORY_COUNT; ++index) {
    al_owning_profile_counters[index].calls = 0u;
    al_owning_profile_counters[index].ticks = 0u;
    al_owning_profile_counters[index].failures = 0u;
  }
  al_owning_profile_timer_failed = 0u;
  return AL_OWNING_PROFILE_STATUS_OK;
}

int32_t al_owning_profile_snapshot_v1(uint32_t abi_version, void *out,
                                      uint32_t capacity_bytes) {
  al_owning_profile_snapshot snapshot;
  uint32_t index;
  int64_t frequency = 0;
  uint8_t *destination;
  const uint8_t *source;
  uint32_t byte_index;
  if (abi_version != AL_OWNING_PROFILE_ABI_VERSION)
    return AL_OWNING_PROFILE_STATUS_VERSION_UNSUPPORTED;
  if (out == 0)
    return AL_OWNING_PROFILE_STATUS_INVALID_ARGUMENT;
  if (capacity_bytes < (uint32_t)sizeof(snapshot))
    return AL_OWNING_PROFILE_STATUS_BUFFER_TOO_SMALL;
  if (al_owning_profile_timer_failed != 0u)
    return AL_OWNING_PROFILE_STATUS_TIMER_FAILURE;
  if (!QueryPerformanceFrequency(&frequency) || frequency <= 0)
    return AL_OWNING_PROFILE_STATUS_TIMER_FAILURE;

  snapshot.abi_version = AL_OWNING_PROFILE_ABI_VERSION;
  snapshot.size_bytes = (uint32_t)sizeof(snapshot);
  snapshot.category_count = AL_OWNING_PROFILE_CATEGORY_COUNT;
  snapshot.reserved = 0u;
  snapshot.qpc_frequency = (uint64_t)frequency;
  for (index = 0u; index < AL_OWNING_PROFILE_CATEGORY_COUNT; ++index)
    snapshot.counters[index] = al_owning_profile_counters[index];

  /* Copy only after all validation and QPC calls have succeeded. Byte-wise
   * copying accepts unaligned caller storage without changing the ABI. */
  destination = (uint8_t *)out;
  source = (const uint8_t *)&snapshot;
  for (byte_index = 0u; byte_index < (uint32_t)sizeof(snapshot); ++byte_index)
    destination[byte_index] = source[byte_index];
  return AL_OWNING_PROFILE_STATUS_OK;
}

#define AL_OWNING_PROFILE_START() uint64_t al_profile_start = al_owning_profile_begin_v1()
#define AL_OWNING_PROFILE_END(category, failed) \
  ((void)al_owning_profile_end_v1((category), al_profile_start, (failed)))
#define AL_OWNING_PROFILE_IMPL(name) name##_impl
#else
#define AL_OWNING_PROFILE_START() ((void)0)
#define AL_OWNING_PROFILE_END(category, failed) ((void)0)
#define AL_OWNING_PROFILE_IMPL(name) name
#endif

#if !AL_OWNING_TRUSTED_GENERATED
static uint32_t al_owning_bit_bytes(uint32_t byte_count) {
  return byte_count / 8u + ((byte_count & 7u) != 0u ? 1u : 0u);
}
#endif

static int32_t al_owning_range_valid(const al_owning_stack_context *ctx,
                                     uint32_t offset, uint32_t byte_count) {
  return offset <= ctx->stack_capacity_bytes &&
         byte_count <= ctx->stack_capacity_bytes - offset;
}

static int32_t al_owning_address_ranges_overlap(uintptr_t left_address,
                                                uintptr_t left_bytes,
                                                uintptr_t right_address,
                                                uintptr_t right_bytes) {
  const uintptr_t maximum_address = ~(uintptr_t)0;
  if (left_bytes == 0u || right_bytes == 0u) return 0;
  if (left_address > maximum_address - left_bytes ||
      right_address > maximum_address - right_bytes)
    return 1;
  return left_address < right_address + right_bytes &&
         right_address < left_address + left_bytes;
}

#if !AL_OWNING_TRUSTED_GENERATED
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

static void al_owning_mark_bits(uint8_t *bitmap, uint32_t offset,
                                uint32_t byte_count, int32_t value) {
  while (byte_count != 0u) {
    const uint32_t bit_offset = offset & 7u;
    uint32_t chunk = 8u - bit_offset;
    uint8_t mask;
    uint32_t bits;
    if (chunk > byte_count) chunk = byte_count;
    bits = chunk == 8u ? 0xffu : ((1u << chunk) - 1u);
    mask = (uint8_t)(bits << bit_offset);
    if (value)
      bitmap[offset >> 3u] = (uint8_t)(bitmap[offset >> 3u] | mask);
    else
      bitmap[offset >> 3u] = (uint8_t)(bitmap[offset >> 3u] & (uint8_t)~mask);
    byte_count -= chunk;
    if (byte_count == 0u) return;
    offset += chunk;
  }
}

static int32_t al_owning_mark_can_batch(const al_owning_stack_context *ctx,
                                        uint32_t offset,
                                        uint32_t byte_count) {
  const uintptr_t maximum_address = ~(uintptr_t)0;
  const uint64_t last_bit = (uint64_t)offset + (uint64_t)byte_count - 1u;
  const uint32_t first_bitmap_byte = offset >> 3u;
  uint32_t last_bitmap_byte;
  uintptr_t bitmap_bytes;
  uintptr_t init_base, poison_base, init_range, poison_range;
  if (byte_count == 0u || last_bit > UINT32_MAX) return 0;
  last_bitmap_byte = (uint32_t)(last_bit >> 3u);
  bitmap_bytes = (uintptr_t)(last_bitmap_byte - first_bitmap_byte) + 1u;
  init_base = (uintptr_t)ctx->init_bitmap;
  poison_base = (uintptr_t)ctx->poison_bitmap;
  if (init_base > maximum_address - first_bitmap_byte ||
      poison_base > maximum_address - first_bitmap_byte)
    return 0;
  init_range = init_base + first_bitmap_byte;
  poison_range = poison_base + first_bitmap_byte;
  return !al_owning_address_ranges_overlap(
             init_range, bitmap_bytes, (uintptr_t)ctx,
             (uintptr_t)sizeof(*ctx)) &&
         !al_owning_address_ranges_overlap(
             poison_range, bitmap_bytes, (uintptr_t)ctx,
             (uintptr_t)sizeof(*ctx)) &&
         !al_owning_address_ranges_overlap(init_range, bitmap_bytes,
                                           poison_range, bitmap_bytes);
}
#endif

static void al_owning_mark(al_owning_stack_context *ctx, uint32_t offset,
                           uint32_t byte_count, int32_t initialized,
                           int32_t poisoned) {
#if AL_OWNING_TRUSTED_GENERATED
  (void)ctx;
  (void)offset;
  (void)byte_count;
  (void)initialized;
  (void)poisoned;
#else
  if (byte_count != 0u &&
      al_owning_mark_can_batch(ctx, offset, byte_count)) {
    al_owning_mark_bits(ctx->init_bitmap, offset, byte_count, initialized);
    if (poisoned)
      al_owning_mark_bits(ctx->poison_bitmap, offset, byte_count, 1);
    else if (!initialized)
      al_owning_mark_bits(ctx->poison_bitmap, offset, byte_count, 0);
  } else {
    uint32_t index;
    for (index = 0u; index < byte_count; ++index) {
      al_owning_bit_set(ctx->init_bitmap, offset + index, initialized);
      if (poisoned)
        al_owning_bit_set(ctx->poison_bitmap, offset + index, 1);
      else if (!initialized)
        al_owning_bit_set(ctx->poison_bitmap, offset + index, 0);
    }
  }
#endif
}

static int32_t al_owning_fill_cache_allowed(const al_owning_stack_context *ctx,
                                            uint32_t offset,
                                            uint32_t byte_count) {
  const uintptr_t maximum_address = ~(uintptr_t)0;
  const uintptr_t stack_base = (uintptr_t)ctx->stack_data;
  uintptr_t destination_address;
  if (byte_count == 0u || stack_base > maximum_address - offset) return 0;
  destination_address = stack_base + offset;
  return !al_owning_address_ranges_overlap(
      destination_address, (uintptr_t)byte_count, (uintptr_t)ctx,
      (uintptr_t)sizeof(*ctx));
}

static void al_owning_fill(al_owning_stack_context *ctx, uint32_t offset,
                           uint32_t byte_count, uint8_t value) {
  uint32_t index;
  if (!al_owning_range_valid(ctx, offset, byte_count)) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u,
                          offset + byte_count, ctx->stack_capacity_bytes);
    return;
  }
  if (byte_count == 0u) return;
  if (al_owning_fill_cache_allowed(ctx, offset, byte_count)) {
    uint8_t *stack_data = ctx->stack_data + offset;
    for (index = 0u; index < byte_count; ++index) {
      stack_data[index] = value;
    }
  } else {
    for (index = 0u; index < byte_count; ++index) {
      ctx->stack_data[offset + index] = value;
    }
  }
}

static void al_owning_poison(al_owning_stack_context *ctx, uint32_t offset,
                             uint32_t byte_count) {
#if AL_OWNING_TRUSTED_GENERATED
  (void)ctx;
  (void)offset;
  (void)byte_count;
#else
  al_owning_fill(ctx, offset, byte_count, (uint8_t)AL_OWNING_POISON);
  al_owning_mark(ctx, offset, byte_count, 0, 1);
#endif
}

/* The runtime is freestanding (no libc memcpy), so bulk copies move 16-byte
 * unaligned chunks. Each chunk is loaded before it is stored, which keeps the
 * forward copy correct when the destination is below the source and the
 * backward copy correct when it is above. */
typedef uint8_t al_owning_chunk16
    __attribute__((vector_size(16), aligned(1), may_alias));

static void al_owning_bytes_copy(uint8_t *destination, const uint8_t *source,
                                 uint32_t byte_count) {
  uint32_t index = 0u;
  for (; byte_count - index >= 16u; index += 16u) {
    const al_owning_chunk16 chunk =
        *(const al_owning_chunk16 *)(const void *)(source + index);
    *(al_owning_chunk16 *)(void *)(destination + index) = chunk;
  }
  for (; index < byte_count; ++index)
    destination[index] = source[index];
}

static void al_owning_stack_bytes_move(al_owning_stack_context *ctx,
                                       uint32_t destination_offset,
                                       uint32_t source_offset,
                                       uint32_t byte_count) {
  uint8_t *destination = ctx->stack_data + destination_offset;
  const uint8_t *source = ctx->stack_data + source_offset;
  if (destination_offset < source_offset) {
    al_owning_bytes_copy(destination, source, byte_count);
  } else if (destination_offset > source_offset) {
    uint32_t remaining = byte_count;
    while (remaining >= 16u) {
      remaining -= 16u;
      {
        const al_owning_chunk16 chunk =
            *(const al_owning_chunk16 *)(const void *)(source + remaining);
        *(al_owning_chunk16 *)(void *)(destination + remaining) = chunk;
      }
    }
    while (remaining > 0u) {
      --remaining;
      destination[remaining] = source[remaining];
    }
  }
}

#if !AL_OWNING_TRUSTED_GENERATED
static uint64_t al_owning_checksum(const uint8_t *bytes, uint32_t byte_count) {
  uint64_t value = 1469598103934665603ull;
  uint32_t index;
  for (index = 0u; index < byte_count; ++index) {
    value ^= bytes[index];
    value *= 1099511628211ull;
  }
  return value;
}
#endif

static void al_owning_event(al_owning_stack_context *ctx, uint32_t kind,
                            uint32_t type_id, uint32_t offset,
                            uint32_t extent_bytes, uint32_t payload_bytes,
                            uint32_t source_offset,
                            uint32_t source_extent_bytes, uint64_t checksum) {
#if AL_OWNING_TRUSTED_GENERATED
  (void)ctx;
  (void)kind;
  (void)type_id;
  (void)offset;
  (void)extent_bytes;
  (void)payload_bytes;
  (void)source_offset;
  (void)source_extent_bytes;
  (void)checksum;
#else
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
#endif
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
  ctx->diagnostic_float_bits = 0u;
}

int32_t AL_OWNING_PROFILE_IMPL(al_owning_build_profile_valid)(
    const al_owning_stack_context *ctx) {
  if (ctx == 0 || ctx->abi_version != AL_OWNING_STACK_ABI_VERSION ||
      ctx->stack_capacity_bytes > (uint32_t)INT32_MAX ||
      ctx->stack_data == 0)
    return 0;
#if AL_OWNING_TRUSTED_GENERATED
  return ctx->init_bitmap == 0 && ctx->poison_bitmap == 0 &&
         ctx->init_bitmap_bytes == 0u;
#else
  return ctx->init_bitmap_bytes >=
             al_owning_bit_bytes(ctx->stack_capacity_bytes) &&
         ctx->init_bitmap != 0 && ctx->poison_bitmap != 0;
#endif
}

#if AL_OWNING_DETAILED_PROFILE
int32_t al_owning_build_profile_valid(const al_owning_stack_context *ctx) {
  AL_OWNING_PROFILE_START();
  int32_t result = al_owning_build_profile_valid_impl(ctx);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_FRAME_VALIDATION, result == 0);
  return result;
}
#endif

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
  ctx->diagnostic_float_bits = 0u;
  if (!al_owning_build_profile_valid(ctx)) {
#if AL_OWNING_TRUSTED_GENERATED
    const uint32_t required_bitmap_bytes = 0u;
#else
    const uint32_t required_bitmap_bytes =
        al_owning_bit_bytes(ctx->stack_capacity_bytes);
#endif
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INVALID_REQUEST, 0u,
                          required_bitmap_bytes, ctx->init_bitmap_bytes);
    return;
  }
#if !AL_OWNING_TRUSTED_GENERATED
  {
    uint32_t index;
    for (index = 0u; index < ctx->init_bitmap_bytes; ++index) {
      ctx->init_bitmap[index] = 0u;
      ctx->poison_bitmap[index] = 0u;
    }
  }
#endif
}

int32_t AL_OWNING_PROFILE_IMPL(al_owning_enter_frame)(
    al_owning_stack_context *ctx, uint32_t error_id) {
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

#if AL_OWNING_DETAILED_PROFILE
int32_t al_owning_enter_frame(al_owning_stack_context *ctx,
                              uint32_t error_id) {
  AL_OWNING_PROFILE_START();
  int32_t result = al_owning_enter_frame_impl(ctx, error_id);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_FRAME_VALIDATION, result != 0);
  return result;
}
#endif

void AL_OWNING_PROFILE_IMPL(al_owning_leave_frame)(
    al_owning_stack_context *ctx) {
  if (ctx == 0)
    return;
  if (ctx->call_depth > 0u)
    --ctx->call_depth;
  ++ctx->frame_return_count;
  al_owning_event(ctx, AL_OWNING_EVENT_FRAME_RETURN, ctx->call_depth,
                  ctx->cursor_bytes, 0u, 0u, 0u, 0u, 0u);
}

#if AL_OWNING_DETAILED_PROFILE
void al_owning_leave_frame(al_owning_stack_context *ctx) {
  AL_OWNING_PROFILE_START();
  uint32_t prior_status = ctx == 0 ? AL_OWNING_STATUS_INTERNAL : ctx->status;
  al_owning_leave_frame_impl(ctx);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_FRAME_VALIDATION,
                        ctx == 0 ||
                            (prior_status == AL_OWNING_STATUS_OK &&
                             ctx->status != AL_OWNING_STATUS_OK));
}
#endif

int32_t AL_OWNING_PROFILE_IMPL(al_owning_reserve_to)(
    al_owning_stack_context *ctx, uint32_t new_cursor, uint32_t error_id) {
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
  al_owning_poison(ctx, ctx->cursor_bytes, new_cursor - ctx->cursor_bytes);
  ctx->cursor_bytes = new_cursor;
  if (new_cursor > ctx->peak_cursor_bytes)
    ctx->peak_cursor_bytes = new_cursor;
  return 0;
}

#if AL_OWNING_DETAILED_PROFILE
int32_t al_owning_reserve_to(al_owning_stack_context *ctx,
                             uint32_t new_cursor, uint32_t error_id) {
  AL_OWNING_PROFILE_START();
  int32_t result = al_owning_reserve_to_impl(ctx, new_cursor, error_id);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_RESERVE_RELEASE, result != 0);
  return result;
}
#endif

void AL_OWNING_PROFILE_IMPL(al_owning_release_to)(
    al_owning_stack_context *ctx, uint32_t new_cursor, uint32_t event_kind,
    uint32_t type_id, uint32_t payload_bytes) {
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
  al_owning_poison(ctx, new_cursor, old_cursor - new_cursor);
  ctx->cursor_bytes = new_cursor;
}

#if AL_OWNING_DETAILED_PROFILE
void al_owning_release_to(al_owning_stack_context *ctx, uint32_t new_cursor,
                          uint32_t event_kind, uint32_t type_id,
                          uint32_t payload_bytes) {
  AL_OWNING_PROFILE_START();
  uint32_t prior_status = ctx == 0 ? AL_OWNING_STATUS_INTERNAL : ctx->status;
  al_owning_release_to_impl(ctx, new_cursor, event_kind, type_id,
                            payload_bytes);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_RESERVE_RELEASE,
                        ctx == 0 ||
                            (prior_status == AL_OWNING_STATUS_OK &&
                             ctx->status != AL_OWNING_STATUS_OK));
}
#endif

int32_t AL_OWNING_PROFILE_IMPL(al_owning_check_initialized)(
    al_owning_stack_context *ctx, uint32_t offset, uint32_t byte_count) {
#if !AL_OWNING_TRUSTED_GENERATED
  uint32_t index;
  int32_t saw_poison = 0;
#endif
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (!al_owning_range_valid(ctx, offset, byte_count) ||
      offset + byte_count > ctx->cursor_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, 0u,
                          offset + byte_count, ctx->stack_capacity_bytes);
    return 1;
  }
#if AL_OWNING_TRUSTED_GENERATED
  return 0;
#else
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
#endif
}

#if AL_OWNING_DETAILED_PROFILE
int32_t al_owning_check_initialized(al_owning_stack_context *ctx,
                                    uint32_t offset, uint32_t byte_count) {
  AL_OWNING_PROFILE_START();
  int32_t result =
      al_owning_check_initialized_impl(ctx, offset, byte_count);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_INITIALIZATION_CHECK,
                        result != 0);
  return result;
}
#endif

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
  }
  al_owning_mark(ctx, offset, 8u, 1, 0);
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

void AL_OWNING_PROFILE_IMPL(al_owning_copy_external)(
    al_owning_stack_context *ctx, uint32_t destination_offset,
    const uint8_t *source, uint32_t payload_bytes, uint32_t extent_bytes,
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

#if AL_OWNING_DETAILED_PROFILE
void al_owning_copy_external(al_owning_stack_context *ctx,
                             uint32_t destination_offset, const uint8_t *source,
                             uint32_t payload_bytes, uint32_t extent_bytes,
                             uint32_t type_id) {
  AL_OWNING_PROFILE_START();
  uint32_t prior_status = ctx == 0 ? AL_OWNING_STATUS_INTERNAL : ctx->status;
  al_owning_copy_external_impl(ctx, destination_offset, source, payload_bytes,
                               extent_bytes, type_id);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_ARENA_COPY,
                        ctx == 0 ||
                            (prior_status == AL_OWNING_STATUS_OK &&
                             ctx->status != AL_OWNING_STATUS_OK));
}
#endif

int32_t AL_OWNING_PROFILE_IMPL(al_owning_copy_external_bounded)(
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

#if AL_OWNING_DETAILED_PROFILE
int32_t al_owning_copy_external_bounded(
    al_owning_stack_context *ctx, uint32_t destination_offset,
    const uint8_t *source, uint32_t source_length, uint32_t source_offset,
    uint32_t payload_bytes, uint32_t extent_bytes, uint32_t type_id,
    uint32_t error_id) {
  AL_OWNING_PROFILE_START();
  int32_t result = al_owning_copy_external_bounded_impl(
      ctx, destination_offset, source, source_length, source_offset,
      payload_bytes, extent_bytes, type_id, error_id);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_ARENA_COPY, result != 0);
  return result;
}
#endif

int32_t AL_OWNING_PROFILE_IMPL(al_owning_copy_constant)(
    al_owning_stack_context *ctx, uint32_t destination_offset,
    const uint8_t *source, uint32_t source_length, uint32_t payload_bytes,
    uint32_t extent_bytes, uint32_t type_id, uint32_t error_id) {
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

#if AL_OWNING_DETAILED_PROFILE
int32_t al_owning_copy_constant(al_owning_stack_context *ctx,
                                uint32_t destination_offset,
                                const uint8_t *source, uint32_t source_length,
                                uint32_t payload_bytes, uint32_t extent_bytes,
                                uint32_t type_id, uint32_t error_id) {
  AL_OWNING_PROFILE_START();
  int32_t result = al_owning_copy_constant_impl(
      ctx, destination_offset, source, source_length, payload_bytes,
      extent_bytes, type_id, error_id);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_ARENA_COPY, result != 0);
  return result;
}
#endif

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

static int32_t al_owning_scan_readable_in_owner(
    al_owning_scan_view *view, uint32_t offset, uint32_t containing_owner_end,
    uint32_t byte_count) {
  uint32_t available;
  if (offset > containing_owner_end || containing_owner_end > view->length) {
    uint32_t readable_end = containing_owner_end;
    if (readable_end > view->length)
      readable_end = view->length;
    available = offset <= readable_end ? readable_end - offset : 0u;
    al_owning_scan_failure(view, byte_count, available);
    return 0;
  }
  available = containing_owner_end - offset;
  if (byte_count > available) {
    al_owning_scan_failure(view, byte_count, available);
    return 0;
  }
  return al_owning_scan_readable(view, offset, byte_count);
}

static uint32_t al_owning_read_u32_le(const uint8_t *bytes, uint32_t offset) {
  return (uint32_t)bytes[offset] | ((uint32_t)bytes[offset + 1u] << 8u) |
         ((uint32_t)bytes[offset + 2u] << 16u) |
         ((uint32_t)bytes[offset + 3u] << 24u);
}

static uint64_t al_owning_read_u64_le(const uint8_t *bytes, uint32_t offset) {
  return (uint64_t)al_owning_read_u32_le(bytes, offset) |
         ((uint64_t)al_owning_read_u32_le(bytes, offset + 4u) << 32u);
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
       type->kind != AL_OWNING_TYPE_F64 &&
       type->kind != AL_OWNING_TYPE_RECORD &&
       type->kind != AL_OWNING_TYPE_STRING &&
       type->kind != AL_OWNING_TYPE_ENUM &&
       type->kind != AL_OWNING_TYPE_OPTION &&
       type->kind != AL_OWNING_TYPE_RESULT &&
       type->kind != AL_OWNING_TYPE_LIST) ||
      (type->kind == AL_OWNING_TYPE_ENUM && type->case_count == 0u) ||
      ((type->kind == AL_OWNING_TYPE_OPTION ||
        type->kind == AL_OWNING_TYPE_RESULT) &&
       (type->case_count != 2u || type->field_count != 2u)) ||
      (type->kind != AL_OWNING_TYPE_ENUM &&
       type->kind != AL_OWNING_TYPE_OPTION &&
       type->kind != AL_OWNING_TYPE_RESULT && type->case_count != 0u) ||
      type->minimum_extent_bytes < type->minimum_payload_bytes ||
      (!payload_dynamic &&
       (type->minimum_payload_bytes != type->fixed_payload_bytes ||
        type->minimum_extent_bytes != type->fixed_extent_bytes))) {
    al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
                          type_index, layout->type_count);
    return 0;
  }
  if ((type->kind == AL_OWNING_TYPE_I64 || type->kind == AL_OWNING_TYPE_BOOL ||
       type->kind == AL_OWNING_TYPE_UNIT ||
       type->kind == AL_OWNING_TYPE_F64 ||
       type->kind == AL_OWNING_TYPE_ENUM) &&
      (type->field_count != 0u || payload_dynamic ||
       type->fixed_payload_bytes != 8u || type->fixed_extent_bytes != 8u ||
       type->minimum_payload_bytes != 8u || type->minimum_extent_bytes != 8u)) {
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
  if (type->kind == AL_OWNING_TYPE_LIST &&
      (type->field_count != 1u || !payload_dynamic ||
       type->minimum_payload_bytes != 8u ||
       type->minimum_extent_bytes != 8u || type->case_count != 0u)) {
    al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
                          type_index, layout->type_count);
    return 0;
  }
  if ((type->kind == AL_OWNING_TYPE_OPTION ||
       type->kind == AL_OWNING_TYPE_RESULT) &&
      (type->minimum_payload_bytes < 8u ||
       type->minimum_extent_bytes < 8u ||
       (type->fixed_extent_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
        (type->fixed_payload_bytes < 8u || type->fixed_extent_bytes < 8u ||
         (type->fixed_extent_bytes & 7u) != 0u)) ||
       (type->minimum_extent_bytes & 7u) != 0u)) {
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
  if (type->kind == AL_OWNING_TYPE_OPTION ||
      type->kind == AL_OWNING_TYPE_RESULT) {
    uint64_t minimum_case_payload = UINT64_MAX;
    uint64_t minimum_case_extent = UINT64_MAX;
    uint64_t fixed_case_payload = 0u;
    uint64_t fixed_case_extent = 0u;
    int32_t all_cases_fixed = 1;
    int32_t have_fixed_case = 0;
    for (index = 0u; index < 2u; ++index) {
      const al_owning_field_descriptor *field =
          &layout->fields[type->first_field + index];
      uint64_t case_minimum_payload;
      uint64_t case_minimum_extent;
      if (field->fixed_offset_bytes != 8u || field->flags != 0u ||
          field->reserved != 0u ||
          (type->kind == AL_OWNING_TYPE_OPTION && index == 1u &&
           field->child_type_index != AL_OWNING_LAYOUT_DYNAMIC_U32) ||
          (field->child_type_index == AL_OWNING_LAYOUT_DYNAMIC_U32 &&
           (type->kind != AL_OWNING_TYPE_OPTION || index != 1u))) {
        al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                              view->error_id, field->child_type_index,
                              layout->type_count);
        return 0;
      }
      if (field->child_type_index == AL_OWNING_LAYOUT_DYNAMIC_U32) {
        case_minimum_payload = 8u;
        case_minimum_extent = 8u;
        if (!have_fixed_case) {
          fixed_case_payload = 8u;
          fixed_case_extent = 8u;
          have_fixed_case = 1;
        } else if (fixed_case_payload != 8u || fixed_case_extent != 8u) {
          all_cases_fixed = 0;
        }
      } else {
        const al_owning_type_descriptor *child;
        uint64_t case_fixed_payload;
        uint64_t case_fixed_extent;
        if (field->child_type_index >= layout->type_count) {
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
                view, layout, field->child_type_index, depth + 1u, state,
                height, visited_fields))
          return 0;
        if ((uint32_t)height[field->child_type_index] > child_height_max)
          child_height_max = (uint32_t)height[field->child_type_index];
        child = &layout->types[field->child_type_index];
        case_minimum_payload = 8u + child->minimum_payload_bytes;
        case_minimum_extent = 8u + child->minimum_extent_bytes;
        if (child->fixed_extent_bytes == AL_OWNING_LAYOUT_DYNAMIC_U32) {
          all_cases_fixed = 0;
        } else {
          case_fixed_payload = 8u + child->fixed_payload_bytes;
          case_fixed_extent = 8u + child->fixed_extent_bytes;
          if (!have_fixed_case) {
            fixed_case_payload = case_fixed_payload;
            fixed_case_extent = case_fixed_extent;
            have_fixed_case = 1;
          } else if (fixed_case_payload != case_fixed_payload ||
                     fixed_case_extent != case_fixed_extent) {
            all_cases_fixed = 0;
          }
        }
      }
      if (case_minimum_payload < minimum_case_payload)
        minimum_case_payload = case_minimum_payload;
      if (case_minimum_extent < minimum_case_extent)
        minimum_case_extent = case_minimum_extent;
      if (case_minimum_payload > UINT32_MAX ||
          case_minimum_extent > UINT32_MAX ||
          fixed_case_payload > UINT32_MAX || fixed_case_extent > UINT32_MAX) {
        al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                              view->error_id, UINT32_MAX, 0u);
        return 0;
      }
    }
    if ((all_cases_fixed && !have_fixed_case) ||
        minimum_case_payload != type->minimum_payload_bytes ||
        minimum_case_extent != type->minimum_extent_bytes ||
        (all_cases_fixed
             ? (type->fixed_payload_bytes != (uint32_t)fixed_case_payload ||
                type->fixed_extent_bytes != (uint32_t)fixed_case_extent)
             : type->fixed_payload_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32)) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL, view->error_id,
                            type_index, layout->type_count);
      return 0;
    }
    computed_height = child_height_max + 1u;
    if (computed_height > AL_OWNING_LAYOUT_MAX_DEPTH - depth) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id, depth + computed_height,
                            AL_OWNING_LAYOUT_MAX_DEPTH);
      return 0;
    }
    height[type_index] = (uint8_t)computed_height;
    state[type_index] = 2u;
    return 1;
  }
  if (type->kind == AL_OWNING_TYPE_LIST) {
    const al_owning_field_descriptor *field =
        &layout->fields[type->first_field];
    const uint32_t child_index = field->child_type_index;
    uint32_t child_height;
    if (field->fixed_offset_bytes != 8u || field->flags != 0u ||
        field->reserved != 0u || child_index >= layout->type_count) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id, child_index, layout->type_count);
      return 0;
    }
    if (*visited_fields >= AL_OWNING_LAYOUT_MAX_FIELDS) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id, *visited_fields + 1u,
                            AL_OWNING_LAYOUT_MAX_FIELDS);
      return 0;
    }
    ++*visited_fields;
    if (!al_owning_validate_type_graph_inner(view, layout, child_index,
                                             depth + 1u, state, height,
                                             visited_fields))
      return 0;
    child_height = (uint32_t)height[child_index];
    computed_height = child_height + 1u;
    if (computed_height > AL_OWNING_LAYOUT_MAX_DEPTH - depth) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id, depth + computed_height,
                            AL_OWNING_LAYOUT_MAX_DEPTH);
      return 0;
    }
    height[type_index] = (uint8_t)computed_height;
    state[type_index] = 2u;
    return 1;
  }
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

/* Clears the memo entries the layout can index. Every state/height read is
 * guarded by type_index < layout->type_count (checked before recursing), and
 * the header check caps type_count at AL_OWNING_LAYOUT_MAX_TYPES, so clearing
 * only type_count entries is exact. Clearing all 4096 entries per call made
 * every measure/locate/compare pay ~8 KB of byte stores (report 266). */
static void
al_owning_initialize_graph_memo(uint8_t state[AL_OWNING_LAYOUT_MAX_TYPES],
                                uint8_t height[AL_OWNING_LAYOUT_MAX_TYPES],
                                uint32_t type_count) {
  volatile uint8_t *state_bytes = state;
  volatile uint8_t *height_bytes = height;
  uint32_t index;
  const uint32_t count = type_count < AL_OWNING_LAYOUT_MAX_TYPES
                             ? type_count
                             : AL_OWNING_LAYOUT_MAX_TYPES;
  /* Keep this explicit volatile fill in the freestanding runtime: aggregate
   * zero initialization can introduce a CRT memset dependency in COFF. */
  for (index = 0u; index < count; ++index) {
    state_bytes[index] = 0u;
    height_bytes[index] = 0u;
  }
}

static int32_t AL_OWNING_PROFILE_IMPL(al_owning_validate_type_graph)(
    al_owning_scan_view *view, const al_owning_layout *layout,
    uint32_t type_index) {
  uint8_t state[AL_OWNING_LAYOUT_MAX_TYPES];
  uint8_t height[AL_OWNING_LAYOUT_MAX_TYPES];
  uint32_t visited_fields = 0u;
  if (layout == 0)
    return 0;
  al_owning_initialize_graph_memo(state, height, layout->type_count);
  return al_owning_validate_type_graph_inner(view, layout, type_index, 0u,
                                             state, height, &visited_fields);
}

#if AL_OWNING_DETAILED_PROFILE
static int32_t al_owning_validate_type_graph(al_owning_scan_view *view,
                                             const al_owning_layout *layout,
                                             uint32_t type_index) {
  AL_OWNING_PROFILE_START();
  int32_t result = al_owning_validate_type_graph_impl(view, layout,
                                                      type_index);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_OTHER_RUNTIME_VALIDATION,
                        result == 0);
  return result;
}
#endif

/* Strict UTF-8 (RFC 3629): no overlongs, no surrogates, nothing above U+10FFFF.
 * Returns 1 when bytes[0, length) is well formed. */
static int32_t al_owning_utf8_valid(const uint8_t *bytes, uint32_t length) {
  uint32_t index = 0u;
  while (index < length) {
    const uint32_t lead = bytes[index];
    uint32_t need;
    uint32_t minimum;
    uint32_t value;
    uint32_t k;
    if (lead < 0x80u) {
      ++index;
      continue;
    }
    if (lead >= 0xC2u && lead <= 0xDFu) {
      need = 1u;
      minimum = 0x80u;
      value = lead & 0x1Fu;
    } else if (lead >= 0xE0u && lead <= 0xEFu) {
      need = 2u;
      minimum = 0x800u;
      value = lead & 0x0Fu;
    } else if (lead >= 0xF0u && lead <= 0xF4u) {
      need = 3u;
      minimum = 0x10000u;
      value = lead & 0x07u;
    } else {
      return 0;
    }
    if (length - index <= need)
      return 0;
    for (k = 1u; k <= need; ++k) {
      const uint32_t next = bytes[index + k];
      if ((next & 0xC0u) != 0x80u)
        return 0;
      value = (value << 6) | (next & 0x3Fu);
    }
    if (value < minimum || value > 0x10FFFFu ||
        (value >= 0xD800u && value <= 0xDFFFu))
      return 0;
    index += need + 1u;
  }
  return 1;
}

static int32_t al_owning_scan_string(al_owning_scan_view *view, uint32_t offset,
                                     uint32_t containing_owner_end,
                                     uint32_t *out_string_bytes,
                                     uint32_t *out_payload_bytes,
                                     uint32_t *out_extent_bytes) {
  uint32_t string_bytes;
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
  string_bytes = al_owning_read_u32_le(view->bytes, offset);
  if (view->bytes[offset + 4u] != 0u || view->bytes[offset + 5u] != 0u ||
      view->bytes[offset + 6u] != 0u || view->bytes[offset + 7u] != 0u) {
    al_owning_scan_failure(
        view, 8u,
        containing_owner_end > offset ? containing_owner_end - offset : 0u);
    return 0;
  }
  payload_wide = 8ull + (uint64_t)string_bytes;
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
  /* Strings created by native code are well formed by construction; bytes
   * admitted from a host or caller are checked here. */
  if (view->external &&
      !al_owning_utf8_valid(view->bytes + offset + 8u, string_bytes)) {
    al_owning_scan_failure(view, extent, available);
    return 0;
  }
  *out_string_bytes = string_bytes;
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
  if (type->kind == AL_OWNING_TYPE_OPTION ||
      type->kind == AL_OWNING_TYPE_RESULT) {
    uint64_t tag;
    const al_owning_field_descriptor *case_field;
    uint32_t child_payload = 0u;
    uint32_t child_extent = 0u;
    uint64_t payload_wide;
    uint64_t extent_wide;
    if (!al_owning_scan_readable_in_owner(view, offset,
                                          containing_owner_end, 8u))
      return 0;
    tag = al_owning_read_u64_le(view->bytes, offset);
    if (tag > 1u) {
      al_owning_scan_failure(view, tag > UINT32_MAX ? UINT32_MAX
                                                    : (uint32_t)tag,
                             2u);
      return 0;
    }
    case_field = &layout->fields[type->first_field + (uint32_t)tag];
    if (case_field->child_type_index == AL_OWNING_LAYOUT_DYNAMIC_U32) {
      if (type->kind != AL_OWNING_TYPE_OPTION || tag != 1u) {
        al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                              view->error_id, case_field->child_type_index,
                              layout->type_count);
        return 0;
      }
      payload = 8u;
      extent = 8u;
    } else {
      uint32_t child_offset;
      if (case_field->child_type_index >= layout->type_count ||
          containing_owner_end - offset < 8u || offset > UINT32_MAX - 8u) {
        al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                              view->error_id, case_field->child_type_index,
                              layout->type_count);
        return 0;
      }
      child_offset = offset + 8u;
      if (!al_owning_scan_value(view, layout, case_field->child_type_index,
                                child_offset, containing_owner_end, depth + 1u,
                                &child_payload, &child_extent, target))
        return 0;
      payload_wide = 8ull + child_payload;
      extent_wide = 8ull + child_extent;
      if (payload_wide > UINT32_MAX || extent_wide > UINT32_MAX ||
          extent_wide > containing_owner_end - offset) {
        al_owning_scan_failure(
            view, extent_wide > UINT32_MAX ? UINT32_MAX : (uint32_t)extent_wide,
            containing_owner_end - offset);
        return 0;
      }
      payload = (uint32_t)payload_wide;
      extent = (uint32_t)extent_wide;
    }
    if (payload < type->minimum_payload_bytes ||
        extent < type->minimum_extent_bytes ||
        (type->fixed_payload_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32 &&
         (payload != type->fixed_payload_bytes ||
          extent != type->fixed_extent_bytes))) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id, extent,
                            type->fixed_extent_bytes);
      return 0;
    }
  } else if (type->kind == AL_OWNING_TYPE_LIST) {
    const al_owning_field_descriptor *element_field =
        &layout->fields[type->first_field];
    const al_owning_type_descriptor *element_type;
    uint32_t count;
    uint32_t reserved;
    uint32_t remaining;
    uint64_t payload_wide = 8u;
    uint64_t extent_wide = 8u;
    if (!al_owning_scan_readable_in_owner(view, offset,
                                          containing_owner_end, 8u))
      return 0;
    count = al_owning_read_u32_le(view->bytes, offset);
    reserved = al_owning_read_u32_le(view->bytes, offset + 4u);
    if (reserved != 0u) {
      al_owning_scan_failure(view, reserved, 0u);
      return 0;
    }
    if (element_field->child_type_index >= layout->type_count ||
        element_field->fixed_offset_bytes != 8u || element_field->flags != 0u ||
        element_field->reserved != 0u) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id, element_field->child_type_index,
                            layout->type_count);
      return 0;
    }
    element_type = &layout->types[element_field->child_type_index];
    if (element_type->minimum_extent_bytes < 8u) {
      al_owning_set_failure(view->ctx, AL_OWNING_STATUS_INTERNAL,
                            view->error_id,
                            element_type->minimum_extent_bytes, 8u);
      return 0;
    }
    remaining = containing_owner_end - offset - 8u;
    if (count > remaining / element_type->minimum_extent_bytes) {
      al_owning_scan_failure(view, count,
                             remaining / element_type->minimum_extent_bytes);
      return 0;
    }
    for (index = 0u; index < count; ++index) {
      uint32_t child_offset;
      uint32_t child_payload = 0u;
      uint32_t child_extent = 0u;
      if (extent_wide > UINT32_MAX ||
          extent_wide > containing_owner_end - offset) {
        al_owning_scan_failure(
            view, extent_wide > UINT32_MAX ? UINT32_MAX
                                           : (uint32_t)extent_wide,
            containing_owner_end - offset);
        return 0;
      }
      child_offset = offset + (uint32_t)extent_wide;
      if (!al_owning_scan_value(view, layout,
                                element_field->child_type_index, child_offset,
                                containing_owner_end, depth + 1u,
                                &child_payload, &child_extent, target))
        return 0;
      if (child_extent < element_type->minimum_extent_bytes ||
          child_payload < element_type->minimum_payload_bytes ||
          payload_wide + child_payload > UINT32_MAX ||
          extent_wide + child_extent > UINT32_MAX) {
        al_owning_scan_failure(
            view, extent_wide + child_extent > UINT32_MAX
                      ? UINT32_MAX
                      : (uint32_t)(extent_wide + child_extent),
            containing_owner_end - offset);
        return 0;
      }
      payload_wide += child_payload;
      extent_wide += child_extent;
    }
    payload = (uint32_t)payload_wide;
    extent = (uint32_t)extent_wide;
  } else if (type->kind == AL_OWNING_TYPE_STRING) {
    uint32_t string_bytes;
    if (!al_owning_scan_string(view, offset, containing_owner_end, &string_bytes,
                               &payload, &extent))
      return 0;
    (void)string_bytes;
  } else if (type->kind == AL_OWNING_TYPE_ENUM) {
    uint64_t ordinal;
    uint32_t required;
    if (!al_owning_scan_readable_in_owner(view, offset,
                                          containing_owner_end, 8u))
      return 0;
    ordinal = al_owning_read_u64_le(view->bytes, offset);
    if (ordinal >= (uint64_t)type->case_count) {
      required = ordinal > UINT32_MAX ? UINT32_MAX : (uint32_t)ordinal;
      al_owning_scan_failure(view, required, type->case_count);
      return 0;
    }
    payload = 8u;
    extent = 8u;
  } else if (type->kind == AL_OWNING_TYPE_I64 ||
             type->kind == AL_OWNING_TYPE_BOOL ||
             type->kind == AL_OWNING_TYPE_UNIT ||
             type->kind == AL_OWNING_TYPE_F64) {
    uint64_t encoded;
    if (!al_owning_scan_readable_in_owner(view, offset,
                                          containing_owner_end, 8u))
      return 0;
    encoded = al_owning_read_u64_le(view->bytes, offset);
    if (type->kind == AL_OWNING_TYPE_BOOL) {
      if (encoded > 1u) {
        al_owning_scan_failure(view,
                               encoded > UINT32_MAX ? UINT32_MAX
                                                    : (uint32_t)encoded,
                               1u);
        return 0;
      }
    } else if (type->kind == AL_OWNING_TYPE_UNIT && encoded != 0u) {
      al_owning_scan_failure(view, 1u, 0u);
      return 0;
    } else if (type->kind == AL_OWNING_TYPE_F64 &&
               (encoded & UINT64_C(0x7ff0000000000000)) ==
                   UINT64_C(0x7ff0000000000000)) {
      al_owning_scan_failure(view, 8u, 0u);
      return 0;
    }
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
      if (!al_owning_scan_readable_in_owner(view, offset,
                                            containing_owner_end, 8u))
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

int32_t AL_OWNING_PROFILE_IMPL(al_owning_measure_value)(
    al_owning_stack_context *ctx, const al_owning_layout *layout,
    uint32_t type_index, uint32_t offset, uint32_t containing_owner_end,
    uint32_t error_id, al_owning_value_size *out_value_size) {
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

#if AL_OWNING_DETAILED_PROFILE
int32_t al_owning_measure_value(al_owning_stack_context *ctx,
                                const al_owning_layout *layout,
                                uint32_t type_index, uint32_t offset,
                                uint32_t containing_owner_end,
                                uint32_t error_id,
                                al_owning_value_size *out_value_size) {
  AL_OWNING_PROFILE_START();
  int32_t result = al_owning_measure_value_impl(
      ctx, layout, type_index, offset, containing_owner_end, error_id,
      out_value_size);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_VALUE_MEASURE, result != 0);
  return result;
}
#endif

int32_t AL_OWNING_PROFILE_IMPL(al_owning_measure_external_value)(
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

#if AL_OWNING_DETAILED_PROFILE
int32_t al_owning_measure_external_value(
    al_owning_stack_context *ctx, const al_owning_layout *layout,
    uint32_t type_index, const uint8_t *source, uint32_t source_length,
    uint32_t source_offset, uint32_t error_id, uint32_t *out_payload_bytes,
    uint32_t *out_extent_bytes) {
  AL_OWNING_PROFILE_START();
  int32_t result = al_owning_measure_external_value_impl(
      ctx, layout, type_index, source, source_length, source_offset, error_id,
      out_payload_bytes, out_extent_bytes);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_EXTERNAL_MEASURE, result != 0);
  return result;
}
#endif

int32_t AL_OWNING_PROFILE_IMPL(al_owning_commit_host_output)(
    al_owning_stack_context *ctx, const al_owning_layout *layout,
    uint32_t type_index, uint32_t start, uint32_t reserved_end,
    uint32_t output_extent, int32_t host_status, uint32_t error_id,
    uint32_t *out_payload) {
  uint32_t payload = 0u;
  uint32_t extent = 0u;
  if (ctx == 0)
    return 1;
  /* Never rewind an unverified range supplied by the boundary caller. */
  if (start > reserved_end || reserved_end != ctx->cursor_bytes ||
      reserved_end > ctx->stack_capacity_bytes || (start & 7u) != 0u ||
      ctx->stack_data == 0) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          reserved_end, ctx->cursor_bytes);
    return 1;
  }
  if (ctx->status == AL_OWNING_STATUS_OK && host_status != 0)
    al_owning_set_failure(ctx, AL_OWNING_STATUS_DIAGNOSTIC, error_id, 0u, 0u);
  if (ctx->status == AL_OWNING_STATUS_OK) {
    if (out_payload == 0 || output_extent < 8u ||
        (output_extent & 7u) != 0u || output_extent > reserved_end - start) {
      al_owning_set_failure(ctx, AL_OWNING_STATUS_DIAGNOSTIC, error_id,
                            output_extent, reserved_end - start);
    } else if (al_owning_measure_external_value(
                   ctx, layout, type_index, ctx->stack_data + start,
                   output_extent, 0u, error_id, &payload, &extent) == 0 &&
               extent != output_extent) {
      al_owning_set_failure(ctx, AL_OWNING_STATUS_DIAGNOSTIC, error_id,
                            extent, output_extent);
    }
    /* The scanner reports raw layout/input faults. At this boundary those are
     * adapter failures, attributed to the verified host call rather than entry
     * input validation. Preserve its measured bounds and source diagnostic. */
    if (ctx->status != AL_OWNING_STATUS_OK)
      ctx->status = AL_OWNING_STATUS_DIAGNOSTIC;
  }
  if (ctx->status != AL_OWNING_STATUS_OK) {
    al_owning_release_to(ctx, start, 0u, 0u, 0u);
    return 1;
  }
  al_owning_release_to(ctx, start + extent, 0u, 0u, 0u);
  al_owning_mark(ctx, start, extent, 1, 0);
  al_owning_update_live(ctx, (int32_t)payload, 0);
  if (ctx->status != AL_OWNING_STATUS_OK) {
    al_owning_release_to(ctx, start, 0u, 0u, 0u);
    return 1;
  }
  al_owning_event(ctx, AL_OWNING_EVENT_ALLOCATE,
                  layout->types[type_index].type_id, start, extent, payload,
                  0u, 0u, 0u);
  *out_payload = payload;
  return 0;
}

#if AL_OWNING_DETAILED_PROFILE
int32_t al_owning_commit_host_output(
    al_owning_stack_context *ctx, const al_owning_layout *layout,
    uint32_t type_index, uint32_t start, uint32_t reserved_end,
    uint32_t output_extent, int32_t host_status, uint32_t error_id,
    uint32_t *out_payload) {
  AL_OWNING_PROFILE_START();
  int32_t result = al_owning_commit_host_output_impl(
      ctx, layout, type_index, start, reserved_end, output_extent, host_status,
      error_id, out_payload);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_HOST_OUTPUT_COMMIT, result != 0);
  return result;
}
#endif

static int32_t al_owning_equal_typed_scan(
    al_owning_scan_view *view, const al_owning_layout *layout,
    uint32_t type_index, uint32_t offset, uint32_t owner_end,
    uint32_t *out_payload, uint32_t *out_extent) {
  uint32_t payload = 0u;
  uint32_t extent = 0u;
  if (!al_owning_scan_value(view, layout, type_index, offset, owner_end, 0u,
                            &payload, &extent, 0))
    return 0;
  *out_payload = payload;
  *out_extent = extent;
  return 1;
}

static int32_t al_owning_equal_typed_inner(
    al_owning_stack_context *ctx, const al_owning_layout *layout,
    al_owning_scan_view *left_view, al_owning_scan_view *right_view,
    uint32_t type_index, uint32_t left_offset, uint32_t left_extent,
    uint32_t right_offset, uint32_t right_extent, uint32_t depth,
    uint32_t error_id) {
  const al_owning_type_descriptor *type;
  uint32_t index;
  if (depth >= AL_OWNING_LAYOUT_MAX_DEPTH || type_index >= layout->type_count) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          depth >= AL_OWNING_LAYOUT_MAX_DEPTH ? depth + 1u
                                                              : type_index,
                          depth >= AL_OWNING_LAYOUT_MAX_DEPTH
                              ? AL_OWNING_LAYOUT_MAX_DEPTH
                              : layout->type_count);
    return 0;
  }
  type = &layout->types[type_index];
  if (left_extent != right_extent)
    return 0;

  if (type->kind == AL_OWNING_TYPE_F64) {
    uint64_t left_bits;
    uint64_t right_bits;
    if (!al_owning_scan_readable_in_owner(left_view, left_offset,
                                          left_view->length, 8u) ||
        !al_owning_scan_readable_in_owner(right_view, right_offset,
                                          right_view->length, 8u))
      return 0;
    left_bits = al_owning_read_u64_le(left_view->bytes, left_offset);
    right_bits = al_owning_read_u64_le(right_view->bytes, right_offset);
    if (left_bits == right_bits)
      return 1;
    return (left_bits & UINT64_C(0x7fffffffffffffff)) == 0u &&
           (right_bits & UINT64_C(0x7fffffffffffffff)) == 0u;
  }

  if (type->kind == AL_OWNING_TYPE_I64 ||
      type->kind == AL_OWNING_TYPE_BOOL ||
      type->kind == AL_OWNING_TYPE_UNIT ||
      type->kind == AL_OWNING_TYPE_ENUM) {
    if (!al_owning_scan_readable_in_owner(left_view, left_offset,
                                          left_view->length, 8u) ||
        !al_owning_scan_readable_in_owner(right_view, right_offset,
                                          right_view->length, 8u))
      return 0;
    return al_owning_read_u64_le(left_view->bytes, left_offset) ==
           al_owning_read_u64_le(right_view->bytes, right_offset);
  }

  if (type->kind == AL_OWNING_TYPE_STRING) {
    uint32_t index;
    if (left_extent != right_extent)
      return 0;
    if (!al_owning_scan_readable_in_owner(left_view, left_offset,
                                          left_view->length, left_extent) ||
        !al_owning_scan_readable_in_owner(right_view, right_offset,
                                          right_view->length, right_extent))
      return 0;
    for (index = 0u; index < left_extent; ++index) {
      if (left_view->bytes[left_offset + index] !=
          right_view->bytes[right_offset + index])
        return 0;
    }
    return 1;
  }

  if (type->kind == AL_OWNING_TYPE_LIST) {
    const al_owning_field_descriptor *element_field =
        &layout->fields[type->first_field];
    const al_owning_type_descriptor *element_type =
        &layout->types[element_field->child_type_index];
    const uint32_t left_count =
        al_owning_read_u32_le(left_view->bytes, left_offset);
    const uint32_t right_count =
        al_owning_read_u32_le(right_view->bytes, right_offset);
    uint32_t left_cursor = 8u;
    uint32_t right_cursor = 8u;
    uint32_t index;
    if (left_count != right_count)
      return 0;
    if (left_extent < 8u || right_extent < 8u ||
        left_count >
            (left_extent - 8u) / element_type->minimum_extent_bytes ||
        right_count >
            (right_extent - 8u) / element_type->minimum_extent_bytes) {
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                            left_count, left_extent);
      return 0;
    }
    for (index = 0u; index < left_count; ++index) {
      uint32_t left_payload = 0u;
      uint32_t left_child_extent = 0u;
      uint32_t right_payload = 0u;
      uint32_t right_child_extent = 0u;
      if (left_cursor > left_extent || right_cursor > right_extent ||
          left_offset > UINT32_MAX - left_cursor ||
          right_offset > UINT32_MAX - right_cursor ||
          !al_owning_equal_typed_scan(
              left_view, layout, element_field->child_type_index,
              left_offset + left_cursor, left_view->length, &left_payload,
              &left_child_extent) ||
          !al_owning_equal_typed_scan(
              right_view, layout, element_field->child_type_index,
              right_offset + right_cursor, right_view->length, &right_payload,
              &right_child_extent) ||
          left_child_extent > left_extent - left_cursor ||
          right_child_extent > right_extent - right_cursor ||
          !al_owning_equal_typed_inner(
              ctx, layout, left_view, right_view,
              element_field->child_type_index, left_offset + left_cursor,
              left_child_extent, right_offset + right_cursor,
              right_child_extent, depth + 1u, error_id))
        return 0;
      left_cursor += left_child_extent;
      right_cursor += right_child_extent;
      (void)left_payload;
      (void)right_payload;
    }
    return left_cursor == left_extent && right_cursor == right_extent;
  }

  if (type->kind == AL_OWNING_TYPE_OPTION ||
      type->kind == AL_OWNING_TYPE_RESULT) {
    uint64_t left_tag;
    uint64_t right_tag;
    const al_owning_field_descriptor *case_field;
    uint32_t child_payload_left = 0u;
    uint32_t child_extent_left = 0u;
    uint32_t child_payload_right = 0u;
    uint32_t child_extent_right = 0u;
    if (left_extent < 8u || right_extent < 8u ||
        !al_owning_scan_readable_in_owner(left_view, left_offset,
                                          left_view->length, 8u) ||
        !al_owning_scan_readable_in_owner(right_view, right_offset,
                                          right_view->length, 8u))
      return 0;
    left_tag = al_owning_read_u64_le(left_view->bytes, left_offset);
    right_tag = al_owning_read_u64_le(right_view->bytes, right_offset);
    if (left_tag != right_tag)
      return 0;
    if (left_tag > 1u) {
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                            left_tag > UINT32_MAX ? UINT32_MAX
                                                  : (uint32_t)left_tag,
                            2u);
      return 0;
    }
    case_field = &layout->fields[type->first_field + (uint32_t)left_tag];
    if (case_field->child_type_index == AL_OWNING_LAYOUT_DYNAMIC_U32)
      return type->kind == AL_OWNING_TYPE_OPTION && left_tag == 1u;
    if (left_offset > UINT32_MAX - 8u || right_offset > UINT32_MAX - 8u ||
        !al_owning_equal_typed_scan(
            left_view, layout, case_field->child_type_index, left_offset + 8u,
            left_view->length, &child_payload_left, &child_extent_left) ||
        !al_owning_equal_typed_scan(
            right_view, layout, case_field->child_type_index,
            right_offset + 8u, right_view->length, &child_payload_right,
            &child_extent_right))
      return 0;
    if (child_extent_left != left_extent - 8u ||
        child_extent_right != right_extent - 8u) {
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                            child_extent_left > child_extent_right
                                ? child_extent_left + 8u
                                : child_extent_right + 8u,
                            left_extent > right_extent ? left_extent
                                                       : right_extent);
      return 0;
    }
    return al_owning_equal_typed_inner(
        ctx, layout, left_view, right_view, case_field->child_type_index,
        left_offset + 8u, child_extent_left, right_offset + 8u,
        child_extent_right, depth + 1u, error_id);
  }

  if (type->kind == AL_OWNING_TYPE_RECORD) {
    uint32_t left_cursor = 0u;
    uint32_t right_cursor = 0u;
    int32_t has_payload_field = 0;
    for (index = 0u; index < type->field_count; ++index) {
      const al_owning_field_descriptor *field =
          &layout->fields[type->first_field + index];
      uint32_t left_child_offset;
      uint32_t right_child_offset;
      uint32_t child_payload_left = 0u;
      uint32_t child_extent_left = 0u;
      uint32_t child_payload_right = 0u;
      uint32_t child_extent_right = 0u;
      if (field->flags == AL_OWNING_FIELD_ZERO_WIDTH)
        continue;
      if (field->flags != 0u || field->child_type_index >= layout->type_count ||
          left_cursor > left_extent || right_cursor > right_extent ||
          left_offset > UINT32_MAX - left_cursor ||
          right_offset > UINT32_MAX - right_cursor) {
        al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                              index, type->field_count);
        return 0;
      }
      has_payload_field = 1;
      left_child_offset = left_offset + left_cursor;
      right_child_offset = right_offset + right_cursor;
      if (!al_owning_equal_typed_scan(
              left_view, layout, field->child_type_index, left_child_offset,
              left_view->length, &child_payload_left, &child_extent_left) ||
          !al_owning_equal_typed_scan(
              right_view, layout, field->child_type_index,
              right_child_offset, right_view->length, &child_payload_right,
              &child_extent_right))
        return 0;
      if (child_extent_left > left_extent - left_cursor ||
          child_extent_right > right_extent - right_cursor) {
        al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                              child_extent_left > child_extent_right
                                  ? child_extent_left
                                  : child_extent_right,
                              left_extent - left_cursor);
        return 0;
      }
      if (!al_owning_equal_typed_inner(
              ctx, layout, left_view, right_view, field->child_type_index,
              left_child_offset, child_extent_left, right_child_offset,
              child_extent_right, depth + 1u, error_id))
        return 0;
      left_cursor += child_extent_left;
      right_cursor += child_extent_right;
    }
    if (!has_payload_field)
      return left_extent == 8u && right_extent == 8u;
    return left_cursor == left_extent && right_cursor == right_extent;
  }

  al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                        type->kind, AL_OWNING_TYPE_F64);
  return 0;
}

int32_t al_owning_equal_typed(al_owning_stack_context *ctx,
                              const al_owning_layout *layout,
                              uint32_t type_index, uint32_t left_offset,
                              uint32_t left_extent_bytes,
                              uint32_t right_offset,
                              uint32_t right_extent_bytes,
                              uint32_t error_id) {
  al_owning_scan_view left_view;
  al_owning_scan_view right_view;
  uint32_t left_payload = 0u;
  uint32_t left_extent = 0u;
  uint32_t right_payload = 0u;
  uint32_t right_extent = 0u;
  uint32_t left_owner_end;
  uint32_t right_owner_end;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 0;
  if (layout == 0 || !al_owning_build_profile_valid(ctx) ||
      left_offset > UINT32_MAX - left_extent_bytes ||
      right_offset > UINT32_MAX - right_extent_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 0;
  }
  left_owner_end = left_offset + left_extent_bytes;
  right_owner_end = right_offset + right_extent_bytes;
  if (left_owner_end > ctx->cursor_bytes ||
      left_owner_end > ctx->stack_capacity_bytes ||
      right_owner_end > ctx->cursor_bytes ||
      right_owner_end > ctx->stack_capacity_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          left_owner_end > right_owner_end ? left_owner_end
                                                           : right_owner_end,
                          ctx->cursor_bytes);
    return 0;
  }
  if (!al_owning_validate_layout_header(ctx, layout, error_id))
    return 0;
  left_view.ctx = ctx;
  left_view.bytes = ctx->stack_data;
  left_view.length = left_owner_end;
  left_view.error_id = error_id;
  left_view.external = 0;
  right_view = left_view;
  right_view.length = right_owner_end;
  if (!al_owning_validate_type_graph(&left_view, layout, type_index) ||
      !al_owning_equal_typed_scan(&left_view, layout, type_index,
                                  left_offset, left_owner_end, &left_payload,
                                  &left_extent) ||
      !al_owning_equal_typed_scan(&right_view, layout, type_index,
                                  right_offset, right_owner_end,
                                  &right_payload, &right_extent))
    return 0;
  if (left_extent != left_extent_bytes || right_extent != right_extent_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          left_extent > right_extent ? left_extent
                                                     : right_extent,
                          left_extent_bytes > right_extent_bytes
                              ? left_extent_bytes
                              : right_extent_bytes);
    return 0;
  }
  return al_owning_equal_typed_inner(
      ctx, layout, &left_view, &right_view, type_index, left_offset,
      left_extent, right_offset, right_extent, 0u, error_id);
}

int32_t AL_OWNING_PROFILE_IMPL(al_owning_locate_field)(
    al_owning_stack_context *ctx, const al_owning_layout *layout,
    uint32_t parent_type_index, uint32_t parent_offset,
    uint32_t parent_extent, uint32_t field_index, uint32_t error_id,
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
#if AL_OWNING_TRUSTED_GENERATED
  /* Trusted generated code (report 268): the layout is compiler-emitted and
   * the parent was built by verified code, so walk only the fields before the
   * target instead of re-validating the type graph and scanning the whole
   * parent on every read (~0.5 us per template hole). Bounds stay checked;
   * the diagnostic profile keeps the full scan below. */
  {
    uint32_t cursor;
    uint32_t index;
    if (!al_owning_validate_type_descriptor(&view, layout, parent_type_index,
                                            &parent) ||
        parent->kind != AL_OWNING_TYPE_RECORD ||
        field_index >= parent->field_count ||
        parent_offset > ctx->cursor_bytes ||
        parent_extent > ctx->cursor_bytes - parent_offset) {
      if (ctx->status == AL_OWNING_STATUS_OK)
        al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                              parent_extent, ctx->cursor_bytes);
      return 1;
    }
    parent_end = parent_offset + parent_extent;
    view.length = parent_end;
    cursor = parent_offset;
    for (index = 0u; index <= field_index; ++index) {
      const al_owning_field_descriptor *field =
          &layout->fields[parent->first_field + index];
      uint32_t field_payload = 0u;
      uint32_t field_extent = 0u;
      if (field->child_type_index >= layout->type_count) {
        al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                              field->child_type_index, layout->type_count);
        return 1;
      }
      if (field->flags != AL_OWNING_FIELD_ZERO_WIDTH) {
        const al_owning_type_descriptor *child =
            &layout->types[field->child_type_index];
        if (child->fixed_extent_bytes != AL_OWNING_LAYOUT_DYNAMIC_U32) {
          field_payload = child->fixed_payload_bytes;
          field_extent = child->fixed_extent_bytes;
        } else if (!al_owning_scan_value(&view, layout,
                                         field->child_type_index, cursor,
                                         parent_end, 1u, &field_payload,
                                         &field_extent, 0)) {
          return 1;
        }
        if (field_extent > parent_end - cursor) {
          al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                                field_extent, parent_end - cursor);
          return 1;
        }
      }
      if (index == field_index) {
        out_field_location->offset_bytes = cursor;
        out_field_location->payload_bytes = field_payload;
        out_field_location->extent_bytes = field_extent;
        return 0;
      }
      cursor += field_extent;
    }
    return 1;
  }
#endif
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

#if AL_OWNING_DETAILED_PROFILE
int32_t al_owning_locate_field(al_owning_stack_context *ctx,
                               const al_owning_layout *layout,
                               uint32_t parent_type_index,
                               uint32_t parent_offset, uint32_t parent_extent,
                               uint32_t field_index, uint32_t error_id,
                               al_owning_field_location *out_field_location) {
  AL_OWNING_PROFILE_START();
  int32_t result = al_owning_locate_field_impl(
      ctx, layout, parent_type_index, parent_offset, parent_extent,
      field_index, error_id, out_field_location);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_STRUCTURAL_LOCATE, result != 0);
  return result;
}
#endif

int32_t AL_OWNING_PROFILE_IMPL(al_owning_locate_sum_case)(
    al_owning_stack_context *ctx, const al_owning_layout *layout,
    uint32_t sum_type_index, uint32_t sum_offset, uint32_t sum_extent_bytes,
    uint32_t error_id, uint32_t *out_case_index,
    al_owning_field_location *out_case_location) {
  al_owning_scan_view view;
  const al_owning_type_descriptor *sum_type;
  al_owning_field_location measured_location;
  uint32_t measured_payload;
  uint32_t measured_extent;
  uint32_t sum_end;
  uint32_t case_index;
  const al_owning_field_descriptor *case_field;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (out_case_index == 0 || out_case_location == 0 ||
      !al_owning_validate_layout_header(ctx, layout, error_id)) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 1;
  }
  view.ctx = ctx;
  view.bytes = ctx->stack_data;
  view.length = ctx->stack_capacity_bytes;
  view.error_id = error_id;
  view.external = 0;
  if (!al_owning_validate_type_descriptor(&view, layout, sum_type_index,
                                          &sum_type) ||
      (sum_type->kind != AL_OWNING_TYPE_OPTION &&
       sum_type->kind != AL_OWNING_TYPE_RESULT) ||
      sum_offset > ctx->cursor_bytes ||
      sum_offset > ctx->stack_capacity_bytes ||
      sum_extent_bytes > ctx->cursor_bytes - sum_offset ||
      sum_extent_bytes > ctx->stack_capacity_bytes - sum_offset ||
      sum_extent_bytes > UINT32_MAX - sum_offset) {
    if (ctx->status == AL_OWNING_STATUS_OK)
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                            sum_extent_bytes, ctx->cursor_bytes);
    return 1;
  }
  sum_end = sum_offset + sum_extent_bytes;
  view.length = sum_end;
  if (!al_owning_validate_type_graph(&view, layout, sum_type_index) ||
      !al_owning_scan_value(&view, layout, sum_type_index, sum_offset, sum_end,
                            0u, &measured_payload, &measured_extent, 0))
    return 1;
  if (measured_extent != sum_extent_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          measured_extent, sum_extent_bytes);
    return 1;
  }
  case_index = (uint32_t)al_owning_read_u64_le(ctx->stack_data, sum_offset);
  case_field = &layout->fields[sum_type->first_field + case_index];
  if (case_field->child_type_index == AL_OWNING_LAYOUT_DYNAMIC_U32) {
    measured_location.offset_bytes = sum_offset + 8u;
    measured_location.payload_bytes = 0u;
    measured_location.extent_bytes = 0u;
  } else {
    measured_location.offset_bytes = sum_offset + 8u;
    measured_location.payload_bytes = measured_payload - 8u;
    measured_location.extent_bytes = measured_extent - 8u;
  }
  *out_case_index = case_index;
  *out_case_location = measured_location;
  return 0;
}

#if AL_OWNING_DETAILED_PROFILE
int32_t al_owning_locate_sum_case(
    al_owning_stack_context *ctx, const al_owning_layout *layout,
    uint32_t sum_type_index, uint32_t sum_offset, uint32_t sum_extent_bytes,
    uint32_t error_id, uint32_t *out_case_index,
    al_owning_field_location *out_case_location) {
  AL_OWNING_PROFILE_START();
  int32_t result = al_owning_locate_sum_case_impl(
      ctx, layout, sum_type_index, sum_offset, sum_extent_bytes, error_id,
      out_case_index, out_case_location);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_STRUCTURAL_LOCATE, result != 0);
  return result;
}
#endif

int32_t al_owning_string_length(al_owning_stack_context *ctx, uint32_t offset,
                                uint32_t extent_bytes, uint32_t error_id,
                                uint32_t *out_string_bytes) {
  al_owning_scan_view view;
  uint32_t string_bytes;
  uint32_t payload;
  uint32_t extent;
  uint32_t owner_end;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (out_string_bytes == 0 || offset > ctx->cursor_bytes ||
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
  if (!al_owning_scan_string(&view, offset, owner_end, &string_bytes, &payload,
                             &extent))
    return 1;
  if (extent != extent_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, extent,
                          extent_bytes);
    return 1;
  }
  *out_string_bytes = string_bytes;
  (void)payload;
  return 0;
}

int32_t al_owning_string_char_length(al_owning_stack_context *ctx,
                                     uint32_t offset, uint32_t extent_bytes,
                                     uint32_t error_id,
                                     uint32_t *out_characters) {
  uint32_t byte_length;
  uint32_t index;
  uint32_t characters = 0u;
  if (al_owning_string_length(ctx, offset, extent_bytes, error_id,
                              &byte_length) != 0)
    return 1;
  if (out_characters == 0) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 1;
  }
  for (index = 0u; index < byte_length; ++index)
    if ((ctx->stack_data[offset + 8u + index] & 0xC0u) != 0x80u)
      ++characters;
  *out_characters = characters;
  return 0;
}

static int32_t al_owning_string_scan_stack_exact(
    al_owning_stack_context *ctx, uint32_t offset, uint32_t extent_bytes,
    uint32_t error_id, uint32_t *out_string_bytes, uint32_t *out_payload_bytes) {
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
  if (!al_owning_scan_string(&view, offset, owner_end, out_string_bytes,
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
    uint32_t right_extent_bytes, uint32_t error_id, uint32_t *out_string_bytes,
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
  if (out_string_bytes == 0 || out_payload_bytes == 0 || out_extent_bytes == 0) {
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
  payload_wide = 8ull + units_wide;
  extent_wide = (payload_wide + 7ull) & ~7ull;
  if (units_wide > INT32_MAX || payload_wide > UINT32_MAX ||
      extent_wide > UINT32_MAX) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_DIAGNOSTIC, error_id,
                          UINT32_MAX, ctx->stack_capacity_bytes);
    return 1;
  }
  *out_string_bytes = (uint32_t)units_wide;
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
  uint32_t string_bytes;
  uint32_t payload;
  uint32_t extent;
  uint32_t left_units;
  uint32_t right_units;
  uint32_t ignored_payload;
  uint32_t left_data_bytes;
  uint32_t right_data_bytes;
  uint32_t cursor;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (al_owning_string_concat_plan(ctx, left_offset, left_extent_bytes,
                                   right_offset, right_extent_bytes, error_id,
                                   &string_bytes, &payload, &extent) != 0)
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
  left_data_bytes = left_units;
  right_data_bytes = right_units;
  al_owning_write_u32_le(ctx->stack_data + destination_offset, 0u, string_bytes);
  ctx->stack_data[destination_offset + 4u] = 0u;
  ctx->stack_data[destination_offset + 5u] = 0u;
  ctx->stack_data[destination_offset + 6u] = 0u;
  ctx->stack_data[destination_offset + 7u] = 0u;
  cursor = destination_offset + 8u;
  al_owning_bytes_copy(ctx->stack_data + cursor,
                       ctx->stack_data + left_offset + 8u, left_data_bytes);
  cursor += left_data_bytes;
  al_owning_bytes_copy(ctx->stack_data + cursor,
                       ctx->stack_data + right_offset + 8u, right_data_bytes);
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

/* A packed List(String) on the owning stack: u32 count, four zero bytes, then
 * each String element back to back. The walk is exact: the last element must
 * end at the list owner end. */
static int32_t al_owning_string_list_walk(
    al_owning_stack_context *ctx, uint32_t list_offset,
    uint32_t list_extent_bytes, uint32_t error_id, uint32_t *out_count,
    uint64_t *out_total_units) {
  al_owning_scan_view view;
  uint32_t owner_end;
  uint32_t count;
  uint32_t cursor;
  uint32_t index;
  uint64_t total_units = 0ull;
  if ((list_offset & 7u) != 0u || list_offset > ctx->cursor_bytes ||
      list_extent_bytes > ctx->cursor_bytes - list_offset ||
      list_extent_bytes < 8u) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          list_extent_bytes, ctx->cursor_bytes);
    return 0;
  }
  owner_end = list_offset + list_extent_bytes;
  count = al_owning_read_u32_le(ctx->stack_data, list_offset);
  if (ctx->stack_data[list_offset + 4u] != 0u ||
      ctx->stack_data[list_offset + 5u] != 0u ||
      ctx->stack_data[list_offset + 6u] != 0u ||
      ctx->stack_data[list_offset + 7u] != 0u) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 8u,
                          list_extent_bytes);
    return 0;
  }
  view.ctx = ctx;
  view.bytes = ctx->stack_data;
  view.length = owner_end;
  view.error_id = error_id;
  view.external = 0;
  cursor = list_offset + 8u;
  for (index = 0u; index < count; ++index) {
    uint32_t units;
    uint32_t payload;
    uint32_t extent;
    if (!al_owning_scan_string(&view, cursor, owner_end, &units, &payload,
                               &extent))
      return 0;
    total_units += units;
    cursor += extent;
  }
  if (cursor != owner_end) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, cursor,
                          owner_end);
    return 0;
  }
  *out_count = count;
  *out_total_units = total_units;
  return 1;
}

static int32_t al_owning_string_result_plan(al_owning_stack_context *ctx,
                                            uint64_t units_wide,
                                            uint32_t error_id,
                                            uint32_t *out_string_bytes,
                                            uint32_t *out_payload_bytes,
                                            uint32_t *out_extent_bytes) {
  uint64_t payload_wide = 8ull + units_wide;
  uint64_t extent_wide = (payload_wide + 7ull) & ~7ull;
  if (units_wide > INT32_MAX || payload_wide > UINT32_MAX ||
      extent_wide > UINT32_MAX) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_DIAGNOSTIC, error_id,
                          UINT32_MAX, ctx->stack_capacity_bytes);
    return 0;
  }
  *out_string_bytes = (uint32_t)units_wide;
  *out_payload_bytes = (uint32_t)payload_wide;
  *out_extent_bytes = (uint32_t)extent_wide;
  return 1;
}

/* Checks an exact, aligned, reserved destination that does not overlap the
 * source owner range, then writes the String header. */
static int32_t al_owning_string_result_begin(
    al_owning_stack_context *ctx, uint32_t destination_offset,
    uint32_t destination_extent_bytes, uint32_t planned_extent,
    uint32_t string_bytes, uint32_t source_offset, uint32_t source_extent_bytes,
    uint32_t error_id) {
  if (planned_extent != destination_extent_bytes ||
      (destination_offset & 7u) != 0u ||
      destination_offset > ctx->cursor_bytes ||
      destination_extent_bytes > ctx->cursor_bytes - destination_offset ||
      (destination_offset < source_offset + source_extent_bytes &&
       source_offset < destination_offset + destination_extent_bytes)) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          planned_extent, destination_extent_bytes);
    return 0;
  }
  al_owning_write_u32_le(ctx->stack_data + destination_offset, 0u, string_bytes);
  ctx->stack_data[destination_offset + 4u] = 0u;
  ctx->stack_data[destination_offset + 5u] = 0u;
  ctx->stack_data[destination_offset + 6u] = 0u;
  ctx->stack_data[destination_offset + 7u] = 0u;
  return 1;
}

static void al_owning_string_result_finish(al_owning_stack_context *ctx,
                                           uint32_t destination_offset,
                                           uint32_t cursor, uint32_t payload,
                                           uint32_t extent, uint32_t type_id) {
  while (cursor < destination_offset + extent)
    ctx->stack_data[cursor++] = 0u;
  al_owning_mark(ctx, destination_offset, extent, 1, 0);
  ctx->deep_copy_bytes += extent;
  al_owning_event(ctx, AL_OWNING_EVENT_ALLOCATE, type_id, destination_offset,
                  extent, payload, 0u, 0u, 0u);
}

int32_t al_owning_string_join_plan(al_owning_stack_context *ctx,
                                   uint32_t list_offset,
                                   uint32_t list_extent_bytes,
                                   uint32_t error_id, uint32_t *out_string_bytes,
                                   uint32_t *out_payload_bytes,
                                   uint32_t *out_extent_bytes) {
  uint32_t count;
  uint64_t units_wide;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (out_string_bytes == 0 || out_payload_bytes == 0 || out_extent_bytes == 0) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 1;
  }
  if (!al_owning_string_list_walk(ctx, list_offset, list_extent_bytes,
                                  error_id, &count, &units_wide) ||
      !al_owning_string_result_plan(ctx, units_wide, error_id, out_string_bytes,
                                    out_payload_bytes, out_extent_bytes))
    return 1;
  return 0;
}

/* One copy per part: every String element's UTF-8 data is copied once into
 * the exact result reserved by the plan. */
int32_t al_owning_string_join_write(al_owning_stack_context *ctx,
                                    uint32_t destination_offset,
                                    uint32_t destination_extent_bytes,
                                    uint32_t list_offset,
                                    uint32_t list_extent_bytes,
                                    uint32_t type_id, uint32_t error_id) {
  uint32_t string_bytes;
  uint32_t payload;
  uint32_t extent;
  uint32_t count;
  uint32_t source;
  uint32_t cursor;
  uint32_t index;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (al_owning_string_join_plan(ctx, list_offset, list_extent_bytes, error_id,
                                 &string_bytes, &payload, &extent) != 0)
    return 1;
  count = al_owning_read_u32_le(ctx->stack_data, list_offset);
  if (!al_owning_string_result_begin(ctx, destination_offset,
                                     destination_extent_bytes, extent,
                                     string_bytes, list_offset,
                                     list_extent_bytes, error_id))
    return 1;
  cursor = destination_offset + 8u;
  source = list_offset + 8u;
  for (index = 0u; index < count; ++index) {
    const uint32_t data_bytes = al_owning_read_u32_le(ctx->stack_data, source);
    al_owning_bytes_copy(ctx->stack_data + cursor,
                         ctx->stack_data + source + 8u, data_bytes);
    cursor += data_bytes;
    source += (8u + data_bytes + 7u) & ~7u;
  }
  al_owning_string_result_finish(ctx, destination_offset, cursor, payload,
                                 extent, type_id);
  return 0;
}

/* n-way String join over separate stack values: `parts` holds (offset, extent)
 * pairs. The compiler fuses a `[a, b, c]` String literal followed by
 * string.join into this call, so the parts are never copied into a list. */
int32_t al_owning_string_join_parts_plan(al_owning_stack_context *ctx,
                                         const uint32_t *parts,
                                         uint32_t count, uint32_t error_id,
                                         uint32_t *out_string_bytes,
                                         uint32_t *out_payload_bytes,
                                         uint32_t *out_extent_bytes) {
  uint64_t units_wide = 0ull;
  uint32_t index;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (parts == 0 || out_string_bytes == 0 || out_payload_bytes == 0 ||
      out_extent_bytes == 0) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 1;
  }
  for (index = 0u; index < count; ++index) {
    uint32_t units;
    uint32_t ignored_payload;
    if (!al_owning_string_scan_stack_exact(ctx, parts[index * 2u],
                                           parts[index * 2u + 1u], error_id,
                                           &units, &ignored_payload))
      return 1;
    units_wide += units;
  }
  if (!al_owning_string_result_plan(ctx, units_wide, error_id, out_string_bytes,
                                    out_payload_bytes, out_extent_bytes))
    return 1;
  return 0;
}

int32_t al_owning_string_join_parts_write(al_owning_stack_context *ctx,
                                          uint32_t destination_offset,
                                          uint32_t destination_extent_bytes,
                                          const uint32_t *parts,
                                          uint32_t count, uint32_t type_id,
                                          uint32_t error_id) {
  uint32_t string_bytes;
  uint32_t payload;
  uint32_t extent;
  uint32_t cursor;
  uint32_t index;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (al_owning_string_join_parts_plan(ctx, parts, count, error_id,
                                       &string_bytes, &payload, &extent) != 0)
    return 1;
  /* The destination is the fresh cursor: every part lies below it. */
  for (index = 0u; index < count; ++index) {
    if (parts[index * 2u] + parts[index * 2u + 1u] > destination_offset) {
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                            parts[index * 2u], destination_offset);
      return 1;
    }
  }
  if (!al_owning_string_result_begin(ctx, destination_offset,
                                     destination_extent_bytes, extent,
                                     string_bytes, destination_offset, 0u,
                                     error_id))
    return 1;
  cursor = destination_offset + 8u;
  for (index = 0u; index < count; ++index) {
    const uint32_t source = parts[index * 2u];
    const uint32_t data_bytes = al_owning_read_u32_le(ctx->stack_data, source);
    al_owning_bytes_copy(ctx->stack_data + cursor,
                         ctx->stack_data + source + 8u, data_bytes);
    cursor += data_bytes;
  }
  al_owning_string_result_finish(ctx, destination_offset, cursor, payload,
                                 extent, type_id);
  return 0;
}

/* Rails ERB::Util.html_escape: exactly & < > " ' are replaced; every other
 * UTF-8 byte is copied unchanged (specials are ASCII, so multi-byte sequences
 * never match). All
 * five specials are below 64, so one mask test classifies a byte. */
#define AL_OWNING_HTML_SPECIAL_MASK                                            \
  ((1ull << 0x22u) | (1ull << 0x26u) | (1ull << 0x27u) | (1ull << 0x3Cu) |   \
   (1ull << 0x3Eu))

static int al_owning_html_is_special(uint32_t unit) {
  return unit < 64u && ((AL_OWNING_HTML_SPECIAL_MASK >> unit) & 1ull) != 0ull;
}

/* Extra bytes an escaped special adds beyond its own one. */
static uint32_t al_owning_html_escape_extra(uint32_t unit) {
  switch (unit) {
  case 0x26u: /* &amp; */
  case 0x27u: /* &#39; */
    return 4u;
  case 0x3Cu: /* &lt; */
  case 0x3Eu: /* &gt; */
    return 3u;
  case 0x22u: /* &quot; */
    return 5u;
  default:
    return 0u;
  }
}

static const char *al_owning_html_escape_entity(uint32_t unit) {
  switch (unit) {
  case 0x26u:
    return "&amp;";
  case 0x27u:
    return "&#39;";
  case 0x3Cu:
    return "&lt;";
  case 0x3Eu:
    return "&gt;";
  case 0x22u:
    return "&quot;";
  default:
    return 0;
  }
}

int32_t al_owning_string_html_escape_plan(
    al_owning_stack_context *ctx, uint32_t offset, uint32_t extent_bytes,
    uint32_t error_id, uint32_t *out_string_bytes, uint32_t *out_payload_bytes,
    uint32_t *out_extent_bytes) {
  uint32_t units;
  uint32_t ignored_payload;
  uint32_t index;
  uint64_t escaped_units = 0ull;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (out_string_bytes == 0 || out_payload_bytes == 0 || out_extent_bytes == 0) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 1;
  }
  if (!al_owning_string_scan_stack_exact(ctx, offset, extent_bytes, error_id,
                                         &units, &ignored_payload))
    return 1;
  escaped_units = units;
  for (index = 0u; index < units; ++index) {
    const uint32_t unit = ctx->stack_data[offset + 8u + index];
    if (al_owning_html_is_special(unit))
      escaped_units += al_owning_html_escape_extra(unit);
  }
  if (!al_owning_string_result_plan(ctx, escaped_units, error_id,
                                    out_string_bytes, out_payload_bytes,
                                    out_extent_bytes))
    return 1;
  return 0;
}

int32_t al_owning_string_html_escape_write(
    al_owning_stack_context *ctx, uint32_t destination_offset,
    uint32_t destination_extent_bytes, uint32_t offset, uint32_t extent_bytes,
    uint32_t type_id, uint32_t error_id) {
  uint32_t string_bytes;
  uint32_t payload;
  uint32_t extent;
  uint32_t units;
  uint32_t cursor;
  uint32_t index;
  uint32_t run_start;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (al_owning_string_html_escape_plan(ctx, offset, extent_bytes, error_id,
                                        &string_bytes, &payload, &extent) != 0)
    return 1;
  units = al_owning_read_u32_le(ctx->stack_data, offset);
  if (!al_owning_string_result_begin(ctx, destination_offset,
                                     destination_extent_bytes, extent,
                                     string_bytes, offset, extent_bytes,
                                     error_id))
    return 1;
  cursor = destination_offset + 8u;
  run_start = 0u;
  for (index = 0u; index < units; ++index) {
    const uint32_t unit = ctx->stack_data[offset + 8u + index];
    if (al_owning_html_is_special(unit)) {
      const char *entity = al_owning_html_escape_entity(unit);
      const uint32_t run_bytes = index - run_start;
      al_owning_bytes_copy(ctx->stack_data + cursor,
                           ctx->stack_data + offset + 8u + run_start,
                           run_bytes);
      cursor += run_bytes;
      while (*entity != '\0')
        ctx->stack_data[cursor++] = (uint8_t)*entity++;
      run_start = index + 1u;
    }
  }
  al_owning_bytes_copy(ctx->stack_data + cursor,
                       ctx->stack_data + offset + 8u + run_start,
                       units - run_start);
  cursor += units - run_start;
  al_owning_string_result_finish(ctx, destination_offset, cursor, payload,
                                 extent, type_id);
  return 0;
}

/* HTML fragment check: a C port of AgentLang.HtmlMarkup.check (F#) for text
 * without template holes. Keep the two in step: a fragment is balanced markup
 * (void elements and "/>" excepted), complete comments and <!doctype>, raw
 * <script>/<style> text up to its own end tag, and no bare '<'. Anything else
 * is rejected. Every special character is ASCII, so UTF-8 continuation bytes
 * are ordinary text, exactly as non-ASCII UTF-16 units are in F#. Nesting
 * deeper than AL_OWNING_HTML_MAX_DEPTH is rejected (same bound in F#). */
#define AL_OWNING_HTML_MAX_DEPTH 256u

enum al_owning_html_state {
  AL_HTML_DATA,
  AL_HTML_TAG_OPEN,
  AL_HTML_TAG_NAME,
  AL_HTML_END_TAG_OPEN,
  AL_HTML_END_TAG_NAME,
  AL_HTML_AFTER_END_TAG_NAME,
  AL_HTML_BEFORE_ATTRIBUTE,
  AL_HTML_ATTRIBUTE_NAME,
  AL_HTML_AFTER_ATTRIBUTE_NAME,
  AL_HTML_BEFORE_ATTRIBUTE_VALUE,
  AL_HTML_ATTRIBUTE_VALUE_DOUBLE,
  AL_HTML_ATTRIBUTE_VALUE_SINGLE,
  AL_HTML_ATTRIBUTE_VALUE_UNQUOTED,
  AL_HTML_SELF_CLOSING,
  AL_HTML_MARKUP_DECLARATION,
  AL_HTML_COMMENT,
  AL_HTML_DOCTYPE,
  AL_HTML_RAW_TEXT
};

typedef struct al_owning_html_name {
  uint32_t start;
  uint32_t length;
} al_owning_html_name;

static uint8_t al_owning_ascii_lower(uint8_t ch) {
  return (ch >= 'A' && ch <= 'Z') ? (uint8_t)(ch + 32u) : ch;
}

static int al_owning_html_tag_start(uint8_t ch) {
  return (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
}

static int al_owning_html_tag_char(uint8_t ch) {
  return al_owning_html_tag_start(ch) || (ch >= '0' && ch <= '9') ||
         ch == '-' || ch == ':';
}

static int al_owning_html_space(uint8_t ch) {
  return ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || ch == '\f';
}

static int al_owning_html_attribute_char(uint8_t ch) {
  return !al_owning_html_space(ch) && ch != '"' && ch != '\'' && ch != '>' &&
         ch != '/' && ch != '=' && ch != '<' && ch >= 0x20u;
}

/* Case-insensitive comparison of text[start..start+length) with a lower-case
 * ASCII literal. */
static int al_owning_html_name_is(const uint8_t *text, al_owning_html_name name,
                                  const char *literal) {
  uint32_t index;
  for (index = 0u; index < name.length; ++index) {
    if (literal[index] == '\0' ||
        al_owning_ascii_lower(text[name.start + index]) != (uint8_t)literal[index])
      return 0;
  }
  return literal[name.length] == '\0';
}

static int al_owning_html_names_equal(const uint8_t *text,
                                      al_owning_html_name left,
                                      al_owning_html_name right) {
  uint32_t index;
  if (left.length != right.length)
    return 0;
  for (index = 0u; index < left.length; ++index) {
    if (al_owning_ascii_lower(text[left.start + index]) !=
        al_owning_ascii_lower(text[right.start + index]))
      return 0;
  }
  return 1;
}

static int al_owning_html_void(const uint8_t *text, al_owning_html_name name) {
  static const char *const names[] = {"area", "base", "br", "col", "embed",
                                      "hr", "img", "input", "link", "meta",
                                      "source", "track", "wbr"};
  uint32_t index;
  for (index = 0u; index < sizeof(names) / sizeof(names[0]); ++index) {
    if (al_owning_html_name_is(text, name, names[index]))
      return 1;
  }
  return 0;
}

static int al_owning_html_raw(const uint8_t *text, al_owning_html_name name) {
  return al_owning_html_name_is(text, name, "script") ||
         al_owning_html_name_is(text, name, "style");
}

/* 1 when text[0..length) is a well-formed fragment. */
static int al_owning_html_fragment_valid(const uint8_t *text, uint32_t length) {
  al_owning_html_name stack[AL_OWNING_HTML_MAX_DEPTH];
  al_owning_html_name name;
  al_owning_html_name raw_element;
  uint32_t depth = 0u;
  uint32_t declaration = 0u; /* bytes seen after "<!" */
  uint32_t comment_tail = 0u; /* consecutive '-' before a possible '>' */
  enum al_owning_html_state state = AL_HTML_DATA;
  uint32_t index;
  /* Field assignments, not aggregate initializers: the freestanding runtime
   * has no memset to lower them to. */
  name.start = 0u;
  name.length = 0u;
  raw_element.start = 0u;
  raw_element.length = 0u;
  for (index = 0u; index < length; ++index) {
    const uint8_t ch = text[index];
    switch (state) {
    case AL_HTML_DATA:
      if (ch == '<')
        state = AL_HTML_TAG_OPEN;
      break;
    case AL_HTML_TAG_OPEN:
      if (al_owning_html_tag_start(ch)) {
        name.start = index;
        name.length = 1u;
        state = AL_HTML_TAG_NAME;
      } else if (ch == '/') {
        state = AL_HTML_END_TAG_OPEN;
      } else if (ch == '!') {
        declaration = 0u;
        state = AL_HTML_MARKUP_DECLARATION;
      } else {
        return 0;
      }
      break;
    case AL_HTML_TAG_NAME:
    case AL_HTML_BEFORE_ATTRIBUTE:
    case AL_HTML_ATTRIBUTE_NAME:
    case AL_HTML_AFTER_ATTRIBUTE_NAME:
    case AL_HTML_ATTRIBUTE_VALUE_UNQUOTED:
    case AL_HTML_SELF_CLOSING: {
      int finish = 0;
      int self_closed = 0;
      if (state == AL_HTML_TAG_NAME) {
        if (al_owning_html_tag_char(ch))
          ++name.length;
        else if (al_owning_html_space(ch))
          state = AL_HTML_BEFORE_ATTRIBUTE;
        else if (ch == '>')
          finish = 1;
        else if (ch == '/')
          state = AL_HTML_SELF_CLOSING;
        else
          return 0;
      } else if (state == AL_HTML_BEFORE_ATTRIBUTE) {
        if (al_owning_html_space(ch)) {
        } else if (ch == '>')
          finish = 1;
        else if (ch == '/')
          state = AL_HTML_SELF_CLOSING;
        else if (al_owning_html_attribute_char(ch))
          state = AL_HTML_ATTRIBUTE_NAME;
        else
          return 0;
      } else if (state == AL_HTML_ATTRIBUTE_NAME) {
        if (al_owning_html_attribute_char(ch)) {
        } else if (ch == '=')
          state = AL_HTML_BEFORE_ATTRIBUTE_VALUE;
        else if (al_owning_html_space(ch))
          state = AL_HTML_AFTER_ATTRIBUTE_NAME;
        else if (ch == '>')
          finish = 1;
        else if (ch == '/')
          state = AL_HTML_SELF_CLOSING;
        else
          return 0;
      } else if (state == AL_HTML_AFTER_ATTRIBUTE_NAME) {
        if (al_owning_html_space(ch)) {
        } else if (ch == '=')
          state = AL_HTML_BEFORE_ATTRIBUTE_VALUE;
        else if (ch == '>')
          finish = 1;
        else if (ch == '/')
          state = AL_HTML_SELF_CLOSING;
        else if (al_owning_html_attribute_char(ch))
          state = AL_HTML_ATTRIBUTE_NAME;
        else
          return 0;
      } else if (state == AL_HTML_ATTRIBUTE_VALUE_UNQUOTED) {
        if (al_owning_html_space(ch))
          state = AL_HTML_BEFORE_ATTRIBUTE;
        else if (ch == '>')
          finish = 1;
        else if (ch == '"' || ch == '\'' || ch == '=' || ch == '<' || ch == '`')
          return 0;
      } else { /* AL_HTML_SELF_CLOSING */
        if (ch != '>')
          return 0;
        finish = 1;
        self_closed = 1;
      }
      if (finish) {
        if (self_closed || al_owning_html_void(text, name)) {
          state = AL_HTML_DATA;
        } else if (al_owning_html_raw(text, name)) {
          raw_element = name;
          state = AL_HTML_RAW_TEXT;
        } else {
          if (depth >= AL_OWNING_HTML_MAX_DEPTH)
            return 0;
          stack[depth++] = name;
          state = AL_HTML_DATA;
        }
      }
      break;
    }
    case AL_HTML_END_TAG_OPEN:
      if (!al_owning_html_tag_start(ch))
        return 0;
      name.start = index;
      name.length = 1u;
      state = AL_HTML_END_TAG_NAME;
      break;
    case AL_HTML_END_TAG_NAME:
    case AL_HTML_AFTER_END_TAG_NAME:
      if (state == AL_HTML_END_TAG_NAME && al_owning_html_tag_char(ch)) {
        ++name.length;
        break;
      }
      if (al_owning_html_space(ch)) {
        state = AL_HTML_AFTER_END_TAG_NAME;
        break;
      }
      if (ch != '>')
        return 0;
      if (al_owning_html_void(text, name) || depth == 0u ||
          !al_owning_html_names_equal(text, stack[depth - 1u], name))
        return 0;
      --depth;
      state = AL_HTML_DATA;
      break;
    case AL_HTML_BEFORE_ATTRIBUTE_VALUE:
      if (al_owning_html_space(ch)) {
      } else if (ch == '"')
        state = AL_HTML_ATTRIBUTE_VALUE_DOUBLE;
      else if (ch == '\'')
        state = AL_HTML_ATTRIBUTE_VALUE_SINGLE;
      else if (ch == '>' || ch == '=' || ch == '<' || ch == '`')
        return 0;
      else
        state = AL_HTML_ATTRIBUTE_VALUE_UNQUOTED;
      break;
    case AL_HTML_ATTRIBUTE_VALUE_DOUBLE:
      if (ch == '"')
        state = AL_HTML_BEFORE_ATTRIBUTE;
      break;
    case AL_HTML_ATTRIBUTE_VALUE_SINGLE:
      if (ch == '\'')
        state = AL_HTML_BEFORE_ATTRIBUTE;
      break;
    case AL_HTML_MARKUP_DECLARATION: {
      static const char doctype[] = "doctype";
      const uint8_t lower = al_owning_ascii_lower(ch);
      const int dash_prefix = declaration < 2u && ch == '-' &&
                              (declaration == 0u || text[index - 1u] == '-');
      const int doctype_prefix = declaration < 7u &&
                                 lower == (uint8_t)doctype[declaration] &&
                                 (declaration == 0u ||
                                  al_owning_ascii_lower(text[index - 1u]) ==
                                      (uint8_t)doctype[declaration - 1u]);
      ++declaration;
      if (dash_prefix && declaration == 2u) {
        comment_tail = 0u;
        state = AL_HTML_COMMENT;
      } else if (doctype_prefix && declaration == 7u) {
        if (depth != 0u)
          return 0;
        state = AL_HTML_DOCTYPE;
      } else if (!dash_prefix && !doctype_prefix) {
        return 0;
      }
      break;
    }
    case AL_HTML_COMMENT:
      if (ch == '>' && comment_tail >= 2u)
        state = AL_HTML_DATA;
      comment_tail = ch == '-' ? comment_tail + 1u : 0u;
      break;
    case AL_HTML_DOCTYPE:
      if (ch == '>')
        state = AL_HTML_DATA;
      else if (ch == '<')
        return 0;
      break;
    case AL_HTML_RAW_TEXT:
      /* Raw text ends only at "</" + its own element name. */
      if (ch == '<' && index + 2u + raw_element.length <= length &&
          text[index + 1u] == '/') {
        al_owning_html_name candidate;
        candidate.start = index + 2u;
        candidate.length = raw_element.length;
        if (al_owning_html_names_equal(text, candidate, raw_element) &&
            (index + 2u + raw_element.length == length ||
             !al_owning_html_tag_char(text[index + 2u + raw_element.length]))) {
          if (depth >= AL_OWNING_HTML_MAX_DEPTH)
            return 0;
          stack[depth++] = raw_element;
          name = candidate;
          index += 1u + raw_element.length;
          state = AL_HTML_END_TAG_NAME;
        }
      }
      break;
    }
  }
  return state == AL_HTML_DATA && depth == 0u;
}

int32_t al_owning_string_html_fragment(al_owning_stack_context *ctx,
                                       uint32_t offset, uint32_t extent_bytes,
                                       uint32_t error_id, uint32_t *out_valid) {
  uint32_t units;
  uint32_t ignored_payload;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (out_valid == 0) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 1;
  }
  if (!al_owning_string_scan_stack_exact(ctx, offset, extent_bytes, error_id,
                                         &units, &ignored_payload))
    return 1;
  *out_valid = (uint32_t)al_owning_html_fragment_valid(
      ctx->stack_data + offset + 8u, units);
  return 0;
}

/* Escapes source[0..length) into destination (Rails ERB::Util.html_escape) and
 * returns the bytes written; runs between specials are range-copied once. */
static uint32_t al_owning_html_escape_into(uint8_t *destination,
                                           const uint8_t *source,
                                           uint32_t length) {
  uint32_t written = 0u;
  uint32_t run_start = 0u;
  uint32_t index;
  for (index = 0u; index < length; ++index) {
    const uint32_t unit = source[index];
    if (al_owning_html_is_special(unit)) {
      const char *entity = al_owning_html_escape_entity(unit);
      al_owning_bytes_copy(destination + written, source + run_start,
                           index - run_start);
      written += index - run_start;
      while (*entity != '\0')
        destination[written++] = (uint8_t)*entity++;
      run_start = index + 1u;
    }
  }
  al_owning_bytes_copy(destination + written, source + run_start,
                       length - run_start);
  return written + (length - run_start);
}

/* html"…" templates: parts are read-only literal byte ranges (bytes != 0) or
 * stack values (bytes == 0; offset/extent): a String inserted as-is (mode 1,
 * Html) or escaped (mode 2, String), or a List<Html> whose items are inserted
 * in order (mode 3). The plan measures the exact result; the write
 * copies every literal and hole byte once into the reserved destination, so
 * literals are never staged in the arena and no escaped temporary exists. */
int32_t al_owning_html_template_plan(al_owning_stack_context *ctx,
                                     const al_owning_template_part *parts,
                                     uint32_t count, uint32_t error_id,
                                     uint32_t *out_string_bytes,
                                     uint32_t *out_payload_bytes,
                                     uint32_t *out_extent_bytes) {
  uint64_t total = 0ull;
  uint32_t index;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (parts == 0 || out_string_bytes == 0 || out_payload_bytes == 0 ||
      out_extent_bytes == 0) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 1;
  }
  for (index = 0u; index < count; ++index) {
    const al_owning_template_part *part = &parts[index];
    if (part->bytes != 0) {
      total += part->offset_or_length;
    } else if (part->mode == 3u) {
      uint32_t item_count;
      uint64_t list_units;
      if (!al_owning_string_list_walk(ctx, part->offset_or_length,
                                      part->extent_bytes, error_id,
                                      &item_count, &list_units))
        return 1;
      total += list_units;
    } else {
      uint32_t units;
      uint32_t ignored_payload;
      uint32_t unit_index;
      if ((part->mode != 1u && part->mode != 2u) ||
          !al_owning_string_scan_stack_exact(ctx, part->offset_or_length,
                                             part->extent_bytes, error_id,
                                             &units, &ignored_payload)) {
        if (ctx->status == AL_OWNING_STATUS_OK)
          al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                                index, part->mode);
        return 1;
      }
      total += units;
      if (part->mode == 2u) {
        const uint8_t *data = ctx->stack_data + part->offset_or_length + 8u;
        for (unit_index = 0u; unit_index < units; ++unit_index) {
          if (al_owning_html_is_special(data[unit_index]))
            total += al_owning_html_escape_extra(data[unit_index]);
        }
      }
    }
  }
  if (!al_owning_string_result_plan(ctx, total, error_id, out_string_bytes,
                                    out_payload_bytes, out_extent_bytes))
    return 1;
  return 0;
}

int32_t al_owning_html_template_write(al_owning_stack_context *ctx,
                                      uint32_t destination_offset,
                                      uint32_t destination_extent_bytes,
                                      uint32_t planned_string_bytes,
                                      uint32_t planned_payload_bytes,
                                      const al_owning_template_part *parts,
                                      uint32_t count, uint32_t type_id,
                                      uint32_t error_id) {
  uint32_t string_bytes;
  uint32_t payload;
  uint32_t extent;
  uint32_t cursor;
  uint32_t index;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
#if AL_OWNING_TRUSTED_GENERATED
  /* The generated code just ran the plan over these same parts and reserved
   * its extent; nothing ran in between, so writing does not plan twice
   * (report 269). */
  string_bytes = planned_string_bytes;
  payload = planned_payload_bytes;
  extent = destination_extent_bytes;
#else
  /* Diagnostic self-check: the plan must reproduce the caller's numbers. */
  if (al_owning_html_template_plan(ctx, parts, count, error_id, &string_bytes,
                                   &payload, &extent) != 0)
    return 1;
  if (string_bytes != planned_string_bytes || payload != planned_payload_bytes ||
      extent != destination_extent_bytes) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, extent,
                          destination_extent_bytes);
    return 1;
  }
#endif
  /* The destination is the fresh cursor: every stack hole lies below it. */
  for (index = 0u; index < count; ++index) {
    if (parts[index].bytes == 0 &&
        parts[index].offset_or_length + parts[index].extent_bytes >
            destination_offset) {
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                            parts[index].offset_or_length, destination_offset);
      return 1;
    }
  }
  if (!al_owning_string_result_begin(ctx, destination_offset,
                                     destination_extent_bytes, extent,
                                     string_bytes, destination_offset, 0u,
                                     error_id))
    return 1;
  cursor = destination_offset + 8u;
  for (index = 0u; index < count; ++index) {
    const al_owning_template_part *part = &parts[index];
    if (part->bytes != 0) {
      al_owning_bytes_copy(ctx->stack_data + cursor, part->bytes,
                           part->offset_or_length);
      cursor += part->offset_or_length;
    } else {
      const uint32_t source = part->offset_or_length;
      const uint32_t data_bytes = al_owning_read_u32_le(ctx->stack_data, source);
      if (part->mode == 3u) {
        /* List<Html>: a u32 item count, 4 reserved bytes, then packed items. */
        uint32_t item = source + 8u;
        uint32_t item_index;
        for (item_index = 0u; item_index < data_bytes; ++item_index) {
          const uint32_t item_bytes = al_owning_read_u32_le(ctx->stack_data, item);
          al_owning_bytes_copy(ctx->stack_data + cursor,
                               ctx->stack_data + item + 8u, item_bytes);
          cursor += item_bytes;
          item += (8u + item_bytes + 7u) & ~7u;
        }
      } else if (part->mode == 2u) {
        cursor += al_owning_html_escape_into(ctx->stack_data + cursor,
                                             ctx->stack_data + source + 8u,
                                             data_bytes);
      } else {
        al_owning_bytes_copy(ctx->stack_data + cursor,
                             ctx->stack_data + source + 8u, data_bytes);
        cursor += data_bytes;
      }
    }
  }
  al_owning_string_result_finish(ctx, destination_offset, cursor, payload,
                                 extent, type_id);
  return 0;
}

/* Ordinal search over UTF-8 bytes: mode 0 = contains, 1 = starts-with,
 * 2 = ends-with. UTF-8 is self-synchronizing, so on well-formed strings a byte
 * match is a whole-character match. An empty needle always matches. */
int32_t al_owning_string_find(al_owning_stack_context *ctx,
                              uint32_t haystack_offset,
                              uint32_t haystack_extent_bytes,
                              uint32_t needle_offset,
                              uint32_t needle_extent_bytes, uint32_t mode,
                              uint32_t error_id, uint32_t *out_found) {
  uint32_t haystack_units;
  uint32_t needle_units;
  uint32_t ignored_payload;
  uint32_t first;
  uint32_t last;
  uint32_t start;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (out_found == 0 || mode > 2u) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, mode, 0u);
    return 1;
  }
  if (!al_owning_string_scan_stack_exact(ctx, haystack_offset,
                                         haystack_extent_bytes, error_id,
                                         &haystack_units, &ignored_payload) ||
      !al_owning_string_scan_stack_exact(ctx, needle_offset,
                                         needle_extent_bytes, error_id,
                                         &needle_units, &ignored_payload))
    return 1;
  *out_found = 0u;
  if (needle_units > haystack_units)
    return 0;
  last = haystack_units - needle_units;
  first = mode == 2u ? last : 0u;
  if (mode == 1u)
    last = 0u;
  for (start = first; start <= last; ++start) {
    const uint8_t *candidate = ctx->stack_data + haystack_offset + 8u + start;
    const uint8_t *needle = ctx->stack_data + needle_offset + 8u;
    uint32_t index = 0u;
    while (index < needle_units && candidate[index] == needle[index])
      ++index;
    if (index == needle_units) {
      *out_found = 1u;
      return 0;
    }
  }
  return 0;
}

/* Writes the header of a list built from two packed lists (concat) or from a
 * list and one element (append, right_offset == UINT32_MAX). The element bytes
 * are then range-copied by the caller. Lists have no length limit; the arena
 * capacity bounds them, and the u32 count cannot overflow within it. */
int32_t al_owning_list_combine_header(al_owning_stack_context *ctx,
                                      uint32_t destination_offset,
                                      uint32_t left_offset,
                                      uint32_t right_offset, uint32_t type_id,
                                      uint32_t error_id) {
  uint64_t total;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if ((destination_offset & 7u) != 0u ||
      !al_owning_range_valid(ctx, destination_offset, 8u) ||
      !al_owning_range_valid(ctx, left_offset, 8u) ||
      (right_offset != UINT32_MAX &&
       !al_owning_range_valid(ctx, right_offset, 8u))) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          destination_offset, ctx->cursor_bytes);
    return 1;
  }
  total = (uint64_t)al_owning_read_u32_le(ctx->stack_data, left_offset) +
          (right_offset == UINT32_MAX
               ? 1ull
               : (uint64_t)al_owning_read_u32_le(ctx->stack_data,
                                                 right_offset));
  if (total > UINT32_MAX) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          UINT32_MAX, ctx->cursor_bytes);
    return 1;
  }
  al_owning_write_u32_le(ctx->stack_data + destination_offset, 0u,
                         (uint32_t)total);
  ctx->stack_data[destination_offset + 4u] = 0u;
  ctx->stack_data[destination_offset + 5u] = 0u;
  ctx->stack_data[destination_offset + 6u] = 0u;
  ctx->stack_data[destination_offset + 7u] = 0u;
  al_owning_mark(ctx, destination_offset, 8u, 1, 0);
  al_owning_event(ctx, AL_OWNING_EVENT_ALLOCATE, type_id, destination_offset,
                  8u, 8u, 0u, 0u, 0u);
  return 0;
}

int32_t al_owning_float_format(al_owning_stack_context *ctx, uint64_t bits,
                               uint8_t *ascii32, uint32_t *out_length,
                               uint32_t error_id) {
  uint8_t formatted[AL_OWNING_FLOAT_ASCII_CAPACITY];
  uint32_t formatted_length = 0u;
  const uint32_t exponent_field = (uint32_t)((bits >> 52u) & 0x7ffu);

  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (ascii32 == 0 || out_length == 0) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 1;
  }
  if (exponent_field == 0x7ffu) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INVALID_REQUEST, error_id, 0u,
                          0u);
    return 1;
  }
  if (!al_owning_float_format_ascii(bits, formatted, &formatted_length) ||
      formatted_length == 0u ||
      formatted_length > AL_OWNING_FLOAT_ASCII_CAPACITY) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 1;
  }
  al_owning_ff_copy_bytes(ascii32, formatted, formatted_length);
  *out_length = formatted_length;
  return 0;
}

int32_t al_owning_int_format(al_owning_stack_context *ctx, int64_t value,
                             uint8_t *ascii32, uint32_t *out_length,
                             uint32_t error_id) {
  uint8_t reversed[20];
  uint64_t magnitude;
  uint32_t digits = 0u;
  uint32_t output_length = 0u;
  int32_t negative = value < 0;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (ascii32 == 0 || out_length == 0) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id, 0u, 0u);
    return 1;
  }
  if (negative) {
    /* This form also handles INT64_MIN without negating it. */
    magnitude = (uint64_t)(-(value + 1)) + UINT64_C(1);
  } else {
    magnitude = (uint64_t)value;
  }
  do {
    reversed[digits++] =
        (uint8_t)('0' + (uint8_t)(magnitude % UINT64_C(10)));
    magnitude /= UINT64_C(10);
  } while (magnitude != 0u);
  if (negative)
    ascii32[output_length++] = (uint8_t)'-';
  while (digits > 0u)
    ascii32[output_length++] = reversed[--digits];
  *out_length = output_length;
  return 0;
}

int32_t al_owning_ascii_string_write(
    al_owning_stack_context *ctx, uint32_t destination_offset,
    uint32_t destination_extent_bytes, const uint8_t *ascii,
    uint32_t ascii_length, uint32_t type_id, uint32_t error_id) {
  uint8_t local_ascii[AL_OWNING_ASCII_CAPACITY];
  uint32_t payload_bytes;
  uint32_t measured_extent;
  uint32_t index;
  uint32_t cursor;
  volatile uint8_t *local_ascii_bytes = local_ascii;
  const volatile uint8_t *ascii_bytes = ascii;
  if (ctx == 0 || ctx->status != AL_OWNING_STATUS_OK)
    return 1;
  if (ctx->stack_data == 0 || ascii == 0 || ascii_length == 0u ||
      ascii_length > AL_OWNING_ASCII_CAPACITY) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          ascii_length, AL_OWNING_ASCII_CAPACITY);
    return 1;
  }

  payload_bytes = 8u + ascii_length;
  measured_extent = (payload_bytes + 7u) & ~7u;
  if (destination_extent_bytes != measured_extent ||
      (destination_offset & 7u) != 0u ||
      destination_offset > UINT32_MAX - destination_extent_bytes ||
      destination_offset > ctx->cursor_bytes ||
      destination_extent_bytes > ctx->cursor_bytes - destination_offset ||
      !al_owning_range_valid(ctx, destination_offset,
                             destination_extent_bytes)) {
    al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                          destination_extent_bytes,
                          ctx->cursor_bytes -
                              (destination_offset <= ctx->cursor_bytes
                                   ? destination_offset
                                   : ctx->cursor_bytes));
    return 1;
  }

  for (index = 0u; index < ascii_length; ++index) {
    if (ascii_bytes[index] > 0x7fu) {
      al_owning_set_failure(ctx, AL_OWNING_STATUS_INTERNAL, error_id,
                            ascii_length, AL_OWNING_ASCII_CAPACITY);
      return 1;
    }
    local_ascii_bytes[index] = ascii_bytes[index];
  }

  al_owning_write_u32_le(ctx->stack_data + destination_offset, 0u,
                         ascii_length);
  for (index = 0u; index < 4u; ++index)
    ctx->stack_data[destination_offset + 4u + index] = 0u;
  cursor = destination_offset + 8u;
  for (index = 0u; index < ascii_length; ++index)
    ctx->stack_data[cursor + index] = local_ascii[index];
  cursor += ascii_length;
  while (cursor < destination_offset + destination_extent_bytes)
    ctx->stack_data[cursor++] = 0u;
  al_owning_mark(ctx, destination_offset, destination_extent_bytes, 1, 0);
  al_owning_event(ctx, AL_OWNING_EVENT_ALLOCATE, type_id, destination_offset,
                  destination_extent_bytes, payload_bytes, 0u, 0u, 0u);
  return 0;
}

void AL_OWNING_PROFILE_IMPL(al_owning_move_range)(
    al_owning_stack_context *ctx, uint32_t destination_offset,
    uint32_t source_offset, uint32_t byte_count, uint32_t payload_bytes,
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
    if (clear_start < source_end)
      al_owning_poison(ctx, clear_start, source_end - clear_start);
  } else if (destination_offset > source_offset) {
    const uint32_t clear_end =
        destination_offset < source_end ? destination_offset : source_end;
    if (source_offset < clear_end)
      al_owning_poison(ctx, source_offset, clear_end - source_offset);
  }
  ctx->move_bytes += byte_count;
  al_owning_event(ctx, event_kind, type_id, destination_offset, byte_count,
                  payload_bytes, source_offset, byte_count, 0u);
}

#if AL_OWNING_DETAILED_PROFILE
void al_owning_move_range(al_owning_stack_context *ctx,
                          uint32_t destination_offset, uint32_t source_offset,
                          uint32_t byte_count, uint32_t payload_bytes,
                          uint32_t type_id, uint32_t event_kind) {
  AL_OWNING_PROFILE_START();
  uint32_t prior_status = ctx == 0 ? AL_OWNING_STATUS_INTERNAL : ctx->status;
  al_owning_move_range_impl(ctx, destination_offset, source_offset,
                            byte_count, payload_bytes, type_id, event_kind);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_ARENA_COPY,
                        ctx == 0 ||
                            (prior_status == AL_OWNING_STATUS_OK &&
                             ctx->status != AL_OWNING_STATUS_OK));
}
#endif

void AL_OWNING_PROFILE_IMPL(al_owning_copy_range)(
    al_owning_stack_context *ctx, uint32_t destination_offset,
    uint32_t source_offset, uint32_t extent_bytes, uint32_t payload_bytes,
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

#if AL_OWNING_DETAILED_PROFILE
void al_owning_copy_range(al_owning_stack_context *ctx,
                          uint32_t destination_offset, uint32_t source_offset,
                          uint32_t extent_bytes, uint32_t payload_bytes,
                          uint32_t type_id, uint32_t event_kind) {
  AL_OWNING_PROFILE_START();
  uint32_t prior_status = ctx == 0 ? AL_OWNING_STATUS_INTERNAL : ctx->status;
  al_owning_copy_range_impl(ctx, destination_offset, source_offset,
                            extent_bytes, payload_bytes, type_id, event_kind);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_ARENA_COPY,
                        ctx == 0 ||
                            (prior_status == AL_OWNING_STATUS_OK &&
                             ctx->status != AL_OWNING_STATUS_OK));
}
#endif

void AL_OWNING_PROFILE_IMPL(al_owning_duplicate)(
    al_owning_stack_context *ctx, uint32_t destination_offset,
    uint32_t source_offset, uint32_t extent_bytes, uint32_t payload_bytes,
    uint32_t type_id) {
#if !AL_OWNING_TRUSTED_GENERATED
  uint64_t checksum;
#endif
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
#if !AL_OWNING_TRUSTED_GENERATED
  checksum = al_owning_checksum(ctx->stack_data + source_offset, extent_bytes);
  ++ctx->duplicate_disjoint_checks;
#endif
  ctx->deep_copy_bytes += extent_bytes;
  al_owning_update_live(ctx, (int32_t)payload_bytes, 0);
  al_owning_event(ctx, AL_OWNING_EVENT_DUPLICATE, type_id, destination_offset,
                  extent_bytes, payload_bytes, source_offset, extent_bytes,
#if AL_OWNING_TRUSTED_GENERATED
                  0u);
#else
                  checksum);
#endif
}

#if AL_OWNING_DETAILED_PROFILE
void al_owning_duplicate(al_owning_stack_context *ctx,
                         uint32_t destination_offset, uint32_t source_offset,
                         uint32_t extent_bytes, uint32_t payload_bytes,
                         uint32_t type_id) {
  AL_OWNING_PROFILE_START();
  uint32_t prior_status = ctx == 0 ? AL_OWNING_STATUS_INTERNAL : ctx->status;
  al_owning_duplicate_impl(ctx, destination_offset, source_offset,
                           extent_bytes, payload_bytes, type_id);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_ARENA_COPY,
                        ctx == 0 ||
                            (prior_status == AL_OWNING_STATUS_OK &&
                             ctx->status != AL_OWNING_STATUS_OK));
}
#endif

void AL_OWNING_PROFILE_IMPL(al_owning_drop)(
    al_owning_stack_context *ctx, uint32_t start_offset,
    uint32_t extent_bytes, uint32_t payload_bytes, uint32_t type_id) {
#if !AL_OWNING_TRUSTED_GENERATED
  uint32_t index;
#endif
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
#if !AL_OWNING_TRUSTED_GENERATED
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
#endif
  if (ctx->status != AL_OWNING_STATUS_OK)
    return;
  al_owning_event(ctx, AL_OWNING_EVENT_DROP, type_id, start_offset,
                  extent_bytes, payload_bytes, 0u, 0u, 0u);
  al_owning_poison(ctx, start_offset, old_cursor - start_offset);
  ctx->cursor_bytes = start_offset;
  al_owning_update_live(ctx, -(int32_t)payload_bytes, 0);
}

#if AL_OWNING_DETAILED_PROFILE
void al_owning_drop(al_owning_stack_context *ctx, uint32_t start_offset,
                    uint32_t extent_bytes, uint32_t payload_bytes,
                    uint32_t type_id) {
  AL_OWNING_PROFILE_START();
  uint32_t prior_status = ctx == 0 ? AL_OWNING_STATUS_INTERNAL : ctx->status;
  al_owning_drop_impl(ctx, start_offset, extent_bytes, payload_bytes,
                      type_id);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_ARENA_COPY,
                        ctx == 0 ||
                            (prior_status == AL_OWNING_STATUS_OK &&
                             ctx->status != AL_OWNING_STATUS_OK));
}
#endif

void AL_OWNING_PROFILE_IMPL(al_owning_store_local)(
    al_owning_stack_context *ctx, uint32_t destination_offset,
    uint32_t source_offset, uint32_t reserved_bytes,
    uint32_t source_extent_bytes, uint32_t source_payload_bytes,
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

#if AL_OWNING_DETAILED_PROFILE
void al_owning_store_local(al_owning_stack_context *ctx,
                           uint32_t destination_offset, uint32_t source_offset,
                           uint32_t reserved_bytes,
                           uint32_t source_extent_bytes,
                           uint32_t source_payload_bytes,
                           uint32_t old_payload_bytes, uint32_t type_id) {
  AL_OWNING_PROFILE_START();
  uint32_t prior_status = ctx == 0 ? AL_OWNING_STATUS_INTERNAL : ctx->status;
  al_owning_store_local_impl(ctx, destination_offset, source_offset,
                             reserved_bytes, source_extent_bytes,
                             source_payload_bytes, old_payload_bytes, type_id);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_ARENA_COPY,
                        ctx == 0 ||
                            (prior_status == AL_OWNING_STATUS_OK &&
                             ctx->status != AL_OWNING_STATUS_OK));
}
#endif

void AL_OWNING_PROFILE_IMPL(al_owning_load_local)(
    al_owning_stack_context *ctx, uint32_t destination_offset,
    uint32_t source_offset, uint32_t extent_bytes, uint32_t payload_bytes,
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

#if AL_OWNING_DETAILED_PROFILE
void al_owning_load_local(al_owning_stack_context *ctx,
                          uint32_t destination_offset, uint32_t source_offset,
                          uint32_t extent_bytes, uint32_t payload_bytes,
                          uint32_t type_id) {
  AL_OWNING_PROFILE_START();
  uint32_t prior_status = ctx == 0 ? AL_OWNING_STATUS_INTERNAL : ctx->status;
  al_owning_load_local_impl(ctx, destination_offset, source_offset,
                            extent_bytes, payload_bytes, type_id);
  AL_OWNING_PROFILE_END(AL_OWNING_PROFILE_ARENA_COPY,
                        ctx == 0 ||
                            (prior_status == AL_OWNING_STATUS_OK &&
                             ctx->status != AL_OWNING_STATUS_OK));
}
#endif

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
  al_owning_poison(ctx, offset, reserved_bytes);
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

/* There is no instruction limit: verified programs have acyclic call graphs
 * and traverse only finite lists, so every execution terminates. Instructions
 * are still counted (saturating) for interpreter parity and lifecycle
 * statistics. */
uint8_t *al_owning_stack_base(al_owning_stack_context *ctx) {
  return ctx == 0 ? 0 : ctx->stack_data;
}
