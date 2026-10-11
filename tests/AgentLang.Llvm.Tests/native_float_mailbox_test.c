#include "mailbox_runtime.h"

#include <string.h>

#if defined(_WIN32)
#define AL_TEST_EXPORT __declspec(dllexport)
#else
#define AL_TEST_EXPORT __attribute__((visibility("default")))
#endif

enum { AL_TEST_REPORT_WORDS = 24u };

/* The focused managed test supplies a diagnostic-profile generated module and
 * calls this helper once for each optimization/policy pair. Static storage
 * keeps this harness independent of a host allocator while remaining aligned
 * and disjoint from the module and report buffers. */
static _Alignas(16) uint8_t al_test_mailbox_storage[1024u * 1024u];

static void al_test_fail(uint64_t *report, uint32_t stage,
                         al_mailbox_result result) {
  report[22] = ((uint64_t)stage << 32u) | (uint32_t)result;
}

static void al_test_call_info_init(al_mailbox_call_info *call_info) {
  memset(call_info, 0, sizeof(*call_info));
  call_info->control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  call_info->struct_size = (uint32_t)sizeof(*call_info);
}

static int32_t al_test_capture_state(al_mailbox_runtime *runtime,
                                     uint32_t suspension_policy,
                                     uint32_t stage, uint64_t *report) {
  al_mailbox_owning_state_view view;
  al_mailbox_owning_stats stats;
  const uint8_t *state_bytes = NULL;
  uint64_t float_bits = 0u;
  al_mailbox_result result;

  memset(&view, 0, sizeof(view));
  result = al_mailbox_get_owning_state_view(runtime, 0u, &view);
  if (result != AL_MAILBOX_OK) {
    al_test_fail(report, 10u + stage, result);
    return 0;
  }
  memset(&stats, 0, sizeof(stats));
  result = al_mailbox_get_owning_stats(runtime, &stats);
  if (result != AL_MAILBOX_OK) {
    al_test_fail(report, 20u + stage, result);
    return 0;
  }

  if (view.pending != 0u &&
      suspension_policy == AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED) {
    const al_owning_bank_stack_slice *state_root;
    const al_owning_stack_context *context = view.associated_context;
    if (context == NULL || view.associated_roots == NULL ||
        view.associated_root_count != 2u) {
      al_test_fail(report, 30u + stage, AL_MAILBOX_INVALID_REFERENCE);
      return 0;
    }
    state_root = &view.associated_roots[0];
    if (context->cursor_bytes > context->stack_capacity_bytes ||
        state_root->source_offset_bytes > state_root->source_owner_end_bytes ||
        8u > state_root->source_owner_end_bytes - state_root->source_offset_bytes ||
        state_root->source_owner_end_bytes > context->cursor_bytes ||
        state_root->source_owner_end_bytes > context->stack_capacity_bytes ||
        context->stack_data == NULL) {
      al_test_fail(report, 40u + stage, AL_MAILBOX_INVALID_REFERENCE);
      return 0;
    }
    state_bytes = context->stack_data + state_root->source_offset_bytes;
  } else {
    const al_owning_byte_bank *bank = view.bank;
    const al_owning_bank_root *state_root;
    if (bank == NULL || bank->bytes == NULL || bank->roots == NULL ||
        bank->root_count == 0u) {
      al_test_fail(report, 50u + stage, AL_MAILBOX_INVALID_REFERENCE);
      return 0;
    }
    state_root = &bank->roots[0];
    if (state_root->type_id != view.state_type_id ||
        state_root->extent_bytes < 8u ||
        state_root->offset_bytes > state_root->owner_end_bytes ||
        8u > state_root->owner_end_bytes - state_root->offset_bytes ||
        state_root->offset_bytes > bank->used_bytes ||
        8u > bank->used_bytes - state_root->offset_bytes ||
        state_root->owner_end_bytes > bank->used_bytes) {
      al_test_fail(report, 60u + stage, AL_MAILBOX_INVALID_REFERENCE);
      return 0;
    }
    state_bytes = bank->bytes + state_root->offset_bytes;
  }

  memcpy(&float_bits, state_bytes, sizeof(float_bits));
  report[3u + stage] = view.pending;
  report[6u + stage] = stats.pinned_scratch_slots;
  report[9u + stage] = stats.outstanding_scratch_leases;
  report[12u + stage] = float_bits;
  report[21] = stats.suspension_policy;
  return 1;
}

/* Report indexes: 0..2 API results; 3..5 pending counts; 6..8 pinned slots;
 * 9..11 outstanding leases; 12..14 State Float bits; 15..17 body fuel;
 * 18..20 callback statuses; 21 selected policy; 22 first helper failure;
 * 23 disposal result. */
AL_TEST_EXPORT int32_t agentlang_test_owning_float_mailbox_lifecycle(
    const al_owning_mailbox_module *module, uint32_t suspension_policy,
    uint64_t *report) {
  al_mailbox_owning_config config;
  al_mailbox_owning_storage_requirements requirements;
  al_mailbox_runtime *runtime = NULL;
  al_mailbox_token token;
  al_mailbox_call_info call_info;
  const uint8_t empty_text = 0u;
  al_mailbox_result result;
  uint32_t stage;

  if (module == NULL || report == NULL ||
      ((uintptr_t)report & (uintptr_t)7u) != 0u ||
      suspension_policy > AL_MAILBOX_OWNING_POLICY_KEEP_ASSOCIATED)
    return 1;
  memset(report, 0, sizeof(uint64_t) * AL_TEST_REPORT_WORDS);
  memset(al_test_mailbox_storage, 0, sizeof(al_test_mailbox_storage));
  memset(&config, 0, sizeof(config));
  config.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  config.struct_size = (uint32_t)sizeof(config);
  config.mailbox_capacity = 1u;
  config.scratch_byte_capacity = 4096u;
  config.retained_byte_capacity = 256u;
  config.text_staging_byte_capacity = 64u;
  config.scratch_slot_capacity = 1u;
  config.suspension_policy = suspension_policy;
  memset(&requirements, 0, sizeof(requirements));
  result = al_mailbox_get_owning_storage_requirements(module, &config,
                                                       &requirements);
  if (result != AL_MAILBOX_OK ||
      requirements.storage_bytes > sizeof(al_test_mailbox_storage)) {
    al_test_fail(report, 1u, result == AL_MAILBOX_OK
                                 ? AL_MAILBOX_INVALID_STORAGE
                                 : result);
    return 1;
  }
  result = al_mailbox_runtime_init_owning(
      module, &config, al_test_mailbox_storage,
      sizeof(al_test_mailbox_storage), &runtime);
  if (result != AL_MAILBOX_OK) {
    al_test_fail(report, 2u, result);
    return 1;
  }

  al_test_call_info_init(&call_info);
  result = al_mailbox_init_text(runtime, 0u, &empty_text, 0u, &call_info);
  report[0] = (uint32_t)result;
  report[15] = call_info.steps_consumed;
  report[18] = (uint32_t)call_info.handler_status;
  if (result != AL_MAILBOX_OK) {
    al_test_fail(report, 3u, result);
    goto cleanup;
  }
  stage = 0u;
  if (!al_test_capture_state(runtime, suspension_policy, stage, report))
    goto cleanup;

  memset(&token, 0, sizeof(token));
  al_test_call_info_init(&call_info);
  result = al_mailbox_begin_text(runtime, 0u, &empty_text, 0u, &token,
                                 &call_info);
  report[1] = (uint32_t)result;
  report[16] = call_info.steps_consumed;
  report[19] = (uint32_t)call_info.handler_status;
  if (result != AL_MAILBOX_OK) {
    al_test_fail(report, 4u, result);
    goto cleanup;
  }
  stage = 1u;
  if (!al_test_capture_state(runtime, suspension_policy, stage, report))
    goto cleanup;

  al_test_call_info_init(&call_info);
  result = al_mailbox_resume_text(runtime, 0u, &token, &empty_text, 0u,
                                  &call_info);
  report[2] = (uint32_t)result;
  report[17] = call_info.steps_consumed;
  report[20] = (uint32_t)call_info.handler_status;
  if (result != AL_MAILBOX_OK) {
    al_test_fail(report, 5u, result);
    goto cleanup;
  }
  stage = 2u;
  if (!al_test_capture_state(runtime, suspension_policy, stage, report))
    goto cleanup;

cleanup:
  if (runtime != NULL) {
    report[23] = (uint32_t)al_mailbox_dispose(runtime);
  }
  return report[22] == 0u && report[0] == AL_MAILBOX_OK &&
                 report[1] == AL_MAILBOX_OK &&
                 report[2] == AL_MAILBOX_OK &&
                 report[23] == AL_MAILBOX_OK
             ? 0
             : 1;
}
