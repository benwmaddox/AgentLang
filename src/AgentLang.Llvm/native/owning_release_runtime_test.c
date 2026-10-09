#include "owning_stack_runtime.h"

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#if !AL_OWNING_TRUSTED_GENERATED
#error "This test must be compiled with AL_OWNING_TRUSTED_GENERATED=1"
#endif

enum {
  AL_TEST_STACK_CAPACITY = 64u,
  AL_TEST_EVENT_CAPACITY = 4u,
  AL_TEST_TYPE_VALUE = 1u,
  AL_TEST_TYPE_EMPTY = 2u,
  AL_TEST_TYPE_STRING = 3u,
  AL_TEST_TYPE_RECORD = 4u,
  AL_TEST_ERROR_ID = 139u
};

_Static_assert(sizeof(al_owning_stack_context) == 168u,
               "ABI 1 context size changed");
_Static_assert(offsetof(al_owning_stack_context, stack_data) == 104u,
               "ABI 1 stack pointer offset changed");
_Static_assert(offsetof(al_owning_stack_context, init_bitmap) == 112u,
               "ABI 1 init bitmap pointer offset changed");
_Static_assert(offsetof(al_owning_stack_context, poison_bitmap) == 120u,
               "ABI 1 poison bitmap pointer offset changed");
_Static_assert(offsetof(al_owning_stack_context, trace_events) == 128u,
               "ABI 1 trace pointer offset changed");

static const al_owning_type_descriptor al_test_types[] = {
    {AL_OWNING_TYPE_RECORD, AL_TEST_TYPE_EMPTY, 0u, 0u, 0u, 8u, 0u, 8u, 0u},
    {AL_OWNING_TYPE_I64, AL_TEST_TYPE_VALUE, 0u, 0u, 8u, 8u, 8u, 8u, 0u},
    {AL_OWNING_TYPE_STRING,
     AL_TEST_TYPE_STRING,
     0u,
     0u,
     AL_OWNING_LAYOUT_DYNAMIC_U32,
     AL_OWNING_LAYOUT_DYNAMIC_U32,
     8u,
     8u,
     0u},
    {AL_OWNING_TYPE_RECORD, AL_TEST_TYPE_RECORD, 0u, 2u, 8u, 8u, 8u, 8u, 0u}};

static const al_owning_field_descriptor al_test_fields[] = {
    {1u, 0u, 0u, 0u},
    {0u, 8u, AL_OWNING_FIELD_ZERO_WIDTH, 0u}};

static const al_owning_layout al_test_layout = {
    AL_OWNING_LAYOUT_ABI_VERSION, al_test_types, 4u, al_test_fields, 2u};

static void al_test_init_context(al_owning_stack_context *ctx,
                                 uint8_t *stack_data,
                                 al_owning_stack_event *trace_events) {
  (void)memset(ctx, 0, sizeof(*ctx));
  ctx->abi_version = AL_OWNING_STACK_ABI_VERSION;
  ctx->stack_capacity_bytes = AL_TEST_STACK_CAPACITY;
  ctx->stack_data = stack_data;
  ctx->trace_events = trace_events;
  ctx->trace_event_capacity = AL_TEST_EVENT_CAPACITY;
}

static int32_t al_test_bytes_equal(const uint8_t *left, const uint8_t *right,
                                   uint32_t byte_count) {
  uint32_t index;
  for (index = 0u; index < byte_count; ++index) {
    if (left[index] != right[index])
      return 0;
  }
  return 1;
}

static int32_t al_test_bytes_are(const uint8_t *bytes, uint32_t byte_count,
                                 uint8_t value) {
  uint32_t index;
  for (index = 0u; index < byte_count; ++index) {
    if (bytes[index] != value)
      return 0;
  }
  return 1;
}

#define AL_CHECK(expression)                                                   \
  do {                                                                         \
    if (!(expression)) {                                                       \
      (void)fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__,          \
                    #expression);                                              \
      return 1;                                                                \
    }                                                                          \
  } while (0)

int main(void) {
  const uint8_t sentinel = 0x6Bu;
  uint8_t stack[AL_TEST_STACK_CAPACITY];
  uint8_t bad_profile_stack[AL_TEST_STACK_CAPACITY];
  uint8_t bad_layout_stack[AL_TEST_STACK_CAPACITY];
  uint8_t overlap_stack[AL_TEST_STACK_CAPACITY];
  uint8_t valid_string[16] = {1u, 0u, 0u, 0u, 0u, 0u, 0u, 0u,
                              0x41u, 0u, 0u, 0u, 0u, 0u, 0u, 0u};
  uint8_t truncated_string[9] = {1u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0x41u};
  uint8_t external_empty[8] = {0u};
  uint8_t external_record[8] = {0x2Au, 0u, 0u, 0u, 0u, 0u, 0u, 0u};
  uint8_t published[16];
  uint8_t expected_i64[8] = {0x88u, 0x77u, 0x66u, 0x55u,
                             0x44u, 0x33u, 0x22u, 0x11u};
  uint8_t saved_local[16];
  uint8_t bitmap_storage[8];
  al_owning_stack_event events[AL_TEST_EVENT_CAPACITY];
  al_owning_stack_context ctx;
  al_owning_stack_context bad_profile_ctx;
  al_owning_stack_context bad_layout_ctx;
  al_owning_stack_context overlap_ctx;
  al_owning_stack_context range_ctx;
  al_owning_stack_context capacity_ctx;
  al_owning_value_size empty_size;
  al_owning_field_location zero_width_field;
  uint32_t payload_bytes;
  uint32_t extent_bytes;
  uint32_t code_units;

  (void)memset(stack, sentinel, sizeof(stack));
  (void)memset(bad_profile_stack, sentinel, sizeof(bad_profile_stack));
  (void)memset(bad_layout_stack, sentinel, sizeof(bad_layout_stack));
  (void)memset(overlap_stack, sentinel, sizeof(overlap_stack));
  (void)memset(published, 0xCD, sizeof(published));
  (void)memset(saved_local, 0xCD, sizeof(saved_local));
  (void)memset(bitmap_storage, 0xB7, sizeof(bitmap_storage));
  (void)memset(events, 0xD3, sizeof(events));

  al_test_init_context(&ctx, stack, events);
  AL_CHECK(al_owning_build_profile_valid(&ctx) == 1);
  al_owning_begin(&ctx);
  AL_CHECK(ctx.status == AL_OWNING_STATUS_OK);
  AL_CHECK(ctx.init_bitmap == 0 && ctx.poison_bitmap == 0 &&
           ctx.init_bitmap_bytes == 0u);
  AL_CHECK(al_test_bytes_are(stack, sizeof(stack), sentinel));
  AL_CHECK(al_test_bytes_are((const uint8_t *)events, sizeof(events), 0xD3u));

  AL_CHECK(al_owning_reserve_to(&ctx, AL_TEST_STACK_CAPACITY,
                                AL_TEST_ERROR_ID) == 0);
  AL_CHECK(al_test_bytes_are(stack, sizeof(stack), sentinel));
  al_owning_release_to(&ctx, 0u, AL_OWNING_EVENT_ARENA_REWIND, 0u, 0u);
  AL_CHECK(ctx.cursor_bytes == 0u);
  AL_CHECK(al_test_bytes_are(stack, sizeof(stack), sentinel));
  AL_CHECK(al_owning_reserve_to(&ctx, AL_TEST_STACK_CAPACITY,
                                AL_TEST_ERROR_ID) == 0);
  AL_CHECK(al_test_bytes_are(stack, sizeof(stack), sentinel));

  al_owning_store_i64(&ctx, 0u, INT64_C(0x1122334455667788),
                      AL_TEST_TYPE_VALUE);
  al_owning_duplicate(&ctx, 8u, 0u, 8u, 8u, AL_TEST_TYPE_VALUE);
  AL_CHECK(ctx.status == AL_OWNING_STATUS_OK);
  AL_CHECK(al_owning_equal(&ctx, 0u, 8u, 8u) == 1);
  AL_CHECK(al_test_bytes_equal(stack, expected_i64, 8u));
  AL_CHECK(al_test_bytes_equal(stack + 8u, expected_i64, 8u));
  al_owning_publish(&ctx, published, sizeof(published), 8u, 8u,
                    AL_TEST_TYPE_VALUE);
  AL_CHECK(ctx.status == AL_OWNING_STATUS_OK);
  AL_CHECK(al_test_bytes_equal(published, expected_i64, 8u));
  AL_CHECK(ctx.duplicate_disjoint_checks == 0u);
  AL_CHECK(ctx.drop_survivor_checks == 0u);

  AL_CHECK(al_owning_measure_external_value(
               &ctx, &al_test_layout, 0u, external_empty,
               sizeof(external_empty), 0u, AL_TEST_ERROR_ID,
               &empty_size.payload_bytes, &empty_size.extent_bytes) == 0);
  AL_CHECK(empty_size.payload_bytes == 0u && empty_size.extent_bytes == 8u);
  al_owning_store_token(&ctx, 16u, AL_TEST_TYPE_EMPTY);
  AL_CHECK(al_test_bytes_are(stack + 16u, 8u, 0u));

  AL_CHECK(al_owning_measure_external_value(
               &ctx, &al_test_layout, 2u, valid_string,
               sizeof(valid_string), 0u, AL_TEST_ERROR_ID, &payload_bytes,
               &extent_bytes) == 0);
  AL_CHECK(payload_bytes == 10u && extent_bytes == 16u);
  al_owning_copy_external(&ctx, 24u, valid_string, payload_bytes, extent_bytes,
                          AL_TEST_TYPE_STRING);
  AL_CHECK(ctx.status == AL_OWNING_STATUS_OK);
  AL_CHECK(al_test_bytes_equal(stack + 24u, valid_string, 10u));
  AL_CHECK(al_test_bytes_are(stack + 34u, 6u, 0u));
  AL_CHECK(al_owning_string_length(&ctx, 24u, 16u, AL_TEST_ERROR_ID,
                                   &code_units) == 0);
  AL_CHECK(code_units == 1u);
  (void)memset(published, 0xCD, sizeof(published));
  al_owning_publish(&ctx, published, sizeof(published), 24u, 16u,
                    AL_TEST_TYPE_STRING);
  AL_CHECK(ctx.status == AL_OWNING_STATUS_OK);
  AL_CHECK(al_test_bytes_equal(published, valid_string, sizeof(valid_string)));

  AL_CHECK(al_owning_measure_external_value(
               &ctx, &al_test_layout, 3u, external_record,
               sizeof(external_record), 0u, AL_TEST_ERROR_ID, &payload_bytes,
               &extent_bytes) == 0);
  AL_CHECK(payload_bytes == 8u && extent_bytes == 8u);
  AL_CHECK(al_owning_copy_external_bounded(
               &ctx, 40u, external_record, sizeof(external_record), 0u,
               payload_bytes, extent_bytes, AL_TEST_TYPE_RECORD,
               AL_TEST_ERROR_ID) == 0);
  AL_CHECK(al_owning_locate_field(&ctx, &al_test_layout, 3u, 40u, 8u, 1u,
                                  AL_TEST_ERROR_ID, &zero_width_field) == 0);
  AL_CHECK(zero_width_field.offset_bytes == 48u &&
           zero_width_field.payload_bytes == 0u &&
           zero_width_field.extent_bytes == 0u);

  al_owning_store_local(&ctx, 48u, 0u, 16u, 8u, 8u, 0u,
                        AL_TEST_TYPE_VALUE);
  AL_CHECK(ctx.status == AL_OWNING_STATUS_OK);
  AL_CHECK(al_test_bytes_equal(stack + 48u, expected_i64, 8u));
  AL_CHECK(al_test_bytes_are(stack + 56u, 8u, 0u));
  (void)memcpy(saved_local, stack + 48u, sizeof(saved_local));
  al_owning_clear_local(&ctx, 48u, 16u, 8u, AL_TEST_TYPE_VALUE);
  AL_CHECK(ctx.status == AL_OWNING_STATUS_OK);
  AL_CHECK(ctx.live_local_payload_bytes == 0u);
  AL_CHECK(al_test_bytes_equal(stack + 48u, saved_local, sizeof(saved_local)));

  al_owning_drop(&ctx, 48u, 16u, 0u, AL_TEST_TYPE_VALUE);
  AL_CHECK(ctx.cursor_bytes == 48u);
  AL_CHECK(al_test_bytes_equal(stack + 48u, saved_local, sizeof(saved_local)));
  al_owning_drop(&ctx, 40u, 8u, 8u, AL_TEST_TYPE_RECORD);
  al_owning_drop(&ctx, 24u, 16u, 10u, AL_TEST_TYPE_STRING);
  al_owning_drop(&ctx, 16u, 8u, 0u, AL_TEST_TYPE_EMPTY);
  al_owning_drop(&ctx, 8u, 8u, 8u, AL_TEST_TYPE_VALUE);
  al_owning_drop(&ctx, 0u, 8u, 0u, AL_TEST_TYPE_VALUE);
  AL_CHECK(ctx.status == AL_OWNING_STATUS_OK);
  AL_CHECK(ctx.cursor_bytes == 0u && ctx.live_payload_bytes == 0u);
  AL_CHECK(al_test_bytes_equal(stack, expected_i64, 8u));
  AL_CHECK(al_test_bytes_equal(stack + 8u, expected_i64, 8u));
  AL_CHECK(al_test_bytes_are(stack + 16u, 8u, 0u));
  AL_CHECK(al_test_bytes_equal(stack + 24u, valid_string, sizeof(valid_string)));
  AL_CHECK(al_test_bytes_equal(stack + 40u, external_record,
                               sizeof(external_record)));
  AL_CHECK(al_test_bytes_equal(stack + 48u, saved_local, sizeof(saved_local)));
  AL_CHECK(al_owning_reserve_to(&ctx, AL_TEST_STACK_CAPACITY,
                               AL_TEST_ERROR_ID) == 0);
  AL_CHECK(al_test_bytes_equal(stack, expected_i64, 8u));
  al_owning_release_to(&ctx, 0u, AL_OWNING_EVENT_ARENA_REWIND, 0u, 0u);
  AL_CHECK(ctx.cursor_bytes == 0u);
  AL_CHECK(al_test_bytes_equal(stack, expected_i64, 8u));
  AL_CHECK(al_test_bytes_are((const uint8_t *)events, sizeof(events), 0xD3u));
  AL_CHECK(ctx.trace_event_count == 0u && ctx.trace_truncated == 0u);
  AL_CHECK(ctx.init_bitmap == 0 && ctx.poison_bitmap == 0 &&
           ctx.init_bitmap_bytes == 0u);
  AL_CHECK(al_test_bytes_are(bitmap_storage, sizeof(bitmap_storage), 0xB7u));

  al_test_init_context(&bad_profile_ctx, bad_profile_stack, 0);
  bad_profile_ctx.init_bitmap = bitmap_storage;
  AL_CHECK(al_owning_build_profile_valid(&bad_profile_ctx) == 0);
  al_owning_begin(&bad_profile_ctx);
  AL_CHECK(bad_profile_ctx.status == AL_OWNING_STATUS_INVALID_REQUEST);
  AL_CHECK(al_test_bytes_are(bad_profile_stack, sizeof(bad_profile_stack),
                             sentinel));
  AL_CHECK(al_test_bytes_are(bitmap_storage, sizeof(bitmap_storage), 0xB7u));

  al_test_init_context(&bad_layout_ctx, bad_layout_stack, 0);
  al_owning_begin(&bad_layout_ctx);
  valid_string[15] = 1u;
  AL_CHECK(al_owning_measure_external_value(
               &bad_layout_ctx, &al_test_layout, 2u, valid_string,
               sizeof(valid_string), 0u, AL_TEST_ERROR_ID, &payload_bytes,
               &extent_bytes) != 0);
  AL_CHECK(bad_layout_ctx.status == AL_OWNING_STATUS_INVALID_REQUEST);
  AL_CHECK(al_test_bytes_are(bad_layout_stack, sizeof(bad_layout_stack),
                             sentinel));
  AL_CHECK(bad_layout_ctx.init_bitmap == 0 &&
           bad_layout_ctx.poison_bitmap == 0 &&
           bad_layout_ctx.init_bitmap_bytes == 0u);

  al_test_init_context(&bad_layout_ctx, bad_layout_stack, 0);
  al_owning_begin(&bad_layout_ctx);
  AL_CHECK(al_owning_measure_external_value(
               &bad_layout_ctx, &al_test_layout, 2u, truncated_string,
               sizeof(truncated_string), 0u, AL_TEST_ERROR_ID, &payload_bytes,
               &extent_bytes) != 0);
  AL_CHECK(bad_layout_ctx.status == AL_OWNING_STATUS_INVALID_REQUEST);

  al_test_init_context(&overlap_ctx, overlap_stack, 0);
  al_owning_begin(&overlap_ctx);
  AL_CHECK(al_owning_reserve_to(&overlap_ctx, AL_TEST_STACK_CAPACITY,
                                AL_TEST_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(
               &overlap_ctx, 8u, overlap_stack, sizeof(overlap_stack), 0u,
               8u, 8u, AL_TEST_TYPE_VALUE, AL_TEST_ERROR_ID) != 0);
  AL_CHECK(overlap_ctx.status == AL_OWNING_STATUS_INVALID_REQUEST);
  AL_CHECK(al_test_bytes_are(overlap_stack, sizeof(overlap_stack), sentinel));

  al_test_init_context(&range_ctx, stack, 0);
  al_owning_begin(&range_ctx);
  AL_CHECK(al_owning_reserve_to(&range_ctx, AL_TEST_STACK_CAPACITY,
                                AL_TEST_ERROR_ID) == 0);
  AL_CHECK(al_owning_check_initialized(&range_ctx, AL_TEST_STACK_CAPACITY,
                                       1u) != 0);
  AL_CHECK(range_ctx.status == AL_OWNING_STATUS_INTERNAL);
  AL_CHECK(range_ctx.required_bytes == AL_TEST_STACK_CAPACITY + 1u);
  al_owning_begin(&range_ctx);
  AL_CHECK(al_owning_reserve_to(&range_ctx, 8u, AL_TEST_ERROR_ID) == 0);
  AL_CHECK(al_owning_check_initialized(&range_ctx, 8u, 1u) != 0);
  AL_CHECK(range_ctx.status == AL_OWNING_STATUS_INTERNAL);
  AL_CHECK(range_ctx.required_bytes == 9u);

  al_test_init_context(&capacity_ctx, stack, 0);
  al_owning_begin(&capacity_ctx);
  AL_CHECK(al_owning_reserve_to(&capacity_ctx, AL_TEST_STACK_CAPACITY + 1u,
                                AL_TEST_ERROR_ID) != 0);
  AL_CHECK(capacity_ctx.status == AL_OWNING_STATUS_STACK_CAPACITY);

  (void)printf("owning_release_runtime_test: passed\n");
  return 0;
}
