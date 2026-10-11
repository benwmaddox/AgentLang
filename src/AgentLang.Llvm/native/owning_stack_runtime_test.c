#include "owning_stack_runtime.h"

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define AL_TEST_STACK_CAPACITY 64u
#define AL_TEST_DYNAMIC_STACK_CAPACITY 128u
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
  AL_TEST_NATIVE_FRAME_DEPTH_ERROR_ID = 181u,
  AL_TEST_TYPE_STRING = 8u,
  AL_TEST_TYPE_TEXT_LEAF = 9u,
  AL_TEST_TYPE_TEXT_ENVELOPE = 10u,
  AL_TEST_TYPE_TEXT_STATE = 11u,
  AL_TEST_TYPE_EMPTY_WRAPPER = 12u,
  AL_TEST_DYNAMIC_ERROR_ID = 201u
};

_Static_assert(sizeof(al_owning_stack_event) == 40u,
               "ABI 1 event size changed");
_Static_assert(offsetof(al_owning_stack_event, checksum) == 32u,
               "ABI 1 event checksum offset changed");
_Static_assert(sizeof(al_owning_type_descriptor) == 36u,
               "Layout ABI 3 type descriptor size changed");
_Static_assert(sizeof(al_owning_field_descriptor) == 16u,
               "Layout ABI 3 field descriptor size changed");
_Static_assert(sizeof(al_owning_value_size) == 8u,
               "Layout ABI 3 value size result changed");
_Static_assert(sizeof(al_owning_field_location) == 12u,
               "Layout ABI 3 field location result changed");
_Static_assert(
    offsetof(al_owning_type_descriptor, kind) == 0u &&
        offsetof(al_owning_type_descriptor, type_id) == 4u &&
        offsetof(al_owning_type_descriptor, first_field) == 8u &&
        offsetof(al_owning_type_descriptor, field_count) == 12u &&
        offsetof(al_owning_type_descriptor, fixed_payload_bytes) == 16u &&
        offsetof(al_owning_type_descriptor, fixed_extent_bytes) == 20u &&
        offsetof(al_owning_type_descriptor, minimum_payload_bytes) == 24u &&
        offsetof(al_owning_type_descriptor, minimum_extent_bytes) == 28u &&
        offsetof(al_owning_type_descriptor, case_count) == 32u,
    "Layout ABI 3 type descriptor offsets changed");
_Static_assert(offsetof(al_owning_field_descriptor, child_type_index) == 0u &&
                   offsetof(al_owning_field_descriptor, fixed_offset_bytes) ==
                       4u &&
                   offsetof(al_owning_field_descriptor, flags) == 8u &&
                   offsetof(al_owning_field_descriptor, reserved) == 12u,
               "Layout ABI 3 field descriptor offsets changed");
_Static_assert(offsetof(al_owning_value_size, payload_bytes) == 0u &&
                   offsetof(al_owning_value_size, extent_bytes) == 4u,
               "Layout ABI 3 value size offsets changed");
_Static_assert(offsetof(al_owning_field_location, offset_bytes) == 0u &&
                   offsetof(al_owning_field_location, payload_bytes) == 4u &&
                   offsetof(al_owning_field_location, extent_bytes) == 8u,
               "Layout ABI 3 field location offsets changed");
_Static_assert(AL_OWNING_LAYOUT_MAX_TYPES == 4096u,
               "Layout ABI type descriptor ceiling changed");
_Static_assert(AL_OWNING_LAYOUT_MAX_FIELDS == 65536u,
               "Layout ABI field descriptor ceiling changed");
#if UINTPTR_MAX == UINT64_MAX
_Static_assert(sizeof(al_owning_stack_context) == 168u,
               "ABI 1 64-bit context size changed");
_Static_assert(sizeof(al_owning_layout) == 40u,
               "Layout ABI 3 layout descriptor size changed");
_Static_assert(offsetof(al_owning_layout, abi_version) == 0u,
               "Layout ABI 3 version offset changed");
_Static_assert(offsetof(al_owning_layout, types) == 8u,
               "Layout ABI 3 type pointer offset changed");
_Static_assert(offsetof(al_owning_layout, type_count) == 16u,
               "Layout ABI 3 type count offset changed");
_Static_assert(offsetof(al_owning_layout, fields) == 24u,
               "Layout ABI 3 field pointer offset changed");
_Static_assert(offsetof(al_owning_layout, field_count) == 32u,
               "Layout ABI 3 field count offset changed");
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

enum {
  AL_TEST_LAYOUT_INT = 0u,
  AL_TEST_LAYOUT_BOOL = 1u,
  AL_TEST_LAYOUT_UNIT = 2u,
  AL_TEST_LAYOUT_STRING = 3u,
  AL_TEST_LAYOUT_TEXT_LEAF = 4u,
  AL_TEST_LAYOUT_TEXT_ENVELOPE = 5u,
  AL_TEST_LAYOUT_TEXT_STATE = 6u,
  AL_TEST_LAYOUT_EMPTY = 7u,
  AL_TEST_LAYOUT_EMPTY_WRAPPER = 8u,
  AL_TEST_LAYOUT_TYPE_COUNT = 9u
};

static const al_owning_type_descriptor al_test_dynamic_types[] = {
    {AL_OWNING_TYPE_I64, AL_TEST_TYPE_INT, 0u, 0u, 8u, 8u, 8u, 8u, 0u},
    {AL_OWNING_TYPE_BOOL, AL_TEST_TYPE_BOOL, 0u, 0u, 8u, 8u, 8u, 8u, 0u},
    {AL_OWNING_TYPE_UNIT, AL_TEST_TYPE_UNIT, 0u, 0u, 8u, 8u, 8u, 8u, 0u},
    {AL_OWNING_TYPE_STRING, AL_TEST_TYPE_STRING, 0u, 0u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 0u},
    {AL_OWNING_TYPE_RECORD, AL_TEST_TYPE_TEXT_LEAF, 0u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 16u, 16u, 0u},
    {AL_OWNING_TYPE_RECORD, AL_TEST_TYPE_TEXT_ENVELOPE, 2u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 24u, 24u, 0u},
    {AL_OWNING_TYPE_RECORD, AL_TEST_TYPE_TEXT_STATE, 4u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 32u, 32u, 0u},
    {AL_OWNING_TYPE_RECORD, AL_TEST_TYPE_EMPTY_RECORD, 6u, 0u, 0u, 8u, 0u, 8u, 0u},
    {AL_OWNING_TYPE_RECORD, AL_TEST_TYPE_EMPTY_WRAPPER, 6u, 1u, 0u, 8u, 0u,
     8u, 0u}};

static const al_owning_field_descriptor al_test_dynamic_fields[] = {
    {AL_TEST_LAYOUT_STRING, 0u, 0u, 0u},
    {AL_TEST_LAYOUT_INT, AL_OWNING_LAYOUT_DYNAMIC_U32, 0u, 0u},
    {AL_TEST_LAYOUT_TEXT_LEAF, 0u, 0u, 0u},
    {AL_TEST_LAYOUT_INT, AL_OWNING_LAYOUT_DYNAMIC_U32, 0u, 0u},
    {AL_TEST_LAYOUT_INT, 0u, 0u, 0u},
    {AL_TEST_LAYOUT_TEXT_ENVELOPE, 8u, 0u, 0u},
    {AL_TEST_LAYOUT_EMPTY, 0u, AL_OWNING_FIELD_ZERO_WIDTH, 0u}};

static const al_owning_layout al_test_dynamic_layout = {
    AL_OWNING_LAYOUT_ABI_VERSION, al_test_dynamic_types,
    AL_TEST_LAYOUT_TYPE_COUNT, al_test_dynamic_fields,
    (uint32_t)(sizeof(al_test_dynamic_fields) /
               sizeof(al_test_dynamic_fields[0]))};

enum {
  AL_TEST_ENUM_LAYOUT_TAG = 0u,
  AL_TEST_ENUM_LAYOUT_INT = 1u,
  AL_TEST_ENUM_LAYOUT_STRING = 2u,
  AL_TEST_ENUM_LAYOUT_FIXED_RECORD = 3u,
  AL_TEST_ENUM_LAYOUT_NESTED_FIXED = 4u,
  AL_TEST_ENUM_LAYOUT_TEXT_RECORD = 5u,
  AL_TEST_ENUM_LAYOUT_NESTED_TEXT = 6u,
  AL_TEST_ENUM_LAYOUT_DYNAMIC_TAIL_RECORD = 7u,
  AL_TEST_ENUM_LAYOUT_TYPE_COUNT = 8u
};

static const al_owning_type_descriptor al_test_enum_types[] = {
    {AL_OWNING_TYPE_ENUM, 301u, 0u, 0u, 8u, 8u, 8u, 8u, 3u},
    {AL_OWNING_TYPE_I64, 302u, 0u, 0u, 8u, 8u, 8u, 8u, 0u},
    {AL_OWNING_TYPE_STRING, 303u, 0u, 0u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 0u},
    {AL_OWNING_TYPE_RECORD, 304u, 0u, 2u, 16u, 16u, 16u, 16u, 0u},
    {AL_OWNING_TYPE_RECORD, 305u, 2u, 1u, 16u, 16u, 16u, 16u, 0u},
    {AL_OWNING_TYPE_RECORD, 306u, 3u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 16u, 16u,
     0u},
    {AL_OWNING_TYPE_RECORD, 307u, 5u, 1u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 16u, 16u,
     0u},
    {AL_OWNING_TYPE_RECORD, 308u, 6u, 3u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 24u, 24u,
     0u}};

static const al_owning_field_descriptor al_test_enum_fields[] = {
    {AL_TEST_ENUM_LAYOUT_TAG, 0u, 0u, 0u},
    {AL_TEST_ENUM_LAYOUT_INT, 8u, 0u, 0u},
    {AL_TEST_ENUM_LAYOUT_FIXED_RECORD, 0u, 0u, 0u},
    {AL_TEST_ENUM_LAYOUT_STRING, 0u, 0u, 0u},
    {AL_TEST_ENUM_LAYOUT_TAG, AL_OWNING_LAYOUT_DYNAMIC_U32, 0u, 0u},
    {AL_TEST_ENUM_LAYOUT_TEXT_RECORD, 0u, 0u, 0u},
    {AL_TEST_ENUM_LAYOUT_STRING, 0u, 0u, 0u},
    {AL_TEST_ENUM_LAYOUT_TAG, AL_OWNING_LAYOUT_DYNAMIC_U32, 0u, 0u},
    {AL_TEST_ENUM_LAYOUT_INT, AL_OWNING_LAYOUT_DYNAMIC_U32, 0u, 0u}};

static const al_owning_layout al_test_enum_layout = {
    AL_OWNING_LAYOUT_ABI_VERSION,
    al_test_enum_types,
    AL_TEST_ENUM_LAYOUT_TYPE_COUNT,
    al_test_enum_fields,
    (uint32_t)(sizeof(al_test_enum_fields) /
               sizeof(al_test_enum_fields[0]))};

enum {
  AL_TEST_SUM_INT = 0u,
  AL_TEST_SUM_STRING = 1u,
  AL_TEST_SUM_EMPTY = 2u,
  AL_TEST_SUM_OPTION_INT = 3u,
  AL_TEST_SUM_RESULT_INT_INT = 4u,
  AL_TEST_SUM_RESULT_INT_STRING = 5u,
  AL_TEST_SUM_OPTION_STRING = 6u,
  AL_TEST_SUM_OPTION_NESTED_RESULT = 7u,
  AL_TEST_SUM_OPTION_EMPTY = 8u,
  AL_TEST_SUM_RESULT_EMPTY_EMPTY = 9u,
  AL_TEST_SUM_OPTION_STRING_RECORD = 10u,
  AL_TEST_SUM_LAYOUT_TYPE_COUNT = 11u
};

static const al_owning_type_descriptor al_test_sum_types[] = {
    {AL_OWNING_TYPE_I64, 401u, 0u, 0u, 8u, 8u, 8u, 8u, 0u},
    {AL_OWNING_TYPE_STRING, 402u, 0u, 0u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 0u},
    {AL_OWNING_TYPE_RECORD, 403u, 0u, 0u, 0u, 8u, 0u, 8u, 0u},
    {AL_OWNING_TYPE_OPTION, 404u, 0u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 2u},
    {AL_OWNING_TYPE_RESULT, 405u, 2u, 2u, 16u, 16u, 16u, 16u, 2u},
    {AL_OWNING_TYPE_RESULT, 406u, 4u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 16u, 16u,
     2u},
    {AL_OWNING_TYPE_OPTION, 407u, 6u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 2u},
    {AL_OWNING_TYPE_OPTION, 408u, 8u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 2u},
    {AL_OWNING_TYPE_OPTION, 409u, 10u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 2u},
    {AL_OWNING_TYPE_RESULT, 410u, 12u, 2u, 8u, 16u, 8u, 16u, 2u},
    {AL_OWNING_TYPE_RECORD, 411u, 14u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 16u, 16u,
     0u}};

static const al_owning_field_descriptor al_test_sum_fields[] = {
    {AL_TEST_SUM_INT, 8u, 0u, 0u},
    {AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 0u, 0u},
    {AL_TEST_SUM_INT, 8u, 0u, 0u},
    {AL_TEST_SUM_INT, 8u, 0u, 0u},
    {AL_TEST_SUM_INT, 8u, 0u, 0u},
    {AL_TEST_SUM_STRING, 8u, 0u, 0u},
    {AL_TEST_SUM_STRING, 8u, 0u, 0u},
    {AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 0u, 0u},
    {AL_TEST_SUM_RESULT_INT_STRING, 8u, 0u, 0u},
    {AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 0u, 0u},
    {AL_TEST_SUM_EMPTY, 8u, 0u, 0u},
    {AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 0u, 0u},
    {AL_TEST_SUM_EMPTY, 8u, 0u, 0u},
    {AL_TEST_SUM_EMPTY, 8u, 0u, 0u},
    {AL_TEST_SUM_OPTION_STRING, 0u, 0u, 0u},
    {AL_TEST_SUM_INT, AL_OWNING_LAYOUT_DYNAMIC_U32, 0u, 0u}};

static const al_owning_layout al_test_sum_layout = {
    AL_OWNING_LAYOUT_ABI_VERSION,
    al_test_sum_types,
    AL_TEST_SUM_LAYOUT_TYPE_COUNT,
    al_test_sum_fields,
    (uint32_t)(sizeof(al_test_sum_fields) /
               sizeof(al_test_sum_fields[0]))};

enum {
  AL_TEST_BOOL_LAYOUT_BOOL = 0u,
  AL_TEST_BOOL_LAYOUT_NOMINAL = 1u,
  AL_TEST_BOOL_LAYOUT_STRING = 2u,
  AL_TEST_BOOL_LAYOUT_RECORD = 3u,
  AL_TEST_BOOL_LAYOUT_NOMINAL_RECORD = 4u,
  AL_TEST_BOOL_LAYOUT_OPTION = 5u,
  AL_TEST_BOOL_LAYOUT_RESULT_BOOL_STRING = 6u,
  AL_TEST_BOOL_LAYOUT_RESULT_STRING_BOOL = 7u,
  AL_TEST_BOOL_LAYOUT_TYPE_COUNT = 8u,
  AL_TEST_BOOL_ERROR_ID = 219u
};

static const al_owning_type_descriptor al_test_bool_types[] = {
    {AL_OWNING_TYPE_BOOL, 601u, 0u, 0u, 8u, 8u, 8u, 8u, 0u},
    {AL_OWNING_TYPE_BOOL, 602u, 0u, 0u, 8u, 8u, 8u, 8u, 0u},
    {AL_OWNING_TYPE_STRING, 603u, 0u, 0u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 0u},
    {AL_OWNING_TYPE_RECORD, 604u, 0u, 1u, 8u, 8u, 8u, 8u, 0u},
    {AL_OWNING_TYPE_RECORD, 608u, 1u, 1u, 8u, 8u, 8u, 8u, 0u},
    {AL_OWNING_TYPE_OPTION, 605u, 2u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 2u},
    {AL_OWNING_TYPE_RESULT, 606u, 4u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 16u, 16u, 2u},
    {AL_OWNING_TYPE_RESULT, 607u, 6u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 16u, 16u, 2u}};

static const al_owning_field_descriptor al_test_bool_fields[] = {
    {AL_TEST_BOOL_LAYOUT_BOOL, 0u, 0u, 0u},
    {AL_TEST_BOOL_LAYOUT_NOMINAL, 0u, 0u, 0u},
    {AL_TEST_BOOL_LAYOUT_BOOL, 8u, 0u, 0u},
    {AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 0u, 0u},
    {AL_TEST_BOOL_LAYOUT_BOOL, 8u, 0u, 0u},
    {AL_TEST_BOOL_LAYOUT_STRING, 8u, 0u, 0u},
    {AL_TEST_BOOL_LAYOUT_STRING, 8u, 0u, 0u},
    {AL_TEST_BOOL_LAYOUT_NOMINAL, 8u, 0u, 0u}};

static const al_owning_layout al_test_bool_layout = {
    AL_OWNING_LAYOUT_ABI_VERSION,
    al_test_bool_types,
    AL_TEST_BOOL_LAYOUT_TYPE_COUNT,
    al_test_bool_fields,
    (uint32_t)(sizeof(al_test_bool_fields) /
               sizeof(al_test_bool_fields[0]))};

typedef struct al_test_fixture {
  uint8_t raw[AL_TEST_DYNAMIC_STACK_CAPACITY + 2u * AL_TEST_GUARD_BYTES];
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

typedef struct al_test_metric_snapshot {
  uint32_t cases;
  uint32_t checks;
  uint32_t peak_cursor;
  uint32_t peak_payload;
  uint32_t peak_local_reserved;
  uint32_t peak_local_payload;
  uint32_t stack_capacity;
  uint32_t bitmap_bytes;
  uint32_t event_count;
  uint32_t trace_capacity;
} al_test_metric_snapshot;

static al_test_metric_snapshot al_test_fixed_baseline;

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

static void al_test_write_u32_le(uint8_t *bytes, uint32_t offset,
                                 uint32_t value) {
  bytes[offset] = (uint8_t)value;
  bytes[offset + 1u] = (uint8_t)(value >> 8u);
  bytes[offset + 2u] = (uint8_t)(value >> 16u);
  bytes[offset + 3u] = (uint8_t)(value >> 24u);
}

static void al_test_write_u64_le(uint8_t *bytes, uint32_t offset,
                                 uint64_t value) {
  uint32_t index;
  for (index = 0u; index < 8u; ++index)
    bytes[offset + index] = (uint8_t)(value >> (index * 8u));
}

static void al_test_build_string(uint8_t *bytes, uint32_t code_units,
                                 const uint8_t *data) {
  uint32_t data_bytes = code_units * 2u;
  uint32_t payload = 8u + data_bytes;
  uint32_t extent = (payload + 7u) & ~7u;
  uint32_t index;
  (void)memset(bytes, 0, extent);
  al_test_write_u32_le(bytes, 0u, code_units);
  for (index = 0u; index < data_bytes; ++index)
    bytes[8u + index] = data[index];
}

static void al_test_build_text_state(uint8_t *bytes, uint32_t count,
                                     const uint8_t *string_data,
                                     uint32_t code_units, uint64_t state_count,
                                     uint64_t tag, uint32_t *out_payload,
                                     uint32_t *out_extent) {
  uint32_t string_payload = 8u + code_units * 2u;
  uint32_t string_extent = (string_payload + 7u) & ~7u;
  uint32_t code_units_offset = 8u + string_extent;
  uint32_t tag_offset = 16u + string_extent;
  uint32_t extent = string_extent + 24u;
  (void)memset(bytes, 0, extent);
  al_test_write_u64_le(bytes, 0u, state_count);
  al_test_write_u32_le(bytes, 8u, count);
  if (code_units * 2u > 0u)
    (void)memcpy(bytes + 16u, string_data, code_units * 2u);
  al_test_write_u64_le(bytes, code_units_offset, code_units);
  al_test_write_u64_le(bytes, tag_offset, tag);
  *out_payload = 32u + code_units * 2u;
  *out_extent = extent;
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

static int al_test_dynamic_external_copy_and_fields(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  uint8_t external[64];
  uint8_t expected[40] = {0};
  const uint8_t text[] = {0x41u, 0x00u};
  uint32_t payload;
  uint32_t extent;
  uint32_t measured_payload = 0u;
  uint32_t measured_extent = 0u;
  al_owning_value_size measured;
  al_owning_field_location location;
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  al_test_build_text_state(external, 1u, text, 1u, UINT64_C(0x1122334455667788),
                           UINT64_C(0x8877665544332211), &payload, &extent);
  AL_CHECK(payload == 34u && extent == 40u);
  /* Independent byte oracle for State{Int64, Envelope{String,Int64},Int64}.
   * In particular, nonzero trailing fields after the dynamic String prove
   * that the bounded external copy includes nested padding and later fields. */
  al_test_write_u64_le(expected, 0u, UINT64_C(0x1122334455667788));
  al_test_write_u32_le(expected, 8u, 1u);
  expected[16u] = 0x41u;
  al_test_write_u64_le(expected, 24u, 1u);
  al_test_write_u64_le(expected, 32u, UINT64_C(0x8877665544332211));
  AL_CHECK(memcmp(external, expected, sizeof(expected)) == 0);
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_dynamic_layout, AL_TEST_LAYOUT_TEXT_STATE,
               external, extent, 0u, AL_TEST_DYNAMIC_ERROR_ID,
               &measured_payload, &measured_extent) == 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK && measured_payload == payload &&
           measured_extent == extent);
  AL_CHECK(al_owning_reserve_to(ctx, extent, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(
               ctx, 0u, external, extent, 0u, payload, extent,
               AL_TEST_TYPE_TEXT_STATE, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK &&
           ctx->input_copy_bytes == extent &&
           ctx->live_payload_bytes == payload);
  AL_CHECK(memcmp(ctx->stack_data, expected, extent) == 0);
  AL_CHECK(ctx->stack_data[18u] == 0u && ctx->stack_data[19u] == 0u &&
           ctx->stack_data[23u] == 0u);
  AL_CHECK(al_owning_measure_value(ctx, &al_test_dynamic_layout,
                                   AL_TEST_LAYOUT_TEXT_STATE, 0u, extent,
                                   AL_TEST_DYNAMIC_ERROR_ID, &measured) == 0);
  AL_CHECK(measured.payload_bytes == payload &&
           measured.extent_bytes == extent);
  AL_CHECK(al_owning_locate_field(
               ctx, &al_test_dynamic_layout, AL_TEST_LAYOUT_TEXT_ENVELOPE, 8u,
               extent - 8u, 1u, AL_TEST_DYNAMIC_ERROR_ID, &location) == 0);
  AL_CHECK(location.offset_bytes == 32u && location.payload_bytes == 8u &&
           location.extent_bytes == 8u);
  AL_CHECK(al_owning_load_i64(ctx, location.offset_bytes) ==
           INT64_C(0x8877665544332211));
  AL_CHECK(al_test_case_begin("dynamic_external_copy_and_fields", &fixture));
  return 1;
failed:
  return 0;
}

static int al_test_dynamic_owner_end_boundary(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  al_owning_value_size measured = {UINT32_C(0xDEADBEEF), UINT32_C(0xC0FFEE00)};
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, 32u, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  al_owning_store_i64(ctx, 0u, 5, AL_TEST_TYPE_STRING);
  al_owning_store_i64(ctx, 8u, INT64_C(0x1111222233334444), AL_TEST_TYPE_INT);
  al_owning_store_i64(ctx, 16u, INT64_C(0x5555666677778888), AL_TEST_TYPE_INT);
  al_owning_store_i64(ctx, 24u, INT64_C(0x0123456789ABCDEF), AL_TEST_TYPE_INT);
  AL_CHECK(ctx->cursor_bytes == 32u);
  AL_CHECK(al_owning_measure_value(ctx, &al_test_dynamic_layout,
                                   AL_TEST_LAYOUT_TEXT_LEAF, 0u, 16u,
                                   AL_TEST_DYNAMIC_ERROR_ID, &measured) != 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_INTERNAL &&
           ctx->error_id == AL_TEST_DYNAMIC_ERROR_ID &&
           ctx->cursor_bytes == 32u && ctx->required_bytes == 24u &&
           ctx->available_bytes == 16u);
  AL_CHECK(measured.payload_bytes == UINT32_C(0xDEADBEEF) &&
           measured.extent_bytes == UINT32_C(0xC0FFEE00));
  AL_CHECK(al_test_fixture_guards_ok(&fixture));
  ++al_test_cases;
  al_test_capture_metrics(&fixture);
  return 1;
failed:
  return 0;
}

static int al_test_dynamic_concat_provenance_and_atomicity(void) {
  al_test_fixture fixture;
  al_test_fixture short_fixture;
  al_test_fixture overlap_fixture;
  al_owning_stack_context *ctx;
  uint8_t left[16];
  uint8_t right[16];
  uint8_t short_before[48];
  uint8_t overlap_before[48];
  uint8_t overlap_init_before[16];
  uint8_t overlap_poison_before[16];
  const uint8_t left_text[] = {0x41u, 0x00u};
  const uint8_t right_text[] = {0x42u, 0x00u, 0x43u, 0x00u};
  uint32_t units;
  uint32_t payload;
  uint32_t extent;
  uint32_t index;
  al_test_build_string(left, 1u, left_text);
  al_test_build_string(right, 2u, right_text);

  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, 48u, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(ctx, 0u, left, sizeof(left), 0u, 10u,
                                           sizeof(left), AL_TEST_TYPE_STRING,
                                           AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(
               ctx, 16u, right, sizeof(right), 0u, 12u, sizeof(right),
               AL_TEST_TYPE_STRING, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_string_concat_plan(ctx, 0u, 16u, 16u, 16u,
                                        AL_TEST_DYNAMIC_ERROR_ID, &units,
                                        &payload, &extent) == 0);
  AL_CHECK(units == 3u && payload == 14u && extent == 16u);
  AL_CHECK(al_owning_string_concat_write(ctx, 32u, extent, 0u, 16u, 16u, 16u,
                                         AL_TEST_TYPE_STRING,
                                         AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(ctx->trace_event_count == 4u &&
           ctx->trace_events[2].kind == AL_OWNING_EVENT_STRING_CONCAT_LEFT &&
           ctx->trace_events[3].kind == AL_OWNING_EVENT_STRING_CONCAT_RIGHT);
  AL_CHECK(ctx->trace_events[2].offset == 40u &&
           ctx->trace_events[2].extent_bytes == 2u &&
           ctx->trace_events[2].payload_bytes == 2u &&
           ctx->trace_events[2].source_offset == 8u &&
           ctx->trace_events[2].source_extent_bytes == 2u);
  AL_CHECK(ctx->trace_events[3].offset == 42u &&
           ctx->trace_events[3].extent_bytes == 4u &&
           ctx->trace_events[3].payload_bytes == 4u &&
           ctx->trace_events[3].source_offset == 24u &&
           ctx->trace_events[3].source_extent_bytes == 4u);
  AL_CHECK(ctx->trace_events[2].offset + ctx->trace_events[2].extent_bytes ==
           ctx->trace_events[3].offset);
  AL_CHECK(ctx->deep_copy_bytes == 16u && ctx->input_copy_bytes == 32u);
  AL_CHECK(ctx->stack_data[32u] == 3u && ctx->stack_data[36u] == 0u &&
           ctx->stack_data[37u] == 0u && ctx->stack_data[38u] == 0u &&
           ctx->stack_data[39u] == 0u);
  AL_CHECK(memcmp(ctx->stack_data + 40u, "A\0B\0C\0", 6u) == 0);
  AL_CHECK(ctx->stack_data[46u] == 0u && ctx->stack_data[47u] == 0u);
  AL_CHECK(ctx->live_payload_bytes == 22u);
  AL_CHECK(al_test_case_begin("dynamic_concat_provenance", &fixture));

  al_test_fixture_init(&short_fixture, 47u);
  ctx = &short_fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, 32u, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(ctx, 0u, left, sizeof(left), 0u, 10u,
                                           sizeof(left), AL_TEST_TYPE_STRING,
                                           AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(
               ctx, 16u, right, sizeof(right), 0u, 12u, sizeof(right),
               AL_TEST_TYPE_STRING, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  (void)memcpy(short_before, ctx->stack_data, sizeof(short_before));
  AL_CHECK(al_owning_reserve_to(ctx, 48u, AL_TEST_DYNAMIC_ERROR_ID) != 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_STACK_CAPACITY &&
           ctx->cursor_bytes == 32u && ctx->required_bytes == 48u &&
           ctx->available_bytes == 47u);
  AL_CHECK(memcmp(short_before, ctx->stack_data, sizeof(short_before)) == 0);
  AL_CHECK(al_test_fixture_guards_ok(&short_fixture));
  ++al_test_cases;
  al_test_capture_metrics(&short_fixture);

  al_test_fixture_init(&overlap_fixture, AL_TEST_STACK_CAPACITY);
  ctx = &overlap_fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, 48u, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(ctx, 0u, left, sizeof(left), 0u, 10u,
                                           sizeof(left), AL_TEST_TYPE_STRING,
                                           AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(
               ctx, 16u, right, sizeof(right), 0u, 12u, sizeof(right),
               AL_TEST_TYPE_STRING, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_string_concat_plan(ctx, 0u, 16u, 16u, 16u,
                                        AL_TEST_DYNAMIC_ERROR_ID, &units,
                                        &payload, &extent) == 0);
  (void)memcpy(overlap_before, ctx->stack_data, sizeof(overlap_before));
  (void)memcpy(overlap_init_before, overlap_fixture.init_bitmap,
               sizeof(overlap_init_before));
  (void)memcpy(overlap_poison_before, overlap_fixture.poison_bitmap,
               sizeof(overlap_poison_before));
  AL_CHECK(al_owning_string_concat_write(ctx, 16u, 16u, 0u, 16u, 16u, 16u,
                                         AL_TEST_TYPE_STRING,
                                         AL_TEST_DYNAMIC_ERROR_ID) != 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_INTERNAL &&
           ctx->cursor_bytes == 48u);
  AL_CHECK(memcmp(overlap_before, ctx->stack_data, sizeof(overlap_before)) ==
           0);
  AL_CHECK(memcmp(overlap_init_before, overlap_fixture.init_bitmap,
                  sizeof(overlap_init_before)) == 0);
  AL_CHECK(memcmp(overlap_poison_before, overlap_fixture.poison_bitmap,
                  sizeof(overlap_poison_before)) == 0);
  AL_CHECK(al_test_fixture_guards_ok(&overlap_fixture));
  ++al_test_cases;
  al_test_capture_metrics(&overlap_fixture);
  for (index = 0u; index < (uint32_t)sizeof(overlap_before); ++index)
    AL_CHECK(overlap_before[index] == overlap_fixture.ctx.stack_data[index]);
  return 1;
failed:
  return 0;
}

static int al_test_dynamic_descriptor_validation(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  al_owning_type_descriptor types[64];
  al_owning_field_descriptor fields[126];
  al_owning_layout layout;
  uint8_t empty_token[8] = {0u};
  uint32_t index;
  uint32_t payload;
  uint32_t extent;
  for (index = 0u; index < 64u; ++index) {
    types[index] =
        (al_owning_type_descriptor){AL_OWNING_TYPE_RECORD,
                                    1000u + index,
                                    index == 0u ? 0u : 2u * (index - 1u),
                                    index == 0u ? 0u : 2u,
                                    0u,
                                    8u,
                                    0u,
                                    8u,
                                    0u};
    if (index != 0u) {
      fields[2u * (index - 1u)] = (al_owning_field_descriptor){
          index - 1u, 0u, AL_OWNING_FIELD_ZERO_WIDTH, 0u};
      fields[2u * (index - 1u) + 1u] = (al_owning_field_descriptor){
          index - 1u, 0u, AL_OWNING_FIELD_ZERO_WIDTH, 0u};
    }
  }
  layout = (al_owning_layout){AL_OWNING_LAYOUT_ABI_VERSION, types, 64u, fields,
                              126u};
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_measure_external_value(
               ctx, &layout, 63u, empty_token, sizeof(empty_token), 0u,
               AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) == 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK && payload == 0u && extent == 8u);
  AL_CHECK(al_test_case_begin("dynamic_repeated_empty_dag", &fixture));

  /* A malformed child index is rejected before an external read. */
  fields[124u].child_type_index = 64u;
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_measure_external_value(
               ctx, &layout, 63u, empty_token, sizeof(empty_token), 0u,
               AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) != 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_INTERNAL && ctx->cursor_bytes == 0u);
  ++al_test_cases;
  al_test_capture_metrics(&fixture);
  fields[124u].child_type_index = 62u;

  /* A 65th descriptor on one path is rejected even though all values are
   * zero-width empty records. */
  {
    al_owning_type_descriptor too_deep_types[65];
    al_owning_field_descriptor too_deep_fields[64];
    al_owning_layout too_deep_layout;
    for (index = 0u; index < 65u; ++index) {
      too_deep_types[index] =
          (al_owning_type_descriptor){AL_OWNING_TYPE_RECORD,
                                      2000u + index,
                                      index == 0u ? 0u : index - 1u,
                                      index == 0u ? 0u : 1u,
                                      0u,
                                      8u,
                                      0u,
                                      8u,
                                      0u};
      if (index != 0u)
        too_deep_fields[index - 1u] = (al_owning_field_descriptor){
            index - 1u, 0u, AL_OWNING_FIELD_ZERO_WIDTH, 0u};
    }
    too_deep_layout =
        (al_owning_layout){AL_OWNING_LAYOUT_ABI_VERSION, too_deep_types, 65u,
                           too_deep_fields, 64u};
    al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
    ctx = &fixture.ctx;
    AL_CHECK(al_owning_measure_external_value(
                 ctx, &too_deep_layout, 64u, empty_token, sizeof(empty_token),
                 0u, AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) != 0);
    AL_CHECK(ctx->status == AL_OWNING_STATUS_INTERNAL &&
             ctx->cursor_bytes == 0u);
    ++al_test_cases;
    al_test_capture_metrics(&fixture);
  }

  /* A two-node record cycle is rejected by the tri-color check. */
  {
    al_owning_type_descriptor cycle_types[2] = {
        {AL_OWNING_TYPE_RECORD, 3000u, 0u, 1u, 8u, 8u, 8u, 8u, 0u},
        {AL_OWNING_TYPE_RECORD, 3001u, 1u, 1u, 8u, 8u, 8u, 8u, 0u}};
    al_owning_field_descriptor cycle_fields[2] = {{1u, 0u, 0u, 0u},
                                                  {0u, 0u, 0u, 0u}};
    al_owning_layout cycle_layout = {AL_OWNING_LAYOUT_ABI_VERSION, cycle_types,
                                     2u, cycle_fields, 2u};
    al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
    ctx = &fixture.ctx;
    AL_CHECK(al_owning_measure_external_value(
                 ctx, &cycle_layout, 0u, empty_token, sizeof(empty_token), 0u,
                 AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) != 0);
    AL_CHECK(ctx->status == AL_OWNING_STATUS_INTERNAL &&
             ctx->cursor_bytes == 0u);
    ++al_test_cases;
    al_test_capture_metrics(&fixture);
  }

  /* Over-limit descriptor counts are rejected by the fixed header check
   * before the deliberately small backing arrays can be indexed. */
  {
    al_owning_layout oversized_types = {AL_OWNING_LAYOUT_ABI_VERSION,
                                        al_test_dynamic_types,
                                        AL_OWNING_LAYOUT_MAX_TYPES + 1u, 0, 0u};
    al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
    ctx = &fixture.ctx;
    AL_CHECK(al_owning_measure_external_value(
                 ctx, &oversized_types, 0u, empty_token, sizeof(empty_token),
                 0u, AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) != 0);
    AL_CHECK(ctx->status == AL_OWNING_STATUS_INTERNAL &&
             ctx->required_bytes == AL_OWNING_LAYOUT_MAX_TYPES + 1u &&
             ctx->available_bytes == AL_OWNING_LAYOUT_MAX_TYPES &&
             ctx->cursor_bytes == 0u);
    ++al_test_cases;
    al_test_capture_metrics(&fixture);
  }
  {
    al_owning_layout oversized_fields = {
        AL_OWNING_LAYOUT_ABI_VERSION, al_test_dynamic_types, 1u,
        al_test_dynamic_fields, AL_OWNING_LAYOUT_MAX_FIELDS + 1u};
    al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
    ctx = &fixture.ctx;
    AL_CHECK(al_owning_measure_external_value(
                 ctx, &oversized_fields, 0u, empty_token, sizeof(empty_token),
                 0u, AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) != 0);
    AL_CHECK(ctx->status == AL_OWNING_STATUS_INTERNAL &&
             ctx->required_bytes == AL_OWNING_LAYOUT_MAX_FIELDS + 1u &&
             ctx->available_bytes == AL_OWNING_LAYOUT_MAX_FIELDS &&
             ctx->cursor_bytes == 0u);
    AL_CHECK(al_test_fixture_guards_ok(&fixture));
    ++al_test_cases;
    al_test_capture_metrics(&fixture);
  }
  return 1;
failed:
  return 0;
}

static int al_test_dynamic_duplicate_drop_reuse(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  uint8_t original[40];
  uint8_t replacement[40];
  uint32_t original_payload;
  uint32_t original_extent;
  uint32_t replacement_payload;
  uint32_t replacement_extent;
  uint32_t poison_before;
  al_owning_value_size measured;
  al_test_build_text_state(original, 0u, 0, 0u, UINT64_C(111), UINT64_C(222),
                           &original_payload, &original_extent);
  al_test_build_text_state(replacement, 0u, 0, 0u, UINT64_C(333), UINT64_C(444),
                           &replacement_payload, &replacement_extent);
  AL_CHECK(original_payload == 32u && original_extent == 32u);
  AL_CHECK(replacement_payload == 32u && replacement_extent == 32u);

  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, 64u, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(
               ctx, 0u, original, original_extent, 0u, original_payload,
               original_extent, AL_TEST_TYPE_TEXT_STATE,
               AL_TEST_DYNAMIC_ERROR_ID) == 0);
  al_owning_duplicate(ctx, 32u, 0u, original_extent, original_payload,
                      AL_TEST_TYPE_TEXT_STATE);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK &&
           ctx->duplicate_disjoint_checks == 1u &&
           ctx->live_payload_bytes == 64u);
  al_owning_store_i64(ctx, 32u, INT64_C(999), AL_TEST_TYPE_INT);
  AL_CHECK(al_owning_load_i64(ctx, 0u) == INT64_C(111));
  AL_CHECK(al_owning_load_i64(ctx, 32u) == INT64_C(999));
  al_owning_drop(ctx, 32u, original_extent, original_payload,
                 AL_TEST_TYPE_TEXT_STATE);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK && ctx->cursor_bytes == 32u &&
           ctx->drop_survivor_checks == 1u &&
           ctx->live_payload_bytes == original_payload);
  AL_CHECK(memcmp(ctx->stack_data, original, original_extent) == 0);
  AL_CHECK(ctx->stack_data[32u] == 0xA5u && ctx->stack_data[63u] == 0xA5u);
  poison_before = ctx->poison_reuse_checks;
  AL_CHECK(al_owning_reserve_to(ctx, 64u, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(
               ctx, 32u, replacement, replacement_extent, 0u,
               replacement_payload, replacement_extent, AL_TEST_TYPE_TEXT_STATE,
               AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_measure_value(ctx, &al_test_dynamic_layout,
                                   AL_TEST_LAYOUT_TEXT_STATE, 32u, 64u,
                                   AL_TEST_DYNAMIC_ERROR_ID, &measured) == 0);
  AL_CHECK(ctx->poison_reuse_checks > poison_before &&
           measured.payload_bytes == replacement_payload &&
           measured.extent_bytes == replacement_extent);
  AL_CHECK(al_owning_load_i64(ctx, 0u) == INT64_C(111));
  AL_CHECK(al_owning_load_i64(ctx, 32u) == INT64_C(333));
  AL_CHECK(al_test_case_begin("dynamic_duplicate_drop_reuse", &fixture));
  return 1;
failed:
  return 0;
}

static int al_test_dynamic_local_suffix_compaction(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  uint8_t long_state[40];
  uint8_t short_state[40];
  uint8_t empty_string[8];
  uint32_t long_payload;
  uint32_t long_extent;
  uint32_t short_payload;
  uint32_t short_extent;
  uint32_t index;
  uint32_t compact_event;
  uint64_t move_before;
  const uint8_t text[] = {0x51u, 0x00u};
  al_owning_value_size measured;
  al_test_build_text_state(long_state, 1u, text, 1u, UINT64_C(71), UINT64_C(72),
                           &long_payload, &long_extent);
  al_test_build_text_state(short_state, 0u, 0, 0u, UINT64_C(81), UINT64_C(82),
                           &short_payload, &short_extent);
  al_test_build_string(empty_string, 0u, 0);
  AL_CHECK(long_payload == 34u && long_extent == 40u && short_payload == 32u &&
           short_extent == 32u);

  /* Reserve packed locals A/B, store each from a distinct operand copy, and
   * keep an empty String operand after them. */
  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  ctx = &fixture.ctx;
  al_owning_local_reserve(ctx, 48u);
  AL_CHECK(al_owning_reserve_to(ctx, 88u, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(
               ctx, 48u, long_state, long_extent, 0u, long_payload, long_extent,
               AL_TEST_TYPE_TEXT_STATE, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  al_owning_store_local(ctx, 0u, 48u, long_extent, long_extent, long_payload,
                        0u, AL_TEST_TYPE_TEXT_STATE);
  AL_CHECK(ctx->live_payload_bytes == long_payload &&
           ctx->live_local_payload_bytes == long_payload &&
           ctx->live_payload_bytes + ctx->live_local_payload_bytes <=
               ctx->cursor_bytes);
  al_owning_drop(ctx, 48u, long_extent, long_payload, AL_TEST_TYPE_TEXT_STATE);
  AL_CHECK(ctx->cursor_bytes == 48u && ctx->live_payload_bytes == 0u &&
           ctx->live_local_payload_bytes == long_payload);
  AL_CHECK(al_owning_reserve_to(ctx, 56u, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  al_owning_store_i64(ctx, 48u, INT64_C(1234567), AL_TEST_TYPE_INT);
  al_owning_update_live(ctx, 8, 0);
  AL_CHECK(ctx->live_payload_bytes == 8u &&
           ctx->live_local_payload_bytes == long_payload &&
           ctx->live_payload_bytes + ctx->live_local_payload_bytes <=
               ctx->cursor_bytes);
  al_owning_store_local(ctx, 40u, 48u, 8u, 8u, 8u, 0u, AL_TEST_TYPE_INT);
  AL_CHECK(ctx->live_payload_bytes == 8u &&
           ctx->live_local_payload_bytes == long_payload + 8u &&
           ctx->active_local_reserved_bytes == 48u &&
           ctx->live_payload_bytes + ctx->live_local_payload_bytes <=
               ctx->cursor_bytes);
  al_owning_drop(ctx, 48u, 8u, 8u, AL_TEST_TYPE_INT);
  AL_CHECK(ctx->cursor_bytes == 48u && ctx->live_payload_bytes == 0u &&
           ctx->live_local_payload_bytes == 42u &&
           ctx->active_local_reserved_bytes == 48u);
  AL_CHECK(al_owning_reserve_to(ctx, 56u, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(
               ctx, 48u, empty_string, sizeof(empty_string), 0u, 8u, 8u,
               AL_TEST_TYPE_STRING, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(ctx->live_payload_bytes == 8u &&
           ctx->live_local_payload_bytes == 42u &&
           ctx->active_local_reserved_bytes == 48u &&
           ctx->live_payload_bytes + ctx->live_local_payload_bytes <=
               ctx->cursor_bytes);

  /* Shrink local A from 40 to 32 bytes. Move the complete later-local and
   * operand suffix, remove A's old logical payload, then store its replacement
   * into the same slot. */
  move_before = ctx->move_bytes;
  al_owning_move_range(ctx, 32u, 40u, 16u, 16u, AL_TEST_TYPE_TEXT_STATE,
                       AL_OWNING_EVENT_LOCAL_COMPACT);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK &&
           ctx->move_bytes - move_before == 16u);
  al_owning_update_live(ctx, -(int32_t)long_payload, -(int32_t)long_payload);
  al_owning_release_to(ctx, 48u, 0u, 0u, 0u);
  al_owning_local_release(ctx, 8u);
  AL_CHECK(ctx->live_payload_bytes == 8u &&
           ctx->live_local_payload_bytes == 8u &&
           ctx->active_local_reserved_bytes == 40u &&
           ctx->live_payload_bytes + ctx->live_local_payload_bytes <=
               ctx->cursor_bytes);
  AL_CHECK(al_owning_reserve_to(ctx, 80u, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(ctx, 48u, short_state, short_extent,
                                           0u, short_payload, short_extent,
                                           AL_TEST_TYPE_TEXT_STATE,
                                           AL_TEST_DYNAMIC_ERROR_ID) == 0);
  al_owning_store_local(ctx, 0u, 48u, short_extent, short_extent, short_payload,
                        0u, AL_TEST_TYPE_TEXT_STATE);
  AL_CHECK(ctx->live_payload_bytes == 40u &&
           ctx->live_local_payload_bytes == 40u &&
           ctx->active_local_reserved_bytes == 40u &&
           ctx->live_payload_bytes + ctx->live_local_payload_bytes <=
               ctx->cursor_bytes);
  al_owning_drop(ctx, 48u, short_extent, short_payload,
                 AL_TEST_TYPE_TEXT_STATE);
  AL_CHECK(ctx->cursor_bytes == 48u && ctx->live_payload_bytes == 8u &&
           ctx->live_local_payload_bytes == 40u &&
           ctx->active_local_reserved_bytes == 40u &&
           ctx->live_payload_bytes + ctx->live_local_payload_bytes <=
               ctx->cursor_bytes);
  AL_CHECK(memcmp(ctx->stack_data, short_state, short_extent) == 0);
  AL_CHECK(al_owning_load_i64(ctx, 32u) == INT64_C(1234567));
  AL_CHECK(al_owning_string_length(ctx, 40u, 8u, AL_TEST_DYNAMIC_ERROR_ID,
                                   &index) == 0 &&
           index == 0u);
  AL_CHECK(al_owning_measure_value(ctx, &al_test_dynamic_layout,
                                   AL_TEST_LAYOUT_TEXT_STATE, 0u, short_extent,
                                   AL_TEST_DYNAMIC_ERROR_ID, &measured) == 0 &&
           measured.extent_bytes == short_extent);
  compact_event = UINT32_MAX;
  for (index = 0u; index < ctx->trace_event_count; ++index) {
    if (ctx->trace_events[index].kind == AL_OWNING_EVENT_LOCAL_COMPACT)
      compact_event = index;
  }
  AL_CHECK(compact_event != UINT32_MAX);
  AL_CHECK(ctx->trace_events[compact_event].offset == 32u &&
           ctx->trace_events[compact_event].extent_bytes == 16u &&
           ctx->trace_events[compact_event].payload_bytes == 16u &&
           ctx->trace_events[compact_event].source_offset == 40u &&
           ctx->trace_events[compact_event].source_extent_bytes == 16u);

  /* Grow the same local by eight bytes. Shift the younger local, existing
   * operand, and incoming replacement as one suffix. */
  AL_CHECK(al_owning_reserve_to(ctx, 88u, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(
               ctx, 48u, long_state, long_extent, 0u, long_payload, long_extent,
               AL_TEST_TYPE_TEXT_STATE, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(ctx->live_payload_bytes == 42u &&
           ctx->live_local_payload_bytes == 40u &&
           ctx->live_payload_bytes + ctx->live_local_payload_bytes <=
               ctx->cursor_bytes);
  al_owning_local_reserve(ctx, 8u);
  AL_CHECK(al_owning_reserve_to(ctx, 96u, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  move_before = ctx->move_bytes;
  al_owning_move_range(ctx, 40u, 32u, 56u, 50u, AL_TEST_TYPE_TEXT_STATE,
                       AL_OWNING_EVENT_LOCAL_COMPACT);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK &&
           ctx->move_bytes - move_before == 56u);
  AL_CHECK(ctx->stack_data[32u] == 0xA5u && ctx->stack_data[39u] == 0xA5u &&
           (fixture.init_bitmap[4u] & 0xFFu) == 0u &&
           (fixture.poison_bitmap[4u] & 0xFFu) == 0xFFu);
  al_owning_store_local(ctx, 0u, 56u, long_extent, long_extent, long_payload,
                        short_payload, AL_TEST_TYPE_TEXT_STATE);
  AL_CHECK(ctx->live_payload_bytes == 42u &&
           ctx->live_local_payload_bytes == 42u &&
           ctx->active_local_reserved_bytes == 48u &&
           ctx->live_payload_bytes + ctx->live_local_payload_bytes <=
               ctx->cursor_bytes);
  al_owning_drop(ctx, 56u, long_extent, long_payload, AL_TEST_TYPE_TEXT_STATE);
  AL_CHECK(ctx->cursor_bytes == 56u && ctx->live_payload_bytes == 8u &&
           ctx->live_local_payload_bytes == 42u &&
           ctx->active_local_reserved_bytes == 48u &&
           ctx->live_payload_bytes + ctx->live_local_payload_bytes <=
               ctx->cursor_bytes);
  AL_CHECK(memcmp(ctx->stack_data, long_state, long_extent) == 0);
  AL_CHECK(al_owning_load_i64(ctx, 40u) == INT64_C(1234567));
  AL_CHECK(al_owning_string_length(ctx, 48u, 8u, AL_TEST_DYNAMIC_ERROR_ID,
                                   &index) == 0 &&
           index == 0u);
  compact_event = UINT32_MAX;
  for (index = 0u; index < ctx->trace_event_count; ++index) {
    if (ctx->trace_events[index].kind == AL_OWNING_EVENT_LOCAL_COMPACT)
      compact_event = index;
  }
  AL_CHECK(compact_event != UINT32_MAX);
  AL_CHECK(ctx->trace_events[compact_event].offset == 40u &&
           ctx->trace_events[compact_event].extent_bytes == 56u &&
           ctx->trace_events[compact_event].payload_bytes == 50u &&
           ctx->trace_events[compact_event].source_offset == 32u &&
           ctx->trace_events[compact_event].source_extent_bytes == 56u);
  AL_CHECK(al_owning_measure_value(ctx, &al_test_dynamic_layout,
                                   AL_TEST_LAYOUT_TEXT_STATE, 0u, long_extent,
                                   AL_TEST_DYNAMIC_ERROR_ID, &measured) == 0 &&
           measured.payload_bytes == long_payload &&
           measured.extent_bytes == long_extent);
  al_owning_clear_local(ctx, 0u, long_extent, long_payload,
                        AL_TEST_TYPE_TEXT_STATE);
  al_owning_clear_local(ctx, 40u, 8u, 8u, AL_TEST_TYPE_INT);
  al_owning_local_release(ctx, 48u);
  AL_CHECK(ctx->live_payload_bytes == 8u &&
           ctx->live_local_payload_bytes == 0u &&
           ctx->active_local_reserved_bytes == 0u && ctx->cursor_bytes == 56u);
  al_owning_drop(ctx, 48u, 8u, 8u, AL_TEST_TYPE_STRING);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK &&
           ctx->live_payload_bytes == 0u && ctx->cursor_bytes == 48u);
  AL_CHECK(al_test_case_begin("dynamic_local_shadow_shrink_grow", &fixture));
  return 1;
failed:
  return 0;
}

static int al_test_dynamic_move_range_source_only_invalidation(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  uint8_t expected_destination[32];
  uint8_t expected_gap[32];
  uint8_t expected_tail[32];
  uint8_t poisoned[32];
  uint8_t gap_init_before[4];
  uint8_t gap_poison_before[4];
  uint32_t index;
  uint32_t trace_before;

  (void)memset(poisoned, 0xA5, sizeof(poisoned));
  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, AL_TEST_DYNAMIC_STACK_CAPACITY,
                                AL_TEST_DYNAMIC_ERROR_ID) == 0);
  for (index = 0u; index < 16u; ++index)
    al_owning_store_i64(ctx, index * 8u, 1000 + index, AL_TEST_TYPE_INT);
  al_owning_update_live(ctx, AL_TEST_DYNAMIC_STACK_CAPACITY, 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK &&
           ctx->live_payload_bytes == AL_TEST_DYNAMIC_STACK_CAPACITY);

  /* Disjoint left move: destination=[0,32), live gap=[32,64),
   * source=[64,96), and an initialized tail=[96,128). */
  (void)memcpy(expected_destination, ctx->stack_data + 64u,
               sizeof(expected_destination));
  (void)memcpy(expected_gap, ctx->stack_data + 32u, sizeof(expected_gap));
  (void)memcpy(expected_tail, ctx->stack_data + 96u, sizeof(expected_tail));
  (void)memcpy(gap_init_before, fixture.init_bitmap + 4u,
               sizeof(gap_init_before));
  (void)memcpy(gap_poison_before, fixture.poison_bitmap + 4u,
               sizeof(gap_poison_before));
  trace_before = ctx->trace_event_count;
  al_owning_move_range(ctx, 0u, 64u, 32u, 32u, AL_TEST_TYPE_STATE,
                       AL_OWNING_EVENT_FIELD_EXTRACT);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK && ctx->move_bytes == 32u &&
           ctx->live_payload_bytes == AL_TEST_DYNAMIC_STACK_CAPACITY &&
           ctx->cursor_bytes == AL_TEST_DYNAMIC_STACK_CAPACITY);
  AL_CHECK(ctx->trace_event_count == trace_before + 1u &&
           ctx->trace_events[trace_before].kind ==
               AL_OWNING_EVENT_FIELD_EXTRACT &&
           ctx->trace_events[trace_before].offset == 0u &&
           ctx->trace_events[trace_before].extent_bytes == 32u &&
           ctx->trace_events[trace_before].payload_bytes == 32u &&
           ctx->trace_events[trace_before].source_offset == 64u &&
           ctx->trace_events[trace_before].source_extent_bytes == 32u);
  al_owning_update_live(ctx, -32, 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK &&
           ctx->live_payload_bytes == 96u);
  AL_CHECK(memcmp(ctx->stack_data, expected_destination, 32u) == 0 &&
           memcmp(ctx->stack_data + 32u, expected_gap, 32u) == 0 &&
           memcmp(ctx->stack_data + 64u, poisoned, 32u) == 0 &&
           memcmp(ctx->stack_data + 96u, expected_tail, 32u) == 0);
  AL_CHECK(memcmp(fixture.init_bitmap + 4u, gap_init_before,
                  sizeof(gap_init_before)) == 0 &&
           memcmp(fixture.poison_bitmap + 4u, gap_poison_before,
                  sizeof(gap_poison_before)) == 0);
  for (index = 8u; index < 12u; ++index)
    AL_CHECK(fixture.init_bitmap[index] == 0u &&
             fixture.poison_bitmap[index] == 0xFFu);
  for (index = 0u; index < 4u; ++index)
    AL_CHECK(fixture.init_bitmap[index] == 0xFFu &&
             fixture.poison_bitmap[index] == 0xFFu);
  for (index = 12u; index < 16u; ++index)
    AL_CHECK(fixture.init_bitmap[index] == 0xFFu &&
             fixture.poison_bitmap[index] == 0xFFu);
  for (index = 0u; index < 4u; ++index)
    AL_CHECK(al_owning_load_i64(ctx, index * 8u) == 1008 + index);
  for (index = 0u; index < 4u; ++index)
    AL_CHECK(fixture.poison_bitmap[index] == 0u);
  for (index = 4u; index < 8u; ++index)
    AL_CHECK(al_owning_load_i64(ctx, index * 8u) == 1000 + index);

  /* Disjoint right move with the same live gap and tail geometry. */
  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, AL_TEST_DYNAMIC_STACK_CAPACITY,
                                AL_TEST_DYNAMIC_ERROR_ID) == 0);
  for (index = 0u; index < 16u; ++index)
    al_owning_store_i64(ctx, index * 8u, 2000 + index, AL_TEST_TYPE_INT);
  al_owning_update_live(ctx, AL_TEST_DYNAMIC_STACK_CAPACITY, 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK &&
           ctx->live_payload_bytes == AL_TEST_DYNAMIC_STACK_CAPACITY);
  (void)memcpy(expected_destination, ctx->stack_data, 32u);
  (void)memcpy(expected_gap, ctx->stack_data + 32u, 32u);
  (void)memcpy(expected_tail, ctx->stack_data + 96u, 32u);
  (void)memcpy(gap_init_before, fixture.init_bitmap + 4u,
               sizeof(gap_init_before));
  (void)memcpy(gap_poison_before, fixture.poison_bitmap + 4u,
               sizeof(gap_poison_before));
  trace_before = ctx->trace_event_count;
  al_owning_move_range(ctx, 64u, 0u, 32u, 32u, AL_TEST_TYPE_STATE,
                       AL_OWNING_EVENT_FIELD_EXTRACT);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK && ctx->move_bytes == 32u &&
           ctx->live_payload_bytes == AL_TEST_DYNAMIC_STACK_CAPACITY &&
           ctx->cursor_bytes == AL_TEST_DYNAMIC_STACK_CAPACITY);
  AL_CHECK(ctx->trace_event_count == trace_before + 1u &&
           ctx->trace_events[trace_before].kind ==
               AL_OWNING_EVENT_FIELD_EXTRACT &&
           ctx->trace_events[trace_before].offset == 64u &&
           ctx->trace_events[trace_before].extent_bytes == 32u &&
           ctx->trace_events[trace_before].payload_bytes == 32u &&
           ctx->trace_events[trace_before].source_offset == 0u &&
           ctx->trace_events[trace_before].source_extent_bytes == 32u);
  al_owning_update_live(ctx, -32, 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK &&
           ctx->live_payload_bytes == 96u);
  AL_CHECK(memcmp(ctx->stack_data + 64u, expected_destination, 32u) == 0 &&
           memcmp(ctx->stack_data + 32u, expected_gap, 32u) == 0 &&
           memcmp(ctx->stack_data, poisoned, 32u) == 0 &&
           memcmp(ctx->stack_data + 96u, expected_tail, 32u) == 0);
  AL_CHECK(memcmp(fixture.init_bitmap + 4u, gap_init_before,
                  sizeof(gap_init_before)) == 0 &&
           memcmp(fixture.poison_bitmap + 4u, gap_poison_before,
                  sizeof(gap_poison_before)) == 0);
  for (index = 0u; index < 4u; ++index)
    AL_CHECK(fixture.init_bitmap[index] == 0u &&
             fixture.poison_bitmap[index] == 0xFFu);
  for (index = 8u; index < 12u; ++index)
    AL_CHECK(fixture.init_bitmap[index] == 0xFFu &&
             fixture.poison_bitmap[index] == 0xFFu);
  for (index = 12u; index < 16u; ++index)
    AL_CHECK(fixture.init_bitmap[index] == 0xFFu &&
             fixture.poison_bitmap[index] == 0xFFu);
  for (index = 0u; index < 4u; ++index)
    AL_CHECK(al_owning_load_i64(ctx, 64u + index * 8u) == 2000 + index);
  for (index = 8u; index < 12u; ++index)
    AL_CHECK(fixture.poison_bitmap[index] == 0u);
  for (index = 4u; index < 8u; ++index)
    AL_CHECK(al_owning_load_i64(ctx, index * 8u) == 2000 + index);
  AL_CHECK(al_test_fixture_guards_ok(&fixture));
  AL_CHECK(al_test_case_begin("dynamic_move_range_source_only_invalidation",
                              &fixture));
  return 1;
failed:
  return 0;
}

static int al_test_dynamic_return_larger_than_arguments(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  uint8_t return_range[24];
  const uint8_t text[] = {0x52u, 0x00u};
  uint32_t event_index = UINT32_MAX;
  uint32_t index;
  uint32_t string_length;
  al_test_build_string(return_range, 1u, text);
  al_test_write_u64_le(return_range, 16u, UINT64_C(0xA1B2C3D4E5F60718));
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, 56u, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  al_owning_store_token(ctx, 0u, AL_TEST_TYPE_UNIT);
  AL_CHECK(al_owning_copy_external_bounded(
               ctx, 8u, return_range, sizeof(return_range), 0u, 18u,
               sizeof(return_range), AL_TEST_TYPE_TEXT_ENVELOPE,
               AL_TEST_DYNAMIC_ERROR_ID) == 0);
  al_owning_move_range(ctx, 32u, 8u, 24u, 18u, AL_TEST_TYPE_TEXT_ENVELOPE,
                       AL_OWNING_EVENT_CALL_RETURN_MOVE);
  al_owning_move_range(ctx, 0u, 32u, 24u, 18u, AL_TEST_TYPE_TEXT_ENVELOPE,
                       AL_OWNING_EVENT_CALL_RETURN_MOVE);
  al_owning_release_to(ctx, 24u, 0u, 0u, 0u);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK && ctx->cursor_bytes == 24u &&
           ctx->move_bytes == 48u && ctx->live_payload_bytes == 18u);
  AL_CHECK(memcmp(ctx->stack_data, return_range, sizeof(return_range)) == 0);
  AL_CHECK(al_owning_string_length(ctx, 0u, 16u, AL_TEST_DYNAMIC_ERROR_ID,
                                   &string_length) == 0 &&
           string_length == 1u);
  AL_CHECK(al_owning_load_i64(ctx, 16u) ==
           (int64_t)UINT64_C(0xA1B2C3D4E5F60718));
  AL_CHECK(ctx->trace_event_count == 4u);
  for (index = 0u; index < ctx->trace_event_count; ++index) {
    if (ctx->trace_events[index].kind == AL_OWNING_EVENT_CALL_RETURN_MOVE &&
        ctx->trace_events[index].offset == 0u)
      event_index = index;
  }
  AL_CHECK(event_index != UINT32_MAX &&
           ctx->trace_events[event_index].source_offset == 32u &&
           ctx->trace_events[event_index].extent_bytes == 24u &&
           ctx->trace_events[event_index].source_extent_bytes == 24u);
  AL_CHECK(
      al_test_case_begin("dynamic_return_larger_than_arguments", &fixture));
  return 1;
failed:
  return 0;
}

static int al_test_dynamic_constant_copy(void) {
  al_test_fixture fixture;
  al_test_fixture short_fixture;
  al_owning_stack_context *ctx;
  uint8_t literal[16];
  uint8_t before[16];
  const uint8_t text[] = {0x34u, 0x12u, 0xFEu, 0xD8u};
  uint32_t length;
  al_test_build_string(literal, 2u, text);
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, sizeof(literal),
                                AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_constant(ctx, 0u, literal, sizeof(literal), 12u,
                                   sizeof(literal), AL_TEST_TYPE_STRING,
                                   AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(ctx->input_copy_bytes == 0u && ctx->deep_copy_bytes == 16u &&
           ctx->live_payload_bytes == 12u);
  AL_CHECK(memcmp(ctx->stack_data, literal, sizeof(literal)) == 0);
  AL_CHECK(al_owning_string_length(ctx, 0u, sizeof(literal),
                                   AL_TEST_DYNAMIC_ERROR_ID, &length) == 0 &&
           length == 2u);
  AL_CHECK(al_test_case_begin("dynamic_constant_copy", &fixture));

  al_test_fixture_init(&short_fixture, AL_TEST_STACK_CAPACITY);
  ctx = &short_fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, sizeof(literal),
                                AL_TEST_DYNAMIC_ERROR_ID) == 0);
  (void)memcpy(before, ctx->stack_data, sizeof(before));
  AL_CHECK(al_owning_copy_constant(ctx, 0u, literal, sizeof(literal) - 1u, 12u,
                                   sizeof(literal), AL_TEST_TYPE_STRING,
                                   AL_TEST_DYNAMIC_ERROR_ID) != 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_INTERNAL &&
           ctx->cursor_bytes == sizeof(literal) &&
           memcmp(before, ctx->stack_data, sizeof(before)) == 0);
  AL_CHECK(al_test_fixture_guards_ok(&short_fixture));
  ++al_test_cases;
  al_test_capture_metrics(&short_fixture);
  return 1;
failed:
  return 0;
}

static int al_test_dynamic_malformed_external_values(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  uint8_t malformed[16];
  uint32_t payload = UINT32_C(0xDEADBEEF);
  uint32_t extent = UINT32_C(0xC0FFEE00);
  const uint8_t text[] = {0x61u, 0x00u};
  al_test_build_string(malformed, 1u, text);
  malformed[4u] = 1u;
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_dynamic_layout, AL_TEST_LAYOUT_STRING, malformed,
               sizeof(malformed), 0u, AL_TEST_DYNAMIC_ERROR_ID, &payload,
               &extent) != 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_INVALID_REQUEST &&
           ctx->error_id == AL_TEST_DYNAMIC_ERROR_ID &&
           ctx->cursor_bytes == 0u && payload == UINT32_C(0xDEADBEEF) &&
           extent == UINT32_C(0xC0FFEE00));
  ++al_test_cases;
  al_test_capture_metrics(&fixture);

  al_test_build_string(malformed, 1u, text);
  malformed[15u] = 0x7Fu;
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_dynamic_layout, AL_TEST_LAYOUT_STRING, malformed,
               sizeof(malformed), 0u, AL_TEST_DYNAMIC_ERROR_ID, &payload,
               &extent) != 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_INVALID_REQUEST &&
           ctx->cursor_bytes == 0u);
  ++al_test_cases;
  al_test_capture_metrics(&fixture);

  al_test_write_u32_le(malformed, 0u, UINT32_MAX);
  (void)memset(malformed + 4u, 0, sizeof(malformed) - 4u);
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_dynamic_layout, AL_TEST_LAYOUT_STRING, malformed,
               8u, 0u, AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) != 0);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_INVALID_REQUEST &&
           ctx->required_bytes == UINT32_MAX && ctx->cursor_bytes == 0u);
  AL_CHECK(al_test_fixture_guards_ok(&fixture));
  ++al_test_cases;
  al_test_capture_metrics(&fixture);
  return 1;
failed:
  return 0;
}

static int al_test_closed_enum_valid_values(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  uint8_t enum_bytes[8] = {0u};
  uint8_t fixed_record[16] = {0u};
  uint8_t dynamic_record[24] = {0u};
  const uint8_t string_data[] = {0x41u, 0x00u};
  uint32_t payload;
  uint32_t extent;
  uint32_t ordinal;

  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_test_case_begin("closed_enum_valid_values", &fixture));
  for (ordinal = 0u; ordinal < 3u; ++ordinal) {
    al_test_write_u64_le(enum_bytes, 0u, ordinal);
    AL_CHECK(al_owning_measure_external_value(
                 ctx, &al_test_enum_layout, AL_TEST_ENUM_LAYOUT_TAG,
                 enum_bytes, sizeof(enum_bytes), 0u, AL_TEST_DYNAMIC_ERROR_ID,
                 &payload, &extent) == 0);
    AL_CHECK(payload == 8u && extent == 8u);
  }

  al_test_write_u64_le(fixed_record, 0u, 2u);
  al_test_write_u64_le(fixed_record, 8u, 77u);
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_enum_layout, AL_TEST_ENUM_LAYOUT_FIXED_RECORD,
               fixed_record, sizeof(fixed_record), 0u,
               AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) == 0);
  AL_CHECK(payload == 16u && extent == 16u);
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_enum_layout, AL_TEST_ENUM_LAYOUT_NESTED_FIXED,
               fixed_record, sizeof(fixed_record), 0u,
               AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) == 0);
  AL_CHECK(payload == 16u && extent == 16u);

  al_test_build_string(dynamic_record, 1u, string_data);
  al_test_write_u64_le(dynamic_record, 16u, 1u);
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_enum_layout, AL_TEST_ENUM_LAYOUT_TEXT_RECORD,
               dynamic_record, sizeof(dynamic_record), 0u,
               AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) == 0);
  AL_CHECK(payload == 18u && extent == 24u);
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_enum_layout, AL_TEST_ENUM_LAYOUT_NESTED_TEXT,
               dynamic_record, sizeof(dynamic_record), 0u,
               AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) == 0);
  AL_CHECK(payload == 18u && extent == 24u);
  return 1;
failed:
  return 0;
}

static int al_test_closed_enum_invalid_root_tags(void) {
  static const uint64_t invalid_tags[] = {3u, UINT64_MAX};
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  uint8_t enum_bytes[8];
  uint8_t retained[8];
  uint8_t retained_before[8];
  uint8_t stack_before[16];
  uint32_t payload;
  uint32_t extent;
  uint32_t index;

  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_test_case_begin("closed_enum_invalid_root_tags", &fixture));
  for (index = 0u;
       index < (uint32_t)(sizeof(invalid_tags) / sizeof(invalid_tags[0]));
       ++index) {
    al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
    ctx = &fixture.ctx;
    al_test_write_u64_le(enum_bytes, 0u, invalid_tags[index]);
    (void)memset(retained, 0x6bu, sizeof(retained));
    (void)memcpy(retained_before, retained, sizeof(retained));
    (void)memcpy(stack_before, ctx->stack_data, sizeof(stack_before));
    payload = UINT32_C(0xDEADBEEF);
    extent = UINT32_C(0xC0FFEE00);
    AL_CHECK(al_owning_measure_external_value(
                 ctx, &al_test_enum_layout, AL_TEST_ENUM_LAYOUT_TAG,
                 enum_bytes, sizeof(enum_bytes), 0u, AL_TEST_DYNAMIC_ERROR_ID,
                 &payload, &extent) != 0);
    AL_CHECK(ctx->status == AL_OWNING_STATUS_INVALID_REQUEST &&
             ctx->cursor_bytes == 0u);
    AL_CHECK(payload == UINT32_C(0xDEADBEEF) &&
             extent == UINT32_C(0xC0FFEE00));
    AL_CHECK(memcmp(ctx->stack_data, stack_before, sizeof(stack_before)) == 0);
    AL_CHECK(memcmp(retained, retained_before, sizeof(retained)) == 0);
  }
  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, 8u, AL_TEST_DYNAMIC_ERROR_ID) == 0);
  al_owning_store_i64(ctx, 0u, 3, 301u);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK);
  {
    al_owning_value_size value_size;
    AL_CHECK(al_owning_measure_value(
                 ctx, &al_test_enum_layout, AL_TEST_ENUM_LAYOUT_TAG, 0u, 8u,
                 AL_TEST_DYNAMIC_ERROR_ID, &value_size) != 0);
  }
  AL_CHECK(ctx->status == AL_OWNING_STATUS_INTERNAL &&
           ctx->error_id == AL_TEST_DYNAMIC_ERROR_ID);
  return 1;
failed:
  return 0;
}

static int al_test_closed_enum_invalid_nested_tags(void) {
  static const uint32_t type_indexes[] = {
      AL_TEST_ENUM_LAYOUT_FIXED_RECORD, AL_TEST_ENUM_LAYOUT_NESTED_FIXED,
      AL_TEST_ENUM_LAYOUT_TEXT_RECORD, AL_TEST_ENUM_LAYOUT_NESTED_TEXT};
  static const uint32_t source_lengths[] = {16u, 16u, 24u, 24u};
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  uint8_t fixed_record[16] = {0u};
  uint8_t dynamic_record[24] = {0u};
  uint8_t retained[8];
  uint8_t retained_before[8];
  uint8_t stack_before[16];
  const uint8_t string_data[] = {0x42u, 0x00u};
  const uint8_t *sources[4] = {fixed_record, fixed_record, dynamic_record,
                               dynamic_record};
  uint32_t payload;
  uint32_t extent;
  uint32_t index;

  al_test_write_u64_le(fixed_record, 0u, 3u);
  al_test_write_u64_le(fixed_record, 8u, 77u);
  al_test_build_string(dynamic_record, 1u, string_data);
  al_test_write_u64_le(dynamic_record, 16u, 3u);
  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_test_case_begin("closed_enum_invalid_nested_tags", &fixture));
  for (index = 0u; index < 4u; ++index) {
    al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
    ctx = &fixture.ctx;
    (void)memset(retained, 0x39u, sizeof(retained));
    (void)memcpy(retained_before, retained, sizeof(retained));
    (void)memcpy(stack_before, ctx->stack_data, sizeof(stack_before));
    payload = UINT32_C(0xDEADBEEF);
    extent = UINT32_C(0xC0FFEE00);
    AL_CHECK(al_owning_measure_external_value(
                 ctx, &al_test_enum_layout, type_indexes[index], sources[index],
                 source_lengths[index], 0u, AL_TEST_DYNAMIC_ERROR_ID, &payload,
                 &extent) != 0);
    AL_CHECK(ctx->status == AL_OWNING_STATUS_INVALID_REQUEST &&
             ctx->cursor_bytes == 0u);
    AL_CHECK(payload == UINT32_C(0xDEADBEEF) &&
             extent == UINT32_C(0xC0FFEE00));
    AL_CHECK(memcmp(ctx->stack_data, stack_before, sizeof(stack_before)) == 0);
    AL_CHECK(memcmp(retained, retained_before, sizeof(retained)) == 0);
  }
  return 1;
failed:
  return 0;
}

static int al_test_short_external_value_rejected(
    const al_owning_layout *layout, uint32_t type_index,
    const uint8_t *source, uint32_t source_length) {
  al_test_fixture fixture;
  uint8_t stack_before[16];
  uint32_t payload = UINT32_C(0xDEADBEEF);
  uint32_t extent = UINT32_C(0xC0FFEE00);
  int32_t result;
  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  (void)memset(fixture.ctx.stack_data, 0xA7, sizeof(stack_before));
  (void)memcpy(stack_before, fixture.ctx.stack_data, sizeof(stack_before));
  result = al_owning_measure_external_value(
      &fixture.ctx, layout, type_index, source, source_length,
      0u, AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent);
  return result != 0 &&
         fixture.ctx.status == AL_OWNING_STATUS_INVALID_REQUEST &&
         fixture.ctx.cursor_bytes == 0u &&
         payload == UINT32_C(0xDEADBEEF) &&
         extent == UINT32_C(0xC0FFEE00) &&
         memcmp(fixture.ctx.stack_data, stack_before, sizeof(stack_before)) ==
             0;
}

static int al_test_short_internal_enum_rejected(void) {
  al_test_fixture fixture;
  al_owning_value_size value_size = {UINT32_C(0xDEADBEEF),
                                     UINT32_C(0xC0FFEE00)};
  int32_t result;
  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  if (al_owning_reserve_to(&fixture.ctx, 8u, AL_TEST_DYNAMIC_ERROR_ID) != 0)
    return 0;
  al_owning_store_i64(&fixture.ctx, 0u, 0, 301u);
  if (fixture.ctx.status != AL_OWNING_STATUS_OK)
    return 0;
  result = al_owning_measure_value(
      &fixture.ctx, &al_test_enum_layout, AL_TEST_ENUM_LAYOUT_TAG, 0u, 4u,
      AL_TEST_DYNAMIC_ERROR_ID, &value_size);
  return result != 0 && fixture.ctx.status == AL_OWNING_STATUS_INTERNAL &&
         fixture.ctx.cursor_bytes == 8u &&
         value_size.payload_bytes == UINT32_C(0xDEADBEEF) &&
         value_size.extent_bytes == UINT32_C(0xC0FFEE00);
}

static int al_test_closed_enum_short_extents(void) {
  al_test_fixture fixture;
  uint8_t short_enum[8] = {0u};
  uint8_t short_scalar[8] = {0u};
  uint8_t short_empty_record[8] = {0u};
  uint8_t dynamic_record[32] = {0u};
  const uint8_t string_data[] = {0x43u, 0x00u};
  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  AL_CHECK(al_test_case_begin("closed_enum_short_extents", &fixture));

  AL_CHECK(al_test_short_external_value_rejected(
      &al_test_enum_layout, AL_TEST_ENUM_LAYOUT_TAG, short_enum, 4u));
  AL_CHECK(al_test_short_external_value_rejected(
      &al_test_enum_layout, AL_TEST_ENUM_LAYOUT_INT, short_scalar, 4u));

  al_test_build_string(dynamic_record, 1u, string_data);
  al_test_write_u64_le(dynamic_record, 16u, 1u);
  AL_CHECK(al_test_short_external_value_rejected(
      &al_test_enum_layout, AL_TEST_ENUM_LAYOUT_TEXT_RECORD, dynamic_record,
      20u));

  al_test_write_u64_le(dynamic_record, 24u, 99u);
  AL_CHECK(al_test_short_external_value_rejected(
      &al_test_enum_layout, AL_TEST_ENUM_LAYOUT_DYNAMIC_TAIL_RECORD,
      dynamic_record, 24u));
  AL_CHECK(al_test_short_external_value_rejected(
      &al_test_dynamic_layout, AL_TEST_LAYOUT_EMPTY, short_empty_record,
      4u));
  AL_CHECK(al_test_short_internal_enum_rejected());
  return 1;
failed:
  return 0;
}

static int al_test_enum_descriptor_rejected(
    const al_owning_type_descriptor *types, uint32_t type_index) {
  al_test_fixture fixture;
  al_owning_layout layout = al_test_enum_layout;
  uint8_t enum_bytes[8] = {0u};
  uint32_t payload;
  uint32_t extent;
  layout.types = types;
  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  return al_owning_measure_external_value(
             &fixture.ctx, &layout, type_index, enum_bytes,
             sizeof(enum_bytes), 0u, AL_TEST_DYNAMIC_ERROR_ID, &payload,
             &extent) != 0 &&
         fixture.ctx.status == AL_OWNING_STATUS_INTERNAL;
}

static int al_test_closed_enum_descriptor_validation(void) {
  al_test_fixture fixture;
  al_owning_type_descriptor types[AL_TEST_ENUM_LAYOUT_TYPE_COUNT];
  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  AL_CHECK(al_test_case_begin("closed_enum_descriptor_validation", &fixture));

  (void)memcpy(types, al_test_enum_types, sizeof(types));
  types[AL_TEST_ENUM_LAYOUT_TAG].case_count = 0u;
  AL_CHECK(al_test_enum_descriptor_rejected(types, AL_TEST_ENUM_LAYOUT_TAG));
  (void)memcpy(types, al_test_enum_types, sizeof(types));
  types[AL_TEST_ENUM_LAYOUT_INT].case_count = 1u;
  AL_CHECK(al_test_enum_descriptor_rejected(types, AL_TEST_ENUM_LAYOUT_INT));
  (void)memcpy(types, al_test_enum_types, sizeof(types));
  types[AL_TEST_ENUM_LAYOUT_TAG].fixed_payload_bytes = 16u;
  types[AL_TEST_ENUM_LAYOUT_TAG].fixed_extent_bytes = 16u;
  types[AL_TEST_ENUM_LAYOUT_TAG].minimum_payload_bytes = 16u;
  types[AL_TEST_ENUM_LAYOUT_TAG].minimum_extent_bytes = 16u;
  AL_CHECK(al_test_enum_descriptor_rejected(types, AL_TEST_ENUM_LAYOUT_TAG));
  (void)memcpy(types, al_test_enum_types, sizeof(types));
  types[AL_TEST_ENUM_LAYOUT_TAG].field_count = 1u;
  AL_CHECK(al_test_enum_descriptor_rejected(types, AL_TEST_ENUM_LAYOUT_TAG));
  return 1;
failed:
  return 0;
}

static int al_test_closed_enum_abi2_rejected(void) {
  al_test_fixture fixture;
  al_owning_layout stale_layout = al_test_enum_layout;
  uint8_t enum_bytes[8] = {0u};
  uint32_t payload;
  uint32_t extent;
  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  AL_CHECK(al_test_case_begin("closed_enum_abi2_rejected", &fixture));
  stale_layout.abi_version = 2u;
  AL_CHECK(al_owning_measure_external_value(
               &fixture.ctx, &stale_layout, AL_TEST_ENUM_LAYOUT_TAG,
               enum_bytes, sizeof(enum_bytes), 0u, AL_TEST_DYNAMIC_ERROR_ID,
               &payload, &extent) != 0 &&
           fixture.ctx.status == AL_OWNING_STATUS_INTERNAL &&
           fixture.ctx.cursor_bytes == 0u);
  return 1;
failed:
  return 0;
}

static int al_test_sum_external_rejected(const al_owning_layout *layout,
                                        uint32_t type_index,
                                        const uint8_t *bytes,
                                        uint32_t byte_count) {
  al_test_fixture fixture;
  uint32_t payload = UINT32_C(0xAABBCCDD);
  uint32_t extent = UINT32_C(0x11223344);
  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  return al_owning_measure_external_value(
             &fixture.ctx, layout, type_index, bytes, byte_count, 0u,
             AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) != 0 &&
         fixture.ctx.status != AL_OWNING_STATUS_OK &&
         payload == UINT32_C(0xAABBCCDD) && extent == UINT32_C(0x11223344);
}

static int al_test_bool_external_rejected(uint32_t type_index,
                                          const uint8_t *bytes,
                                          uint32_t byte_count,
                                          uint32_t source_offset) {
  al_test_fixture fixture;
  uint8_t before[sizeof(fixture.raw)];
  uint32_t payload = UINT32_C(0xAABBCCDD);
  uint32_t extent = UINT32_C(0x11223344);
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  (void)memcpy(before, fixture.raw, sizeof(before));
  return al_owning_measure_external_value(
             &fixture.ctx, &al_test_bool_layout, type_index, bytes,
             byte_count, source_offset, AL_TEST_BOOL_ERROR_ID, &payload,
             &extent) != 0 &&
         fixture.ctx.status == AL_OWNING_STATUS_INVALID_REQUEST &&
         fixture.ctx.error_id == AL_TEST_BOOL_ERROR_ID &&
         fixture.ctx.cursor_bytes == 0u &&
         payload == UINT32_C(0xAABBCCDD) &&
         extent == UINT32_C(0x11223344) &&
         memcmp(before, fixture.raw, sizeof(before)) == 0 &&
         al_test_fixture_guards_ok(&fixture);
}

static int al_test_bool_external_valid(uint32_t type_index,
                                       const uint8_t *bytes,
                                       uint32_t byte_count,
                                       uint32_t expected_payload,
                                       uint32_t expected_extent) {
  al_test_fixture fixture;
  uint32_t payload = UINT32_C(0xAABBCCDD);
  uint32_t extent = UINT32_C(0x11223344);
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  return al_owning_measure_external_value(
             &fixture.ctx, &al_test_bool_layout, type_index, bytes,
             byte_count, 0u, AL_TEST_BOOL_ERROR_ID, &payload, &extent) == 0 &&
         fixture.ctx.status == AL_OWNING_STATUS_OK &&
         fixture.ctx.cursor_bytes == 0u && payload == expected_payload &&
         extent == expected_extent && al_test_fixture_guards_ok(&fixture);
}

static int al_test_bool_stack_rejected(const uint8_t bytes[8]) {
  al_test_fixture fixture;
  uint8_t before[8];
  al_owning_value_size measured = {UINT32_C(0xAABBCCDD),
                                   UINT32_C(0x11223344)};
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  if (al_owning_reserve_to(&fixture.ctx, 8u, AL_TEST_BOOL_ERROR_ID) != 0)
    return 0;
  al_owning_copy_external(&fixture.ctx, 0u, bytes, 8u, 8u, 601u);
  if (fixture.ctx.status != AL_OWNING_STATUS_OK)
    return 0;
  (void)memcpy(before, fixture.ctx.stack_data, sizeof(before));
  return al_owning_measure_value(
             &fixture.ctx, &al_test_bool_layout, AL_TEST_BOOL_LAYOUT_BOOL,
             0u, 8u, AL_TEST_BOOL_ERROR_ID, &measured) != 0 &&
         fixture.ctx.status == AL_OWNING_STATUS_INTERNAL &&
         fixture.ctx.cursor_bytes == 8u &&
         measured.payload_bytes == UINT32_C(0xAABBCCDD) &&
         measured.extent_bytes == UINT32_C(0x11223344) &&
         memcmp(before, fixture.ctx.stack_data, sizeof(before)) == 0 &&
         al_test_fixture_guards_ok(&fixture);
}

static int al_test_bool_stack_short_owner_rejected(const uint8_t bytes[8],
                                                   uint32_t owner_end) {
  al_test_fixture fixture;
  uint8_t before[8];
  al_owning_value_size measured = {UINT32_C(0xAABBCCDD),
                                   UINT32_C(0x11223344)};
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  if (al_owning_reserve_to(&fixture.ctx, 8u, AL_TEST_BOOL_ERROR_ID) != 0)
    return 0;
  al_owning_copy_external(&fixture.ctx, 0u, bytes, 8u, 8u, 601u);
  if (fixture.ctx.status != AL_OWNING_STATUS_OK)
    return 0;
  (void)memcpy(before, fixture.ctx.stack_data, sizeof(before));
  return al_owning_measure_value(
             &fixture.ctx, &al_test_bool_layout, AL_TEST_BOOL_LAYOUT_BOOL,
             0u, owner_end, AL_TEST_BOOL_ERROR_ID, &measured) != 0 &&
         fixture.ctx.status == AL_OWNING_STATUS_INTERNAL &&
         fixture.ctx.cursor_bytes == 8u &&
         measured.payload_bytes == UINT32_C(0xAABBCCDD) &&
         measured.extent_bytes == UINT32_C(0x11223344) &&
         memcmp(before, fixture.ctx.stack_data, sizeof(before)) == 0 &&
         al_test_fixture_guards_ok(&fixture);
}

static int al_test_bool_descriptor_rejected(
    const al_owning_type_descriptor *types, uint32_t bool_type_index) {
  al_owning_layout layout = al_test_bool_layout;
  al_test_fixture fixture;
  uint8_t bytes[8] = {0u};
  uint32_t payload = UINT32_C(0xAABBCCDD);
  uint32_t extent = UINT32_C(0x11223344);
  layout.types = types;
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  return al_owning_measure_external_value(
             &fixture.ctx, &layout, bool_type_index, bytes,
             sizeof(bytes), 0u, AL_TEST_BOOL_ERROR_ID, &payload, &extent) !=
             0 &&
         fixture.ctx.status == AL_OWNING_STATUS_INTERNAL &&
         payload == UINT32_C(0xAABBCCDD) &&
         extent == UINT32_C(0x11223344);
}

static int al_test_bool_canonical_scanner(void) {
  al_test_fixture fixture;
  al_owning_type_descriptor bad_types[AL_TEST_BOOL_LAYOUT_TYPE_COUNT];
  uint8_t bytes[32] = {0u};
  uint8_t short_source[8] = {0u};
  uint8_t aligned_short_source[15] = {0u};
  uint8_t malformed_source[32];
  const uint64_t bad_values[] = {
      UINT64_C(2), UINT64_C(0x0100000000000000),
      UINT64_C(0x8000000000000000), UINT64_MAX};
  uint32_t index;

  for (index = 0u; index < 2u; ++index) {
    al_test_write_u64_le(bytes, 0u, index);
    AL_CHECK(al_test_bool_external_valid(AL_TEST_BOOL_LAYOUT_BOOL, bytes,
                                         8u, 8u, 8u));
    AL_CHECK(al_test_bool_external_valid(AL_TEST_BOOL_LAYOUT_NOMINAL, bytes,
                                         8u, 8u, 8u));
  }

  for (index = 0u;
       index < sizeof(bad_values) / sizeof(bad_values[0]); ++index) {
    al_test_write_u64_le(bytes, 0u, bad_values[index]);
    AL_CHECK(al_test_bool_external_rejected(AL_TEST_BOOL_LAYOUT_BOOL, bytes,
                                            8u, 0u));
    AL_CHECK(al_test_bool_external_rejected(AL_TEST_BOOL_LAYOUT_NOMINAL,
                                            bytes, 8u, 0u));
    AL_CHECK(al_test_bool_stack_rejected(bytes));
  }

  al_test_write_u64_le(short_source, 0u, 1u);
  for (index = 0u; index < 8u; ++index) {
    AL_CHECK(al_test_bool_external_rejected(AL_TEST_BOOL_LAYOUT_BOOL,
                                            short_source, index, 0u));
    AL_CHECK(al_test_bool_stack_short_owner_rejected(short_source, index));
  }
  AL_CHECK(al_test_bool_external_rejected(AL_TEST_BOOL_LAYOUT_BOOL,
                                          short_source, sizeof(short_source),
                                          1u));
  al_test_write_u64_le(aligned_short_source, 8u, 1u);
  AL_CHECK(al_test_bool_external_rejected(AL_TEST_BOOL_LAYOUT_BOOL,
                                          aligned_short_source,
                                          sizeof(aligned_short_source), 8u));

  al_test_write_u64_le(bytes, 0u, bad_values[0]);
  AL_CHECK(al_test_bool_external_rejected(AL_TEST_BOOL_LAYOUT_RECORD, bytes,
                                          8u, 0u));
  AL_CHECK(al_test_bool_external_rejected(
      AL_TEST_BOOL_LAYOUT_NOMINAL_RECORD, bytes, 8u, 0u));
  al_test_write_u64_le(bytes, 0u, 0u);
  al_test_write_u64_le(bytes, 8u, bad_values[0]);
  AL_CHECK(al_test_bool_external_rejected(AL_TEST_BOOL_LAYOUT_OPTION, bytes,
                                          16u, 0u));
  AL_CHECK(al_test_bool_external_rejected(AL_TEST_BOOL_LAYOUT_OPTION, bytes,
                                          15u, 0u));
  AL_CHECK(al_test_bool_external_rejected(
      AL_TEST_BOOL_LAYOUT_RESULT_BOOL_STRING, bytes, 16u, 0u));
  al_test_write_u64_le(bytes, 0u, 1u);
  AL_CHECK(al_test_bool_external_rejected(
      AL_TEST_BOOL_LAYOUT_RESULT_STRING_BOOL, bytes, 16u, 0u));

  /* A None tag leaves its inactive payload unexamined. */
  al_test_write_u64_le(bytes, 0u, 1u);
  al_test_write_u64_le(bytes, 8u, bad_values[0]);
  AL_CHECK(al_test_bool_external_valid(AL_TEST_BOOL_LAYOUT_OPTION, bytes,
                                       16u, 8u, 8u));

  /* A selected String alternative may begin with 2, although that bit pattern
   * would be an invalid Bool in the overlapping inactive alternative. */
  (void)memset(bytes, 0, sizeof(bytes));
  al_test_write_u64_le(bytes, 0u, 1u);
  al_test_write_u32_le(bytes, 8u, 2u);
  bytes[16u] = 0x6fu;
  bytes[18u] = 0x6bu;
  (void)memcpy(malformed_source, bytes, sizeof(bytes));
  AL_CHECK(al_test_bool_external_valid(
      AL_TEST_BOOL_LAYOUT_RESULT_BOOL_STRING, bytes, 24u, 20u, 24u));
  al_test_write_u64_le(bytes, 0u, 0u);
  AL_CHECK(al_test_bool_external_valid(
      AL_TEST_BOOL_LAYOUT_RESULT_STRING_BOOL, bytes, 24u, 20u, 24u));
  AL_CHECK(memcmp(bytes + 8u, malformed_source + 8u, 16u) == 0);

  for (index = 0u; index < 2u; ++index) {
    const uint32_t bool_type_index = index == 0u
                                         ? AL_TEST_BOOL_LAYOUT_BOOL
                                         : AL_TEST_BOOL_LAYOUT_NOMINAL;
    uint32_t size_field;
    for (size_field = 0u; size_field < 4u; ++size_field) {
      (void)memcpy(bad_types, al_test_bool_types, sizeof(bad_types));
      switch (size_field) {
      case 0u:
        bad_types[bool_type_index].fixed_payload_bytes = 16u;
        break;
      case 1u:
        bad_types[bool_type_index].fixed_extent_bytes = 16u;
        break;
      case 2u:
        bad_types[bool_type_index].minimum_payload_bytes = 16u;
        break;
      default:
        bad_types[bool_type_index].minimum_extent_bytes = 16u;
        break;
      }
      AL_CHECK(al_test_bool_descriptor_rejected(bad_types, bool_type_index));
    }
    (void)memcpy(bad_types, al_test_bool_types, sizeof(bad_types));
    bad_types[bool_type_index].field_count = 1u;
    AL_CHECK(al_test_bool_descriptor_rejected(bad_types, bool_type_index));
  }

  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  AL_CHECK(al_test_fixture_guards_ok(&fixture));
  ++al_test_cases;
  return 1;
failed:
  return 0;
}

static int al_test_sum_descriptor_rejected(
    const al_owning_type_descriptor *types,
    const al_owning_field_descriptor *fields, uint32_t type_index,
    const uint8_t *bytes, uint32_t byte_count) {
  al_test_fixture fixture;
  al_owning_layout layout = al_test_sum_layout;
  uint32_t payload = 0u;
  uint32_t extent = 0u;
  layout.types = types;
  layout.fields = fields;
  al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
  return al_owning_measure_external_value(
             &fixture.ctx, &layout, type_index, bytes, byte_count, 0u,
             AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) != 0 &&
         fixture.ctx.status == AL_OWNING_STATUS_INTERNAL;
}

static int al_test_option_result_layout_and_scanner(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx;
  al_owning_value_size measured = {UINT32_C(0xAABBCCDD),
                                   UINT32_C(0x11223344)};
  al_owning_field_location location = {UINT32_C(0xAABBCCDD),
                                       UINT32_C(0x11223344),
                                       UINT32_C(0x55667788)};
  al_owning_field_location unchanged_location = location;
  al_owning_type_descriptor bad_types[AL_TEST_SUM_LAYOUT_TYPE_COUNT];
  al_owning_field_descriptor bad_fields[
      sizeof(al_test_sum_fields) / sizeof(al_test_sum_fields[0])];
  uint8_t none_int[8] = {0u};
  uint8_t none_with_payload[16] = {0u};
  uint8_t some_int[16] = {0u};
  uint8_t ok_int[16] = {0u};
  uint8_t error_string[24] = {0u};
  uint8_t error_empty_string[16] = {0u};
  uint8_t some_empty[16] = {0u};
  uint8_t result_empty[16] = {0u};
  uint8_t nested_string[32] = {0u};
  uint8_t option_string_record[32] = {0u};
  uint8_t malformed[32] = {0u};
  uint8_t original_stack[AL_TEST_STACK_CAPACITY];
  uint32_t payload = UINT32_C(0xAABBCCDD);
  uint32_t extent = UINT32_C(0x11223344);
  uint32_t case_index = UINT32_C(0x55667788);
  uint64_t copy_bytes_before;

  al_test_write_u64_le(some_int, 0u, 0u);
  al_test_write_u64_le(none_int, 0u, 1u);
  al_test_write_u64_le(none_with_payload, 0u, 1u);
  al_test_write_u64_le(none_with_payload, 8u,
                       UINT64_C(0x2122232425262728));
  al_test_write_u64_le(some_int, 8u, UINT64_C(0x0102030405060708));
  al_test_write_u64_le(ok_int, 0u, 0u);
  al_test_write_u64_le(ok_int, 8u, UINT64_C(0x1112131415161718));
  al_test_write_u64_le(error_string, 0u, 1u);
  al_test_write_u32_le(error_string, 8u, 1u);
  error_string[16u] = 0x41u;
  al_test_write_u64_le(error_empty_string, 0u, 1u);
  al_test_write_u64_le(some_empty, 0u, 0u);
  al_test_write_u64_le(result_empty, 0u, 0u);
  al_test_write_u64_le(nested_string, 0u, 0u);
  al_test_write_u64_le(nested_string, 8u, 1u);
  al_test_write_u32_le(nested_string, 16u, 1u);
  nested_string[24u] = 0x5au;
  al_test_write_u64_le(option_string_record, 0u, 0u);
  al_test_write_u32_le(option_string_record, 8u, 1u);
  option_string_record[16u] = 0x41u;
  al_test_write_u64_le(option_string_record, 24u,
                       UINT64_C(0x8877665544332211));

  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_sum_layout, AL_TEST_SUM_OPTION_INT, none_int,
               sizeof(none_int), 0u, AL_TEST_DYNAMIC_ERROR_ID, &payload,
               &extent) == 0 &&
           payload == 8u && extent == 8u);
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_sum_layout, AL_TEST_SUM_OPTION_INT, some_int,
               sizeof(some_int), 0u, AL_TEST_DYNAMIC_ERROR_ID, &payload,
               &extent) == 0 &&
           payload == 16u && extent == 16u);
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_sum_layout, AL_TEST_SUM_RESULT_INT_INT, ok_int,
               sizeof(ok_int), 0u, AL_TEST_DYNAMIC_ERROR_ID, &payload,
               &extent) == 0 &&
           payload == 16u && extent == 16u);
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_sum_layout, AL_TEST_SUM_RESULT_INT_STRING,
               error_string, sizeof(error_string), 0u,
               AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) == 0 &&
           payload == 18u && extent == 24u);
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_sum_layout, AL_TEST_SUM_RESULT_INT_STRING,
               error_empty_string, sizeof(error_empty_string), 0u,
               AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) == 0 &&
           payload == 16u && extent == 16u);
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_sum_layout, AL_TEST_SUM_OPTION_EMPTY, some_empty,
               sizeof(some_empty), 0u, AL_TEST_DYNAMIC_ERROR_ID, &payload,
               &extent) == 0 &&
           payload == 8u && extent == 16u);
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_sum_layout, AL_TEST_SUM_RESULT_EMPTY_EMPTY,
               result_empty, sizeof(result_empty), 0u,
               AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) == 0 &&
           payload == 8u && extent == 16u);
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_sum_layout, AL_TEST_SUM_OPTION_NESTED_RESULT,
               nested_string, sizeof(nested_string), 0u,
               AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) == 0 &&
           payload == 26u && extent == 32u);
  AL_CHECK(al_owning_measure_external_value(
               ctx, &al_test_sum_layout, AL_TEST_SUM_OPTION_STRING_RECORD,
               option_string_record, sizeof(option_string_record), 0u,
               AL_TEST_DYNAMIC_ERROR_ID, &payload, &extent) == 0 &&
           payload == 26u && extent == 32u);

  AL_CHECK(al_owning_reserve_to(ctx, sizeof(error_string),
                                AL_TEST_DYNAMIC_ERROR_ID) == 0);
  al_owning_copy_external(ctx, 0u, error_string, 18u, sizeof(error_string),
                          406u);
  AL_CHECK(ctx->status == AL_OWNING_STATUS_OK &&
           al_owning_measure_value(ctx, &al_test_sum_layout,
                                   AL_TEST_SUM_RESULT_INT_STRING, 0u,
                                   sizeof(error_string),
                                   AL_TEST_DYNAMIC_ERROR_ID, &measured) == 0 &&
           measured.payload_bytes == 18u && measured.extent_bytes == 24u);
  (void)memcpy(original_stack, ctx->stack_data, sizeof(error_string));
  copy_bytes_before = ctx->deep_copy_bytes;
  AL_CHECK(al_owning_locate_sum_case(
               ctx, &al_test_sum_layout, AL_TEST_SUM_RESULT_INT_STRING, 0u,
               sizeof(error_string), AL_TEST_DYNAMIC_ERROR_ID, &case_index,
               &location) == 0 &&
           case_index == 1u && location.offset_bytes == 8u &&
           location.payload_bytes == 10u && location.extent_bytes == 16u);
  AL_CHECK(ctx->deep_copy_bytes == copy_bytes_before &&
           memcmp(original_stack, ctx->stack_data, sizeof(error_string)) ==
               0);
  ctx->stack_data[0] = 2u;
  case_index = UINT32_C(0x55667788);
  location = unchanged_location;
  AL_CHECK(al_owning_locate_sum_case(
               ctx, &al_test_sum_layout, AL_TEST_SUM_RESULT_INT_STRING, 0u,
               sizeof(error_string), AL_TEST_DYNAMIC_ERROR_ID, &case_index,
               &location) != 0 &&
           case_index == UINT32_C(0x55667788) &&
           memcmp(&location, &unchanged_location, sizeof(location)) == 0);
  /* Use a fresh fixture for the record with a dynamic sum before its tail. */
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, sizeof(option_string_record),
                                AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_copy_external_bounded(
               ctx, 0u, option_string_record, sizeof(option_string_record),
               0u, 26u, sizeof(option_string_record), 411u,
               AL_TEST_DYNAMIC_ERROR_ID) == 0);
  AL_CHECK(al_owning_locate_field(
               ctx, &al_test_sum_layout, AL_TEST_SUM_OPTION_STRING_RECORD,
               0u, sizeof(option_string_record), 1u,
               AL_TEST_DYNAMIC_ERROR_ID, &location) == 0 &&
           location.offset_bytes == 24u && location.payload_bytes == 8u &&
           location.extent_bytes == 8u);
  AL_CHECK(al_owning_locate_field(
               ctx, &al_test_sum_layout, AL_TEST_SUM_OPTION_STRING_RECORD,
               0u, sizeof(option_string_record), 0u,
               AL_TEST_DYNAMIC_ERROR_ID, &location) == 0 &&
           location.offset_bytes == 0u && location.payload_bytes == 18u &&
           location.extent_bytes == 24u);
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  ctx = &fixture.ctx;
  AL_CHECK(al_owning_reserve_to(ctx, sizeof(none_int),
                                AL_TEST_DYNAMIC_ERROR_ID) == 0);
  al_owning_copy_external(ctx, 0u, none_int, 8u, sizeof(none_int), 404u);
  AL_CHECK(al_owning_locate_sum_case(
               ctx, &al_test_sum_layout, AL_TEST_SUM_OPTION_INT, 0u,
               sizeof(none_int), AL_TEST_DYNAMIC_ERROR_ID, &case_index,
               &location) == 0 &&
           case_index == 1u && location.offset_bytes == 8u &&
           location.payload_bytes == 0u && location.extent_bytes == 0u);

  al_test_write_u64_le(malformed, 0u, 2u);
  AL_CHECK(al_test_sum_external_rejected(&al_test_sum_layout,
                                        AL_TEST_SUM_OPTION_INT, malformed,
                                        8u));
  AL_CHECK(al_test_sum_external_rejected(&al_test_sum_layout,
                                        AL_TEST_SUM_OPTION_INT, malformed,
                                        7u));
  al_test_write_u64_le(malformed, 0u, 0u);
  AL_CHECK(al_test_sum_external_rejected(&al_test_sum_layout,
                                        AL_TEST_SUM_OPTION_INT, malformed,
                                        8u));
  (void)memcpy(malformed, error_string, sizeof(error_string));
  malformed[18u] = 0x7fu;
  AL_CHECK(al_test_sum_external_rejected(&al_test_sum_layout,
                                        AL_TEST_SUM_RESULT_INT_STRING,
                                        malformed, sizeof(error_string)));
  (void)memcpy(malformed, nested_string, sizeof(nested_string));
  al_test_write_u64_le(malformed, 8u, 2u);
  AL_CHECK(al_test_sum_external_rejected(
      &al_test_sum_layout, AL_TEST_SUM_OPTION_NESTED_RESULT, malformed,
      sizeof(nested_string)));

  (void)memcpy(bad_types, al_test_sum_types, sizeof(bad_types));
  bad_types[AL_TEST_SUM_OPTION_INT].case_count = 1u;
  AL_CHECK(al_test_sum_descriptor_rejected(
      bad_types, al_test_sum_fields, AL_TEST_SUM_OPTION_INT, some_int,
      sizeof(some_int)));
  (void)memcpy(bad_types, al_test_sum_types, sizeof(bad_types));
  bad_types[AL_TEST_SUM_OPTION_INT].field_count = 1u;
  AL_CHECK(al_test_sum_descriptor_rejected(
      bad_types, al_test_sum_fields, AL_TEST_SUM_OPTION_INT, some_int,
      sizeof(some_int)));
  (void)memcpy(bad_fields, al_test_sum_fields, sizeof(bad_fields));
  bad_fields[0].fixed_offset_bytes = 16u;
  AL_CHECK(al_test_sum_descriptor_rejected(
      al_test_sum_types, bad_fields, AL_TEST_SUM_OPTION_INT, some_int,
      sizeof(some_int)));
  (void)memcpy(bad_fields, al_test_sum_fields, sizeof(bad_fields));
  bad_fields[0].flags = AL_OWNING_FIELD_ZERO_WIDTH;
  AL_CHECK(al_test_sum_descriptor_rejected(
      al_test_sum_types, bad_fields, AL_TEST_SUM_OPTION_INT, some_int,
      sizeof(some_int)));
  (void)memcpy(bad_fields, al_test_sum_fields, sizeof(bad_fields));
  bad_fields[0].reserved = 1u;
  AL_CHECK(al_test_sum_descriptor_rejected(
      al_test_sum_types, bad_fields, AL_TEST_SUM_OPTION_INT, some_int,
      sizeof(some_int)));
  (void)memcpy(bad_fields, al_test_sum_fields, sizeof(bad_fields));
  bad_fields[0].child_type_index = AL_OWNING_LAYOUT_DYNAMIC_U32;
  AL_CHECK(al_test_sum_descriptor_rejected(
      al_test_sum_types, bad_fields, AL_TEST_SUM_OPTION_INT, none_int,
      sizeof(none_int)));
  (void)memcpy(bad_fields, al_test_sum_fields, sizeof(bad_fields));
  bad_fields[1].child_type_index = AL_TEST_SUM_INT;
  AL_CHECK(al_test_sum_descriptor_rejected(
      al_test_sum_types, bad_fields, AL_TEST_SUM_OPTION_INT, none_int,
      sizeof(none_int)));
  (void)memcpy(bad_fields, al_test_sum_fields, sizeof(bad_fields));
  bad_fields[3].child_type_index = AL_OWNING_LAYOUT_DYNAMIC_U32;
  AL_CHECK(al_test_sum_descriptor_rejected(
      al_test_sum_types, bad_fields, AL_TEST_SUM_RESULT_INT_INT, ok_int,
      sizeof(ok_int)));
  (void)memcpy(bad_fields, al_test_sum_fields, sizeof(bad_fields));
  bad_fields[5].child_type_index = AL_TEST_SUM_LAYOUT_TYPE_COUNT;
  AL_CHECK(al_test_sum_descriptor_rejected(
      al_test_sum_types, bad_fields, AL_TEST_SUM_RESULT_INT_STRING, ok_int,
      sizeof(ok_int)));
  (void)memcpy(bad_types, al_test_sum_types, sizeof(bad_types));
  (void)memcpy(bad_fields, al_test_sum_fields, sizeof(bad_fields));
  bad_fields[0].child_type_index = AL_TEST_SUM_OPTION_INT;
  AL_CHECK(al_test_sum_descriptor_rejected(
      bad_types, bad_fields, AL_TEST_SUM_OPTION_INT, some_int,
      sizeof(some_int)));

  /* A None row is always absent; coherent metadata cannot turn it into a
   * second payload-bearing case. */
  (void)memcpy(bad_types, al_test_sum_types, sizeof(bad_types));
  (void)memcpy(bad_fields, al_test_sum_fields, sizeof(bad_fields));
  bad_fields[1].child_type_index = AL_TEST_SUM_INT;
  bad_types[AL_TEST_SUM_OPTION_INT].fixed_payload_bytes = 16u;
  bad_types[AL_TEST_SUM_OPTION_INT].fixed_extent_bytes = 16u;
  bad_types[AL_TEST_SUM_OPTION_INT].minimum_payload_bytes = 16u;
  bad_types[AL_TEST_SUM_OPTION_INT].minimum_extent_bytes = 16u;
  AL_CHECK(al_test_sum_descriptor_rejected(
      bad_types, bad_fields, AL_TEST_SUM_OPTION_INT, none_with_payload,
      sizeof(none_with_payload)));

  {
    al_owning_layout bad_layout = al_test_sum_layout;
    bad_layout.types = bad_types;
    bad_layout.fields = bad_fields;
    al_test_fixture_init(&fixture, AL_TEST_DYNAMIC_STACK_CAPACITY);
    ctx = &fixture.ctx;
    AL_CHECK(al_owning_reserve_to(ctx, sizeof(none_with_payload),
                                  AL_TEST_DYNAMIC_ERROR_ID) == 0);
    (void)memcpy(ctx->stack_data, none_with_payload,
                 sizeof(none_with_payload));
    case_index = UINT32_C(0x55667788);
    location = unchanged_location;
    AL_CHECK(al_owning_locate_sum_case(
                 ctx, &bad_layout, AL_TEST_SUM_OPTION_INT, 0u,
                 sizeof(none_with_payload), AL_TEST_DYNAMIC_ERROR_ID,
                 &case_index, &location) != 0 &&
             case_index == UINT32_C(0x55667788) &&
             memcmp(&location, &unchanged_location, sizeof(location)) == 0);
  }

  AL_CHECK(unchanged_location.offset_bytes == UINT32_C(0xAABBCCDD) &&
           unchanged_location.payload_bytes == UINT32_C(0x11223344) &&
           unchanged_location.extent_bytes == UINT32_C(0x55667788));
  al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
  AL_CHECK(al_test_case_begin("option_result_layout_scanner", &fixture));
  return 1;
failed:
  return 0;
}

static int al_test_record_copy_preserves_source(void) {
  al_test_fixture fixture;
  al_owning_stack_context *ctx = &fixture.ctx;
  uint8_t before[AL_TEST_STACK_CAPACITY];
  uint32_t direction;
  uint32_t invalid;
  for (direction = 0u; direction < 2u; ++direction) {
    const uint32_t source = direction == 0u ? 0u : 32u;
    const uint32_t destination = direction == 0u ? 32u : 0u;
    al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
    AL_CHECK(al_owning_reserve_to(ctx, 48u, 210u) == 0);
    al_owning_store_i64(ctx, source, INT64_C(0x0102030405060708),
                        AL_TEST_TYPE_INT);
    al_owning_store_i64(ctx, source + 8u, 0, AL_TEST_TYPE_INT);
    al_owning_store_i64(ctx, 24u, 7711, AL_TEST_TYPE_INT);
    (void)memcpy(before, ctx->stack_data, sizeof(before));
    al_owning_copy_range(ctx, destination, source, 16u, 10u,
                         AL_TEST_TYPE_TEXT_LEAF, AL_OWNING_EVENT_RECORD_BUILD);
    AL_CHECK(ctx->status == AL_OWNING_STATUS_OK);
    AL_CHECK(memcmp(ctx->stack_data + source, before + source, 16u) == 0);
    AL_CHECK(memcmp(ctx->stack_data + destination, before + source, 16u) == 0);
    AL_CHECK(memcmp(ctx->stack_data + 16u, before + 16u, 16u) == 0);
    AL_CHECK(al_owning_check_initialized(ctx, source, 16u) == 0);
    AL_CHECK(al_owning_check_initialized(ctx, destination, 16u) == 0);
    AL_CHECK(ctx->deep_copy_bytes == 16u && ctx->move_bytes == 0u);
    AL_CHECK(ctx->duplicate_disjoint_checks == 0u &&
             ctx->live_payload_bytes == 10u);
    AL_CHECK(fixture.events[ctx->trace_event_count - 1u].kind ==
             AL_OWNING_EVENT_RECORD_BUILD);
    AL_CHECK(fixture.events[ctx->trace_event_count - 1u].source_offset ==
             source);
    AL_CHECK(al_test_case_begin("record_copy_preserves_source", &fixture));
  }
  /* Overlap, uninitialized source, unreserved destination, invalid payload,
     and overflowing source offsets must fail without modifying storage. */
  for (invalid = 0u; invalid < 5u; ++invalid) {
    const uint32_t source = invalid == 1u   ? 16u
                            : invalid == 4u ? UINT32_MAX
                                            : 0u;
    const uint32_t destination = invalid == 0u ? 4u : invalid == 2u ? 48u : 32u;
    al_test_fixture_init(&fixture, AL_TEST_STACK_CAPACITY);
    AL_CHECK(al_owning_reserve_to(ctx, 40u, 211u) == 0);
    al_owning_store_i64(ctx, 0u, 112233, AL_TEST_TYPE_INT);
    (void)memcpy(before, ctx->stack_data, sizeof(before));
    al_owning_copy_range(ctx, destination, source, 8u, invalid == 3u ? 9u : 8u,
                         AL_TEST_TYPE_INT, AL_OWNING_EVENT_RECORD_BUILD);
    AL_CHECK(ctx->status == AL_OWNING_STATUS_INTERNAL);
    AL_CHECK(memcmp(ctx->stack_data, before, sizeof(before)) == 0);
    AL_CHECK(ctx->deep_copy_bytes == 0u && ctx->move_bytes == 0u &&
             ctx->duplicate_disjoint_checks == 0u);
    AL_CHECK(ctx->live_payload_bytes == 0u && ctx->cursor_bytes == 40u);
    ctx->status = AL_OWNING_STATUS_OK;
    AL_CHECK(al_test_case_begin("record_copy_rejects_invalid_range", &fixture));
  }
  return 1;
failed:
  return 0;
}

int main(int argc, char **argv) {
  if (argc == 2 && strcmp(argv[1], "--bool-scanner-only") == 0) {
    if (!al_test_bool_canonical_scanner())
      return 1;
    (void)printf(
        "{\"suite\":\"owning_stack_runtime_bool_scanner_v1\",\"status\":\"pass\",\"cases\":%u,\"checks\":%u}\n",
        al_test_cases, al_test_checks);
    return 0;
  }
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
  al_test_fixed_baseline.cases = al_test_cases;
  al_test_fixed_baseline.checks = al_test_checks;
  al_test_fixed_baseline.peak_cursor = al_test_peak_cursor;
  al_test_fixed_baseline.peak_payload = al_test_peak_payload;
  al_test_fixed_baseline.peak_local_reserved = al_test_peak_local_reserved;
  al_test_fixed_baseline.peak_local_payload = al_test_peak_local_payload;
  al_test_fixed_baseline.stack_capacity = al_test_stack_capacity;
  al_test_fixed_baseline.bitmap_bytes = al_test_bitmap_bytes;
  al_test_fixed_baseline.event_count = al_test_event_count;
  al_test_fixed_baseline.trace_capacity = al_test_trace_capacity;
  if (!al_test_dynamic_external_copy_and_fields() ||
      !al_test_dynamic_owner_end_boundary() ||
      !al_test_dynamic_concat_provenance_and_atomicity() ||
      !al_test_dynamic_descriptor_validation() ||
      !al_test_dynamic_duplicate_drop_reuse() ||
      !al_test_dynamic_local_suffix_compaction() ||
      !al_test_dynamic_move_range_source_only_invalidation() ||
      !al_test_dynamic_return_larger_than_arguments() ||
      !al_test_dynamic_constant_copy() ||
      !al_test_dynamic_malformed_external_values() ||
      !al_test_closed_enum_valid_values() ||
      !al_test_closed_enum_invalid_root_tags() ||
      !al_test_closed_enum_invalid_nested_tags() ||
      !al_test_closed_enum_short_extents() ||
      !al_test_closed_enum_descriptor_validation() ||
      !al_test_closed_enum_abi2_rejected() ||
      !al_test_option_result_layout_and_scanner() ||
      !al_test_record_copy_preserves_source()) {
    return 1;
  }
  {
    if (!al_test_bool_canonical_scanner())
      return 1;
  }
  (void)printf(
      "{\"suite\":\"owning_stack_runtime_storage_v2\",\"status\":\"pass\","
      "\"cases\":%u,\"checks\":%u,\"fixed_baseline\":{\"suite\":\"owning_"
      "stack_runtime_storage_v1\",\"cases\":%u,\"checks\":%u,\"peak_operand_"
      "bytes\":%u,\"peak_local_bytes\":%u,\"peak_cursor_bytes\":%u,\"max_"
      "stack_capacity_bytes\":%u},\"dynamic_cases\":{\"cases\":%u,\"checks\":%"
      "u},"
      "\"abi\":{\"event_size_bytes\":%zu,\"event_checksum_offset_bytes\":%zu,"
      "\"context_size_bytes\":%zu,\"context_offsets_bytes\":{\"stack_data\":%"
      "zu,"
      "\"init_bitmap\":%zu,\"poison_bitmap\":%zu,\"trace_events\":%zu,"
      "\"deep_copy_bytes\":%zu,\"move_bytes\":%zu,\"input_copy_bytes\":%zu,"
      "\"retained_copy_bytes\":%zu},\"layout_abi_version\":%u,\"layout_size_"
      "bytes\":%zu,\"layout_offsets_bytes\":{\"types\":%zu,\"type_count\":%zu,"
      "\"fields\":%zu,\"field_count\":%zu},\"type_descriptor_size_bytes\":%zu,"
      "\"field_descriptor_size_bytes\":%zu,\"value_size_bytes\":%zu,"
      "\"field_location_size_bytes\":%zu},\"payload\":{\"all_cases_peak_"
      "operand_"
      "bytes\":%u,\"all_cases_peak_local_bytes\":%u,\"fixed_peak_operand_"
      "bytes\":%u,"
      "\"fixed_peak_local_bytes\":%u},\"reserved\":{\"all_cases_max_stack_"
      "capacity_"
      "bytes\":%u,\"all_cases_peak_cursor_bytes\":%u,\"all_cases_peak_local_"
      "reserved_bytes\":%u,\"fixed_peak_cursor_bytes\":%u},\"metadata\":{"
      "\"bitmap_bytes_each\":%u,\"bitmap_total_bytes\":%u,\"trace_event_"
      "count\":%u,"
      "\"trace_event_capacity\":%u,\"trace_bytes\":%u,\"layout_validation_"
      "memo_table_bytes\":%u,\"layout_max_types\":%u,\"layout_max_fields\":%u,"
      "\"layout_max_depth\":%u}}\n",
      al_test_cases, al_test_checks, al_test_fixed_baseline.cases,
      al_test_fixed_baseline.checks, al_test_fixed_baseline.peak_payload,
      al_test_fixed_baseline.peak_local_payload,
      al_test_fixed_baseline.peak_cursor, al_test_fixed_baseline.stack_capacity,
      al_test_cases - al_test_fixed_baseline.cases,
      al_test_checks - al_test_fixed_baseline.checks,
      sizeof(al_owning_stack_event), offsetof(al_owning_stack_event, checksum),
      sizeof(al_owning_stack_context),
      offsetof(al_owning_stack_context, stack_data),
      offsetof(al_owning_stack_context, init_bitmap),
      offsetof(al_owning_stack_context, poison_bitmap),
      offsetof(al_owning_stack_context, trace_events),
      offsetof(al_owning_stack_context, deep_copy_bytes),
      offsetof(al_owning_stack_context, move_bytes),
      offsetof(al_owning_stack_context, input_copy_bytes),
      offsetof(al_owning_stack_context, retained_copy_bytes),
      AL_OWNING_LAYOUT_ABI_VERSION, sizeof(al_owning_layout),
      offsetof(al_owning_layout, types), offsetof(al_owning_layout, type_count),
      offsetof(al_owning_layout, fields),
      offsetof(al_owning_layout, field_count),
      sizeof(al_owning_type_descriptor), sizeof(al_owning_field_descriptor),
      sizeof(al_owning_value_size), sizeof(al_owning_field_location),
      al_test_peak_payload, al_test_peak_local_payload,
      al_test_fixed_baseline.peak_payload,
      al_test_fixed_baseline.peak_local_payload, al_test_stack_capacity,
      al_test_peak_cursor, al_test_peak_local_reserved,
      al_test_fixed_baseline.peak_cursor, al_test_bitmap_bytes,
      al_test_bitmap_bytes * 2u, al_test_event_count, al_test_trace_capacity,
      al_test_trace_capacity * (uint32_t)sizeof(al_owning_stack_event),
      2u * AL_OWNING_LAYOUT_MAX_TYPES, AL_OWNING_LAYOUT_MAX_TYPES,
      AL_OWNING_LAYOUT_MAX_FIELDS, AL_OWNING_LAYOUT_MAX_DEPTH);
  return 0;
}
