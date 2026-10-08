#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include "mailbox_runtime.h"

#include <inttypes.h>
#include <stdio.h>
#include <string.h>

#define TEST_STORAGE_BYTES 8192u
#define TEST_SCRATCH_BYTES 256u
#define TEST_SCRATCH_NODES 16u
#define TEST_RETAINED_BYTES 128u
#define TEST_RETAINED_NODES 16u
#define TEST_WORKSPACE_SLOTS 6u
#define TEST_STATE_TYPE_ID 3u
#define TEST_CONTINUATION_TYPE_ID 4u
#define TEST_INT_TYPE_ID 0u

static uint32_t test_handler_calls[3];
static uint32_t test_scratch_poison_checks;
static al_mailbox_runtime *test_reentry_runtime;
static uint32_t test_reentry_mailbox_id;
static uint32_t test_reentry_armed;
static al_mailbox_token test_reentry_token;
static al_mailbox_result test_reentry_result;

static const uint32_t test_state_fields[] = {TEST_INT_TYPE_ID};
static const uint32_t test_continuation_fields[] = {TEST_INT_TYPE_ID};
static const al_type_desc test_types[] = {
    {AL_RUNTIME_TYPE_INT, 0u, NULL},
    {AL_RUNTIME_TYPE_BOOL, 0u, NULL},
    {AL_RUNTIME_TYPE_UNIT, 0u, NULL},
    {AL_RUNTIME_TYPE_RECORD, 1u, test_state_fields},
    {AL_RUNTIME_TYPE_RECORD, 1u, test_continuation_fields},
};
static const al_program_desc test_program = {test_types, 5u, 0u};
static const char *const test_type_names[] = {"Int", "Bool", "Unit", "State",
                                              "Continuation"};

static const uint32_t test_begin_inputs[] = {TEST_STATE_TYPE_ID,
                                             TEST_INT_TYPE_ID};
static const uint32_t test_begin_outputs[] = {TEST_STATE_TYPE_ID,
                                              TEST_CONTINUATION_TYPE_ID};
static const uint32_t test_init_inputs[] = {TEST_INT_TYPE_ID};
static const uint32_t test_init_outputs[] = {TEST_STATE_TYPE_ID};
static const uint32_t test_resume_inputs[] = {
    TEST_STATE_TYPE_ID, TEST_CONTINUATION_TYPE_ID, TEST_INT_TYPE_ID};
static const uint32_t test_resume_outputs[] = {TEST_STATE_TYPE_ID};
static const al_module_diagnostic test_resume_diagnostics[] = {
    {77, 1u, 1u, 1u, "RUNTIME_DIVIDE_BY_ZERO", "divide", "mailbox.flow"},
};

static void test_initialize_entry(al_runtime_context *context, int64_t *outputs,
                                  uint32_t output_capacity, int32_t *status);
static void test_begin_entry(al_runtime_context *context, int64_t *outputs,
                             uint32_t output_capacity, int32_t *status);
static void test_resume_entry(al_runtime_context *context, int64_t *outputs,
                              uint32_t output_capacity, int32_t *status);
static void test_unused_entry(al_runtime_context *context, int64_t *outputs,
                              uint32_t output_capacity, int32_t *status);

static const al_module_entry test_entries[] = {
    {sizeof(al_module_entry), 0u, "mailbox.begin", test_begin_entry, 2u, 2u,
     TEST_WORKSPACE_SLOTS, 0u, test_begin_inputs, test_begin_outputs, NULL},
    {sizeof(al_module_entry), 1u, "mailbox.initialize", test_initialize_entry,
     1u, 1u, TEST_WORKSPACE_SLOTS, 0u, test_init_inputs, test_init_outputs,
     NULL},
    {sizeof(al_module_entry), 2u, "mailbox.resume", test_resume_entry, 3u, 1u,
     TEST_WORKSPACE_SLOTS, 1u, test_resume_inputs, test_resume_outputs,
     test_resume_diagnostics},
};

static const al_module_desc test_module = {
    AL_MODULE_ABI_VERSION,
    sizeof(al_module_desc),
    AL_RUNTIME_ABI_VERSION,
    3u,
    {1u,  2u,  3u,  4u,  5u,  6u,  7u,  8u,  9u,  10u, 11u,
     12u, 13u, 14u, 15u, 16u, 17u, 18u, 19u, 20u, 21u, 22u,
     23u, 24u, 25u, 26u, 27u, 28u, 29u, 30u, 31u, 32u},
    &test_program,
    test_entries,
    test_type_names,
};

static uint32_t test_failures;

static void write_json_string(const char *value) {
  const unsigned char *cursor = (const unsigned char *)value;
  (void)putchar('"');
  while (*cursor != 0u) {
    if (*cursor == '"' || *cursor == '\\') {
      (void)putchar('\\');
      (void)putchar((int)*cursor);
    } else if (*cursor == '\n') {
      (void)fputs("\\n", stdout);
    } else if (*cursor == '\r') {
      (void)fputs("\\r", stdout);
    } else if (*cursor == '\t') {
      (void)fputs("\\t", stdout);
    } else {
      (void)putchar((int)*cursor);
    }
    ++cursor;
  }
  (void)putchar('"');
}

static const char *check_case(const char *name) {
  if (strstr(name, "cross-runtime") != NULL) {
    return "cross_runtime_token";
  }
  if (strstr(name, "generation exhaustion") != NULL ||
      strstr(name, "final safe pair") != NULL ||
      strstr(name, "last distinct nonzero") != NULL) {
    return "generation_exhaustion";
  }
  if (strstr(name, "token sequence exhaustion") != NULL ||
      strstr(name, "maximum token sequence") != NULL) {
    return "token_exhaustion";
  }
  if (strstr(name, "module ABI") != NULL ||
      strstr(name, "module rejects") != NULL ||
      strstr(name, "continuation type ID") != NULL ||
      strstr(name, "nominal message type") != NULL ||
      strstr(name, "canonical built-in") != NULL ||
      strstr(name, "canonical Int type ID") != NULL) {
    return "invalid_module";
  }
  if (strstr(name, "storage") != NULL ||
      strstr(name, "descriptor cannot") != NULL) {
    return "invalid_storage";
  }
  if (strstr(name, "disposed") != NULL) {
    return "post_disposal";
  }
  if (strstr(name, "foreign thread") != NULL) {
    return "foreign_thread";
  }
  if (strstr(name, "busy admission") != NULL) {
    return "busy";
  }
  if (strstr(name, "reentrant") != NULL) {
    return "busy";
  }
  if (strstr(name, "alias") != NULL || strstr(name, "overlap") != NULL) {
    return "argument_alias";
  }
  if (strstr(name, "wrong-mailbox token") != NULL) {
    return "wrong_owner_token";
  }
  if (strstr(name, "duplicate token") != NULL ||
      strstr(name, "duplicate identity") != NULL ||
      strstr(name, "completion exactly once") != NULL) {
    return "duplicate_token";
  }
  if (strstr(name, "stale") != NULL ||
      strstr(name, "older completion") != NULL) {
    return "stale_token";
  }
  if (strstr(name, "retained") != NULL || strstr(name, "seven-byte") != NULL) {
    return "retained_capacity";
  }
  if (strstr(name, "scratch") != NULL || strstr(name, "poison") != NULL) {
    return "scratch_cleanup";
  }
  return "general";
}

static void check(uint32_t condition, const char *name) {
  (void)fputs("{\"kind\":\"mailbox-unit-check\",\"case\":", stdout);
  write_json_string(check_case(name));
  (void)fputs(",\"name\":", stdout);
  write_json_string(name);
  (void)printf(",\"passed\":%s}\n", condition != 0u ? "true" : "false");
  if (condition == 0u) {
    (void)fprintf(stderr, "FAIL: %s\n", name);
    ++test_failures;
  }
}

static int32_t test_status_for_runtime(al_runtime_result result) {
  switch (result) {
  case AL_RUNTIME_OK:
    return AL_RUNTIME_STATUS_SUCCESS;
  case AL_RUNTIME_INVALID_REFERENCE:
    return 5;
  case AL_RUNTIME_SCRATCH_CAPACITY:
    return AL_RUNTIME_SCRATCH_CAPACITY;
  case AL_RUNTIME_RETAINED_CAPACITY:
    return AL_RUNTIME_RETAINED_CAPACITY;
  default:
    return AL_RUNTIME_STATUS_INVALID_REQUEST;
  }
}

static uint32_t test_import(al_runtime_context *context, int64_t *outputs,
                            uint32_t output_capacity, int32_t *status,
                            const uint32_t *expected_inputs,
                            uint32_t input_count, uint32_t output_count,
                            uint32_t workspace_capacity) {
  al_runtime_result result =
      al_runtime_validate_request(context, outputs, output_count,
                                  output_capacity, status, workspace_capacity);
  if (result != AL_RUNTIME_OK) {
    return 0u;
  }
  result = al_runtime_import_state(context, &test_program, expected_inputs,
                                   input_count);
  if (result != AL_RUNTIME_OK) {
    *status = test_status_for_runtime(result);
    return 0u;
  }
  return 1u;
}

static uint32_t
test_scratch_and_workspace_are_poisoned(al_runtime_context *context) {
  uint8_t expected[sizeof(int64_t)];
  uint32_t index;

  memset(expected, 0xA5, sizeof(expected));
  if (context == NULL || context->scratch == NULL ||
      context->scratch->used != 0u || context->scratch->node_count != 0u ||
      context->scratch->generation == 0u || context->scratch->data == NULL ||
      context->scratch->byte_capacity == 0u ||
      context->scratch->nodes == NULL ||
      context->scratch->node_capacity == 0u || context->retained == NULL ||
      context->workspace == NULL || context->workspace_capacity == 0u ||
      context->scratch->generation == context->retained->generation ||
      (context->input_owner != NULL &&
       (context->scratch->generation == context->input_owner->generation ||
        context->retained->generation == context->input_owner->generation)) ||
      context->scratch->data[0] != 0xA5u ||
      memcmp(context->scratch->nodes, expected, sizeof(expected)) != 0 ||
      context->retained->used != 0u || context->retained->node_count != 0u ||
      context->retained->data == NULL || context->retained->nodes == NULL ||
      context->retained->data[0] != 0xA5u ||
      memcmp(context->retained->nodes, expected, sizeof(expected)) != 0) {
    return 0u;
  }
  for (index = 0u; index < context->workspace_capacity; ++index) {
    if (memcmp(&context->workspace[index], expected, sizeof(expected)) != 0) {
      return 0u;
    }
  }
  ++test_scratch_poison_checks;
  return 1u;
}

static uint32_t test_make_record(al_runtime_context *context, uint32_t type_id,
                                 const int64_t *fields, uint32_t field_count,
                                 int64_t *out_handle, int32_t *status) {
  al_runtime_result result = al_runtime_make_record(
      context, &test_program, type_id, fields, field_count, out_handle);
  if (result != AL_RUNTIME_OK) {
    *status = test_status_for_runtime(result);
    return 0u;
  }
  return 1u;
}

static uint32_t test_promote(al_runtime_context *context, int64_t *outputs,
                             uint32_t output_capacity,
                             const uint32_t *root_type_ids, uint32_t root_count,
                             int32_t *status) {
  al_runtime_result result =
      al_runtime_promote(context, &test_program, context->workspace,
                         root_type_ids, root_count, outputs, output_capacity);
  if (result != AL_RUNTIME_OK) {
    *status = test_status_for_runtime(result);
    return 0u;
  }
  *status = AL_RUNTIME_STATUS_SUCCESS;
  return 1u;
}

static void test_initialize_entry(al_runtime_context *context, int64_t *outputs,
                                  uint32_t output_capacity, int32_t *status) {
  int64_t *workspace;
  ++test_handler_calls[1];
  if (!test_scratch_and_workspace_are_poisoned(context)) {
    *status = AL_RUNTIME_STATUS_INVALID_REQUEST;
    return;
  }
  if (!test_import(context, outputs, output_capacity, status, test_init_inputs,
                   1u, 1u, TEST_WORKSPACE_SLOTS)) {
    return;
  }
  ++context->steps_consumed;
  workspace = context->workspace;
  if (!test_make_record(context, TEST_STATE_TYPE_ID, &workspace[0], 1u,
                        &workspace[1], status)) {
    return;
  }
  workspace[0] = workspace[1];
  (void)test_promote(context, outputs, output_capacity, test_init_outputs, 1u,
                     status);
}

static void test_begin_entry(al_runtime_context *context, int64_t *outputs,
                             uint32_t output_capacity, int32_t *status) {
  int64_t *workspace;
  ++test_handler_calls[0];
  if (test_reentry_armed != 0u) {
    test_reentry_armed = 0u;
    test_reentry_result =
        al_mailbox_begin(test_reentry_runtime, test_reentry_mailbox_id, 999,
                         NULL, &test_reentry_token, NULL);
  }
  if (!test_scratch_and_workspace_are_poisoned(context)) {
    *status = AL_RUNTIME_STATUS_INVALID_REQUEST;
    return;
  }
  if (!test_import(context, outputs, output_capacity, status, test_begin_inputs,
                   2u, 2u, TEST_WORKSPACE_SLOTS)) {
    return;
  }
  ++context->steps_consumed;
  workspace = context->workspace;
  if (!test_make_record(context, TEST_CONTINUATION_TYPE_ID, &workspace[1], 1u,
                        &workspace[2], status)) {
    return;
  }
  workspace[1] = workspace[2];
  (void)test_promote(context, outputs, output_capacity, test_begin_outputs, 2u,
                     status);
}

static void test_resume_entry(al_runtime_context *context, int64_t *outputs,
                              uint32_t output_capacity, int32_t *status) {
  int64_t *workspace;
  int64_t change;
  int64_t total;
  al_runtime_result result;
  ++test_handler_calls[2];
  if (!test_scratch_and_workspace_are_poisoned(context)) {
    *status = AL_RUNTIME_STATUS_INVALID_REQUEST;
    return;
  }
  if (!test_import(context, outputs, output_capacity, status,
                   test_resume_inputs, 3u, 1u, TEST_WORKSPACE_SLOTS)) {
    return;
  }
  ++context->steps_consumed;
  workspace = context->workspace;
  if (workspace[2] == 0) {
    context->error_metadata_id = 77;
    context->error_argument0 = 1;
    context->error_argument1 = 0;
    *status = AL_RUNTIME_STATUS_DIAGNOSTIC;
    return;
  }
  result = al_runtime_get_field(context, &test_program, TEST_STATE_TYPE_ID,
                                workspace[0], 0u, &workspace[3]);
  if (result != AL_RUNTIME_OK) {
    *status = test_status_for_runtime(result);
    return;
  }
  result =
      al_runtime_get_field(context, &test_program, TEST_CONTINUATION_TYPE_ID,
                           workspace[1], 0u, &workspace[4]);
  if (result != AL_RUNTIME_OK) {
    *status = test_status_for_runtime(result);
    return;
  }
  change = workspace[4] / workspace[2];
  if (__builtin_add_overflow(workspace[3], change, &total)) {
    context->error_metadata_id = 78;
    *status = AL_RUNTIME_STATUS_DIAGNOSTIC;
    return;
  }
  workspace[3] = total;
  if (!test_make_record(context, TEST_STATE_TYPE_ID, &workspace[3], 1u,
                        &workspace[5], status)) {
    return;
  }
  workspace[0] = workspace[5];
  (void)test_promote(context, outputs, output_capacity, test_resume_outputs, 1u,
                     status);
}

static void test_unused_entry(al_runtime_context *context, int64_t *outputs,
                              uint32_t output_capacity, int32_t *status) {
  (void)context;
  (void)outputs;
  (void)output_capacity;
  *status = AL_RUNTIME_STATUS_INVALID_REQUEST;
}

static al_mailbox_config make_config(uint32_t mailbox_count) {
  al_mailbox_config config;
  memset(&config, 0, sizeof(config));
  config.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  config.struct_size = (uint32_t)sizeof(config);
  config.mailbox_capacity = mailbox_count;
  config.scratch_byte_capacity = TEST_SCRATCH_BYTES;
  config.scratch_node_capacity = TEST_SCRATCH_NODES;
  config.retained_byte_capacity = TEST_RETAINED_BYTES;
  config.retained_node_capacity = TEST_RETAINED_NODES;
  config.init_entry_id = 1u;
  config.begin_entry_id = 0u;
  config.resume_entry_id = 2u;
  return config;
}

static al_mailbox_call_limits make_limits(uint32_t bytes, uint32_t nodes) {
  al_mailbox_call_limits limits;
  limits.control_abi_version = AL_MAILBOX_CONTROL_ABI_VERSION;
  limits.struct_size = (uint32_t)sizeof(limits);
  limits.retained_byte_limit = bytes;
  limits.retained_node_limit = nodes;
  return limits;
}

static int64_t view_state_total(const al_mailbox_state_view *view) {
  uint64_t handle;
  uint32_t node_index;
  const al_node *node;
  int64_t total;
  if (view == NULL || view->owner == NULL || view->roots == NULL ||
      view->root_type_ids == NULL || view->root_count == 0u ||
      view->state_type_id != TEST_STATE_TYPE_ID ||
      view->root_type_ids[0] != TEST_STATE_TYPE_ID) {
    return INT64_MIN;
  }
  handle = (uint64_t)view->roots[0];
  node_index = (uint32_t)handle;
  if ((uint32_t)(handle >> 32u) != view->owner->generation ||
      node_index == 0u || node_index > view->owner->node_count) {
    return INT64_MIN;
  }
  node = &view->owner->nodes[node_index - 1u];
  if (node->type_id != TEST_STATE_TYPE_ID || node->field_count != 1u ||
      node->payload_bytes != sizeof(int64_t) ||
      node->payload_offset > view->owner->used ||
      node->payload_bytes > view->owner->used - node->payload_offset) {
    return INT64_MIN;
  }
  memcpy(&total, view->owner->data + node->payload_offset, sizeof(total));
  return total;
}

static uint32_t stale_state_handle_rejected(const al_mailbox_state_view *view,
                                            int64_t stale_handle) {
  __declspec(align(8)) uint8_t scratch_data[TEST_SCRATCH_BYTES];
  __declspec(align(8)) uint8_t retained_data[TEST_RETAINED_BYTES];
  __declspec(align(8)) al_node scratch_nodes[TEST_SCRATCH_NODES];
  __declspec(align(8)) al_node retained_nodes[TEST_RETAINED_NODES];
  __declspec(align(8)) al_runtime_context context;
  __declspec(align(8)) al_arena scratch;
  __declspec(align(8)) al_arena retained;
  __declspec(align(8)) int64_t workspace[TEST_WORKSPACE_SLOTS];
  __declspec(align(8)) int64_t input_roots[1];
  __declspec(align(4)) uint32_t input_types[1] = {TEST_STATE_TYPE_ID};
  __declspec(align(4)) uint32_t expected_types[1] = {TEST_STATE_TYPE_ID};
  al_runtime_result result;

  if (view == NULL || view->owner == NULL ||
      view->owner->generation > UINT32_MAX - 2u) {
    return 0u;
  }
  memset(&context, 0, sizeof(context));
  memset(&scratch, 0, sizeof(scratch));
  memset(&retained, 0, sizeof(retained));
  scratch.data = scratch_data;
  scratch.byte_capacity = sizeof(scratch_data);
  scratch.nodes = scratch_nodes;
  scratch.node_capacity = TEST_SCRATCH_NODES;
  scratch.generation = view->owner->generation + 1u;
  retained.data = retained_data;
  retained.byte_capacity = sizeof(retained_data);
  retained.nodes = retained_nodes;
  retained.node_capacity = TEST_RETAINED_NODES;
  retained.generation = view->owner->generation + 2u;
  input_roots[0] = stale_handle;
  context.abi_version = AL_RUNTIME_ABI_VERSION;
  context.error_metadata_id = -1;
  context.scratch = &scratch;
  context.retained = &retained;
  context.workspace = workspace;
  context.workspace_capacity = TEST_WORKSPACE_SLOTS;
  context.input_owner = view->owner;
  context.input_roots = input_roots;
  context.input_root_type_ids = input_types;
  context.input_root_count = 1u;
  result = al_runtime_import_state(&context, &test_program, expected_types, 1u);
  return result == AL_RUNTIME_INVALID_REFERENCE;
}

static uint32_t get_state(al_mailbox_runtime *runtime, uint32_t mailbox_id,
                          int64_t expected_total, uint32_t expected_pending) {
  al_mailbox_state_view view;
  if (al_mailbox_get_state_view(runtime, mailbox_id, &view) != AL_MAILBOX_OK) {
    return 0u;
  }
  return view.pending == expected_pending &&
         view_state_total(&view) == expected_total &&
         view.root_count == (expected_pending != 0u ? 2u : 1u);
}

typedef struct foreign_call {
  al_mailbox_runtime *runtime;
  al_mailbox_token token;
  al_mailbox_result resume_result;
  al_mailbox_result dispose_result;
  al_mailbox_result init_result;
  al_mailbox_result begin_result;
  al_mailbox_result stats_result;
  al_mailbox_result view_result;
} foreign_call;

static DWORD WINAPI foreign_thread_proc(LPVOID parameter) {
  foreign_call *call = (foreign_call *)parameter;
  call->resume_result =
      al_mailbox_resume(call->runtime, 0u, &call->token, 2, NULL, NULL);
  call->dispose_result = al_mailbox_dispose(call->runtime);
  call->init_result =
      al_mailbox_init_mailbox(call->runtime, 1u, 77, NULL, NULL);
  call->begin_result = al_mailbox_begin(
      call->runtime, 1u, 77, NULL, &(al_mailbox_token){{0u, 0u, 0u}}, NULL);
  call->stats_result =
      al_mailbox_get_stats(call->runtime, &(al_mailbox_runtime_stats){0});
  call->view_result =
      al_mailbox_get_state_view(call->runtime, 0u, &(al_mailbox_state_view){0});
  return 0;
}

static uint32_t run_foreign_thread(al_mailbox_runtime *runtime,
                                   const al_mailbox_token *token) {
  foreign_call call;
  HANDLE thread;
  DWORD wait_result;
  memset(&call, 0, sizeof(call));
  call.runtime = runtime;
  call.token = *token;
  call.resume_result = AL_MAILBOX_OK;
  call.dispose_result = AL_MAILBOX_OK;
  thread = CreateThread(NULL, 0u, foreign_thread_proc, &call, 0u, NULL);
  if (thread == NULL) {
    return 0u;
  }
  wait_result = WaitForSingleObject(thread, INFINITE);
  (void)CloseHandle(thread);
  return wait_result == WAIT_OBJECT_0 &&
         call.resume_result == AL_MAILBOX_WRONG_THREAD &&
         call.dispose_result == AL_MAILBOX_WRONG_THREAD &&
         call.init_result == AL_MAILBOX_WRONG_THREAD &&
         call.begin_result == AL_MAILBOX_WRONG_THREAD &&
         call.stats_result == AL_MAILBOX_WRONG_THREAD &&
         call.view_result == AL_MAILBOX_WRONG_THREAD;
}

static void test_contract_and_sizes(void) {
  al_mailbox_config config = make_config(2u);
  al_mailbox_storage_requirements requirements;
  al_module_desc malformed = test_module;
  uint32_t bad_resume_inputs[3] = {TEST_STATE_TYPE_ID, TEST_STATE_TYPE_ID,
                                   TEST_INT_TYPE_ID};
  uint32_t alias_init_inputs[1] = {5u};
  uint32_t alias_begin_inputs[2] = {TEST_STATE_TYPE_ID, 5u};
  uint32_t alias_resume_inputs[3] = {TEST_STATE_TYPE_ID,
                                     TEST_CONTINUATION_TYPE_ID, 5u};
  al_module_entry bad_entries[3];
  al_module_entry alias_entries[3];
  al_module_entry unused_entries[4];
  al_type_desc alias_types[6];
  al_type_desc bad_builtin_types[5];
  al_program_desc alias_program = test_program;
  al_program_desc bad_builtin_program = test_program;
  al_module_desc alias_module = test_module;
  al_module_desc unused_module = test_module;
  al_module_desc bad_builtin_module = test_module;
  const char *const alias_names[6] = {"Int",   "Bool",         "Unit",
                                      "State", "Continuation", "MessageInt"};
  const char *const bad_builtin_names[5] = {"Integer", "Bool", "Unit", "State",
                                            "Continuation"};
  al_mailbox_result result;

  result =
      al_mailbox_get_storage_requirements(&test_module, &config, &requirements);
  check(result == AL_MAILBOX_OK, "checked storage requirement succeeds");
  check(requirements.control_abi_version == AL_MAILBOX_CONTROL_ABI_VERSION &&
            requirements.struct_size == sizeof(requirements) &&
            requirements.storage_alignment == 8u &&
            requirements.workspace_capacity == TEST_WORKSPACE_SLOTS,
        "storage requirements report ABI and alignment");
  check(requirements.retained_reserved_bytes ==
            2u * config.mailbox_capacity *
                (sizeof(al_arena) + config.retained_byte_capacity +
                 config.retained_node_capacity * sizeof(al_node)),
        "retained reservation reports both descriptors and banks per mailbox");
  check(requirements.scratch_reserved_bytes ==
            sizeof(al_arena) + config.scratch_byte_capacity +
                config.scratch_node_capacity * sizeof(al_node),
        "scratch reservation includes its descriptor and node table");
  check(requirements.storage_bytes ==
            requirements.retained_reserved_bytes +
                requirements.scratch_reserved_bytes +
                requirements.workspace_reserved_bytes +
                requirements.controller_reserved_bytes,
        "backing categories reconcile exactly to checked storage bytes");

  config.mailbox_capacity = 0u;
  check(al_mailbox_get_storage_requirements(&test_module, &config,
                                            &requirements) ==
            AL_MAILBOX_INVALID_ARGUMENT,
        "zero mailbox capacity rejects");
  config = make_config(2u);
  config.control_abi_version = 0u;
  check(al_mailbox_get_storage_requirements(&test_module, &config,
                                            &requirements) ==
            AL_MAILBOX_INVALID_ARGUMENT,
        "wrong control ABI version rejects");
  config = make_config(2u);
  config.mailbox_capacity = UINT32_MAX;
  config.retained_byte_capacity = UINT32_MAX;
  config.retained_node_capacity = UINT32_MAX;
  check(al_mailbox_get_storage_requirements(
            &test_module, &config, &requirements) == AL_MAILBOX_SIZE_OVERFLOW,
        "checked layout rejects multiplication overflow");

  config = make_config(2u);
  malformed.abi_version = AL_MODULE_ABI_VERSION + 1u;
  check(al_mailbox_get_storage_requirements(
            &malformed, &config, &requirements) == AL_MAILBOX_INVALID_MODULE,
        "wrong module ABI version rejects");

  memcpy(unused_entries, test_entries, sizeof(test_entries));
  unused_entries[3] = (al_module_entry){sizeof(al_module_entry),
                                        3u,
                                        "unused.zero-output",
                                        test_unused_entry,
                                        0u,
                                        0u,
                                        0u,
                                        0u,
                                        NULL,
                                        NULL,
                                        NULL};
  unused_module.entry_count = 4u;
  unused_module.entries = unused_entries;
  check(al_mailbox_get_storage_requirements(&unused_module, &config,
                                            &requirements) == AL_MAILBOX_OK,
        "unselected zero-output entry is valid module metadata");

  bad_builtin_module.type_names = bad_builtin_names;
  check(al_mailbox_get_storage_requirements(&bad_builtin_module, &config,
                                            &requirements) ==
            AL_MAILBOX_INVALID_MODULE,
        "canonical built-in type names are required");
  memcpy(bad_builtin_types, test_types, sizeof(test_types));
  bad_builtin_types[0].kind = AL_RUNTIME_TYPE_BOOL;
  bad_builtin_program.types = bad_builtin_types;
  bad_builtin_module = test_module;
  bad_builtin_module.program = &bad_builtin_program;
  check(al_mailbox_get_storage_requirements(&bad_builtin_module, &config,
                                            &requirements) ==
            AL_MAILBOX_INVALID_MODULE,
        "canonical Int type ID must carry the Int kind");

  memcpy(alias_types, test_types, sizeof(test_types));
  alias_types[5] = (al_type_desc){AL_RUNTIME_TYPE_INT, 0u, NULL};
  alias_program.types = alias_types;
  alias_program.type_count = 6u;
  memcpy(alias_entries, test_entries, sizeof(test_entries));
  alias_entries[0].input_type_ids = alias_begin_inputs;
  alias_entries[1].input_type_ids = alias_init_inputs;
  alias_entries[2].input_type_ids = alias_resume_inputs;
  alias_module = test_module;
  alias_module.program = &alias_program;
  alias_module.entries = alias_entries;
  alias_module.type_names = alias_names;
  check(al_mailbox_get_storage_requirements(
            &alias_module, &config, &requirements) == AL_MAILBOX_INVALID_MODULE,
        "Int-backed nominal message type cannot bypass canonical Int ID");

  memcpy(bad_entries, test_entries, sizeof(bad_entries));
  bad_entries[2].input_type_ids = bad_resume_inputs;
  malformed = test_module;
  malformed.entries = bad_entries;
  check(al_mailbox_get_storage_requirements(
            &malformed, &config, &requirements) == AL_MAILBOX_INVALID_MODULE,
        "inconsistent continuation type ID rejects");
  check(
      al_mailbox_get_storage_requirements(
          &test_module, &config,
          (al_mailbox_storage_requirements *)(void *)((uint8_t *)&requirements +
                                                      1u)) ==
          AL_MAILBOX_INVALID_ARGUMENT,
      "unaligned requirement output rejects");
}

int main(void) {
  __declspec(align(8)) uint8_t storage_a[TEST_STORAGE_BYTES];
  __declspec(align(8)) uint8_t storage_b[TEST_STORAGE_BYTES];
  al_mailbox_config config = make_config(2u);
  al_mailbox_storage_requirements requirements;
  al_mailbox_runtime *runtime_a = NULL;
  al_mailbox_runtime *runtime_b = NULL;
  al_mailbox_call_info call_info;
  al_mailbox_runtime_stats stats;
  al_mailbox_runtime_stats stats_b = {0};
  al_mailbox_state_view active_view = {0};
  al_mailbox_state_view empty_view = {0};
  al_mailbox_call_limits limits;
  al_mailbox_token token_a;
  al_mailbox_token token_b;
  al_mailbox_token token_a2;
  al_mailbox_token token_max;
  al_mailbox_token token_generation_boundary;
  al_mailbox_token untouched;
  al_mailbox_call_info aliased_outputs;
  uint32_t before_calls;
  int64_t initial_state_handle = 0;
  uint32_t initial_state_generation = 0u;
  al_mailbox_result result;
  al_mailbox_result generation_result;

  memset(test_handler_calls, 0, sizeof(test_handler_calls));
  test_scratch_poison_checks = 0u;
  test_reentry_runtime = NULL;
  test_reentry_armed = 0u;
  test_contract_and_sizes();
  check(test_failures == 0u,
        "contract tests pass before runtime behavior tests");
  if (test_failures != 0u) {
    return 1;
  }

  result =
      al_mailbox_get_storage_requirements(&test_module, &config, &requirements);
  check(result == AL_MAILBOX_OK &&
            requirements.storage_bytes <= sizeof(storage_a),
        "behavior fixture fits bounded test storage");
  if (result != AL_MAILBOX_OK ||
      requirements.storage_bytes > sizeof(storage_a)) {
    return 1;
  }
  check(al_mailbox_runtime_init(&test_module, &config, storage_a,
                                requirements.storage_bytes - 1u,
                                &runtime_a) == AL_MAILBOX_INVALID_STORAGE &&
            runtime_a == NULL,
        "one-byte-short storage rejects before initialization");
  check(al_mailbox_runtime_init(&test_module, &config, storage_a + 1u,
                                requirements.storage_bytes,
                                &runtime_a) == AL_MAILBOX_INVALID_STORAGE,
        "misaligned storage rejects");
  check(al_mailbox_runtime_init(&test_module, &config,
                                (void *)(uintptr_t)&test_module,
                                requirements.storage_bytes,
                                &runtime_a) == AL_MAILBOX_INVALID_STORAGE,
        "module descriptor cannot be reused as writable runtime storage");
  check(al_mailbox_runtime_init(&test_module, &config, storage_a,
                                requirements.storage_bytes,
                                &runtime_a) == AL_MAILBOX_OK,
        "exact checked storage initializes runtime A");
  check(al_mailbox_runtime_init(&test_module, &config, storage_b,
                                requirements.storage_bytes,
                                &runtime_b) == AL_MAILBOX_OK,
        "second runtime receives a distinct runtime identity");
  if (runtime_a == NULL || runtime_b == NULL) {
    return 1;
  }

  check(al_mailbox_init_mailbox(runtime_a, 0u, 10, NULL, &call_info) ==
                AL_MAILBOX_OK &&
            call_info.handler_status == AL_RUNTIME_STATUS_SUCCESS &&
            call_info.steps_consumed == 1u && get_state(runtime_a, 0u, 10, 0u),
        "init stores opaque State in mailbox A");
  check(al_mailbox_get_state_view(runtime_a, 0u, &active_view) == AL_MAILBOX_OK,
        "initial state view exposes only a borrowed arena root");
  if (active_view.owner != NULL && active_view.roots != NULL) {
    initial_state_handle = active_view.roots[0];
    initial_state_generation = active_view.owner->generation;
  }
  check(al_mailbox_init_mailbox(runtime_a, 1u, 100, NULL, &call_info) ==
                AL_MAILBOX_OK &&
            get_state(runtime_a, 1u, 100, 0u),
        "init stores an independent State in mailbox B");
  check(al_mailbox_init_mailbox(runtime_a, 0u, 999, NULL, NULL) ==
            AL_MAILBOX_ALREADY_INITIALIZED,
        "mailbox cannot be initialized twice");
  check(al_mailbox_init_mailbox(runtime_a, 2u, 1, NULL, NULL) ==
            AL_MAILBOX_INVALID_MAILBOX,
        "mailbox ID outside fixed capacity rejects");
  check(al_mailbox_init_mailbox(runtime_b, 0u, 50, NULL, &call_info) ==
                AL_MAILBOX_OK &&
            get_state(runtime_b, 0u, 50, 0u),
        "second runtime initializes an independent mailbox");

  memset(&untouched, 0x6D, sizeof(untouched));
  memset(&test_reentry_token, 0x4B, sizeof(test_reentry_token));
  test_reentry_runtime = runtime_a;
  test_reentry_mailbox_id = 1u;
  test_reentry_result = AL_MAILBOX_OK;
  test_reentry_armed = 1u;
  check(al_mailbox_begin(runtime_a, 0u, 14, NULL, &token_a, &call_info) ==
                AL_MAILBOX_OK &&
            call_info.handler_status == AL_RUNTIME_STATUS_SUCCESS &&
            get_state(runtime_a, 0u, 10, 1u),
        "begin retains State and Continuation before issuing token");
  test_reentry_armed = 0u;
  test_reentry_runtime = NULL;
  check(test_reentry_result == AL_MAILBOX_BUSY &&
            test_reentry_token.opaque[0] == UINT64_C(0x4B4B4B4B4B4B4B4B) &&
            get_state(runtime_a, 1u, 100, 0u),
        "reentrant begin for another mailbox is rejected by the shared lease");
  before_calls = test_handler_calls[0];
  check(al_mailbox_begin(runtime_a, 1u, 3, NULL,
                         (al_mailbox_token *)(void *)storage_a,
                         NULL) == AL_MAILBOX_INVALID_ARGUMENT &&
            test_handler_calls[0] == before_calls &&
            get_state(runtime_a, 0u, 10, 1u) &&
            get_state(runtime_a, 1u, 100, 0u),
        "begin rejects token output that aliases caller-owned runtime storage");
  memset(&aliased_outputs, 0x5C, sizeof(aliased_outputs));
  before_calls = test_handler_calls[0];
  check(al_mailbox_begin(runtime_a, 1u, 3, NULL,
                         (al_mailbox_token *)(void *)&aliased_outputs,
                         &aliased_outputs) == AL_MAILBOX_INVALID_ARGUMENT &&
            test_handler_calls[0] == before_calls &&
            ((const uint8_t *)&aliased_outputs)[0] == 0x5Cu &&
            get_state(runtime_a, 1u, 100, 0u),
        "begin rejects overlapping token and call-info output buffers");
  before_calls = test_handler_calls[2];
  check(
      al_mailbox_resume(runtime_a, 0u, &token_a, 2, NULL,
                        (al_mailbox_call_info *)(void *)storage_a) ==
              AL_MAILBOX_INVALID_ARGUMENT &&
          al_mailbox_resume(
              runtime_a, 0u, &token_a, 2,
              (const al_mailbox_call_limits *)(const void *)storage_a,
              NULL) == AL_MAILBOX_INVALID_ARGUMENT &&
          al_mailbox_resume(runtime_a, 0u, &token_a, 2, NULL,
                            (al_mailbox_call_info *)(uintptr_t)&test_module) ==
              AL_MAILBOX_INVALID_ARGUMENT &&
          al_mailbox_get_stats(runtime_a,
                               (al_mailbox_runtime_stats *)(void *)storage_a) ==
              AL_MAILBOX_INVALID_ARGUMENT &&
          al_mailbox_get_state_view(
              runtime_a, 0u, (al_mailbox_state_view *)(void *)storage_a) ==
              AL_MAILBOX_INVALID_ARGUMENT &&
          test_handler_calls[2] == before_calls &&
          get_state(runtime_a, 0u, 10, 1u),
      "resume/stats/view reject buffers aliasing runtime or module storage");
  check(al_mailbox_get_state_view(runtime_a, 0u, &active_view) ==
                AL_MAILBOX_OK &&
            active_view.owner->generation != initial_state_generation,
        "begin stages into a freshly generated retained bank");
  check(al_mailbox_begin(runtime_a, 1u, 6, NULL, &token_b, &call_info) ==
                AL_MAILBOX_OK &&
            get_state(runtime_a, 1u, 100, 1u),
        "overlapping begin suspends mailbox B");
  before_calls = test_handler_calls[0];
  check(al_mailbox_begin(runtime_a, 0u, 3, NULL, &untouched, NULL) ==
                AL_MAILBOX_BUSY &&
            test_handler_calls[0] == before_calls &&
            untouched.opaque[0] == UINT64_C(0x6D6D6D6D6D6D6D6D),
        "busy admission rejects before handler and preserves token output");
  before_calls = test_handler_calls[2];
  check(al_mailbox_resume(runtime_a, 1u, &token_a, 2, NULL, NULL) ==
                AL_MAILBOX_WRONG_OWNER_TOKEN &&
            test_handler_calls[2] == before_calls &&
            get_state(runtime_a, 1u, 100, 1u),
        "wrong-mailbox token rejects before handler and preserves state");
  check(run_foreign_thread(runtime_a, &token_a) != 0u &&
            get_state(runtime_a, 0u, 10, 1u),
        "foreign thread cannot resume or dispose the runtime");
  before_calls = test_handler_calls[2];
  check(al_mailbox_resume(runtime_b, 0u, &token_b, 2, NULL, NULL) ==
                AL_MAILBOX_CROSS_RUNTIME_TOKEN &&
            test_handler_calls[2] == before_calls &&
            get_state(runtime_b, 0u, 50, 0u),
        "cross-runtime token rejects before handler");

  check(al_mailbox_resume(runtime_a, 1u, &token_b, 2, NULL, &call_info) ==
                AL_MAILBOX_OK &&
            get_state(runtime_a, 1u, 103, 0u),
        "mailbox B resumes with its own token and primitive message");
  before_calls = test_handler_calls[2];
  check(al_mailbox_resume(runtime_a, 1u, &token_b, 2, NULL, NULL) ==
                AL_MAILBOX_DUPLICATE_TOKEN &&
            test_handler_calls[2] == before_calls &&
            get_state(runtime_a, 1u, 103, 0u),
        "last completed token is a bounded duplicate identity");

  check(view_state_total(&empty_view) == INT64_MIN,
        "empty state view fails closed");
  check(al_mailbox_resume(runtime_a, 0u, &token_a, 0, NULL, &call_info) ==
                AL_MAILBOX_HANDLER_FAILURE &&
            call_info.handler_status == AL_RUNTIME_STATUS_DIAGNOSTIC &&
            call_info.error_metadata_id == 77 &&
            call_info.error_argument0 == 1 && call_info.error_argument1 == 0 &&
            get_state(runtime_a, 0u, 10, 1u),
        "handler diagnostic preserves State and retryable pending token");
  limits = make_limits(7u, TEST_RETAINED_NODES);
  check(al_mailbox_resume(runtime_a, 0u, &token_a, 2, &limits, &call_info) ==
                AL_MAILBOX_RETAINED_CAPACITY &&
            call_info.handler_status == AL_RUNTIME_RETAINED_CAPACITY &&
            call_info.error_argument0 == 8 && call_info.error_argument1 == 1 &&
            get_state(runtime_a, 0u, 10, 1u),
        "seven-byte retained limit preserves State and token after capacity "
        "error");
  check(al_mailbox_resume(runtime_a, 0u, &token_a, 2, NULL, &call_info) ==
                AL_MAILBOX_OK &&
            get_state(runtime_a, 0u, 17, 0u),
        "same completion token retries successfully after failures");
  before_calls = test_handler_calls[2];
  check(al_mailbox_resume(runtime_a, 0u, &token_a, 2, NULL, NULL) ==
                AL_MAILBOX_DUPLICATE_TOKEN &&
            test_handler_calls[2] == before_calls,
        "successful resume consumes its completion exactly once");

  limits = make_limits(TEST_RETAINED_BYTES, 0u);
  untouched.opaque[0] = UINT64_C(0x123456789ABCDEF0);
  check(al_mailbox_begin(runtime_a, 0u, 5, &limits, &untouched, &call_info) ==
                AL_MAILBOX_RETAINED_CAPACITY &&
            call_info.error_argument1 == 2 &&
            untouched.opaque[0] == UINT64_C(0x123456789ABCDEF0) &&
            get_state(runtime_a, 0u, 17, 0u),
        "explicit zero node limit is literal and failed begin issues no token");
  check(al_mailbox_begin(runtime_a, 0u, 5, NULL, &token_a2, &call_info) ==
                AL_MAILBOX_OK &&
            get_state(runtime_a, 0u, 17, 1u),
        "begin after a completed turn starts a new pending identity");
  check(al_mailbox_resume(runtime_a, 0u, &token_a2, 2, NULL, NULL) ==
                AL_MAILBOX_OK &&
            get_state(runtime_a, 0u, 19, 0u),
        "second A completion produces expected truncating result");
  check(al_mailbox_get_state_view(runtime_a, 0u, &active_view) ==
                AL_MAILBOX_OK &&
            active_view.owner->generation != initial_state_generation &&
            stale_state_handle_rejected(&active_view, initial_state_handle),
        "reused bank rejects a saved handle from its prior generation");
  before_calls = test_handler_calls[2];
  check(al_mailbox_resume(runtime_a, 0u, &token_a, 2, NULL, NULL) ==
                AL_MAILBOX_STALE_TOKEN &&
            test_handler_calls[2] == before_calls,
        "older completion outside bounded last-completed slot is stale");

  check(al_mailbox_init_mailbox(runtime_b, 1u, 500, NULL, &call_info) ==
                AL_MAILBOX_OK &&
            get_state(runtime_b, 1u, 500, 0u),
        "second runtime prepares an independent mailbox for token boundary "
        "tests");
  check(al_mailbox_test_set_next_token_sequence(runtime_b, UINT64_MAX) ==
            AL_MAILBOX_OK,
        "test-only sequence hook positions the exhaustion boundary");
  check(al_mailbox_begin(runtime_b, 1u, 1, NULL, &token_max, NULL) ==
                AL_MAILBOX_OK &&
            token_max.opaque[1] == UINT64_MAX,
        "maximum token sequence may be issued once without wrapping");
  check(al_mailbox_resume(runtime_b, 1u, &token_max, 1, NULL, NULL) ==
                AL_MAILBOX_OK &&
            get_state(runtime_b, 1u, 501, 0u),
        "maximum sequence completion is accepted once");
  before_calls = test_handler_calls[0];
  check(al_mailbox_begin(runtime_b, 1u, 1, NULL, &untouched, NULL) ==
                AL_MAILBOX_TOKEN_EXHAUSTED &&
            test_handler_calls[0] == before_calls,
        "token sequence exhaustion rejects before handler execution");

  check(al_mailbox_get_stats(runtime_a, &stats) == AL_MAILBOX_OK &&
            stats.mailbox_capacity == 2u && stats.initialized_mailboxes == 2u &&
            stats.pending_mailboxes == 0u &&
            stats.retained_reserved_bytes ==
                requirements.retained_reserved_bytes &&
            stats.live_retained_bytes >= 16u &&
            stats.scratch_high_water_bytes >= 8u &&
            stats.scratch_high_water_nodes >= 1u &&
            stats.scratch_lease_acquisitions == stats.scratch_lease_returns &&
            stats.outstanding_scratch_leases == 0u &&
            stats.handler_invocations == stats.scratch_lease_acquisitions,
        "stats separate both-bank backing from live state and balance scratch "
        "leases");
  check(al_mailbox_get_stats(runtime_b, &stats_b) == AL_MAILBOX_OK &&
            stats_b.outstanding_scratch_leases == 0u &&
            stats_b.scratch_lease_acquisitions ==
                stats_b.scratch_lease_returns &&
            test_scratch_poison_checks ==
                stats.handler_invocations + stats_b.handler_invocations,
        "every direct handler observes fresh poisoned scratch and workspace");

  check(al_mailbox_test_set_next_generation(runtime_a, (uint64_t)UINT32_MAX -
                                                           1u) == AL_MAILBOX_OK,
        "test-only generation hook positions the final safe pair");
  generation_result = al_mailbox_begin(runtime_a, 0u, 1, NULL,
                                       &token_generation_boundary, NULL);
  check(generation_result == AL_MAILBOX_OK,
        "generation boundary begin executes the last safe generation pair");
  if (generation_result == AL_MAILBOX_OK) {
    check(get_state(runtime_a, 0u, 19, 1u) &&
              al_mailbox_get_state_view(runtime_a, 0u, &active_view) ==
                  AL_MAILBOX_OK &&
              active_view.owner->generation == UINT32_MAX,
          "last distinct nonzero scratch and staging generations are usable");
    before_calls = test_handler_calls[2];
    check(al_mailbox_resume(runtime_a, 0u, &token_generation_boundary, 2, NULL,
                            NULL) == AL_MAILBOX_GENERATION_EXHAUSTED &&
              test_handler_calls[2] == before_calls &&
              get_state(runtime_a, 0u, 19, 1u),
          "generation exhaustion rejects before handler and preserves pending "
          "state/token");
  } else {
    check(0u,
          "generation exhaustion rejects before handler and preserves pending "
          "state/token");
  }

  check(al_mailbox_dispose(runtime_a) == AL_MAILBOX_OK &&
            al_mailbox_dispose(runtime_a) == AL_MAILBOX_OK,
        "disposal is idempotent on creator thread");
  check(al_mailbox_get_stats(runtime_a, &stats) == AL_MAILBOX_OK &&
            stats.initialized_mailboxes == 0u &&
            stats.pending_mailboxes == 0u && stats.live_retained_bytes == 0u &&
            stats.live_retained_nodes == 0u &&
            stats.outstanding_scratch_leases == 0u &&
            stats.scratch_lease_acquisitions == stats.scratch_lease_returns,
        "post-dispose stats expose fully released state and balanced cleanup");
  check(al_mailbox_get_stats(
            runtime_a, (al_mailbox_runtime_stats *)(uintptr_t)&test_module) ==
            AL_MAILBOX_INVALID_ARGUMENT,
        "post-dispose stats reject output aliasing live module metadata");
  check(al_mailbox_begin(runtime_a, 0u, 1, NULL, &untouched, NULL) ==
                AL_MAILBOX_DISPOSED &&
            al_mailbox_get_state_view(runtime_a, 0u,
                                      &(al_mailbox_state_view){0}) ==
                AL_MAILBOX_DISPOSED,
        "disposed runtime rejects later work");
  check(al_mailbox_dispose(runtime_b) == AL_MAILBOX_OK,
        "second runtime disposes independently");

  check(al_mailbox_test_exhaust_instance_counter() == AL_MAILBOX_OK,
        "test-only hook reaches process instance ID boundary");
  runtime_b = NULL;
  check(al_mailbox_runtime_init(&test_module, &config, storage_b,
                                requirements.storage_bytes,
                                &runtime_b) == AL_MAILBOX_INSTANCE_EXHAUSTED &&
            runtime_b == NULL,
        "process runtime instance IDs reject exhaustion without reuse");

  if (test_failures != 0u) {
    (void)fprintf(stderr, "mailbox_runtime_test: %" PRIu32 " failure(s)\n",
                  test_failures);
    (void)printf("{\"kind\":\"mailbox-unit-summary\",\"passed\":false,"
                 "\"failureCount\":%" PRIu32 "}\n",
                 test_failures);
    return 1;
  }
  (void)puts(
      "{\"kind\":\"mailbox-unit-summary\",\"passed\":true,\"failureCount\":0}");
  return 0;
}
