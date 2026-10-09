#include "mailbox_runtime.h"

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#if !defined(AL_OWNING_TRUSTED_GENERATED) || !AL_OWNING_TRUSTED_GENERATED
#error "owning_mailbox_release_test requires the trusted generated profile"
#endif

enum {
  TEST_STRING_TYPE_ID = 301u,
  TEST_STATE_TYPE_ID = 302u,
  TEST_CONTINUATION_TYPE_ID = 303u,
  TEST_I64_TYPE_ID = 304u
};

static uint32_t test_callback_work;

static const al_owning_type_descriptor test_types[] = {
    {AL_OWNING_TYPE_STRING, TEST_STRING_TYPE_ID, 0u, 0u,
     AL_OWNING_LAYOUT_DYNAMIC_U32, AL_OWNING_LAYOUT_DYNAMIC_U32, 8u, 8u},
    {AL_OWNING_TYPE_RECORD, TEST_STATE_TYPE_ID, 0u, 1u, 8u, 8u, 8u, 8u},
    {AL_OWNING_TYPE_RECORD, TEST_CONTINUATION_TYPE_ID, 1u, 1u, 8u, 8u, 8u,
     8u},
    {AL_OWNING_TYPE_I64, TEST_I64_TYPE_ID, 0u, 0u, 8u, 8u, 8u, 8u}};

static const al_owning_field_descriptor test_fields[] = {
    {3u, 0u, 0u, 0u}, {3u, 0u, 0u, 0u}};

static const al_owning_layout test_layout = {
    AL_OWNING_LAYOUT_ABI_VERSION, test_types, 4u, test_fields, 2u};

static int64_t test_read_i64(const uint8_t *bytes) {
  uint64_t value = 0u;
  uint32_t index;
  for (index = 0u; index < 8u; ++index)
    value |= (uint64_t)bytes[index] << (index * 8u);
  return (int64_t)value;
}

static int32_t test_profile_preflight(al_owning_stack_context *context) {
  if (al_owning_build_profile_valid(context) == 0)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  ++test_callback_work;
  return AL_OWNING_STATUS_OK;
}

static int32_t test_import_string(al_owning_stack_context *context,
                                  const al_owning_external_slice *input) {
  uint32_t payload_bytes;
  uint32_t extent_bytes;
  uint32_t destination_offset;
  if (input == NULL || input->bytes == NULL || input->type_index != 0u ||
      al_owning_measure_external_value(
          context, &test_layout, input->type_index, input->bytes,
          input->extent_bytes, 0u, 1u, &payload_bytes, &extent_bytes) != 0 ||
      extent_bytes != input->extent_bytes)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  destination_offset = context->cursor_bytes;
  if (al_owning_reserve_to(context, destination_offset + extent_bytes, 1u) != 0)
    return (int32_t)context->status;
  al_owning_copy_external(context, destination_offset, input->bytes,
                           payload_bytes, extent_bytes, TEST_STRING_TYPE_ID);
  return (int32_t)context->status;
}

static int32_t test_write_record(al_owning_stack_context *context,
                                 uint32_t type_index, int64_t value,
                                 al_owning_bank_stack_slice *output) {
  uint32_t offset = context->cursor_bytes;
  if (al_owning_reserve_to(context, offset + 8u, 1u) != 0)
    return (int32_t)context->status;
  al_owning_store_i64(context, offset, value, TEST_I64_TYPE_ID);
  if (context->status != AL_OWNING_STATUS_OK)
    return (int32_t)context->status;
  output->type_index = type_index;
  output->source_offset_bytes = offset;
  output->source_owner_end_bytes = offset + 8u;
  output->reserved = 0u;
  return AL_OWNING_STATUS_OK;
}

static int32_t test_initialize(al_owning_stack_context *context,
                               const al_owning_external_slice *inputs,
                               uint32_t input_count,
                               al_owning_bank_stack_slice *outputs,
                               uint32_t output_capacity) {
  int32_t status;
  if (test_profile_preflight(context) != AL_OWNING_STATUS_OK)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  if (inputs == NULL || input_count != 1u || outputs == NULL ||
      output_capacity != 1u)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  status = test_import_string(context, &inputs[0]);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  return test_write_record(context, 1u, (int64_t)inputs[0].extent_bytes,
                           &outputs[0]);
}

static int32_t test_begin(al_owning_stack_context *context,
                          const al_owning_external_slice *inputs,
                          uint32_t input_count,
                          al_owning_bank_stack_slice *outputs,
                          uint32_t output_capacity) {
  int32_t status;
  if (test_profile_preflight(context) != AL_OWNING_STATUS_OK)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  if (inputs == NULL || input_count != 2u || outputs == NULL ||
      output_capacity != 2u || inputs[0].bytes == NULL)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  status = test_import_string(context, &inputs[1]);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  status = test_write_record(context, 1u,
                             test_read_i64(inputs[0].bytes) + 1,
                             &outputs[0]);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  return test_write_record(context, 2u,
                           (int64_t)inputs[1].extent_bytes, &outputs[1]);
}

static int32_t test_resume(al_owning_stack_context *context,
                           const al_owning_external_slice *inputs,
                           uint32_t input_count,
                           al_owning_bank_stack_slice *outputs,
                           uint32_t output_capacity) {
  int32_t status;
  int64_t state;
  int64_t continuation;
  if (test_profile_preflight(context) != AL_OWNING_STATUS_OK)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  if (inputs == NULL || input_count != 3u || outputs == NULL ||
      output_capacity != 1u || inputs[0].bytes == NULL ||
      inputs[1].bytes == NULL)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  status = test_import_string(context, &inputs[2]);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  state = test_read_i64(inputs[0].bytes);
  continuation = test_read_i64(inputs[1].bytes);
  status = test_write_record(context, 1u,
                             state + continuation + inputs[2].extent_bytes,
                             &outputs[0]);
  return status;
}

static int32_t test_associated_resume(
    al_owning_stack_context *context,
    const al_owning_bank_stack_slice *retained_inputs,
    uint32_t retained_count, const al_owning_external_slice *completion,
    uint32_t protected_cursor_bytes, al_owning_bank_stack_slice *outputs,
    uint32_t output_capacity) {
  int32_t status;
  int64_t state;
  int64_t continuation;
  if (test_profile_preflight(context) != AL_OWNING_STATUS_OK)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  if (retained_inputs == NULL || retained_count != 2u || completion == NULL ||
      outputs == NULL || output_capacity != 1u ||
      context->cursor_bytes != protected_cursor_bytes)
    return AL_OWNING_STATUS_INVALID_REQUEST;
  status = test_import_string(context, completion);
  if (status != AL_OWNING_STATUS_OK)
    return status;
  state = test_read_i64(context->stack_data +
                        retained_inputs[0].source_offset_bytes);
  continuation = test_read_i64(context->stack_data +
                               retained_inputs[1].source_offset_bytes);
  status = test_write_record(context, 1u,
                             state + continuation + completion->extent_bytes,
                             &outputs[0]);
  return status;
}

static const al_owning_mailbox_module test_module = {
    AL_OWNING_MAILBOX_ABI_VERSION,
    sizeof(al_owning_mailbox_module),
    &test_layout,
    {{1u, 1u, {0u, 0u, 0u}, {1u, 0u}, test_initialize},
     {2u, 2u, {1u, 0u, 0u}, {1u, 2u}, test_begin},
     {3u, 1u, {1u, 2u, 0u}, {1u, 0u}, test_resume}},
    test_associated_resume};

static al_mailbox_owning_config test_config(uint32_t scratch_capacity) {
  al_mailbox_owning_config config = {
      AL_MAILBOX_CONTROL_ABI_VERSION,
      sizeof(al_mailbox_owning_config),
      1u,
      scratch_capacity,
      64u,
      37u,
      1u,
      AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED};
  return config;
}

static void test_trusted_storage_and_cancel(void) {
  static _Alignas(8) uint8_t storage[8192];
  const uint8_t initial[] = {'I'};
  const uint8_t message[] = {'B'};
  const uint8_t malformed[] = {0xc0u, 0x80u};
  const uint8_t untouched = 0x7eu;
  al_mailbox_owning_config config = test_config(72u);
  al_mailbox_owning_storage_requirements requirements;
  al_mailbox_owning_state_view view;
  al_mailbox_owning_stats owning_stats;
  al_mailbox_reset_stats reset_stats;
  al_mailbox_call_info call_info;
  al_mailbox_runtime *runtime = NULL;
  al_mailbox_result result;
  al_mailbox_token token;
  uint32_t index;

  memset(storage, untouched, sizeof(storage));
  assert(al_mailbox_get_owning_storage_requirements(
             &test_module, &config, &requirements) == AL_MAILBOX_OK);
  assert(requirements.scratch_reserved_bytes ==
         (uint64_t)config.scratch_byte_capacity *
             config.scratch_slot_capacity);
  assert(requirements.storage_bytes == requirements.retained_reserved_bytes +
                                          requirements.scratch_reserved_bytes +
                                          requirements.text_staging_reserved_bytes +
                                          requirements.controller_reserved_bytes);
  assert(al_mailbox_runtime_init_owning(
             &test_module, &config, storage, sizeof(storage), &runtime) ==
         AL_MAILBOX_OK);
  assert(runtime != NULL);

  assert(al_mailbox_init_text(runtime, 0u, malformed, sizeof(malformed), NULL) ==
         AL_MAILBOX_INVALID_TEXT_ENCODING);
  memset(&call_info, 0, sizeof(call_info));
  result = al_mailbox_init_text(runtime, 0u, initial, sizeof(initial),
                                &call_info);
  if (result != AL_MAILBOX_OK)
    fprintf(stderr, "trusted init failed: result=%d status=%d error=%d\n",
            (int)result, call_info.handler_status,
            call_info.error_metadata_id);
  assert(result == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) == AL_MAILBOX_OK);
  for (index = view.bank->used_bytes; index < view.bank->byte_capacity; ++index)
    assert(view.bank->bytes[index] == untouched);
  assert(al_mailbox_begin_text(runtime, 0u, message, sizeof(message), &token,
                               NULL) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) == AL_MAILBOX_OK);
  assert(view.pending == 1u && view.associated_context != NULL);
  assert(view.associated_context->init_bitmap == NULL &&
         view.associated_context->poison_bitmap == NULL &&
         view.associated_context->init_bitmap_bytes == 0u);
  assert(al_owning_build_profile_valid(view.associated_context) != 0);
  for (index = view.associated_context->cursor_bytes;
       index < config.scratch_byte_capacity; ++index)
    assert(view.associated_context->stack_data[index] == untouched);
  for (index = view.bank->used_bytes; index < view.bank->byte_capacity; ++index)
    assert(view.bank->bytes[index] == untouched);

  assert(al_mailbox_cancel_text(runtime, 0u, &token) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) == AL_MAILBOX_OK);
  assert(view.pending == 0u && view.associated_context == NULL &&
         view.bank->root_count == 1u && test_read_i64(view.bank->bytes) == 17);
  assert(al_mailbox_begin_text(runtime, 0u, message, sizeof(message), &token,
                               NULL) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) == AL_MAILBOX_OK);
  assert(view.pending == 1u && view.associated_context != NULL);
  for (index = view.associated_context->cursor_bytes;
       index < config.scratch_byte_capacity; ++index)
    assert(view.associated_context->stack_data[index] == untouched);
  assert(al_mailbox_cancel_text(runtime, 0u, &token) == AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) == AL_MAILBOX_OK);
  assert(view.pending == 0u && view.associated_context == NULL &&
         view.bank->root_count == 1u && test_read_i64(view.bank->bytes) == 18);
  assert(al_mailbox_get_owning_stats(runtime, &owning_stats) == AL_MAILBOX_OK);
  assert(al_mailbox_get_reset_stats(runtime, &reset_stats) == AL_MAILBOX_OK);
  assert(reset_stats.reset_profile == AL_MAILBOX_RESET_PROFILE_TRUSTED);
  assert(reset_stats.full_capacity_payload_write_bytes_requested == 0u);
  assert(reset_stats.live_prefix_payload_write_bytes_requested == 0u);
  assert(reset_stats.bitmap_store_operations == 0u);
  assert(reset_stats.turn_reset_cursor_extent_bytes > 0u);
  assert(owning_stats.scratch_lease_acquisitions ==
         owning_stats.scratch_lease_returns);
  assert(al_mailbox_dispose(runtime) == AL_MAILBOX_OK);
}

static void test_capacity_preserves_state(void) {
  static _Alignas(8) uint8_t storage[8192];
  const uint8_t initial[] = {'I'};
  const uint8_t message[] = {'B'};
  const uint64_t token_sentinel[3] = {UINT64_C(0x1111111111111111),
                                      UINT64_C(0x2222222222222222),
                                      UINT64_C(0x3333333333333333)};
  al_mailbox_owning_config config = test_config(24u);
  al_mailbox_runtime *runtime = NULL;
  al_mailbox_owning_state_view view;
  al_mailbox_token token;
  uint64_t state_before;

  memset(storage, 0x6du, sizeof(storage));
  memcpy(token.opaque, token_sentinel, sizeof(token_sentinel));
  assert(al_mailbox_runtime_init_owning(
             &test_module, &config, storage, sizeof(storage), &runtime) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_init_text(runtime, 0u, initial, sizeof(initial), NULL) ==
         AL_MAILBOX_OK);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) == AL_MAILBOX_OK);
  state_before = (uint64_t)test_read_i64(view.bank->bytes);
  assert(al_mailbox_begin_text(runtime, 0u, message, sizeof(message), &token,
                               NULL) == AL_MAILBOX_SCRATCH_CAPACITY);
  assert(memcmp(token.opaque, token_sentinel, sizeof(token_sentinel)) == 0);
  assert(al_mailbox_get_owning_state_view(runtime, 0u, &view) == AL_MAILBOX_OK);
  assert(view.pending == 0u &&
         (uint64_t)test_read_i64(view.bank->bytes) == state_before);
  assert(al_mailbox_dispose(runtime) == AL_MAILBOX_OK);
}

static void test_profile_mismatch_is_refused_before_work(void) {
  _Alignas(8) uint8_t stack_data[64];
  _Alignas(8) uint8_t init_bitmap[8];
  _Alignas(8) uint8_t poison_bitmap[8];
  al_owning_stack_context context;
  al_owning_external_slice unused_input;
  al_owning_bank_stack_slice unused_output;
  uint32_t work_before = test_callback_work;

  memset(&context, 0, sizeof(context));
  memset(&unused_input, 0, sizeof(unused_input));
  memset(&unused_output, 0, sizeof(unused_output));
  context.abi_version = AL_OWNING_STACK_ABI_VERSION;
  context.stack_capacity_bytes = sizeof(stack_data);
  context.init_bitmap_bytes = sizeof(init_bitmap);
  context.stack_data = stack_data;
  context.init_bitmap = init_bitmap;
  context.poison_bitmap = poison_bitmap;
  context.status = AL_OWNING_STATUS_OK;

  assert(al_owning_build_profile_valid(&context) == 0);
  assert(test_module.entries[0].execute(&context, &unused_input, 1u,
                                       &unused_output, 1u) ==
         AL_OWNING_STATUS_INVALID_REQUEST);
  assert(test_callback_work == work_before);
}

int main(void) {
  test_profile_mismatch_is_refused_before_work();
  test_trusted_storage_and_cancel();
  test_capacity_preserves_state();
  return 0;
}
