#include "mailbox_runtime.h"

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

enum {
  TEST_STRING_TYPE_ID = 201u,
  TEST_STATE_TYPE_ID = 202u,
  TEST_CONTINUATION_TYPE_ID = 203u,
  TEST_I64_TYPE_ID = 204u
};

static uint32_t test_fail_next_resume;

static void test_write_u64_le(uint8_t *bytes, uint32_t offset,
                              uint64_t value) {
  uint32_t index;
  for (index = 0u; index < 8u; ++index)
    bytes[offset + index] = (uint8_t)(value >> (index * 8u));
}

typedef struct test_cancel_thread_call {
  al_mailbox_runtime *runtime;
  uint32_t mailbox_id;
  const al_mailbox_token *token;
  al_mailbox_result result;
} test_cancel_thread_call;

typedef struct test_reset_stats_thread_call {
  al_mailbox_runtime *runtime;
  al_mailbox_reset_stats stats;
  al_mailbox_result result;
} test_reset_stats_thread_call;

static DWORD WINAPI test_cancel_from_worker_thread(LPVOID parameter) {
  test_cancel_thread_call *call = (test_cancel_thread_call *)parameter;
  call->result = al_mailbox_cancel_text(call->runtime, call->mailbox_id,
                                        call->token);
  return 0u;
}

static DWORD WINAPI test_get_reset_stats_from_worker_thread(LPVOID parameter) {
  test_reset_stats_thread_call *call =
      (test_reset_stats_thread_call *)parameter;
  call->result =
      al_mailbox_get_reset_stats(call->runtime, &call->stats);
  return 0u;
}

static const al_owning_type_descriptor test_types[] = {
    {AL_OWNING_TYPE_STRING, TEST_STRING_TYPE_ID, 0u, 0u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 0u},
    {AL_OWNING_TYPE_RECORD, TEST_STATE_TYPE_ID, 0u, 1u, 8u, 8u, 8u, 8u, 0u},
    {AL_OWNING_TYPE_RECORD, TEST_CONTINUATION_TYPE_ID, 1u, 1u, 8u, 8u, 8u,
     8u, 0u},
    {AL_OWNING_TYPE_I64, TEST_I64_TYPE_ID, 0u, 0u, 8u, 8u, 8u, 8u, 0u}};

static const al_owning_field_descriptor test_fields[] = {
    {3u, 0u, 0u, 0u}, {3u, 0u, 0u, 0u}};

static const al_owning_layout test_layout = {
    AL_OWNING_LAYOUT_ABI_VERSION, test_types, 4u, test_fields, 2u};

static uint32_t test_read_u32(const uint8_t *bytes) {
  return (uint32_t)bytes[0] | ((uint32_t)bytes[1] << 8u) |
         ((uint32_t)bytes[2] << 16u) | ((uint32_t)bytes[3] << 24u);
}

static int64_t test_read_i64(const uint8_t *bytes) {
  uint64_t bits = 0u;
  uint32_t index;
  for (index = 0u; index < 8u; ++index)
    bits |= (uint64_t)bytes[index] << (index * 8u);
  return (int64_t)bits;
}

static int32_t test_import_text(al_owning_stack_context *context,
                                const al_owning_external_slice *input,
                                uint32_t *out_units) {
  uint32_t payload_bytes;
  uint32_t extent_bytes;
  uint32_t offset = context->cursor_bytes;
  if (input == NULL || input->type_index != 0u || input->bytes == NULL ||
      al_owning_measure_external_value(
          context, &test_layout, input->type_index, input->bytes,
          input->extent_bytes, 0u, 1u, &payload_bytes, &extent_bytes) != 0 ||
      extent_bytes != input->extent_bytes || payload_bytes < 8u)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  if (al_owning_reserve_to(context, offset + extent_bytes, 1u) != 0 ||
      al_owning_copy_external_bounded(
          context, offset, input->bytes, input->extent_bytes, 0u,
          payload_bytes, extent_bytes, TEST_STRING_TYPE_ID, 1u) != 0)
    return (int32_t)context->status;
  *out_units = test_read_u32(input->bytes);
  return AL_OWNING_STATUS_OK;
}

static int32_t test_write_record(al_owning_stack_context *context,
                                 uint32_t type_index, uint32_t type_id,
                                 uint32_t offset, int64_t value,
                                 al_owning_bank_stack_slice *output) {
  if (al_owning_reserve_to(context, offset + 8u, 2u) != 0)
    return (int32_t)context->status;
  al_owning_store_i64(context, offset, value, TEST_I64_TYPE_ID);
  if (context->status != AL_OWNING_STATUS_OK)
    return (int32_t)context->status;
  output->type_index = type_index;
  output->source_offset_bytes = offset;
  output->source_owner_end_bytes = offset + 8u;
  output->reserved = 0u;
  (void)type_id;
  return AL_OWNING_STATUS_OK;
}

static int32_t test_initialize(al_owning_stack_context *context,
                               const al_owning_external_slice *inputs,
                               uint32_t input_count,
                               al_owning_bank_stack_slice *outputs,
                               uint32_t output_capacity) {
  uint32_t units;
  uint32_t offset;
  int32_t status;
  if (input_count != 1u || output_capacity != 1u)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  status = test_import_text(context, &inputs[0], &units);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  offset = context->cursor_bytes;
  return test_write_record(context, 1u, TEST_STATE_TYPE_ID, offset,
                           (int64_t)units, &outputs[0]);
}

static int32_t test_begin(al_owning_stack_context *context,
                          const al_owning_external_slice *inputs,
                          uint32_t input_count,
                          al_owning_bank_stack_slice *outputs,
                          uint32_t output_capacity) {
  uint32_t units;
  uint32_t text_extent;
  uint32_t state_offset;
  uint32_t continuation_offset;
  int64_t state_value;
  int32_t status;
  if (input_count != 2u || output_capacity != 2u || inputs[0].type_index != 1u)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  status = test_import_text(context, &inputs[1], &units);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  text_extent = context->cursor_bytes;
  state_value = test_read_i64(inputs[0].bytes);
  state_offset = text_extent;
  status = test_write_record(context, 1u, TEST_STATE_TYPE_ID, state_offset,
                             state_value + 1, &outputs[0]);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  continuation_offset = context->cursor_bytes;
  return test_write_record(context, 2u, TEST_CONTINUATION_TYPE_ID,
                           continuation_offset, (int64_t)units,
                           &outputs[1]);
}

static int32_t test_resume(al_owning_stack_context *context,
                           const al_owning_external_slice *inputs,
                           uint32_t input_count,
                           al_owning_bank_stack_slice *outputs,
                           uint32_t output_capacity) {
  uint32_t units;
  uint32_t state_offset;
  int64_t state_value;
  int64_t continuation_value;
  int32_t status;
  if (input_count != 3u || output_capacity != 1u ||
      inputs[0].type_index != 1u || inputs[1].type_index != 2u)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  status = test_import_text(context, &inputs[2], &units);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  if (test_fail_next_resume != 0u) {
    test_fail_next_resume = 0u;
    al_owning_set_failure(context, AL_OWNING_STATUS_DIAGNOSTIC, 77u, 0u,
                          context->stack_capacity_bytes);
    return AL_OWNING_STATUS_DIAGNOSTIC;
  }
  state_value = test_read_i64(inputs[0].bytes);
  continuation_value = test_read_i64(inputs[1].bytes);
  state_offset = context->cursor_bytes;
  return test_write_record(context, 1u, TEST_STATE_TYPE_ID, state_offset,
                           state_value + continuation_value + units,
                           &outputs[0]);
}

static int32_t test_associated_resume(
    al_owning_stack_context *context,
    const al_owning_bank_stack_slice *retained_inputs,
    uint32_t retained_count, const al_owning_external_slice *completion,
    uint32_t protected_cursor_bytes, al_owning_bank_stack_slice *outputs,
    uint32_t output_capacity) {
  uint32_t units;
  uint32_t state_offset;
  int64_t state_value;
  int64_t continuation_value;
  int32_t status;
  if (context == NULL || retained_inputs == NULL || retained_count != 2u ||
      completion == NULL || outputs == NULL || output_capacity != 1u ||
      context->cursor_bytes != protected_cursor_bytes ||
      retained_inputs[0].type_index != 1u ||
      retained_inputs[1].type_index != 2u ||
      retained_inputs[0].source_offset_bytes + 8u >
          retained_inputs[0].source_owner_end_bytes ||
      retained_inputs[1].source_offset_bytes + 8u >
          retained_inputs[1].source_owner_end_bytes ||
      retained_inputs[0].source_owner_end_bytes > protected_cursor_bytes ||
      retained_inputs[1].source_owner_end_bytes > protected_cursor_bytes)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  status = test_import_text(context, completion, &units);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  if (test_fail_next_resume != 0u) {
    test_fail_next_resume = 0u;
    al_owning_set_failure(context, AL_OWNING_STATUS_DIAGNOSTIC, 77u, 0u,
                          context->stack_capacity_bytes);
    return AL_OWNING_STATUS_DIAGNOSTIC;
  }
  state_value = test_read_i64(
      context->stack_data + retained_inputs[0].source_offset_bytes);
  continuation_value = test_read_i64(
      context->stack_data + retained_inputs[1].source_offset_bytes);
  state_offset = context->cursor_bytes;
  return test_write_record(context, 1u, TEST_STATE_TYPE_ID, state_offset,
                           state_value + continuation_value + units,
                           &outputs[0]);
}

static const al_owning_mailbox_module test_module = {
    AL_OWNING_MAILBOX_ABI_VERSION,
    sizeof(al_owning_mailbox_module),
    &test_layout,
    {{1u, 1u, {0u, 0u, 0u}, {1u, 0u}, test_initialize},
     {2u, 2u, {1u, 0u, 0u}, {1u, 2u}, test_begin},
     {3u, 1u, {1u, 2u, 0u}, {1u, 0u}, test_resume}},
    test_associated_resume};

enum {
  TEST_ENUM_STRING_TYPE_ID = 210u,
  TEST_ENUM_TAG_TYPE_ID = 211u,
  TEST_ENUM_VALUE_TYPE_ID = 212u,
  TEST_ENUM_STATE_TYPE_ID = 213u,
  TEST_ENUM_CONTINUATION_TYPE_ID = 214u
};

static const al_owning_type_descriptor test_enum_types[] = {
    {AL_OWNING_TYPE_STRING, TEST_ENUM_STRING_TYPE_ID, 0u, 0u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 0u},
    {AL_OWNING_TYPE_ENUM, TEST_ENUM_TAG_TYPE_ID, 0u, 0u, 8u, 8u, 8u, 8u,
     3u},
    {AL_OWNING_TYPE_I64, TEST_ENUM_VALUE_TYPE_ID, 0u, 0u, 8u, 8u, 8u, 8u,
     0u},
    {AL_OWNING_TYPE_RECORD, TEST_ENUM_STATE_TYPE_ID, 0u, 2u, 16u, 16u, 16u,
     16u, 0u},
    {AL_OWNING_TYPE_RECORD, TEST_ENUM_CONTINUATION_TYPE_ID, 2u, 0u, 0u, 8u,
     0u, 8u, 0u}};

static const al_owning_field_descriptor test_enum_fields[] = {
    {2u, 0u, 0u, 0u}, {1u, 8u, 0u, 0u}};

static const al_owning_layout test_enum_layout = {
    AL_OWNING_LAYOUT_ABI_VERSION, test_enum_types, 5u, test_enum_fields, 2u};

static uint32_t test_enum_begin_handler_calls;

static int32_t test_enum_initialize(
    al_owning_stack_context *context, const al_owning_external_slice *inputs,
    uint32_t input_count, al_owning_bank_stack_slice *outputs,
    uint32_t output_capacity) {
  uint32_t offset;
  if (input_count != 1u || output_capacity != 1u || inputs[0].type_index != 0u)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  offset = context->cursor_bytes;
  if (offset > UINT32_MAX - 16u) {
    al_owning_set_failure(context, AL_OWNING_STATUS_INTERNAL, 1u, UINT32_MAX,
                          context->cursor_bytes);
    return (int32_t)context->status;
  }
  if (al_owning_reserve_to(context, offset + 16u, 1u) != 0)
    return (int32_t)context->status;
  al_owning_store_i64(context, offset, 5, TEST_ENUM_VALUE_TYPE_ID);
  al_owning_store_i64(context, offset + 8u, 1, TEST_ENUM_TAG_TYPE_ID);
  if (context->status != AL_OWNING_STATUS_OK)
    return (int32_t)context->status;
  outputs[0] = (al_owning_bank_stack_slice){3u, offset, offset + 16u, 0u};
  return AL_OWNING_STATUS_OK;
}

static int32_t test_enum_begin(
    al_owning_stack_context *context, const al_owning_external_slice *inputs,
    uint32_t input_count, al_owning_bank_stack_slice *outputs,
    uint32_t output_capacity) {
  uint32_t state_offset;
  uint32_t continuation_offset;
  if (input_count != 2u || output_capacity != 2u ||
      inputs[0].type_index != 3u || inputs[1].type_index != 0u)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  ++test_enum_begin_handler_calls;
  state_offset = context->cursor_bytes;
  if (state_offset > UINT32_MAX - 16u) {
    al_owning_set_failure(context, AL_OWNING_STATUS_INTERNAL, 2u, UINT32_MAX,
                          context->cursor_bytes);
    return (int32_t)context->status;
  }
  if (al_owning_reserve_to(context, state_offset + 16u, 2u) != 0 ||
      al_owning_copy_external_bounded(
          context, state_offset, inputs[0].bytes, inputs[0].extent_bytes, 0u,
          16u, 16u, TEST_ENUM_STATE_TYPE_ID, 2u) != 0)
    return (int32_t)context->status;
  outputs[0] = (al_owning_bank_stack_slice){
      3u, state_offset, state_offset + 16u, 0u};
  continuation_offset = context->cursor_bytes;
  if (continuation_offset > UINT32_MAX - 8u) {
    al_owning_set_failure(context, AL_OWNING_STATUS_INTERNAL, 2u, UINT32_MAX,
                          context->cursor_bytes);
    return (int32_t)context->status;
  }
  if (al_owning_reserve_to(context, continuation_offset + 8u, 2u) != 0)
    return (int32_t)context->status;
  al_owning_store_token(context, continuation_offset,
                        TEST_ENUM_CONTINUATION_TYPE_ID);
  if (context->status != AL_OWNING_STATUS_OK)
    return (int32_t)context->status;
  outputs[1] = (al_owning_bank_stack_slice){
      4u, continuation_offset, continuation_offset + 8u, 0u};
  return AL_OWNING_STATUS_OK;
}

static int32_t test_enum_resume(
    al_owning_stack_context *context, const al_owning_external_slice *inputs,
    uint32_t input_count, al_owning_bank_stack_slice *outputs,
    uint32_t output_capacity) {
  (void)context;
  (void)inputs;
  (void)input_count;
  (void)outputs;
  (void)output_capacity;
  return AL_OWNING_STATUS_INVALID_REQUEST;
}

static int32_t test_enum_associated_resume(
    al_owning_stack_context *context,
    const al_owning_bank_stack_slice *retained_inputs,
    uint32_t retained_count, const al_owning_external_slice *completion,
    uint32_t protected_cursor_bytes, al_owning_bank_stack_slice *outputs,
    uint32_t output_capacity) {
  (void)context;
  (void)retained_inputs;
  (void)retained_count;
  (void)completion;
  (void)protected_cursor_bytes;
  (void)outputs;
  (void)output_capacity;
  return AL_OWNING_STATUS_INVALID_REQUEST;
}

static const al_owning_mailbox_module test_enum_module = {
    AL_OWNING_MAILBOX_ABI_VERSION,
    sizeof(al_owning_mailbox_module),
    &test_enum_layout,
    {{1u, 1u, {0u, 0u, 0u}, {3u, 0u}, test_enum_initialize},
     {2u, 2u, {3u, 0u, 0u}, {3u, 4u}, test_enum_begin},
     {3u, 1u, {3u, 4u, 0u}, {3u, 0u}, test_enum_resume}},
    test_enum_associated_resume};

static void test_generic_mailbox_enum_preflight(void) {
  static _Alignas(8) uint8_t enum_storage[8192];
  al_mailbox_owning_config config = {
      AL_MAILBOX_CONTROL_ABI_VERSION, sizeof(al_mailbox_owning_config),
      1u, 64u, 64u, 64u, 1u, AL_MAILBOX_OWNING_POLICY_RETURN};
  al_mailbox_owning_storage_requirements requirements;
  al_mailbox_runtime *runtime = NULL;
  al_mailbox_call_info call_info;
  al_mailbox_owning_state_view view;
  al_mailbox_owning_stats before_stats;
  al_mailbox_owning_stats after_stats;
  al_mailbox_token token;
  uint8_t active_before[16];
  uint8_t *state_bytes;
  const uint8_t input[] = {'x'};

  test_enum_begin_handler_calls = 0u;
  assert(al_mailbox_get_owning_storage_requirements(
             &test_enum_module, &config, &requirements) == AL_MAILBOX_OK);
  assert(requirements.storage_bytes <= sizeof(enum_storage));
  assert(al_mailbox_runtime_init_owning(
             &test_enum_module, &config, enum_storage, sizeof(enum_storage),
             &runtime) == AL_MAILBOX_OK);
  assert(al_mailbox_init_text(runtime, 0u, input, sizeof(input), &call_info) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.bank->root_count == 1u && view.bank->used_bytes == 16u &&
         view.bank->roots[0].type_id == TEST_ENUM_STATE_TYPE_ID);
  state_bytes = (uint8_t *)(uintptr_t)(
      view.bank->bytes + view.bank->roots[0].offset_bytes);
  test_write_u64_le(state_bytes, 8u, 3u);
  memcpy(active_before, state_bytes, sizeof(active_before));
  assert(al_mailbox_get_owning_stats(runtime, &before_stats) == AL_MAILBOX_OK);
  memset(&token, 0x5a, sizeof(token));
  assert(al_mailbox_begin_text(runtime, 0u, input, sizeof(input), &token,
                               &call_info) == AL_MAILBOX_INVALID_REFERENCE);
  assert(al_mailbox_get_owning_stats(runtime, &after_stats) == AL_MAILBOX_OK);
  assert(after_stats.handler_invocations == before_stats.handler_invocations &&
         test_enum_begin_handler_calls == 0u);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.pending == 0u && view.bank->root_count == 1u &&
         view.bank->used_bytes == 16u &&
         memcmp(view.bank->bytes, active_before, sizeof(active_before)) == 0);
  assert(token.opaque[0] == UINT64_C(0x5a5a5a5a5a5a5a5a) &&
         token.opaque[1] == UINT64_C(0x5a5a5a5a5a5a5a5a) &&
         token.opaque[2] == UINT64_C(0x5a5a5a5a5a5a5a5a));

  test_write_u64_le(state_bytes, 8u, 1u);
  assert(al_mailbox_begin_text(runtime, 0u, input, sizeof(input), &token,
                               &call_info) == AL_MAILBOX_OK);
  assert(test_enum_begin_handler_calls == 1u);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.pending != 0u && view.bank->root_count == 2u &&
         view.bank->roots[0].type_id == TEST_ENUM_STATE_TYPE_ID &&
         view.bank->roots[1].type_id == TEST_ENUM_CONTINUATION_TYPE_ID);
  assert(al_mailbox_dispose(runtime) == AL_MAILBOX_OK);
}

enum {
  TEST_SUM_STRING_TYPE_ID = 310u,
  TEST_SUM_INT_TYPE_ID = 311u,
  TEST_SUM_OPTION_TYPE_ID = 312u,
  TEST_SUM_STATE_TYPE_ID = 313u,
  TEST_SUM_CONTINUATION_TYPE_ID = 314u
};

static const al_owning_type_descriptor test_sum_mailbox_types[] = {
    {AL_OWNING_TYPE_STRING, TEST_SUM_STRING_TYPE_ID, 0u, 0u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 0u},
    {AL_OWNING_TYPE_I64, TEST_SUM_INT_TYPE_ID, 0u, 0u, 8u, 8u, 8u, 8u, 0u},
    {AL_OWNING_TYPE_OPTION, TEST_SUM_OPTION_TYPE_ID, 0u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 2u},
    {AL_OWNING_TYPE_RECORD, TEST_SUM_STATE_TYPE_ID, 2u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 16u, 16u,
     0u},
    {AL_OWNING_TYPE_RECORD, TEST_SUM_CONTINUATION_TYPE_ID, 4u, 2u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 16u, 16u,
     0u}};

static const al_owning_field_descriptor test_sum_mailbox_fields[] = {
    {0u, 8u, 0u, 0u},
    {AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 0u, 0u},
    {2u, 0u, 0u, 0u},
    {1u, AL_OWNING_LAYOUT_DYNAMIC_U32, 0u, 0u},
    {2u, 0u, 0u, 0u},
    {1u, AL_OWNING_LAYOUT_DYNAMIC_U32, 0u, 0u}};

static const al_owning_layout test_sum_mailbox_layout = {
    AL_OWNING_LAYOUT_ABI_VERSION, test_sum_mailbox_types, 5u,
    test_sum_mailbox_fields, 6u};

static uint32_t test_sum_mailbox_begin_handler_calls;

static int32_t test_sum_mailbox_import_text(
    al_owning_stack_context *context, const al_owning_external_slice *input,
    uint32_t *out_units) {
  uint32_t payload_bytes;
  uint32_t extent_bytes;
  uint32_t offset = context->cursor_bytes;
  if (input == NULL || input->type_index != 0u || input->bytes == NULL ||
      al_owning_measure_external_value(
          context, &test_sum_mailbox_layout, input->type_index, input->bytes,
          input->extent_bytes, 0u, 20u, &payload_bytes, &extent_bytes) != 0 ||
      extent_bytes != input->extent_bytes || payload_bytes < 8u ||
      offset > UINT32_MAX - extent_bytes)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  if (al_owning_reserve_to(context, offset + extent_bytes, 20u) != 0 ||
      al_owning_copy_external_bounded(
          context, offset, input->bytes, input->extent_bytes, 0u,
          payload_bytes, extent_bytes, TEST_SUM_STRING_TYPE_ID, 20u) != 0)
    return (int32_t)context->status;
  *out_units = test_read_u32(input->bytes);
  return AL_OWNING_STATUS_OK;
}

static int32_t test_sum_mailbox_build_state(
    al_owning_stack_context *context, const al_owning_external_slice *input,
    al_owning_bank_stack_slice *output) {
  uint32_t offset = context->cursor_bytes;
  uint32_t units;
  int32_t status;
  if (offset > UINT32_MAX - 8u ||
      al_owning_reserve_to(context, offset + 8u, 20u) != 0)
    return (int32_t)context->status;
  al_owning_store_i64(context, offset, 0, TEST_SUM_OPTION_TYPE_ID);
  status = test_sum_mailbox_import_text(context, input, &units);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  if (context->cursor_bytes > UINT32_MAX - 8u ||
      al_owning_reserve_to(context, context->cursor_bytes + 8u, 20u) != 0)
    return (int32_t)context->status;
  al_owning_store_i64(context, context->cursor_bytes - 8u,
                      (int64_t)units, TEST_SUM_INT_TYPE_ID);
  if (context->status != AL_OWNING_STATUS_OK)
    return (int32_t)context->status;
  *output = (al_owning_bank_stack_slice){
      3u, offset, context->cursor_bytes, 0u};
  return AL_OWNING_STATUS_OK;
}

static int32_t test_sum_mailbox_build_continuation(
    al_owning_stack_context *context, int64_t units,
    al_owning_bank_stack_slice *output) {
  uint32_t offset = context->cursor_bytes;
  if (offset > UINT32_MAX - 16u ||
      al_owning_reserve_to(context, offset + 16u, 21u) != 0)
    return (int32_t)context->status;
  al_owning_store_i64(context, offset, 1, TEST_SUM_OPTION_TYPE_ID);
  al_owning_store_i64(context, offset + 8u, units, TEST_SUM_INT_TYPE_ID);
  if (context->status != AL_OWNING_STATUS_OK)
    return (int32_t)context->status;
  *output = (al_owning_bank_stack_slice){
      4u, offset, offset + 16u, 0u};
  return AL_OWNING_STATUS_OK;
}

static int32_t test_sum_mailbox_initialize(
    al_owning_stack_context *context, const al_owning_external_slice *inputs,
    uint32_t input_count, al_owning_bank_stack_slice *outputs,
    uint32_t output_capacity) {
  if (input_count != 1u || output_capacity != 1u)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  return test_sum_mailbox_build_state(context, &inputs[0], &outputs[0]);
}

static int32_t test_sum_mailbox_begin(
    al_owning_stack_context *context, const al_owning_external_slice *inputs,
    uint32_t input_count, al_owning_bank_stack_slice *outputs,
    uint32_t output_capacity) {
  uint32_t units;
  int32_t status;
  if (input_count != 2u || output_capacity != 2u ||
      inputs[0].type_index != 3u || inputs[1].type_index != 0u)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  ++test_sum_mailbox_begin_handler_calls;
  status = test_sum_mailbox_build_state(context, &inputs[1], &outputs[0]);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  units = test_read_u32(inputs[1].bytes);
  return test_sum_mailbox_build_continuation(
      context, (int64_t)units, &outputs[1]);
}

static int32_t test_sum_mailbox_resume(
    al_owning_stack_context *context, const al_owning_external_slice *inputs,
    uint32_t input_count, al_owning_bank_stack_slice *outputs,
    uint32_t output_capacity) {
  if (input_count != 3u || output_capacity != 1u ||
      inputs[0].type_index != 3u || inputs[1].type_index != 4u ||
      inputs[2].type_index != 0u)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  return test_sum_mailbox_build_state(context, &inputs[2], &outputs[0]);
}

static int32_t test_sum_mailbox_associated_resume(
    al_owning_stack_context *context,
    const al_owning_bank_stack_slice *retained_inputs,
    uint32_t retained_count, const al_owning_external_slice *completion,
    uint32_t protected_cursor_bytes, al_owning_bank_stack_slice *outputs,
    uint32_t output_capacity) {
  if (retained_inputs == NULL || retained_count != 2u || completion == NULL ||
      output_capacity != 1u || context->cursor_bytes != protected_cursor_bytes)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  return test_sum_mailbox_build_state(context, completion, &outputs[0]);
}

static const al_owning_mailbox_module test_sum_mailbox_module = {
    AL_OWNING_MAILBOX_ABI_VERSION,
    sizeof(al_owning_mailbox_module),
    &test_sum_mailbox_layout,
    {{1u, 1u, {0u, 0u, 0u}, {3u, 0u}, test_sum_mailbox_initialize},
     {2u, 2u, {3u, 0u, 0u}, {3u, 4u}, test_sum_mailbox_begin},
     {3u, 1u, {3u, 4u, 0u}, {3u, 0u}, test_sum_mailbox_resume}},
    test_sum_mailbox_associated_resume};

static void test_generic_mailbox_nested_sum_roots(void) {
  static _Alignas(8) uint8_t sum_storage[8192];
  al_mailbox_owning_config config = {
      AL_MAILBOX_CONTROL_ABI_VERSION, sizeof(al_mailbox_owning_config),
      1u, 128u, 128u, 64u, 1u, AL_MAILBOX_OWNING_POLICY_RETURN};
  al_mailbox_runtime *runtime = NULL;
  al_mailbox_owning_state_view view;
  al_mailbox_token token;
  const uint8_t initial[] = {'A'};
  const uint8_t begin[] = {'B', 'C'};

  {
    al_owning_type_descriptor bad_types[5];
    al_owning_field_descriptor bad_fields[6];
    al_owning_layout bad_layout = test_sum_mailbox_layout;
    al_owning_mailbox_module bad_module = test_sum_mailbox_module;
    (void)memcpy(bad_types, test_sum_mailbox_types, sizeof(bad_types));
    (void)memcpy(bad_fields, test_sum_mailbox_fields, sizeof(bad_fields));
    bad_fields[1u].child_type_index = 1u;
    bad_types[2u].fixed_payload_bytes = 16u;
    bad_types[2u].fixed_extent_bytes = 16u;
    bad_types[2u].minimum_payload_bytes = 16u;
    bad_types[2u].minimum_extent_bytes = 16u;
    bad_types[3u].minimum_payload_bytes = 24u;
    bad_types[3u].minimum_extent_bytes = 24u;
    bad_types[4u].minimum_payload_bytes = 24u;
    bad_types[4u].minimum_extent_bytes = 24u;
    bad_layout.types = bad_types;
    bad_layout.fields = bad_fields;
    bad_module.layout = &bad_layout;
    assert(al_mailbox_runtime_init_owning(
               &bad_module, &config, sum_storage, sizeof(sum_storage),
               &runtime) == AL_MAILBOX_INVALID_MODULE);
    assert(runtime == NULL);
  }

  test_sum_mailbox_begin_handler_calls = 0u;
  assert(al_mailbox_runtime_init_owning(
             &test_sum_mailbox_module, &config, sum_storage,
             sizeof(sum_storage), &runtime) == AL_MAILBOX_OK);
  assert(al_mailbox_init_text(runtime, 0u, initial, sizeof(initial), NULL) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.bank->root_count == 1u && view.bank->used_bytes == 32u &&
         view.bank->roots[0].type_id == TEST_SUM_STATE_TYPE_ID &&
         view.bank->roots[0].payload_bytes == 26u &&
         view.bank->roots[0].extent_bytes == 32u &&
         test_read_u32(view.bank->bytes + 8u) == 1u &&
         view.bank->bytes[16u] == (uint8_t)'A' &&
         test_read_i64(view.bank->bytes + 24u) == 1);

  test_write_u64_le((uint8_t *)(uintptr_t)view.bank->bytes, 0u, 2u);
  assert(al_mailbox_begin_text(runtime, 0u, begin, sizeof(begin), &token,
                               NULL) == AL_MAILBOX_INVALID_REFERENCE);
  assert(test_sum_mailbox_begin_handler_calls == 0u);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.pending == 0u && view.bank->root_count == 1u &&
         test_read_i64(view.bank->bytes) == 2);
  test_write_u64_le((uint8_t *)(uintptr_t)view.bank->bytes, 0u, 0u);

  assert(al_mailbox_begin_text(runtime, 0u, begin, sizeof(begin), &token,
                               NULL) == AL_MAILBOX_OK);
  assert(test_sum_mailbox_begin_handler_calls == 1u);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.pending == 1u && view.bank->root_count == 2u &&
         view.bank->used_bytes == 48u &&
         view.bank->roots[0].type_id == TEST_SUM_STATE_TYPE_ID &&
         view.bank->roots[0].extent_bytes == 32u &&
         view.bank->roots[1].type_id == TEST_SUM_CONTINUATION_TYPE_ID &&
         view.bank->roots[1].offset_bytes == 32u &&
         view.bank->roots[1].extent_bytes == 16u &&
         test_read_i64(view.bank->bytes + 32u) == 1 &&
         test_read_i64(view.bank->bytes + 40u) == 2);
  assert(al_mailbox_cancel_text(runtime, 0u, &token) == AL_MAILBOX_OK);
  assert(al_mailbox_dispose(runtime) == AL_MAILBOX_OK);
}

static const al_owning_type_descriptor test_text_state_types[] = {
    {AL_OWNING_TYPE_STRING, TEST_STRING_TYPE_ID, 0u, 0u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 0u},
    {AL_OWNING_TYPE_RECORD, TEST_STATE_TYPE_ID, 0u, 1u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u, 0u},
    {AL_OWNING_TYPE_RECORD, TEST_CONTINUATION_TYPE_ID, 1u, 1u, 8u, 8u, 8u,
     8u, 0u},
    {AL_OWNING_TYPE_I64, TEST_I64_TYPE_ID, 0u, 0u, 8u, 8u, 8u, 8u, 0u}};

static const al_owning_field_descriptor test_text_state_fields[] = {
    {0u, 0u, 0u, 0u}, {3u, 0u, 0u, 0u}};

static const al_owning_layout test_text_state_layout = {
    AL_OWNING_LAYOUT_ABI_VERSION, test_text_state_types, 4u,
    test_text_state_fields, 2u};

static int32_t test_text_state_initialize(
    al_owning_stack_context *context, const al_owning_external_slice *inputs,
    uint32_t input_count, al_owning_bank_stack_slice *outputs,
    uint32_t output_capacity) {
  uint32_t units;
  uint32_t offset;
  int32_t status;
  if (input_count != 1u || output_capacity != 1u)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  offset = context->cursor_bytes;
  status = test_import_text(context, &inputs[0], &units);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  outputs[0] = (al_owning_bank_stack_slice){1u, offset,
                                           context->cursor_bytes, 0u};
  return AL_OWNING_STATUS_OK;
}

static int32_t test_text_state_begin(
    al_owning_stack_context *context, const al_owning_external_slice *inputs,
    uint32_t input_count, al_owning_bank_stack_slice *outputs,
    uint32_t output_capacity) {
  uint32_t units;
  uint32_t state_offset;
  uint32_t continuation_offset;
  int32_t status;
  if (input_count != 2u || output_capacity != 2u ||
      inputs[0].type_index != 1u || inputs[1].type_index != 0u)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  state_offset = context->cursor_bytes;
  status = test_import_text(context, &inputs[1], &units);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  outputs[0] = (al_owning_bank_stack_slice){1u, state_offset,
                                           context->cursor_bytes, 0u};
  continuation_offset = context->cursor_bytes;
  status = test_write_record(context, 2u, TEST_CONTINUATION_TYPE_ID,
                             continuation_offset, (int64_t)units,
                             &outputs[1]);
  return status;
}

static int32_t test_text_state_resume(
    al_owning_stack_context *context, const al_owning_external_slice *inputs,
    uint32_t input_count, al_owning_bank_stack_slice *outputs,
    uint32_t output_capacity) {
  uint32_t units;
  uint32_t offset;
  int32_t status;
  if (input_count != 3u || output_capacity != 1u ||
      inputs[0].type_index != 1u || inputs[1].type_index != 2u ||
      inputs[2].type_index != 0u)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  offset = context->cursor_bytes;
  status = test_import_text(context, &inputs[2], &units);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  outputs[0] = (al_owning_bank_stack_slice){1u, offset,
                                           context->cursor_bytes, 0u};
  return AL_OWNING_STATUS_OK;
}

static int32_t test_text_state_associated_resume(
    al_owning_stack_context *context,
    const al_owning_bank_stack_slice *retained_inputs,
    uint32_t retained_count, const al_owning_external_slice *completion,
    uint32_t protected_cursor_bytes, al_owning_bank_stack_slice *outputs,
    uint32_t output_capacity) {
  uint32_t units;
  uint32_t offset;
  int32_t status;
  if (retained_inputs == NULL || retained_count != 2u || completion == NULL ||
      output_capacity != 1u || context->cursor_bytes != protected_cursor_bytes)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  offset = context->cursor_bytes;
  status = test_import_text(context, completion, &units);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  outputs[0] = (al_owning_bank_stack_slice){1u, offset,
                                           context->cursor_bytes, 0u};
  return AL_OWNING_STATUS_OK;
}

static const al_owning_mailbox_module test_text_state_module = {
    AL_OWNING_MAILBOX_ABI_VERSION,
    sizeof(al_owning_mailbox_module),
    &test_text_state_layout,
    {{1u, 1u, {0u, 0u, 0u}, {1u, 0u}, test_text_state_initialize},
     {2u, 2u, {1u, 0u, 0u}, {1u, 2u}, test_text_state_begin},
     {3u, 1u, {1u, 2u, 0u}, {1u, 0u}, test_text_state_resume}},
    test_text_state_associated_resume};

static void test_assert_active_bytes(al_mailbox_runtime *runtime,
                                    const uint8_t *expected,
                                    uint32_t expected_bytes,
                                    uint32_t expected_roots,
                                    uint32_t expected_pending) {
  al_mailbox_owning_state_view view;
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.pending == expected_pending);
  assert(view.bank->used_bytes == expected_bytes);
  assert(view.bank->root_count == expected_roots);
  assert(memcmp(view.bank->bytes, expected, expected_bytes) == 0);
}

static void test_keep_associated_policy(void) {
  static _Alignas(8) uint8_t storage[65536];
  al_mailbox_owning_config config = {
      AL_MAILBOX_CONTROL_ABI_VERSION, sizeof(al_mailbox_owning_config),
      3u, 72u, 256u, 37u, 2u,
      AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED};
  al_mailbox_owning_storage_requirements requirements;
  al_mailbox_runtime *runtime = NULL;
  al_mailbox_owning_state_view view0;
  al_mailbox_owning_state_view view1;
  al_mailbox_owning_state_view view2;
  al_mailbox_owning_stats stats_before;
  al_mailbox_owning_stats stats;
  al_mailbox_call_info call_info;
  al_mailbox_token token0;
  al_mailbox_token token1;
  al_mailbox_token token2;
  al_owning_bank_stack_slice roots_before[2];
  uint8_t prefix_before[72];
  uint32_t protected_cursor;
  const uint8_t initial[] = {'I'};
  const uint8_t begin0[] = {'B', 'C'};
  const uint8_t begin1[] = {'D'};
  const uint8_t completion[] = {'Q'};
  const uint8_t begin2[] = {'E'};
  const uint8_t completion1[] = {'R'};

  assert(al_mailbox_get_owning_storage_requirements(
             &test_module, &config, &requirements) == AL_MAILBOX_OK);
  assert(requirements.storage_bytes <= sizeof(storage));
  assert(requirements.scratch_reserved_bytes == 2u * (72u + 2u * 9u));
  assert(requirements.storage_bytes == requirements.retained_reserved_bytes +
                                          requirements.scratch_reserved_bytes +
                                          requirements.text_staging_reserved_bytes +
                                          requirements.controller_reserved_bytes);
  assert(al_mailbox_runtime_init_owning(
             &test_module, &config, storage, sizeof(storage), &runtime) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_init_text(runtime, 0u, initial, sizeof(initial),
                              &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_init_text(runtime, 1u, initial, sizeof(initial),
                              &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_init_text(runtime, 2u, initial, sizeof(initial),
                              &call_info) == AL_MAILBOX_OK);

  assert(al_mailbox_begin_text(runtime, 0u, begin0, sizeof(begin0), &token0,
                               &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_begin_text(runtime, 1u, begin1, sizeof(begin1), &token1,
                               &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view0) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 1u, &view1) ==
         AL_MAILBOX_OK);
  assert(view0.pending == 1u && view1.pending == 1u);
  assert(view0.bank->root_count == 1u && view1.bank->root_count == 1u);
  assert(test_read_i64(view0.bank->bytes) == 1 &&
         test_read_i64(view1.bank->bytes) == 1);
  assert(view0.associated_context != NULL && view1.associated_context != NULL);
  assert(view0.associated_roots != NULL && view1.associated_roots != NULL);
  assert(view0.associated_root_count == 2u &&
         view1.associated_root_count == 2u);
  assert(view0.scratch_slot_index != view1.scratch_slot_index);
  assert(((uintptr_t)view0.associated_context->stack_data & 7u) == 0u);
  assert(((uintptr_t)view1.associated_context->stack_data & 7u) == 0u);
  assert(((uintptr_t)view1.associated_context->stack_data -
          (uintptr_t)view0.associated_context->stack_data) % 8u == 0u);
  assert(view0.associated_context->cursor_bytes <= 72u &&
         view1.associated_context->cursor_bytes <= 72u);
  assert(test_read_i64(view0.associated_context->stack_data +
                       view0.associated_roots[0].source_offset_bytes) == 2);
  assert(test_read_i64(view0.associated_context->stack_data +
                       view0.associated_roots[1].source_offset_bytes) == 2);
  assert(test_read_i64(view1.associated_context->stack_data +
                       view1.associated_roots[0].source_offset_bytes) == 2);
  assert(test_read_i64(view1.associated_context->stack_data +
                       view1.associated_roots[1].source_offset_bytes) == 1);
  protected_cursor = view0.associated_context->cursor_bytes;
  assert(protected_cursor <= sizeof(prefix_before));
  memcpy(prefix_before, view0.associated_context->stack_data,
         protected_cursor);
  memcpy(roots_before, view0.associated_roots, sizeof(roots_before));

  assert(al_mailbox_get_owning_stats(runtime, &stats_before) == AL_MAILBOX_OK);
  assert(stats_before.pinned_scratch_slots == 2u &&
         stats_before.pinned_scratch_bytes == 144u &&
         stats_before.outstanding_scratch_leases == 2u);
  assert(stats_before.begin_publication_copy_bytes == 0u &&
         stats_before.resume_root_import_bytes == 0u);
  assert(al_mailbox_test_set_next_token_sequence(runtime, 77u) ==
         AL_MAILBOX_OK);

  token2.opaque[0] = UINT64_C(0x1111111111111111);
  token2.opaque[1] = UINT64_C(0x2222222222222222);
  token2.opaque[2] = UINT64_C(0x3333333333333333);
  assert(al_mailbox_begin_text(runtime, 2u, begin2, sizeof(begin2), &token2,
                               &call_info) == AL_MAILBOX_SCRATCH_CAPACITY);
  assert(token2.opaque[0] == UINT64_C(0x1111111111111111) &&
         token2.opaque[1] == UINT64_C(0x2222222222222222) &&
         token2.opaque[2] == UINT64_C(0x3333333333333333));
  assert(al_mailbox_get_owning_stats(runtime, &stats) == AL_MAILBOX_OK);
  assert(stats.handler_invocations == stats_before.handler_invocations &&
         stats.utf8_input_bytes == stats_before.utf8_input_bytes &&
         stats.utf16_staging_bytes == stats_before.utf16_staging_bytes &&
         stats.input_import_bytes == stats_before.input_import_bytes);

  test_fail_next_resume = 1u;
  assert(al_mailbox_resume_text(runtime, 0u, &token0, completion,
                                sizeof(completion), &call_info) ==
         AL_MAILBOX_HANDLER_FAILURE);
  assert(call_info.handler_status == AL_OWNING_STATUS_DIAGNOSTIC);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view2) ==
         AL_MAILBOX_OK);
  assert(view2.pending == 1u && view2.associated_context ==
                                    view0.associated_context);
  assert(view2.associated_context->cursor_bytes == protected_cursor);
  assert(memcmp(view2.associated_context->stack_data, prefix_before,
                protected_cursor) == 0);
  assert(memcmp(view2.associated_roots, roots_before, sizeof(roots_before)) ==
         0);
  assert(test_read_i64(view2.bank->bytes) == 1);

  assert(al_mailbox_resume_text(runtime, 0u, &token0, completion,
                                sizeof(completion), &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view2) ==
         AL_MAILBOX_OK);
  assert(view2.pending == 0u && view2.associated_context == NULL &&
         test_read_i64(view2.bank->bytes) == 5);
  assert(al_mailbox_get_owning_stats(runtime, &stats) == AL_MAILBOX_OK);
  assert(stats.pinned_scratch_slots == 1u &&
         stats.pinned_scratch_bytes == 72u &&
         stats.outstanding_scratch_leases == 1u);

  assert(al_mailbox_begin_text(runtime, 2u, begin2, sizeof(begin2), &token2,
                               &call_info) == AL_MAILBOX_OK);
  assert(token2.opaque[1] == 77u);
  assert(al_mailbox_resume_text(runtime, 1u, &token1, completion1,
                                sizeof(completion1), &call_info) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_resume_text(runtime, 2u, &token2, completion1,
                                sizeof(completion1), &call_info) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_stats(runtime, &stats) == AL_MAILBOX_OK);
  assert(stats.pinned_scratch_slots == 0u &&
         stats.pinned_scratch_bytes == 0u &&
         stats.outstanding_scratch_leases == 0u &&
         stats.begin_publication_copy_bytes == 0u &&
         stats.resume_root_import_bytes == 0u);
  assert(al_mailbox_dispose(runtime) == AL_MAILBOX_OK);
}

static void test_return_cancellation(void) {
  static _Alignas(8) uint8_t storage[65536];
  al_mailbox_owning_config config = {
      AL_MAILBOX_CONTROL_ABI_VERSION, sizeof(al_mailbox_owning_config),
      2u, 72u, 64u, 37u, 1u, AL_MAILBOX_OWNING_POLICY_RETURN};
  al_mailbox_runtime *runtime = NULL;
  al_mailbox_owning_state_view view0;
  al_mailbox_owning_state_view view1;
  al_mailbox_owning_stats before;
  al_mailbox_owning_stats after;
  al_mailbox_call_info call_info;
  al_mailbox_token token0;
  al_mailbox_token token1;
  al_mailbox_token token2;
  al_mailbox_token stale;
  al_owning_bank_root roots_before[2];
  al_owning_bank_root corrupt_roots_before[2];
  uint8_t bank_backing_before[64];
  uint32_t bank_capacity;
  HANDLE worker;
  DWORD worker_exit_code;
  test_cancel_thread_call worker_call;
  const uint8_t initial[] = {'I'};
  const uint8_t begin0[] = {'B', 'C'};
  const uint8_t begin1[] = {'D'};
  const uint8_t next_begin[] = {'E'};
  const uint8_t completion[] = {'Q'};

  assert(al_mailbox_runtime_init_owning(
             &test_module, &config, storage, sizeof(storage), &runtime) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_init_text(runtime, 0u, initial, sizeof(initial),
                              &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_init_text(runtime, 1u, initial, sizeof(initial),
                              &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_begin_text(runtime, 0u, begin0, sizeof(begin0), &token0,
                               &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_begin_text(runtime, 1u, begin1, sizeof(begin1), &token1,
                               &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view0) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 1u, &view1) ==
         AL_MAILBOX_OK);
  assert(view0.pending == 1u && view0.bank->root_count == 2u &&
         test_read_i64(view0.bank->bytes) == 2);
  bank_capacity = view0.bank->byte_capacity;
  assert(bank_capacity <= sizeof(bank_backing_before));
  memcpy(bank_backing_before, view0.bank->bytes, bank_capacity);
  memcpy(roots_before, view0.bank->roots, sizeof(roots_before));
  assert(view1.pending == 1u && view1.bank->root_count == 2u &&
         test_read_i64(view1.bank->bytes) == 2);
  assert(al_mailbox_get_owning_stats(runtime, &before) == AL_MAILBOX_OK);

  worker_call = (test_cancel_thread_call){runtime, 0u, &token0, AL_MAILBOX_OK};
  worker = CreateThread(NULL, 0u, test_cancel_from_worker_thread, &worker_call,
                        0u, NULL);
  assert(worker != NULL);
  assert(WaitForSingleObject(worker, INFINITE) == WAIT_OBJECT_0);
  assert(GetExitCodeThread(worker, &worker_exit_code) != 0 &&
         worker_exit_code == 0u);
  assert(CloseHandle(worker) != 0);
  assert(worker_call.result == AL_MAILBOX_WRONG_THREAD);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view0) ==
         AL_MAILBOX_OK);
  assert(view0.pending == 1u && view0.bank->root_count == 2u &&
         view0.bank->used_bytes == 16u &&
         memcmp(view0.bank->bytes, bank_backing_before, bank_capacity) == 0 &&
         memcmp(view0.bank->roots, roots_before, sizeof(roots_before)) == 0);

  assert(al_mailbox_cancel_text(runtime, 0u, &token1) ==
         AL_MAILBOX_WRONG_OWNER_TOKEN);
  stale = token0;
  ++stale.opaque[1];
  assert(al_mailbox_cancel_text(runtime, 0u, &stale) ==
         AL_MAILBOX_STALE_TOKEN);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view0) ==
         AL_MAILBOX_OK);
  assert(view0.pending == 1u && view0.bank->root_count == 2u &&
         test_read_i64(view0.bank->bytes) == 2);

  /* Defensive host-boundary test: deliberately corrupt the borrowed metadata
   * to prove a failed RETURN trim preserves the pending operation for repair. */
  ((al_owning_bank_root *)(void *)view0.bank->roots)[1].owner_end_bytes -= 1u;
  memcpy(corrupt_roots_before, view0.bank->roots,
         sizeof(corrupt_roots_before));
  assert(al_mailbox_cancel_text(runtime, 0u, &token0) ==
         AL_MAILBOX_INVALID_REFERENCE);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view0) ==
         AL_MAILBOX_OK);
  assert(view0.pending == 1u && view0.bank->root_count == 2u &&
         view0.bank->used_bytes == 16u &&
         memcmp(view0.bank->bytes, bank_backing_before, bank_capacity) == 0 &&
         memcmp(view0.bank->roots, corrupt_roots_before,
                sizeof(corrupt_roots_before)) == 0);
  memcpy((al_owning_bank_root *)(void *)view0.bank->roots, roots_before,
         sizeof(roots_before));

  assert(al_mailbox_cancel_text(runtime, 0u, &token0) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view0) ==
         AL_MAILBOX_OK);
  assert(view0.pending == 0u && view0.bank->root_count == 1u &&
         view0.bank->used_bytes == 8u &&
         test_read_i64(view0.bank->bytes) == 2 &&
         view0.bank->byte_capacity == bank_capacity &&
         memcmp(view0.bank->bytes, bank_backing_before, bank_capacity) == 0);
  assert(al_mailbox_get_owning_state_view(runtime, 1u, &view1) ==
         AL_MAILBOX_OK);
  assert(view1.pending == 1u && view1.bank->root_count == 2u &&
         test_read_i64(view1.bank->bytes) == 2);
  assert(al_mailbox_get_owning_stats(runtime, &after) == AL_MAILBOX_OK);
  assert(after.publication_copy_bytes == before.publication_copy_bytes);

  assert(al_mailbox_cancel_text(runtime, 0u, &token0) ==
         AL_MAILBOX_DUPLICATE_TOKEN);
  assert(al_mailbox_resume_text(runtime, 0u, &token0, completion,
                                sizeof(completion), &call_info) ==
         AL_MAILBOX_DUPLICATE_TOKEN);
  assert(al_mailbox_begin_text(runtime, 0u, next_begin, sizeof(next_begin),
                               &token2, &call_info) == AL_MAILBOX_OK);
  assert(token2.opaque[1] == 3u);
  assert(al_mailbox_resume_text(runtime, 0u, &token0, completion,
                                sizeof(completion), &call_info) ==
         AL_MAILBOX_DUPLICATE_TOKEN);
  assert(al_mailbox_cancel_text(runtime, 0u, &token2) == AL_MAILBOX_OK);
  assert(al_mailbox_cancel_text(runtime, 0u, &token0) == AL_MAILBOX_STALE_TOKEN);
  assert(al_mailbox_cancel_text(runtime, 1u, &token1) == AL_MAILBOX_OK);
  assert(al_mailbox_dispose(runtime) == AL_MAILBOX_OK);
}

static void test_keep_cancellation(void) {
  static _Alignas(8) uint8_t storage[65536];
  al_mailbox_owning_config config = {
      AL_MAILBOX_CONTROL_ABI_VERSION, sizeof(al_mailbox_owning_config),
      3u, 72u, 64u, 37u, 2u,
      AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED};
  al_mailbox_runtime *runtime = NULL;
  al_mailbox_owning_state_view view0;
  al_mailbox_owning_state_view view1;
  al_mailbox_owning_stats before;
  al_mailbox_owning_stats after;
  al_mailbox_call_info call_info;
  al_mailbox_token token0;
  al_mailbox_token token1;
  al_mailbox_token token2;
  al_mailbox_token token3;
  al_mailbox_token stale;
  al_owning_bank_stack_slice roots_before[2];
  al_owning_bank_stack_slice corrupt_roots_before[2];
  uint8_t prefix_before[72];
  uint8_t retained_state_before[64];
  uint32_t protected_cursor;
  uint32_t retained_state_bytes;
  const uint8_t initial[] = {'I'};
  const uint8_t begin0[] = {'B', 'C'};
  const uint8_t begin1[] = {'D'};
  const uint8_t begin2[] = {'F'};
  const uint8_t next_begin[] = {'E'};
  const uint8_t completion[] = {'R'};

  assert(al_mailbox_runtime_init_owning(
             &test_module, &config, storage, sizeof(storage), &runtime) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_init_text(runtime, 0u, initial, sizeof(initial),
                              &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_init_text(runtime, 1u, initial, sizeof(initial),
                              &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_init_text(runtime, 2u, initial, sizeof(initial),
                              &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_begin_text(runtime, 0u, begin0, sizeof(begin0), &token0,
                               &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_begin_text(runtime, 1u, begin1, sizeof(begin1), &token1,
                               &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view0) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 1u, &view1) ==
         AL_MAILBOX_OK);
  assert(view0.pending == 1u && view1.pending == 1u &&
         view0.associated_context != NULL && view1.associated_context != NULL);
  assert(test_read_i64(view0.associated_context->stack_data +
                       view0.associated_roots[0].source_offset_bytes) == 2);
  protected_cursor = view0.associated_context->cursor_bytes;
  assert(protected_cursor <= sizeof(prefix_before));
  memcpy(prefix_before, view0.associated_context->stack_data,
         protected_cursor);
  memcpy(roots_before, view0.associated_roots, sizeof(roots_before));
  retained_state_bytes = view0.bank->used_bytes;
  assert(retained_state_bytes <= sizeof(retained_state_before));
  memcpy(retained_state_before, view0.bank->bytes, retained_state_bytes);
  assert(al_mailbox_get_owning_stats(runtime, &before) == AL_MAILBOX_OK);

  assert(al_mailbox_cancel_text(runtime, 0u, &token1) ==
         AL_MAILBOX_WRONG_OWNER_TOKEN);
  stale = token0;
  ++stale.opaque[1];
  assert(al_mailbox_cancel_text(runtime, 0u, &stale) ==
         AL_MAILBOX_STALE_TOKEN);
  ((al_owning_bank_stack_slice *)(void *)view0.associated_roots)
      ->source_owner_end_bytes =
      roots_before[0].source_offset_bytes;
  memcpy(corrupt_roots_before, view0.associated_roots,
         sizeof(corrupt_roots_before));
  assert(al_mailbox_cancel_text(runtime, 0u, &token0) ==
         AL_MAILBOX_INVALID_REFERENCE);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view0) ==
         AL_MAILBOX_OK);
  assert(view0.pending == 1u && view0.associated_context != NULL &&
         view0.associated_context->cursor_bytes == protected_cursor);
  assert(memcmp(view0.associated_context->stack_data, prefix_before,
                protected_cursor) == 0);
  assert(memcmp(view0.associated_roots, corrupt_roots_before,
                sizeof(corrupt_roots_before)) == 0);
  assert(view0.pending == 1u && view0.bank->used_bytes == retained_state_bytes &&
         view0.bank->root_count == 1u &&
         memcmp(view0.bank->bytes, retained_state_before,
                retained_state_bytes) == 0);
  memcpy((al_owning_bank_stack_slice *)(void *)view0.associated_roots,
         roots_before, sizeof(roots_before));

  assert(al_mailbox_cancel_text(runtime, 0u, &token0) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view0) ==
         AL_MAILBOX_OK);
  assert(view0.pending == 0u && view0.associated_context == NULL &&
         view0.bank->root_count == 1u && view0.bank->used_bytes == 8u &&
         test_read_i64(view0.bank->bytes) == 2);
  assert(al_mailbox_get_owning_state_view(runtime, 1u, &view1) ==
         AL_MAILBOX_OK);
  assert(view1.pending == 1u && view1.associated_context != NULL &&
         test_read_i64(view1.bank->bytes) == 1);
  assert(al_mailbox_get_owning_stats(runtime, &after) == AL_MAILBOX_OK);
  assert(after.publication_copy_bytes == before.publication_copy_bytes + 8u &&
         after.pinned_scratch_slots == 1u);

  assert(al_mailbox_begin_text(runtime, 2u, begin2, sizeof(begin2), &token2,
                               &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_cancel_text(runtime, 0u, &token0) ==
         AL_MAILBOX_DUPLICATE_TOKEN);
  assert(al_mailbox_resume_text(runtime, 0u, &token0, completion,
                                sizeof(completion), &call_info) ==
         AL_MAILBOX_DUPLICATE_TOKEN);
  assert(al_mailbox_cancel_text(runtime, 1u, &token1) == AL_MAILBOX_OK);
  assert(al_mailbox_cancel_text(runtime, 2u, &token2) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_stats(runtime, &after) == AL_MAILBOX_OK);
  assert(after.pinned_scratch_slots == 0u &&
         after.outstanding_scratch_leases == 0u);

  assert(al_mailbox_begin_text(runtime, 0u, next_begin, sizeof(next_begin),
                               &token3, &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_resume_text(runtime, 0u, &token0, completion,
                                sizeof(completion), &call_info) ==
         AL_MAILBOX_DUPLICATE_TOKEN);
  assert(al_mailbox_cancel_text(runtime, 0u, &token0) ==
         AL_MAILBOX_DUPLICATE_TOKEN);
  assert(al_mailbox_resume_text(runtime, 0u, &token3, completion,
                                sizeof(completion), &call_info) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_resume_text(runtime, 0u, &token0, completion,
                                sizeof(completion), &call_info) ==
         AL_MAILBOX_STALE_TOKEN);
  assert(al_mailbox_cancel_text(runtime, 0u, &token0) == AL_MAILBOX_STALE_TOKEN);
  assert(al_mailbox_dispose(runtime) == AL_MAILBOX_OK);
}

static void test_keep_state_admission_capacity(void) {
  static _Alignas(8) uint8_t storage[65536];
  al_mailbox_owning_config config = {
      AL_MAILBOX_CONTROL_ABI_VERSION, sizeof(al_mailbox_owning_config),
      1u, 128u, 16u, 128u, 1u,
      AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED};
  al_mailbox_runtime *runtime = NULL;
  al_mailbox_owning_state_view view;
  al_mailbox_owning_stats before_cancel;
  al_mailbox_owning_stats after_failed_cancel;
  al_mailbox_owning_stats after_cancel;
  al_mailbox_call_info call_info;
  al_mailbox_token token;
  al_owning_bank_stack_slice roots_before[2];
  al_owning_bank_stack_slice corrupt_roots_before[2];
  const uint8_t initial[] = {'I'};
  const uint8_t large_state[] = "abcdefghijkl";
  const uint8_t admitted_state[] = {'B'};
  uint8_t initial_copy[24];
  uint8_t retained_before[24];
  uint8_t corrupted_prefix[128];
  uint32_t protected_cursor;

  assert(al_mailbox_runtime_init_owning(
             &test_text_state_module, &config, storage, sizeof(storage),
             &runtime) == AL_MAILBOX_OK);
  assert(al_mailbox_init_text(runtime, 0u, initial, sizeof(initial),
                              &call_info) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.bank->root_count == 1u && view.bank->used_bytes == 16u);
  memcpy(initial_copy, view.bank->bytes, view.bank->used_bytes);

  memset(&token, 0x5a, sizeof(token));
  assert(al_mailbox_begin_text(runtime, 0u, large_state,
                               (uint32_t)sizeof(large_state) - 1u, &token,
                               &call_info) == AL_MAILBOX_RETAINED_CAPACITY);
  assert(token.opaque[0] == UINT64_C(0x5a5a5a5a5a5a5a5a) &&
         token.opaque[1] == UINT64_C(0x5a5a5a5a5a5a5a5a) &&
         token.opaque[2] == UINT64_C(0x5a5a5a5a5a5a5a5a));
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.pending == 0u && view.associated_context == NULL &&
         view.bank->root_count == 1u && view.bank->used_bytes == 16u &&
         memcmp(view.bank->bytes, initial_copy, 16u) == 0);
  assert(al_mailbox_get_owning_stats(runtime, &before_cancel) == AL_MAILBOX_OK);
  assert(before_cancel.pending_mailboxes == 0u &&
         before_cancel.pinned_scratch_slots == 0u);

  assert(al_mailbox_begin_text(runtime, 0u, admitted_state,
                               sizeof(admitted_state), &token,
                               &call_info) == AL_MAILBOX_OK);
  assert(token.opaque[1] == 1u);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.pending == 1u && view.associated_context != NULL &&
         view.associated_root_count == 2u && view.bank->used_bytes == 16u);
  protected_cursor = view.associated_context->cursor_bytes;
  assert(protected_cursor == 24u &&
         protected_cursor <= sizeof(corrupted_prefix));
  memcpy(roots_before, view.associated_roots, sizeof(roots_before));
  memcpy(retained_before, view.bank->bytes, view.bank->used_bytes);
  assert(al_mailbox_get_owning_stats(runtime, &before_cancel) == AL_MAILBOX_OK);

  /* Defensive host-boundary test: corrupt the borrowed State descriptor and
   * enlarge its valid dynamic extent so inactive-bank staging hits capacity.
   * A normal admitted Begin cannot reach this state: its State was preflighted. */
  view.associated_context->stack_data[0] = 8u;
  view.associated_context->stack_data[1] = 0u;
  view.associated_context->stack_data[2] = 0u;
  view.associated_context->stack_data[3] = 0u;
  ((al_owning_bank_stack_slice *)(void *)view.associated_roots)
      ->source_owner_end_bytes = protected_cursor;
  memcpy(corrupt_roots_before, view.associated_roots,
         sizeof(corrupt_roots_before));
  memcpy(corrupted_prefix, view.associated_context->stack_data,
         protected_cursor);
  assert(al_mailbox_cancel_text(runtime, 0u, &token) ==
         AL_MAILBOX_RETAINED_CAPACITY);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.pending == 1u && view.associated_context != NULL &&
         view.associated_context->cursor_bytes == protected_cursor &&
         view.associated_root_count == 2u);
  assert(memcmp(view.associated_context->stack_data, corrupted_prefix,
                protected_cursor) == 0);
  assert(memcmp(view.associated_roots, corrupt_roots_before,
                sizeof(corrupt_roots_before)) == 0);
  assert(view.bank->used_bytes == 16u && view.bank->root_count == 1u &&
         memcmp(view.bank->bytes, retained_before, 16u) == 0);
  assert(al_mailbox_get_owning_stats(runtime, &after_failed_cancel) ==
         AL_MAILBOX_OK);
  assert(after_failed_cancel.pending_mailboxes == 1u &&
         after_failed_cancel.pinned_scratch_slots == 1u &&
         after_failed_cancel.outstanding_scratch_leases == 1u &&
         after_failed_cancel.publication_copy_bytes ==
             before_cancel.publication_copy_bytes);

  view.associated_context->stack_data[0] = 1u;
  view.associated_context->stack_data[1] = 0u;
  view.associated_context->stack_data[2] = 0u;
  view.associated_context->stack_data[3] = 0u;
  memcpy((al_owning_bank_stack_slice *)(void *)view.associated_roots,
         roots_before, sizeof(roots_before));
  assert(al_mailbox_cancel_text(runtime, 0u, &token) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) == AL_MAILBOX_OK);
  assert(view.pending == 0u && view.bank->root_count == 1u &&
         view.bank->used_bytes == 16u && test_read_u32(view.bank->bytes) == 1u &&
         view.bank->bytes[8u] == (uint8_t)'B');
  assert(al_mailbox_get_owning_stats(runtime, &after_cancel) == AL_MAILBOX_OK);
  assert(after_cancel.pending_mailboxes == 0u &&
         after_cancel.pinned_scratch_slots == 0u &&
         after_cancel.outstanding_scratch_leases == 0u &&
         after_cancel.publication_copy_bytes ==
             before_cancel.publication_copy_bytes + 16u);
  assert(al_mailbox_dispose(runtime) == AL_MAILBOX_OK);
}

static void test_reset_profile_payload_scope(void) {
  static _Alignas(8) uint8_t storage[65536];
  const uint32_t scratch_capacity = 72u;
  const uint8_t initial[] = {'I'};
  const uint8_t begin0[] = {'B'};
  const uint8_t begin1[] = {'C'};
  const uint8_t sentinel = 0x5au;
  al_mailbox_owning_config config = {
      AL_MAILBOX_CONTROL_ABI_VERSION, sizeof(al_mailbox_owning_config),
      1u, scratch_capacity, 64u, 37u, 1u,
      AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED};
  al_mailbox_runtime *runtime = NULL;
  al_mailbox_owning_state_view view;
  al_mailbox_owning_stats owning_stats;
  al_mailbox_reset_stats reset_stats;
  al_owning_stack_context probe_context;
  al_mailbox_token first_token;
  al_mailbox_token second_token;
  test_reset_stats_thread_call thread_call;
  HANDLE worker;
  uint32_t bitmap_bytes = scratch_capacity / 8u +
                          (scratch_capacity % 8u != 0u ? 1u : 0u);
#if defined(AL_MAILBOX_FAST_RESET) && AL_MAILBOX_FAST_RESET
  const uint32_t expected_profile = AL_MAILBOX_RESET_PROFILE_FAST;
#else
  const uint32_t expected_profile = AL_MAILBOX_RESET_PROFILE_DIAGNOSTIC;
#endif

  assert(al_mailbox_get_reset_stats(NULL, &reset_stats) ==
         AL_MAILBOX_INVALID_ARGUMENT);
  assert(al_mailbox_runtime_init_owning(
             &test_module, &config, storage, sizeof(storage), &runtime) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_get_reset_stats(runtime, NULL) ==
         AL_MAILBOX_INVALID_ARGUMENT);
  assert(al_mailbox_get_reset_stats(
             runtime,
             (al_mailbox_reset_stats *)(void *)((uint8_t *)&reset_stats + 1u)) ==
         AL_MAILBOX_INVALID_ARGUMENT);
  assert(al_mailbox_get_reset_stats(
             runtime, (al_mailbox_reset_stats *)(void *)runtime) ==
         AL_MAILBOX_INVALID_ARGUMENT);

  memset(&thread_call, 0, sizeof(thread_call));
  thread_call.runtime = runtime;
  worker = CreateThread(NULL, 0u, test_get_reset_stats_from_worker_thread,
                        &thread_call, 0u, NULL);
  assert(worker != NULL);
  assert(WaitForSingleObject(worker, INFINITE) == WAIT_OBJECT_0);
  assert(CloseHandle(worker) != 0);
  assert(thread_call.result == AL_MAILBOX_WRONG_THREAD);

  assert(al_mailbox_init_text(runtime, 0u, initial, sizeof(initial), NULL) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_begin_text(runtime, 0u, begin0, sizeof(begin0),
                               &first_token, NULL) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) == AL_MAILBOX_OK);
  assert(view.pending == 1u && view.associated_context != NULL &&
         view.associated_context->cursor_bytes < scratch_capacity - 1u);
  view.associated_context->stack_data[scratch_capacity - 1u] = sentinel;
  assert(al_mailbox_cancel_text(runtime, 0u, &first_token) == AL_MAILBOX_OK);

  assert(al_mailbox_begin_text(runtime, 0u, begin1, sizeof(begin1),
                               &second_token, NULL) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) == AL_MAILBOX_OK);
  assert(view.pending == 1u && view.associated_context != NULL);
#if defined(AL_MAILBOX_FAST_RESET) && AL_MAILBOX_FAST_RESET
  assert(view.associated_context->stack_data[scratch_capacity - 1u] == sentinel);
#else
  assert(view.associated_context->stack_data[scratch_capacity - 1u] ==
         0xA5u);
#endif
  probe_context = *view.associated_context;
  probe_context.cursor_bytes = scratch_capacity;
  probe_context.peak_cursor_bytes = scratch_capacity;
  probe_context.status = AL_OWNING_STATUS_OK;
  assert(al_owning_check_initialized(&probe_context, scratch_capacity - 1u,
                                     1u) != 0);
  assert(probe_context.status == AL_OWNING_STATUS_INTERNAL);
  assert(al_mailbox_cancel_text(runtime, 0u, &second_token) == AL_MAILBOX_OK);

  assert(al_mailbox_get_owning_stats(runtime, &owning_stats) == AL_MAILBOX_OK);
  assert(al_mailbox_get_reset_stats(runtime, &reset_stats) == AL_MAILBOX_OK);
  assert(reset_stats.control_abi_version == AL_MAILBOX_CONTROL_ABI_VERSION &&
         reset_stats.struct_size == sizeof(reset_stats) &&
         reset_stats.reset_profile == expected_profile &&
         reset_stats.reserved == 0u);
  assert(owning_stats.scratch_lease_acquisitions == 3u &&
         owning_stats.scratch_lease_returns == 3u &&
         owning_stats.turn_reset_bytes ==
             reset_stats.turn_reset_cursor_extent_bytes);
#if defined(AL_MAILBOX_FAST_RESET) && AL_MAILBOX_FAST_RESET
  assert(reset_stats.full_capacity_payload_write_bytes_requested == 0u);
#else
  assert(reset_stats.full_capacity_payload_write_bytes_requested ==
         (uint64_t)owning_stats.scratch_lease_returns * scratch_capacity * 2u);
#endif
  assert(reset_stats.live_prefix_payload_write_bytes_requested ==
         reset_stats.turn_reset_cursor_extent_bytes);
  assert(reset_stats.bitmap_store_operations ==
         2u * reset_stats.turn_reset_cursor_extent_bytes +
             4u * bitmap_bytes * owning_stats.scratch_lease_returns);
  assert(al_mailbox_dispose(runtime) == AL_MAILBOX_OK);
  assert(al_mailbox_get_reset_stats(runtime, &reset_stats) == AL_MAILBOX_OK);
  assert(al_mailbox_dispose(runtime) == AL_MAILBOX_OK);
}

int main(void) {
  static _Alignas(8) uint8_t storage[65536];
  al_mailbox_owning_config config = {
      AL_MAILBOX_CONTROL_ABI_VERSION, sizeof(al_mailbox_owning_config),
      1u, 32u, 1024u, 128u, 1u, AL_MAILBOX_OWNING_POLICY_RETURN};
  al_mailbox_owning_storage_requirements requirements;
  al_mailbox_runtime *runtime = NULL;
  al_mailbox_call_info call_info;
  al_mailbox_owning_state_view view;
  al_mailbox_owning_stats stats;
  al_mailbox_token token;
  uint8_t active_copy[64];
  uint8_t malformed_utf8[] = {0xc0u, 0x80u};
  uint32_t active_bytes;
  uint32_t active_roots;
  const uint8_t ascii[] = {'A'};
  const uint8_t oversized_begin[] = {'1', '2', '3', '4', '5',
                                     '6', '7', '8', '9'};
  const uint8_t supplementary[] = {0xf0u, 0x9fu, 0x99u, 0x82u};
  const uint8_t failed_resume[] = {'Z'};
  const uint8_t successful_resume[] = {'Q'};

  assert(al_mailbox_get_owning_storage_requirements(
             &test_module, &config, &requirements) == AL_MAILBOX_OK);
  assert(requirements.storage_bytes <= sizeof(storage));
  assert(al_mailbox_runtime_init_owning(
             &test_module, &config, storage, sizeof(storage), &runtime) ==
         AL_MAILBOX_OK);
  assert(runtime != NULL);

  assert(al_mailbox_init_text(runtime, 0u, malformed_utf8,
                              sizeof(malformed_utf8), &call_info) ==
         AL_MAILBOX_INVALID_TEXT_ENCODING);
  assert(al_mailbox_init_text(runtime, 0u, ascii, sizeof(ascii), &call_info) ==
         AL_MAILBOX_OK);
  assert(call_info.handler_status == AL_OWNING_STATUS_OK);
  assert(call_info.error_argument0 == 0 && call_info.error_argument1 == 0);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.bank->root_count == 1u && view.bank->used_bytes == 8u);
  assert(test_read_i64(view.bank->bytes) == 1);

  token.opaque[0] = UINT64_C(0x1111111111111111);
  token.opaque[1] = UINT64_C(0x2222222222222222);
  token.opaque[2] = UINT64_C(0x3333333333333333);
  assert(al_mailbox_begin_text(runtime, 0u, oversized_begin,
                               sizeof(oversized_begin), &token, &call_info) ==
         AL_MAILBOX_SCRATCH_CAPACITY);
  assert(token.opaque[0] == UINT64_C(0x1111111111111111) &&
         token.opaque[1] == UINT64_C(0x2222222222222222) &&
         token.opaque[2] == UINT64_C(0x3333333333333333));
  assert(al_mailbox_get_owning_stats(runtime, &stats) == AL_MAILBOX_OK);
  assert(stats.handler_invocations == 1u && stats.utf8_input_bytes == 1u &&
         stats.utf16_staging_bytes == 16u && stats.input_import_bytes == 16u);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.pending == 0u && view.bank->root_count == 1u &&
         test_read_i64(view.bank->bytes) == 1);

  assert(al_mailbox_begin_text(runtime, 0u, supplementary,
                               sizeof(supplementary), &token, &call_info) ==
         AL_MAILBOX_OK);
  assert(call_info.handler_status == AL_OWNING_STATUS_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.pending != 0u && view.bank->root_count == 2u);
  assert(test_read_i64(view.bank->bytes + view.bank->roots[0].offset_bytes) ==
         2);
  assert(test_read_i64(view.bank->bytes + view.bank->roots[1].offset_bytes) ==
         2);
  active_bytes = view.bank->used_bytes;
  active_roots = view.bank->root_count;
  assert(active_bytes <= sizeof(active_copy));
  memcpy(active_copy, view.bank->bytes, active_bytes);

  assert(al_mailbox_resume_text(runtime, 0u, &token, malformed_utf8,
                                sizeof(malformed_utf8), &call_info) ==
         AL_MAILBOX_INVALID_TEXT_ENCODING);
  test_assert_active_bytes(runtime, active_copy, active_bytes, active_roots, 1u);

  test_fail_next_resume = 1u;
  assert(al_mailbox_resume_text(runtime, 0u, &token, failed_resume,
                                sizeof(failed_resume), &call_info) ==
         AL_MAILBOX_HANDLER_FAILURE);
  assert(call_info.handler_status == AL_OWNING_STATUS_DIAGNOSTIC);
  test_assert_active_bytes(runtime, active_copy, active_bytes, active_roots, 1u);

  assert(al_mailbox_resume_text(runtime, 0u, &token, successful_resume,
                                sizeof(successful_resume), &call_info) ==
         AL_MAILBOX_OK);
  assert(call_info.handler_status == AL_OWNING_STATUS_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) ==
         AL_MAILBOX_OK);
  assert(view.pending == 0u && view.bank->root_count == 1u);
  assert(test_read_i64(view.bank->bytes) == 5);
  assert(al_mailbox_resume_text(runtime, 0u, &token, successful_resume,
                                sizeof(successful_resume), &call_info) ==
         AL_MAILBOX_DUPLICATE_TOKEN);

  assert(al_mailbox_get_owning_stats(runtime, &stats) == AL_MAILBOX_OK);
  assert(stats.struct_size == sizeof(stats));
  assert(stats.utf8_input_bytes == 7u);
  assert(stats.utf16_staging_bytes == 64u);
  assert(stats.input_import_bytes == 64u);
  assert(stats.publication_copy_bytes == 32u);
  assert(stats.deep_copy_bytes == 0u && stats.move_bytes == 0u);
  assert(stats.returned_output_descriptors == 4u);
  assert(stats.turn_reset_bytes == 96u);
  assert(stats.handler_invocations == 4u && stats.handler_failures == 1u);
  assert(stats.scratch_lease_acquisitions == 4u &&
         stats.scratch_lease_returns == 4u &&
         stats.outstanding_scratch_leases == 0u);

  assert(al_mailbox_dispose(runtime) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_stats(runtime, &stats) == AL_MAILBOX_OK);
  assert(stats.initialized_mailboxes == 0u && stats.live_retained_bytes == 0u &&
         stats.live_retained_roots == 0u && stats.pending_mailboxes == 0u);
  assert(al_mailbox_dispose(runtime) == AL_MAILBOX_OK);
  test_generic_mailbox_enum_preflight();
  test_generic_mailbox_nested_sum_roots();
  test_keep_associated_policy();
  test_return_cancellation();
  test_keep_cancellation();
  test_keep_state_admission_capacity();
  test_reset_profile_payload_scope();
  puts("owning_mailbox_test: passed");
  return 0;
}
