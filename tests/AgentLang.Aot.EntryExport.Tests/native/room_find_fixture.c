#include "campfire_room_host_abi.h"
#include "owning_stack_runtime.h"

/* Synthetic adapter only: deliberately produces valid and malformed candidates
 * to test the compiler's native boundary. It does not implement SQL or policy. */
typedef struct fixture_state {
  uint32_t mode;
  uint32_t calls;
  int64_t last_user;
  int64_t last_room;
  uint32_t last_capacity;
  uint32_t reserved;
} fixture_state;

static void write64(uint8_t *output, uint32_t offset, uint64_t value) {
  uint32_t index;
  for (index = 0u; index < 8u; ++index)
    output[offset + index] = (uint8_t)(value >> (index * 8u));
}

static void clear_fixture_bytes(void *memory, uint32_t count) {
  volatile uint8_t *bytes = (volatile uint8_t *)memory;
  uint32_t index;
  for (index = 0u; index < count; ++index)
    bytes[index] = 0u;
}

static uint32_t write_string(uint8_t *output, uint32_t offset,
                              const uint16_t *text, uint32_t count) {
  uint32_t index;
  uint32_t extent = (8u + count * 2u + 7u) & ~7u;
  for (index = 0u; index < extent; ++index)
    output[offset + index] = 0u;
  write64(output, offset, count);
  for (index = 0u; index < count; ++index) {
    output[offset + 8u + index * 2u] = (uint8_t)text[index];
    output[offset + 9u + index * 2u] = (uint8_t)(text[index] >> 8u);
  }
  return offset + extent;
}

int32_t al_host_campfire_rooms_find_for_user_v1(
    void *provider_context, int64_t user_id, int64_t room_id, uint8_t *output,
    uint32_t output_capacity_bytes, uint32_t *output_extent_bytes) {
  static const uint16_t name[] = { 'L', 'o', 'b', 'b', 'y' };
  static const uint16_t timestamp[] = { '2', '0', '2', '6' };
  static const uint16_t unicode[] = { 0x03BBu, 0xD83Du, 0xDE80u };
  fixture_state *state = (fixture_state *)provider_context;
  uint32_t cursor;
  uint32_t index;
  if (state == 0 || output == 0 || output_extent_bytes == 0)
    return 1;
  ++state->calls;
  state->last_user = user_id;
  state->last_room = room_id;
  state->last_capacity = output_capacity_bytes;
  *output_extent_bytes = 0u;
  if (output_capacity_bytes < 16u)
    return 2;
  for (index = 0u; index < output_capacity_bytes; ++index)
    output[index] = 0u;
  if (state->mode == 6u) {
    write64(output, 0u, 0u);
    return 7;
  }
  if (state->mode == 7u) {
    write64(output, 0u, 9u);
    *output_extent_bytes = 16u;
    return 0;
  }
  if (state->mode == 8u) {
    *output_extent_bytes = output_capacity_bytes + 8u;
    return 0;
  }
  if (state->mode == 12u)
    return 0;
  if (state->mode == 13u) {
    write64(output, 8u, 9u);
    *output_extent_bytes = 16u;
    return 0;
  }
  if (state->mode == 1u || state->mode == 11u) {
    write64(output, 8u, 1u);
    *output_extent_bytes = state->mode == 11u ? 24u : 16u;
    return 0;
  }
  if (state->mode >= 2u && state->mode <= 5u) {
    write64(output, 0u, 1u);
    write64(output, 8u, state->mode - 2u);
    *output_extent_bytes = 16u;
    return 0;
  }
  /* Enough room for the largest fixture before any candidate record writes. */
  if (output_capacity_bytes < 104u)
    return 2;
  write64(output, 16u, (uint64_t)room_id);
  write64(output, 24u, state->mode == 15u ? 1u : 0u);
  cursor = 32u;
  if (state->mode != 15u)
    cursor = write_string(output, cursor,
                         state->mode == 16u ? unicode : name,
                         state->mode == 16u ? 3u : 5u);
  write64(output, cursor, state->mode == 10u ? 99u : 0u);
  cursor += 8u;
  write64(output, cursor, (uint64_t)user_id);
  cursor += 8u;
  cursor = write_string(output, cursor, timestamp, 4u);
  cursor = write_string(output, cursor, timestamp, 4u);
  if (state->mode == 9u)
    write64(output, 32u, 0x7FFFFFFFu);
  *output_extent_bytes = cursor;
  return 0;
}

/* Focused direct tests for suffix commit/rollback, independent of LLVM glue. */
#define REQUIRE(condition) do { if (!(condition)) return __LINE__; } while (0)
__declspec(dllexport) int32_t al_test_host_commit_boundaries(void) {
  static const al_owning_type_descriptor types[2] = {
    { AL_OWNING_TYPE_I64, 1u, 0u, 0u, 8u, 8u, 8u, 8u, 0u },
    { AL_OWNING_TYPE_BOOL, 2u, 0u, 0u, 8u, 8u, 8u, 8u, 0u }
  };
  static const al_owning_layout layout = { AL_OWNING_LAYOUT_ABI_VERSION, types, 2u, 0, 0u };
  uint8_t storage[80];
  uint8_t init[8];
  uint8_t poison[8];
  al_owning_stack_context ctx;
  uint32_t payload;
  uint32_t mode;
  clear_fixture_bytes(storage, sizeof(storage));
  clear_fixture_bytes(init, sizeof(init));
  clear_fixture_bytes(poison, sizeof(poison));
  clear_fixture_bytes(&ctx, sizeof(ctx));
  ctx.abi_version = AL_OWNING_STACK_ABI_VERSION;
  ctx.stack_capacity_bytes = 64u;
  ctx.stack_data = storage + 8u;
  ctx.init_bitmap = init;
  ctx.poison_bitmap = poison;
  ctx.init_bitmap_bytes = 8u;
  for (mode = 0u; mode < 7u; ++mode) {
    al_owning_begin(&ctx);
    REQUIRE(ctx.status == 0u);
    REQUIRE(al_owning_reserve_to(&ctx, 8u, 201u) == 0);
    al_owning_store_i64(&ctx, 0u, 42, 1u);
    REQUIRE(al_owning_reserve_to(&ctx, 32u, 201u) == 0);
    write64(ctx.stack_data, 8u, mode == 4u ? 2u : 21u);
    payload = 12345u;
    if (mode == 0u) {
      REQUIRE(al_owning_commit_host_output(&ctx, &layout, 0u, 8u, 32u,
                                           8u, 0, 202u, &payload) == 0);
      REQUIRE(ctx.cursor_bytes == 16u && payload == 8u);
      REQUIRE(al_owning_load_i64(&ctx, 8u) == 21);
      REQUIRE(ctx.deep_copy_bytes == 0u && ctx.input_copy_bytes == 0u);
      REQUIRE(ctx.live_payload_bytes == 8u && ctx.peak_live_payload_bytes == 8u);
    } else {
      uint32_t extent = mode == 1u ? 32u : mode == 2u ? 16u : 8u;
      int32_t status = mode == 3u ? 7 : 0;
      uint32_t type = mode == 4u ? 1u : mode == 5u ? 99u : 0u;
      if (mode == 6u)
        al_owning_set_failure(&ctx, AL_OWNING_STATUS_DIAGNOSTIC, 203u, 0u, 0u);
      REQUIRE(al_owning_commit_host_output(&ctx, &layout, type, 8u, 32u,
                                           extent, status, 202u, &payload) != 0);
      REQUIRE(ctx.cursor_bytes == 8u && payload == 12345u);
      REQUIRE(ctx.error_id == (mode == 6u ? 203u : 202u));
      ctx.status = 0u; /* Inspect the preserved older value after rollback. */
    }
    REQUIRE(al_owning_load_i64(&ctx, 0u) == 42);
    REQUIRE(storage[0] == 0u && storage[7] == 0u && storage[72] == 0u);
  }
  return 0;
}
