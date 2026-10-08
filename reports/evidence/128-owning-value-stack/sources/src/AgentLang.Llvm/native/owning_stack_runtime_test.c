#include "owning_stack_runtime.h"

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define AL_TEST_STACK_CAPACITY 64u
#define AL_TEST_GUARD_BYTES 8u
#define AL_TEST_CANARY 0xC7u
#define AL_TEST_BITMAP_CANARY 0xD3u
#define AL_TEST_EVENT_CAPACITY 32u

enum {
  AL_TEST_TYPE_INT = 1u,
  AL_TEST_TYPE_BOOL = 2u,
  AL_TEST_TYPE_UNIT = 3u,
  AL_TEST_TYPE_LEAF = 4u,
  AL_TEST_TYPE_ENVELOPE = 5u,
  AL_TEST_TYPE_STATE = 6u,
  AL_TEST_TYPE_EMPTY_RECORD = 7u,
  AL_TEST_NATIVE_FRAME_DEPTH_ERROR_ID = 181u
};

_Static_assert(sizeof(al_owning_stack_event) == 40u,
               "ABI 1 event size changed");
_Static_assert(offsetof(al_owning_stack_event, checksum) == 32u,
               "ABI 1 event checksum offset changed");
#if UINTPTR_MAX == UINT64_MAX
_Static_assert(sizeof(al_owning_stack_context) == 168u,
               "ABI 1 64-bit context size changed");
_Static_assert(offsetof(al_owning_stack_context, stack_data) == 104u,
               "ABI 1 stack pointer offset changed");
_Static_assert(offsetof(al_owning_stack_context, init_bitmap) == 112u,
               "ABI 1 init bitmap pointer offset changed");
_Static_assert(offsetof(al_owning_stack_context, poison_bitmap) == 120u,
               "ABI 1 poison bitmap pointer offset changed");
_Static_assert(offsetof(al_owning_stack_context, trace_events) == 128u,
               "ABI 1 trace pointer offset changed");
_Static_assert(offsetof(al_owning_stack_context, deep_copy_bytes) == 136u,
               "ABI 1 deep copy counter offset changed");
_Static_assert(offsetof(al_owning_stack_context, move_bytes) == 144u,
               "ABI 1 move counter offset changed");
_Static_assert(offsetof(al_owning_stack_context, input_copy_bytes) == 152u,
               "ABI 1 input copy counter offset changed");
_Static_assert(offsetof(al_owning_stack_context, retained_copy_bytes) == 160u,
               "ABI 1 retained copy counter offset changed");
#endif

typedef struct al_test_fixture {
  uint8_t raw[AL_TEST_STACK_CAPACITY + 2u * AL_TEST_GUARD_BYTES];
  uint8_t init_bitmap[16u];
  uint8_t poison_bitmap[16u];
  al_owning_stack_event events[AL_TEST_EVENT_CAPACITY];
  al_owning_stack_context ctx;
} al_test_fixture;

static uint32_t al_test_checks;
static uint32_t al_test_cases;
static uint32_t al_test_peak_cursor;
static uint32_t al_test_peak_payload;
static uint32_t al_test_peak_local_reserved;
static uint32_t al_test_peak_local_payload;
static uint32_t al_test_stack_capacity;
static uint32_t al_test_bitmap_bytes;
static uint32_t al_test_event_count;
static uint32_t al_test_trace_capacity;

#define AL_CHECK(expression)                                                   \
  do {                                                                         \
    ++al_test_checks;                                                          \
    if (!(expression)) {                                                       \
      (void)fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__,            \
                    #expression);                                              \
      goto failed;                                                             \
    }                                                                          \
  } while (0)

static uint32_t al_test_bitmap_size(uint32_t capacity) {
  return capacity / 8u + ((capacity & 7u) != 0u ? 1u : 0u);
}

static void al_test_fixture_init(al_test_fixture *fixture, uint32_t capacity) {
  (void)memset(fixture, 0, sizeof(*fixture));
  (void)memset(fixture->raw, AL_TEST_CANARY, sizeof(fixture->raw));
  (void)memset(fixture->init_bitmap, AL_TEST_BITMAP_CANARY,
               sizeof(fixture->init_bitmap));
  (void)memset(fixture->poison_bitmap, AL_TEST_BITMAP_CANARY,
               sizeof(fixture->poison_bitmap));
  fixture->ctx.abi_version = AL_OWNING_STACK_ABI_VERSION;
  fixture->ctx.stack_capacity_bytes = capacity;
  fixture->ctx.stack_data = fixture->raw + AL_TEST_GUARD_BYTES;
  fixture->ctx.init_bitmap = fixture->init_bitmap;
  fixture->ctx.poison_bitmap = fixture->poison_bitmap;
  fixture->ctx.init_bitmap_bytes = al_test_bitmap_size(capacity);
  fixture->ctx.trace_events = fixture->events;
  fixture->ctx.trace_event_capacity = AL_TEST_EVENT_CAPACITY;
  al_owning_begin(&fixture->ctx);
}

static int32_t al_test_fixture_guards_ok(const al_test_fixture *fixture) {
  uint32_t index;
  const uint32_t bitmap_bytes = fixture->ctx.init_bitmap_bytes;
  for (index = 0u; index < AL_TEST_GUARD_BYTES; ++index) {
    if (fixture->raw[index] != AL_TEST_CANARY)
      return 0;
    if (fixture->raw[AL_TEST_GUARD_BYTES + fixture->ctx.stack_capacity_bytes +
                     index] != AL_TEST_CANARY)
      return 0;
  }
  for (index = bitmap_bytes; index < (uint32_t)sizeof(fixture->init_bitmap);
       ++index) {
    if (fixture->init_bitmap[index] != AL_TEST_BITMAP_CANARY)
      return 0;
    if (fixture->poison_bitmap[index] != AL_TEST_BITMAP_CANARY)
      return 0;
  }
  return 1;
}

static void al_test_capture_metrics(const al_test_fixture *fixture) {
  if (fixture->ctx.peak_cursor_bytes > al_test_peak_cursor)
    al_test_peak_cursor = fixture->ctx.peak_cursor_bytes;
  if (fixture->ctx.peak_live_payload_bytes > al_test_peak_payload)
    al_test_peak_payload = fixture->ctx.peak_live_payload_bytes;
  if (fixture->ctx.peak_local_reserved_bytes > al_test_peak_local_reserved)
    al_test_peak_local_reserved = fixture->ctx.peak_local_reserved_bytes;
  if (fixture->ctx.peak_live_local_payload_bytes > al_test_peak_local_payload)
    al_test_peak_local_payload = fixture->ctx.peak_live_local_payload_bytes;
  if (fixture->ctx.stack_capacity_bytes > al_test_stack_capacity)
    al_test_stack_capacity = fixture->ctx.stack_capacity_bytes;
  if (fixture->ctx.init_bitmap_bytes > al_test_bitmap_bytes)
    al_test_bitmap_bytes = fixture->ctx.init_bitmap_bytes;
  if (fixture->ctx.trace_event_count > al_test_event_count)
    al_test_event_count = fixture->ctx.trace_event_count;
  if (fixture->ctx.trace_event_capacity > al_test_trace_capacity)
    al_test_trace_capacity = fixture->ctx.trace_event_capacity;
}

static int32_t al_test_case_begin(const char *name,
                                  const al_test_fixture *fixture) {
  (void)name;
  if (fixture->ctx.status != AL_OWNING_STATUS_OK ||
      !al_test_fixture_guards_ok(fixture))
    return 0;
  ++al_test_cases;
  al_test_capture_metrics(fixture);
  return 1;
}

static int al_test_duplicate_drop_reuse(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  uint32_t poison_checks_before_reuse;
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK);
  AL_CHECK(al_owning_reserve_to(ctx, 32u, 100u) == 0);
  al_owning_store_i64(ctx, 0u, INT64_C(0x1111222233334444),
                      1u); /* Envelope.leaf.value */
  al_owning_store_i64(ctx, 8u, INT64_C(0x5555666677778888),
                      1u); /* Envelope.tag */
  al_owning_update_live(ctx, 16, 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK);
  al_owning_duplicate(ctx, 16u, 0u, 16u, 16u, AL_TEST_TYPE_ENVELOPE);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK);
  AL_CHECK(ctx->duplicate_disjoint_checks == 1u);
  AL_CHECK(ctx->deep_copy_bytes == 16u);
  AL_CHECK(ctx->trace_event_count == 3u &&
           ctx->trace_events[2].kind == AL_OWNING_EVENT_DUPLICATE);
  AL_CHECK(ctx->trace_events[2].offset + ctx->trace_events[2].extent_bytes <=
               ctx->trace_events[2].source_offset ||
           ctx->trace_events[2].source_offset +
                   ctx->trace_events[2].source_extent_bytes <=
               ctx->trace_events[2].offset);
  AL_CHECK(memcmp(ctx->stack_data, ctx->stack_data + 16u, 16u) == 0);
  al_owning_store_i64(ctx, 24u, INT64_C(0x0123456789ABCDEF),
                      1u); /* Mutate only the test copy. */
  AL_CHECK(al_owning_load_i64(ctx, 8u) == INT64_C(0x5555666677778888));
  AL_CHECK(al_owning_load_i64(ctx, 24u) == INT64_C(0x0123456789ABCDEF));
  al_owning_drop(ctx, 16u, 16u, 16u, AL_TEST_TYPE_ENVELOPE);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK);
  AL_CHECK(ctx->cursor_bytes == 16u);
  AL_CHECK(ctx->drop_survivor_checks == 1u);
  AL_CHECK(al_owning_load_i64(ctx, 0u) == INT64_C(0x1111222233334444));
  AL_CHECK(al_owning_load_i64(ctx, 8u) == INT64_C(0x5555666677778888));
  AL_CHECK(ctx->stack_data[16u] == 0xA5u && ctx->stack_data[31u] == 0xA5u);
  AL_CHECK((fixture.init_bitmap[2u] & 0xFFu) == 0u &&
           (fixture.init_bitmap[3u] & 0xFFu) == 0u);
  AL_CHECK((fixture.poison_bitmap[2u] & 0xFFu) == 0xFFu &&
           (fixture.poison_bitmap[3u] & 0xFFu) == 0xFFu);
  poison_checks_before_reuse = ctx->poison_reuse_checks;
  AL_CHECK(al_owning_reserve_to(ctx, 32u, 101u) == 0);
  al_owning_store_i64(ctx, 16u, INT64_C(0x0102030405060708), 1u);
  al_owning_store_i64(ctx, 24u, INT64_C(0x1112131415161718), 1u);
  AL_CHECK(al_owning_check_initialized(ctx, 16u, 16u) == 0);
  AL_CHECK(ctx->poison_reuse_checks == poison_checks_before_reuse + 1u);
  AL_CHECK(al_owning_load_i64(ctx, 16u) == INT64_C(0x0102030405060708));
  AL_CHECK(al_owning_load_i64(ctx, 24u) == INT64_C(0x1112131415161718));
  al_owning_update_live(ctx, 16, 0);
  al_owning_release_to(ctx, 0u, 0u, 0u, 0u);
  al_owning_update_live(ctx, -32, 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK && ctx->live_payload_bytes == 0u);
  AL_CHECK(al_test_case_begin("duplicate_drop_reuse", &fixture));
  return 1;
failed:
  return 0;
}

static int al_test_primitive_slots(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, 24u, 105u) == 0);
  al_owning_store_i64(ctx, 0u, -9, AL_TEST_TYPE_INT);
  al_owning_store_i64(ctx, 8u, 1, AL_TEST_TYPE_BOOL);
  al_owning_store_token(ctx, 16u, AL_TEST_TYPE_UNIT);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK);
  AL_CHECK(al_owning_load_i64(ctx, 0u) == -9 &&
           al_owning_load_i64(ctx, 8u) == 1);
  AL_CHECK(memcmp(ctx->stack_data + 16u, "\0\0\0\0\0\0\0\0", 8u) == 0);
  AL_CHECK(fixture.events[0].extent_bytes == 8u &&
           fixture.events[0].payload_bytes == 8u);
  AL_CHECK(fixture.events[1].extent_bytes == 8u &&
           fixture.events[1].payload_bytes == 8u);
  AL_CHECK(fixture.events[2].extent_bytes == 8u &&
           fixture.events[2].payload_bytes == 0u);
  al_owning_update_live(ctx, 16, 0);
  al_owning_release_to(ctx, 0u, 0u, 0u, 0u);
  al_owning_update_live(ctx, -16, 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK && ctx->live_payload_bytes == 0u);
  AL_CHECK(al_test_case_begin("primitive_slots", &fixture));
  return 1;
failed:
  return 0;
}

static int al_test_inline_nested_and_empty(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK);
  /* Leaf=8, Envelope={Leaf,Int}=16, State={Int,Envelope}=24; adjacent States
   * follow. */
  AL_CHECK(al_owning_reserve_to(ctx, 56u, 110u) == 0);
  al_owning_store_i64(ctx, 0u, 10, 1u);
  al_owning_store_i64(ctx, 8u, 11, 1u);
  al_owning_store_i64(ctx, 16u, 12, 1u);
  al_owning_store_i64(ctx, 24u, 20, 1u);
  al_owning_store_i64(ctx, 32u, 21, 1u);
  al_owning_store_i64(ctx, 40u, 22, 1u);
  al_owning_store_token(
      ctx, 48u, AL_TEST_TYPE_EMPTY_RECORD); /* Empty record: zero payload, one
                                               8-byte token extent. */
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK);
  AL_CHECK(al_owning_load_i64(ctx, 0u) == 10 &&
           al_owning_load_i64(ctx, 8u) == 11 &&
           al_owning_load_i64(ctx, 16u) == 12);
  AL_CHECK(al_owning_load_i64(ctx, 24u) == 20 &&
           al_owning_load_i64(ctx, 32u) == 21 &&
           al_owning_load_i64(ctx, 40u) == 22);
  AL_CHECK(memcmp(ctx->stack_data + 48u, "\0\0\0\0\0\0\0\0", 8u) == 0);
  AL_CHECK(fixture.events[fixture.ctx.trace_event_count - 1u].extent_bytes ==
           8u);
  AL_CHECK(fixture.events[fixture.ctx.trace_event_count - 1u].payload_bytes ==
           0u);
  al_owning_update_live(ctx, 48, 0);
  al_owning_release_to(ctx, 0u, 0u, 0u, 0u);
  al_owning_update_live(ctx, -48, 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK && ctx->live_payload_bytes == 0u);
  AL_CHECK(al_test_case_begin("inline_nested_empty", &fixture));
  return 1;
failed:
  return 0;
}

static int al_test_field_extract_overlap_and_return_widths(void) {
  al_test_fixture larger;
  al_test_fixture smaller;
  al_owning_stack_context *ctx;
  uint8_t expected[16];
  al_test_fixture_init(&larger, AL_TEST_STACK_CAPACITY);
  ctx = &larger.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, 24u, 120u) == 0);
  al_owning_store_i64(ctx, 0u, 100, 1u);  /* State.count */
  al_owning_store_i64(ctx, 8u, 101, 1u);  /* State.last.leaf.value */
  al_owning_store_i64(ctx, 16u, 102, 1u); /* State.last.tag */
  (void)memcpy(expected, ctx->stack_data + 8u, sizeof(expected));
  /* Extract the larger nested field leftward over its containing record's
   * bytes. */
  al_owning_move_range(ctx, 0u, 8u, 16u, 16u, AL_TEST_TYPE_ENVELOPE,
                       AL_OWNING_EVENT_FIELD_EXTRACT);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK);
  AL_CHECK(memcmp(ctx->stack_data, expected, sizeof(expected)) == 0);
  AL_CHECK(ctx->stack_data[16u] == 0xA5u && ctx->stack_data[23u] == 0xA5u);
  AL_CHECK((larger.init_bitmap[2u] & 0xFFu) == 0u &&
           (larger.poison_bitmap[2u] & 0xFFu) == 0xFFu);
  al_owning_release_to(ctx, 16u, 0u, 0u, 0u);
  AL_CHECK(ctx->cursor_bytes == 16u &&
           al_owning_check_initialized(ctx, 0u, 16u) == 0);
  AL_CHECK(al_test_case_begin("field_extract_overlap", &larger));

  /* Simulate a zero-input 16-byte return replacing an 8-byte caller
   * reservation. */
  al_test_fixture_init(&larger, AL_TEST_STACK_CAPACITY);
  ctx = &larger.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, 24u, 121u) == 0);
  al_owning_store_i64(ctx, 0u, 7, 1u);
  al_owning_store_i64(ctx, 8u, 201, 1u);
  al_owning_store_i64(ctx, 16u, 202, 1u);
  (void)memcpy(expected, ctx->stack_data + 8u, sizeof(expected));
  al_owning_move_range(ctx, 0u, 8u, 16u, 16u, AL_TEST_TYPE_ENVELOPE,
                       AL_OWNING_EVENT_CALL_RETURN_MOVE);
  al_owning_release_to(ctx, 16u, 0u, 0u, 0u);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK && ctx->cursor_bytes == 16u);
  AL_CHECK(memcmp(ctx->stack_data, expected, sizeof(expected)) == 0);
  AL_CHECK(ctx->stack_data[16u] == 0xA5u && ctx->stack_data[23u] == 0xA5u);
  AL_CHECK(al_test_case_begin("return_larger_than_input", &larger));

  /* Simulate a 16-byte input consumed by an 8-byte result. */
  al_test_fixture_init(&smaller, AL_TEST_STACK_CAPACITY);
  ctx = &smaller.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, 24u, 122u) == 0);
  al_owning_store_i64(ctx, 0u, 301, 1u);
  al_owning_store_i64(ctx, 8u, 302, 1u);
  al_owning_store_i64(ctx, 16u, 303, 1u);
  al_owning_move_range(ctx, 0u, 16u, 8u, 8u, 1u,
                       AL_OWNING_EVENT_CALL_RETURN_MOVE);
  al_owning_release_to(ctx, 8u, 0u, 0u, 0u);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK && ctx->cursor_bytes == 8u);
  AL_CHECK(al_owning_load_i64(ctx, 0u) == 303);
  AL_CHECK(ctx->stack_data[8u] == 0xA5u && ctx->stack_data[23u] == 0xA5u);
  AL_CHECK(al_test_case_begin("return_smaller_than_input", &smaller));
  return 1;
failed:
  return 0;
}

static int al_test_local_copy_ownership(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  al_owning_local_reserve(ctx, 16u);
  AL_CHECK(al_owning_reserve_to(ctx, 32u, 130u) ==
           0); /* 16 local bytes, then 16 operand bytes. */
  al_owning_store_i64(ctx, 16u, 401, 1u);
  al_owning_store_i64(ctx, 24u, 402, 1u);
  al_owning_update_live(ctx, 16, 0);
  al_owning_store_local(ctx, 0u, 16u, 16u, 16u, 16u, 0u, AL_TEST_TYPE_ENVELOPE);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK &&
           ctx->live_local_payload_bytes == 16u);
  /* Native lowering consumes the source operand after StoreLocal has installed
   * its copy. */
  al_owning_release_to(ctx, 16u, 0u, 0u, 0u);
  al_owning_update_live(ctx, -16, 0);
  AL_CHECK(ctx->stack_data[16u] == 0xA5u && ctx->stack_data[31u] == 0xA5u);
  AL_CHECK(ctx->stack_data[0u] == (uint8_t)(401 & 0xFF));
  AL_CHECK(al_owning_reserve_to(ctx, 32u, 131u) == 0);
  al_owning_load_local(ctx, 16u, 0u, 16u, 16u, AL_TEST_TYPE_ENVELOPE);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK);
  al_owning_store_i64(ctx, 24u, 499,
                      1u); /* Mutate only the loaded test copy. */
  AL_CHECK(al_owning_load_i64(ctx, 24u) == 499);
  AL_CHECK(al_owning_load_i64(ctx, 8u) == 402);
  al_owning_release_to(ctx, 16u, 0u, 0u, 0u);
  al_owning_update_live(ctx, -16, 0);
  al_owning_clear_local(ctx, 0u, 16u, 16u, AL_TEST_TYPE_ENVELOPE);
  al_owning_local_release(ctx, 16u);
  al_owning_release_to(ctx, 0u, 0u, 0u, 0u);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK);
  AL_CHECK(ctx->live_payload_bytes == 0u &&
           ctx->live_local_payload_bytes == 0u);
  AL_CHECK(ctx->active_local_reserved_bytes == 0u);
  AL_CHECK(ctx->stack_data[0u] == 0xA5u && ctx->stack_data[15u] == 0xA5u);
  AL_CHECK(al_test_case_begin("local_copy_ownership", &fixture));
  return 1;
failed:
  return 0;
}

static int al_test_sticky_error_cleanup(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  al_owning_local_reserve(ctx, 8u);
  AL_CHECK(al_owning_reserve_to(ctx, 16u, 170u) == 0);
  al_owning_store_i64(ctx, 8u, 801, 1u);
  al_owning_update_live(ctx, 8, 0);
  al_owning_store_local(ctx, 0u, 8u, 8u, 8u, 8u, 0u, 1u);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK);
  AL_CHECK(ctx->live_payload_bytes == 8u &&
           ctx->live_local_payload_bytes == 8u);

  AL_CHECK(al_owning_reserve_to(ctx, AL_TEST_STACK_CAPACITY + 1u, 171u) != 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_STACK_CAPACITY);
  AL_CHECK(ctx->error_id == 171u &&
           ctx->required_bytes == AL_TEST_STACK_CAPACITY + 1u);
  AL_CHECK(ctx->available_bytes == AL_TEST_STACK_CAPACITY);

  /* Failure unwind must reclaim storage and counters without replacing the
   * original diagnostic. */
  al_owning_release_to(ctx, 8u, 0u, 0u, 0u);
  al_owning_update_live(ctx, -8, 0);
  al_owning_clear_local(ctx, 0u, 8u, 8u, 1u);
  al_owning_local_release(ctx, 8u);
  al_owning_release_to(ctx, 0u, 0u, 0u, 0u);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_STACK_CAPACITY);
  AL_CHECK(ctx->error_id == 171u &&
           ctx->required_bytes == AL_TEST_STACK_CAPACITY + 1u);
  AL_CHECK(ctx->cursor_bytes == 0u && ctx->active_local_reserved_bytes == 0u);
  AL_CHECK(ctx->live_payload_bytes == 0u &&
           ctx->live_local_payload_bytes == 0u);
  AL_CHECK(ctx->stack_data[0u] == 0xA5u && ctx->stack_data[15u] == 0xA5u);
  AL_CHECK((fixture.init_bitmap[0u] & 0xFFu) == 0u &&
           (fixture.init_bitmap[1u] & 0xFFu) == 0u);
  AL_CHECK((fixture.poison_bitmap[0u] & 0xFFu) == 0xFFu &&
           (fixture.poison_bitmap[1u] & 0xFFu) == 0xFFu);
  AL_CHECK(al_test_fixture_guards_ok(&fixture));
  ++al_test_cases;
  al_test_capture_metrics(&fixture);
  return 1;
failed:
  return 0;
}

static int al_test_raw_native_frame_depth_and_unwind(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  uint32_t entered = 0u;
  uint32_t index;
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  /*
   * This pins raw runtime helper entries, not language-call ordinals. The
   * compiled entry wrapper consumes a native frame; the separate language
   * fixture owns the n=64/n=65 parity assertion.
   */
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK && ctx->call_depth == 0u);
  for (index = 0u; index < 66u; ++index) {
    if (al_owning_enter_frame(ctx, AL_TEST_NATIVE_FRAME_DEPTH_ERROR_ID) == 0)
      ++entered;
  }
  AL_CHECK(entered == 66u && ctx->call_depth == 66u &&
           ctx->status == AL_OWNING_STATUS_OK);
  AL_CHECK(al_owning_enter_frame(ctx, AL_TEST_NATIVE_FRAME_DEPTH_ERROR_ID) !=
               0 &&
           ctx->status == AL_OWNING_STATUS_DIAGNOSTIC &&
           ctx->error_id == AL_TEST_NATIVE_FRAME_DEPTH_ERROR_ID &&
           ctx->call_depth == 66u && ctx->frame_return_count == 0u);

  al_owning_leave_frame(ctx);
  AL_CHECK(ctx->call_depth == 65u && ctx->frame_return_count == 1u &&
           ctx->status == AL_OWNING_STATUS_DIAGNOSTIC &&
           ctx->error_id == AL_TEST_NATIVE_FRAME_DEPTH_ERROR_ID);
  for (index = 1u; index < 66u; ++index)
    al_owning_leave_frame(ctx);
  AL_CHECK(ctx->call_depth == 0u && ctx->frame_return_count == 66u);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_DIAGNOSTIC &&
           ctx->error_id == AL_TEST_NATIVE_FRAME_DEPTH_ERROR_ID);
  AL_CHECK(ctx->trace_event_count == AL_TEST_EVENT_CAPACITY &&
           ctx->trace_truncated == 1u);
  AL_CHECK(al_test_fixture_guards_ok(&fixture));
  ++al_test_cases;
  al_test_capture_metrics(&fixture);
  return 1;
failed:
  return 0;
}

static int al_test_capacity_atomicity(void) {
  al_test_fixture exact;
  al_test_fixture short_fixture;
  al_owning_stack_context *ctx;
  uint8_t before[24];
  al_test_fixture_init(&exact, 24u);
  ctx = &exact.ctx;
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK);
  AL_CHECK(al_owning_reserve_to(ctx, 24u, 140u) == 0);
  al_owning_store_i64(ctx, 0u, 501, 1u);
  al_owning_store_i64(ctx, 8u, 502, 1u);
  al_owning_store_i64(ctx, 16u, 503, 1u);
  AL_CHECK(al_owning_load_i64(ctx, 0u) == 501 &&
           al_owning_load_i64(ctx, 16u) == 503);
  AL_CHECK(al_test_case_begin("exact_capacity", &exact));

  al_test_fixture_init(&short_fixture, 23u);
  ctx = &short_fixture.ctx;
  (void)memcpy(before, ctx->stack_data, sizeof(before));
  AL_CHECK(al_owning_reserve_to(ctx, 24u, 141u) != 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_STACK_CAPACITY);
  AL_CHECK(ctx->required_bytes == 24u && ctx->available_bytes == 23u);
  AL_CHECK(ctx->cursor_bytes == 0u &&
           memcmp(before, ctx->stack_data, sizeof(before)) == 0);
  AL_CHECK(al_test_fixture_guards_ok(&short_fixture));
  ++al_test_cases;
  al_test_capture_metrics(&short_fixture);
  return 1;
failed:
  return 0;
}

static int al_test_retained_publish_atomicity(void) {
  al_test_fixture exact;
  al_test_fixture short_fixture;
  uint8_t retained_exact[32];
  uint8_t retained_short[32];
  uint8_t expected[16];
  uint32_t index;
  al_test_fixture_init(&exact, AL_TEST_STACK_CAPACITY);
  AL_CHECK(al_owning_reserve_to(&exact.ctx, 16u, 145u) == 0);
  al_owning_store_i64(&exact.ctx, 0u, 551, AL_TEST_TYPE_INT);
  al_owning_store_i64(&exact.ctx, 8u, 552, AL_TEST_TYPE_INT);
  (void)memcpy(expected, exact.ctx.stack_data, sizeof(expected));
  (void)memset(retained_exact, 0xB6, sizeof(retained_exact));
  al_owning_publish(&exact.ctx, retained_exact + 8u, 16u, 0u, 16u,
                    AL_TEST_TYPE_ENVELOPE);
  AL_CHECK(exact.ctx.status == AL_OWNING_STATUS_OK &&
           exact.ctx.retained_copy_bytes == 16u);
  AL_CHECK(memcmp(retained_exact + 8u, expected, sizeof(expected)) == 0);
  for (index = 0u; index < 8u; ++index) {
    AL_CHECK(retained_exact[index] == 0xB6u);
    AL_CHECK(retained_exact[24u + index] == 0xB6u);
  }
  AL_CHECK(al_test_case_begin("retained_exact_capacity", &exact));

  al_test_fixture_init(&short_fixture, AL_TEST_STACK_CAPACITY);
  AL_CHECK(al_owning_reserve_to(&short_fixture.ctx, 16u, 146u) == 0);
  al_owning_store_i64(&short_fixture.ctx, 0u, 561, AL_TEST_TYPE_INT);
  al_owning_store_i64(&short_fixture.ctx, 8u, 562, AL_TEST_TYPE_INT);
  (void)memset(retained_short, 0xB6, sizeof(retained_short));
  al_owning_publish(&short_fixture.ctx, retained_short + 8u, 15u, 0u, 16u,
                    AL_TEST_TYPE_ENVELOPE);
  AL_CHECK(short_fixture.ctx.status == AL_OWNING_STATUS_RETAINED_CAPACITY);
  AL_CHECK(short_fixture.ctx.required_bytes == 16u &&
           short_fixture.ctx.available_bytes == 15u);
  for (index = 0u; index < (uint32_t)sizeof(retained_short); ++index)
    AL_CHECK(retained_short[index] == 0xB6u);
  AL_CHECK(al_test_fixture_guards_ok(&short_fixture));
  ++al_test_cases;
  al_test_capture_metrics(&short_fixture);
  return 1;
failed:
  return 0;
}

static int al_test_near_uint32_rejections(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  uint8_t before[32];
  uint8_t init_before[16];
  uint8_t poison_before[16];
  al_test_fixture_init(&fixture, 24u);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, 8u, 150u) == 0);
  al_owning_store_i64(ctx, 0u, 601, 1u);
  (void)memcpy(before, ctx->stack_data, sizeof(before));
  (void)memcpy(init_before, fixture.init_bitmap, sizeof(init_before));
  (void)memcpy(poison_before, fixture.poison_bitmap, sizeof(poison_before));
  al_owning_move_range(ctx, UINT32_MAX - 3u, 0u, 8u, 8u, 1u,
                       AL_OWNING_EVENT_FIELD_EXTRACT);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_INTERNAL);
  AL_CHECK(ctx->cursor_bytes == 8u &&
           memcmp(before, ctx->stack_data, sizeof(before)) == 0);
  AL_CHECK(memcmp(init_before, fixture.init_bitmap, sizeof(init_before)) == 0);
  AL_CHECK(
      memcmp(poison_before, fixture.poison_bitmap, sizeof(poison_before)) == 0);
  AL_CHECK(al_test_fixture_guards_ok(&fixture));

  al_test_fixture_init(&fixture, 24u);
  ctx = &fixture.ctx;
  (void)memcpy(before, ctx->stack_data, sizeof(before));
  AL_CHECK(al_owning_reserve_to(ctx, UINT32_MAX, 151u) != 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_STACK_CAPACITY);
  AL_CHECK(ctx->cursor_bytes == 0u &&
           memcmp(before, ctx->stack_data, sizeof(before)) == 0);
  AL_CHECK(al_test_fixture_guards_ok(&fixture));

  al_test_fixture_init(&fixture, 24u);
  ctx = &fixture.ctx;
  ctx->active_local_reserved_bytes = UINT32_MAX - 3u;
  al_owning_local_reserve(ctx, 8u);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_INTERNAL);
  AL_CHECK(ctx->active_local_reserved_bytes == UINT32_MAX - 3u);

  al_test_fixture_init(&fixture, 24u);
  ctx = &fixture.ctx;
  ctx->live_payload_bytes = UINT32_MAX - 3u;
  al_owning_update_live(ctx, 8, 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_INTERNAL);
  AL_CHECK(ctx->live_payload_bytes == UINT32_MAX - 3u);
  ++al_test_cases;
  al_test_capture_metrics(&fixture);

  /* An invalid capacity must be rejected before a small actual buffer/bitmap is
   * touched. */
  al_test_fixture_init(&fixture, 24u);
  ctx = &fixture.ctx;
  ctx->stack_capacity_bytes = UINT32_MAX;
  ctx->init_bitmap_bytes = 3u;
  (void)memcpy(before, ctx->stack_data, sizeof(before));
  (void)memcpy(init_before, fixture.init_bitmap, sizeof(init_before));
  (void)memcpy(poison_before, fixture.poison_bitmap, sizeof(poison_before));
  al_owning_begin(ctx);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_INVALID_REQUEST);
  AL_CHECK(ctx->required_bytes ==
           UINT32_MAX / 8u + 1u); /* Safe ceil division at the uint32 limit. */
  AL_CHECK(memcmp(before, ctx->stack_data, sizeof(before)) == 0);
  AL_CHECK(memcmp(init_before, fixture.init_bitmap, sizeof(init_before)) == 0);
  AL_CHECK(
      memcmp(poison_before, fixture.poison_bitmap, sizeof(poison_before)) == 0);
  ctx->stack_capacity_bytes =
      24u; /* Restore the actual fixture bound before guard inspection. */
  AL_CHECK(al_test_fixture_guards_ok(&fixture));
  ++al_test_cases;
  return 1;
failed:
  return 0;
}

static int al_test_store_local_overlap_rejected(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  uint8_t before[32];
  uint8_t init_before[16];
  uint8_t poison_before[16];
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, 24u, 160u) == 0);
  al_owning_store_i64(ctx, 0u, 701, 1u);
  al_owning_store_i64(ctx, 8u, 702, 1u);
  (void)memcpy(before, ctx->stack_data, sizeof(before));
  (void)memcpy(init_before, fixture.init_bitmap, sizeof(init_before));
  (void)memcpy(poison_before, fixture.poison_bitmap, sizeof(poison_before));
  al_owning_store_local(ctx, 8u, 0u, 16u, 16u, 16u, 0u, 2u);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_INTERNAL);
  AL_CHECK(memcmp(before, ctx->stack_data, sizeof(before)) == 0);
  AL_CHECK(memcmp(init_before, fixture.init_bitmap, sizeof(init_before)) == 0);
  AL_CHECK(
      memcmp(poison_before, fixture.poison_bitmap, sizeof(poison_before)) == 0);
  AL_CHECK(al_test_fixture_guards_ok(&fixture));
  ++al_test_cases;
  al_test_capture_metrics(&fixture);
  return 1;
failed:
  return 0;
}

int main(void) {
  if (!al_test_duplicate_drop_reuse() || !al_test_primitive_slots() ||
      !al_test_inline_nested_and_empty() ||
      !al_test_field_extract_overlap_and_return_widths() ||
      !al_test_local_copy_ownership() || !al_test_sticky_error_cleanup() ||
      !al_test_raw_native_frame_depth_and_unwind() ||
      !al_test_capacity_atomicity() || !al_test_retained_publish_atomicity() ||
      !al_test_near_uint32_rejections() ||
      !al_test_store_local_overlap_rejected()) {
    return 1;
  }
  (void)printf(
      "{\"suite\":\"owning_stack_runtime_storage_v1\",\"status\":\"pass\","
      "\"cases\":%u,\"checks\":%u,\"abi\":{\"event_size_bytes\":%zu,\"event_"
      "checksum_offset_bytes\":%zu,\"context_size_bytes\":%zu,\"context_"
      "offsets_bytes\":{\"stack_data\":%zu,\"init_bitmap\":%zu,\"poison_"
      "bitmap\":%zu,\"trace_events\":%zu,\"deep_copy_bytes\":%zu,\"move_"
      "bytes\":%zu,\"input_copy_bytes\":%zu,\"retained_copy_bytes\":%zu}},"
      "\"payload\":{\"peak_operand_bytes\":%u,\"peak_local_bytes\":%u},"
      "\"reserved\":{\"max_stack_capacity_bytes\":%u,\"peak_cursor_bytes\":%u,"
      "\"peak_local_reserved_bytes\":%u},\"metadata\":{\"bitmap_bytes_each\":%"
      "u,\"bitmap_total_bytes\":%u,\"trace_event_count\":%u,\"trace_event_"
      "capacity\":%u,\"trace_bytes\":%u}}\n",
      al_test_cases, al_test_checks, sizeof(al_owning_stack_event),
      offsetof(al_owning_stack_event, checksum),
      sizeof(al_owning_stack_context),
      offsetof(al_owning_stack_context, stack_data),
      offsetof(al_owning_stack_context, init_bitmap),
      offsetof(al_owning_stack_context, poison_bitmap),
      offsetof(al_owning_stack_context, trace_events),
      offsetof(al_owning_stack_context, deep_copy_bytes),
      offsetof(al_owning_stack_context, move_bytes),
      offsetof(al_owning_stack_context, input_copy_bytes),
      offsetof(al_owning_stack_context, retained_copy_bytes),
      al_test_peak_payload, al_test_peak_local_payload, al_test_stack_capacity,
      al_test_peak_cursor, al_test_peak_local_reserved, al_test_bitmap_bytes,
      al_test_bitmap_bytes * 2u, al_test_event_count, al_test_trace_capacity,
      al_test_trace_capacity * (uint32_t)sizeof(al_owning_stack_event));
  return 0;
}
